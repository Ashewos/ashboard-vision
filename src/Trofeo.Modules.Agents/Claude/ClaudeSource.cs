using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Source Claude Code : les sessions ouvertes (<c>sessions/*.json</c>), le détail de celle qui est choisie (sa
/// transcription lue par la fin, puis seulement les octets ajoutés ; ses tâches), son contexte, et les quotas relevés
/// par le mod <c>ashboard-quotas</c>. Ne contacte rien en ligne et ne lit que les fichiers listés dans le plan. Appelée depuis un seul
/// thread à la fois.
/// </summary>
/// <param name="claudeHome">Le dossier de configuration de Claude Code (voir <see cref="ClaudeHome.Resolve"/>).</param>
/// <param name="isProcessAlive">Dit si un processus tourne encore (en production : <see cref="IsProcessAlive"/>).</param>
public sealed class ClaudeSource(string claudeHome, Func<int, bool> isProcessAlive) : IAgentSource
{
    private const int MaxDetails = 3;
    private static readonly TimeSpan CaptureMaxAge = TimeSpan.FromDays(8);
    private static readonly TimeSpan CaptureDeleteAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan LocateRetryEvery = TimeSpan.FromSeconds(5);

    private readonly ClaudeSessionDirectory _directory = new(Path.Combine(claudeHome, "sessions"), isProcessAlive);
    private readonly string _projectsDir = Path.Combine(claudeHome, "projects");
    private readonly string _statusLineDir = ClaudeHome.StatusLineDir(claudeHome);
    // Dossier des captures d'avant le changement de nom : lu après le nouveau tant que ses captures ont moins de 8 jours.
    private readonly string _legacyStatusLineDir = ClaudeHome.LegacyStatusLineDir(claudeHome);

    // ProjectNames remonte l'arborescence sur le disque : un seul calcul par dossier de travail.
    private readonly Dictionary<string, string> _projectNames = new(StringComparer.Ordinal);
    // Transcription trouvée par session ; une absence n'est cherchée à nouveau que toutes les quelques secondes.
    private readonly Dictionary<string, (string? Path, DateTimeOffset CheckedAt)> _transcripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DetailState> _details = new(StringComparer.Ordinal);
    // Captures déjà lues, par chemin : seuls les fichiers nouveaux ou modifiés sont relus.
    private readonly Dictionary<string, CachedCapture> _captures = new(StringComparer.Ordinal);
    // Liste de tâches par session, avec la signature du dossier (noms, dates, tailles) au moment de la lecture.
    private readonly Dictionary<string, (string Signature, IReadOnlyList<AgentTask> Tasks)> _taskLists = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private long _tick;

    private sealed record CachedCapture(DateTime WriteTimeUtc, long Length, StatusLineRecord Record);

    private sealed class DetailState(string path)
    {
        public string Path { get; } = path;

        public TranscriptTail Tail { get; } = new(path);

        public ClaudeTranscript Transcript { get; } = new();

        public long LastUsed { get; set; }
    }

    /// <summary>Lecteur d'un dossier de tâches (remplaçable par les tests).</summary>
    internal Func<string, IReadOnlyList<AgentTask>?> TaskListReader { get; init; } = ClaudeTaskList.Read;

    /// <summary>Le processus de ce numéro tourne-t-il ? Ne lève jamais d'exception.</summary>
    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Le processus existe mais on n'a pas le droit de l'interroger : il tourne.
            return true;
        }
    }

    /// <inheritdoc />
    public AgentSnapshot Poll(string? selectedSessionId, DateTimeOffset now)
    {
        if (!Directory.Exists(claudeHome))
        {
            return new AgentSnapshot(false, [], null, null);
        }

        Cleanup(now);
        var files = _directory.Read();
        var captures = ReadCaptures(now);

        var sessions = files
            .OrderBy(f => f.StartedAt)
            .ThenBy(f => f.SessionId, StringComparer.Ordinal)
            .Select(f => new AgentSessionSummary(f.SessionId, ProjectName(f.Cwd), ActivityOf(f.Status), f.StartedAt, f.LastActivityAt))
            .ToList();

        AgentSessionDetail? selected = null;
        if (selectedSessionId is not null && files.FirstOrDefault(f => f.SessionId == selectedSessionId) is { } file)
        {
            selected = BuildDetail(file, captures.FirstOrDefault(c => c.SessionId == file.SessionId), captures, now);
        }

        Forget(files);
        return new AgentSnapshot(true, sessions, selected, QuotasOf(captures));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _projectNames.Clear();
        _transcripts.Clear();
        _details.Clear();
        _captures.Clear();
        _taskLists.Clear();
    }

    private static AgentActivity ActivityOf(string status) => status switch
    {
        "busy" => AgentActivity.Working,
        "waiting" => AgentActivity.Waiting,
        _ => AgentActivity.Idle,
    };

    private string ProjectName(string cwd)
    {
        if (_projectNames.TryGetValue(cwd, out var name))
        {
            return name;
        }

        try
        {
            name = ProjectNames.FromWorkingDirectory(cwd);
        }
        catch (Exception)
        {
            // Dossier illisible : on s'en tient à son nom, l'onglet reste affiché.
            var trimmed = cwd.TrimEnd('\\', '/');
            name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        }

        _projectNames[cwd] = name;
        return name;
    }

    /// <summary>
    /// Le détail de la session choisie. Une transcription ou une liste de tâches qui pose problème ne fait perdre que ce
    /// détail : la session reste listée et le sondage continue.
    /// </summary>
    private AgentSessionDetail BuildDetail(ClaudeSessionFile file, StatusLineRecord? capture,
        IReadOnlyList<StatusLineRecord> captures, DateTimeOffset now)
    {
        try
        {
            var transcript = UpdateTranscript(file, now);
            var model = transcript?.Model ?? capture?.ModelId;
            IReadOnlyList<AgentTask> tasks = ReadTasks(file.SessionId) ?? transcript?.Todos ?? [];

            return new AgentSessionDetail(
                file.SessionId,
                transcript?.CurrentTask,
                transcript?.TaskStartedAt,
                [.. transcript?.Feed ?? []],
                [.. tasks],
                model,
                ContextWindow.Percent(transcript?.ContextTokens, capture?.ModelId ?? model, capture, now,
                    KnownWindowForModel(captures, model)));
        }
        catch (Exception)
        {
            return new AgentSessionDetail(file.SessionId, null, null, [], [], capture?.ModelId, null);
        }
    }

    /// <summary>
    /// La fenêtre de contexte relevée par la capture la plus récente (toutes sessions confondues) qui donne une taille pour
    /// ce modèle ; <c>null</c> sans modèle ou sans telle capture. <paramref name="captures"/> va de la plus récente à la plus ancienne.
    /// </summary>
    private static long? KnownWindowForModel(IReadOnlyList<StatusLineRecord> captures, string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        // Le mod peut rapporter « claude-opus-5-5[1m] » là où la transcription écrit « claude-opus-5-5 ».
        var wanted = ModelNames.WithoutSuffix(model);
        return captures.FirstOrDefault(c => c.ContextWindowSize is > 0
                && string.Equals(ModelNames.WithoutSuffix(c.ModelId), wanted, StringComparison.OrdinalIgnoreCase))
            ?.ContextWindowSize;
    }

    /// <summary>Applique à l'état de la session les lignes ajoutées à sa transcription ; <c>null</c> si elle n'existe pas encore.</summary>
    private ClaudeTranscript? UpdateTranscript(ClaudeSessionFile file, DateTimeOffset now)
    {
        var path = LocateTranscript(file, now);
        if (path is null)
        {
            _details.Remove(file.SessionId);
            return null;
        }

        if (!_details.TryGetValue(file.SessionId, out var state) || state.Path != path)
        {
            state = new DetailState(path);
            _details[file.SessionId] = state;
        }

        state.LastUsed = ++_tick;
        var lines = state.Tail.ReadNewLines();
        if (state.Tail.Restarted)
        {
            state.Transcript.Reset();
        }

        foreach (var line in lines)
        {
            try
            {
                state.Transcript.Apply(line);
            }
            catch (Exception)
            {
                // Une ligne inutilisable ne doit pas faire perdre celles qui suivent.
            }
        }

        while (_details.Count > MaxDetails)
        {
            var oldest = _details.MinBy(pair => pair.Value.LastUsed).Key;
            _details.Remove(oldest);
        }

        return state.Transcript;
    }

    private string? LocateTranscript(ClaudeSessionFile file, DateTimeOffset now)
    {
        if (_transcripts.TryGetValue(file.SessionId, out var known) && (known.Path is not null || now - known.CheckedAt < LocateRetryEvery))
        {
            return known.Path;
        }

        var path = TranscriptLocator.Find(_projectsDir, file.Cwd, file.SessionId);
        _transcripts[file.SessionId] = (path, now);
        return path;
    }

    /// <summary>
    /// Les tâches de la session (<c>null</c> sans dossier de tâches). Le dossier n'est relu que si sa signature change.
    /// </summary>
    private IReadOnlyList<AgentTask>? ReadTasks(string sessionId)
    {
        var dir = Path.Combine(claudeHome, "tasks", sessionId);
        var signature = TaskSignature(dir);
        if (signature is null)
        {
            _taskLists.Remove(sessionId);
            return null;
        }

        if (_taskLists.TryGetValue(sessionId, out var cached) && cached.Signature == signature)
        {
            return cached.Tasks;
        }

        var tasks = TaskListReader(dir);
        if (tasks is null)
        {
            _taskLists.Remove(sessionId);
        }
        else
        {
            _taskLists[sessionId] = (signature, tasks);
        }

        return tasks;
    }

    /// <summary>Noms, dates d'écriture et tailles des <c>*.json</c> du dossier ; <c>null</c> si le dossier est absent ou illisible.</summary>
    private static string? TaskSignature(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists)
            {
                return null;
            }

            var signature = new StringBuilder();
            foreach (var file in info.EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                signature.Append(file.Name).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|').Append(file.Length).Append(';');
            }

            return signature.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Oublie ce qu'on garde pour les sessions qui ne sont plus ouvertes.</summary>
    private void Forget(IReadOnlyList<ClaudeSessionFile> live)
    {
        var ids = live.Select(f => f.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _transcripts.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _transcripts.Remove(id);
        }

        foreach (var id in _details.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _details.Remove(id);
        }

        foreach (var id in _taskLists.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _taskLists.Remove(id);
        }
    }

    /// <summary>
    /// Les captures de moins de 8 jours, de la plus récente à la plus ancienne : celles du dossier actuel, puis celles de
    /// l'ancien (pour une même session, le dossier actuel l'emporte). Un fichier n'est relu que s'il est nouveau ou si sa
    /// date d'écriture ou sa taille a changé ; l'âge se juge à chaque relevé.
    /// </summary>
    private IReadOnlyList<StatusLineRecord> ReadCaptures(DateTimeOffset now)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            var records = new List<StatusLineRecord>();
            foreach (var dir in new[] { _statusLineDir, _legacyStatusLineDir })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                // EnumerateFiles d'un DirectoryInfo donne date et taille sans appel supplémentaire par fichier.
                foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*.json"))
                {
                    var path = info.FullName;
                    if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    seen.Add(path);
                    if (!_captures.TryGetValue(path, out var cached)
                        || cached.WriteTimeUtc != info.LastWriteTimeUtc || cached.Length != info.Length)
                    {
                        // Un fichier illisible n'est pas gardé : on le réessaie au prochain relevé.
                        cached = StatusLineFile.Read(path) is { } record
                            ? new CachedCapture(info.LastWriteTimeUtc, info.Length, record)
                            : null;
                        if (cached is null)
                        {
                            _captures.Remove(path);
                            continue;
                        }

                        _captures[path] = cached;
                    }

                    if (now - cached.Record.CapturedAt <= CaptureMaxAge && sessions.Add(cached.Record.SessionId))
                    {
                        records.Add(cached.Record);
                    }
                }
            }

            foreach (var gone in _captures.Keys.Where(path => !seen.Contains(path)).ToList())
            {
                _captures.Remove(gone);
            }

            records.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
            return records;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Les quotas, fenêtre par fenêtre : celle de 5 h vient de la capture la plus récente qui la porte, l'hebdomadaire de la
    /// plus récente qui la porte, toutes sessions confondues. La date est la plus récente des deux.
    /// </summary>
    private static AgentQuotas? QuotasOf(IReadOnlyList<StatusLineRecord> captures)
    {
        // ReadCaptures rend les captures de la plus récente à la plus ancienne.
        var five = captures.FirstOrDefault(c => c.FiveHour is not null);
        var weekly = captures.FirstOrDefault(c => c.SevenDay is not null);
        if (five is null && weekly is null)
        {
            return null;
        }

        var at = five is null ? weekly!.CapturedAt : weekly is null ? five.CapturedAt : Max(five.CapturedAt, weekly.CapturedAt);
        return new AgentQuotas(Window(five?.FiveHour), Window(weekly?.SevenDay), at);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static QuotaWindow? Window(CapturedQuota? quota) => quota is null ? null : new QuotaWindow(quota.UsedPercent, quota.ResetsAt);

    /// <summary>Supprime les vieilles captures des deux dossiers, au plus une fois par heure.</summary>
    private void Cleanup(DateTimeOffset now)
    {
        if (now - _lastCleanup < CleanupEvery)
        {
            return;
        }

        _lastCleanup = now;
        try
        {
            StatusLineFile.DeleteOlderThan(_statusLineDir, now, CaptureDeleteAge);
            StatusLineFile.DeleteOlderThan(_legacyStatusLineDir, now, CaptureDeleteAge);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Simple ménage : on le refera dans une heure.
        }
    }
}
