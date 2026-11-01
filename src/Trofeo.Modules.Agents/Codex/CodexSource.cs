using System.Globalization;
using System.Text;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Source Codex : les transcriptions <c>sessions/AAAA/MM/JJ/rollout-*.jsonl</c> actives depuis moins de 2 h
/// (les 12 plus récentes), lues par la fin puis seulement les octets ajoutés. Ne contacte rien en ligne
/// et l'index local facultatif des titres. Exclut les sous-agents internes. Appelée depuis un seul thread à la fois.
/// La recherche se fait à deux cadences : à chaque relevé, les dossiers des 3 derniers jours (où seuls les fichiers
/// récemment modifiés, selon la date de l'énumération, sont ouverts) et les conversations déjà connues ; tous les
/// dossiers de dates, chaque fichier étant alors ouvert, seulement au premier relevé puis au plus une fois par minute.
/// Une ancienne conversation reprise, ou un fichier récent dont Windows garde une date périmée, est donc retrouvé dans
/// la minute environ, puis suivi à chaque relevé tant qu'il reste actif.
/// </summary>
/// <param name="codexHome">Le dossier de configuration de Codex (voir <see cref="CodexHome.Resolve"/>).</param>
/// <param name="time">Donne le fuseau local, qui nomme les dossiers de jour.</param>
public sealed class CodexSource(string codexHome, TimeProvider time) : IAgentSource
{
    private const int MaxFiles = 12;
    private const int MaxCandidates = 128;
    private const int DaysScanned = 3;
    private const int HeadBytes = 64 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
    // Délai minimal entre deux parcours de tous les dossiers de dates (retrouver une ancienne conversation reprise).
    private static readonly TimeSpan DeepScanInterval = TimeSpan.FromSeconds(60);

    // Les liens de répertoire ne sont pas suivis, ni dans le parcours complet ni dans un dossier de jour.
    private static readonly EnumerationOptions DeepOptions = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 3,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions DayOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly string _sessionsDir = Path.Combine(codexHome, "sessions");
    private readonly CodexTitles _titles = new(codexHome);
    private readonly CodexSessionRecency _recency = new();
    // Les conversations actives trouvées par le dernier parcours complet : surveillées à chaque relevé, où qu'elles soient rangées.
    private readonly HashSet<string> _resumed = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastDeepScan;
    // ProjectNames remonte l'arborescence sur le disque : un seul calcul par dossier de travail.
    private readonly Dictionary<string, string> _projectNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);

    private sealed class FileState(string path)
    {
        public TranscriptTail Tail { get; } = new(path);

        public CodexRollout Rollout { get; } = new();

        // La première ligne (session_meta) reste à lire : la lecture est repartie de la fin et n'a pas encore pu l'obtenir.
        public bool HeadPending { get; set; }
    }

    /// <summary>Lecteur de la première ligne d'un fichier (remplaçable par les tests).</summary>
    internal Func<string, string?> HeadReader { get; init; } = ReadFirstLine;

    private sealed record Candidate(FileInfo File, DateTimeOffset WrittenAt);

    /// <inheritdoc />
    public AgentSnapshot Poll(string? selectedSessionId, DateTimeOffset now)
    {
        if (!Directory.Exists(codexHome))
        {
            return new AgentSnapshot(false, [], null, null);
        }

        var tracked = Candidates(now);
        _titles.Refresh();
        var sessions = new List<(AgentSessionSummary Summary, CodexRollout Rollout)>();
        foreach (var candidate in tracked)
        {
            try
            {
                var rollout = Update(candidate.File.FullName);
                if (rollout.IsInternal)
                {
                    continue;
                }

                var id = rollout.SessionId ?? Path.GetFileNameWithoutExtension(candidate.File.Name);
                var startedAt = rollout.StartedAt ?? new DateTimeOffset(candidate.File.CreationTimeUtc, TimeSpan.Zero);
                var title = _titles.Find(id) ?? rollout.Title ?? ProjectName(rollout.Cwd);
                sessions.Add((new AgentSessionSummary(id, title, rollout.Activity(now), startedAt,
                    rollout.LastEventAt ?? candidate.WrittenAt), rollout));
                if (sessions.Count == MaxFiles)
                {
                    break;
                }
            }
            catch (Exception)
            {
                // Un fichier qui pose problème ne fait perdre que lui-même : les autres sessions restent listées.
            }
        }

        sessions.Sort((a, b) =>
        {
            var byStart = a.Summary.StartedAt.CompareTo(b.Summary.StartedAt);
            return byStart != 0 ? byStart : string.CompareOrdinal(a.Summary.Id, b.Summary.Id);
        });

        // Deux conversations peuvent légitimement partager un titre ou un dossier : rendre leurs onglets distincts.
        var duplicates = sessions.GroupBy(s => s.Summary.ProjectName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < sessions.Count; i++)
        {
            var (summary, rollout) = sessions[i];
            if (duplicates.Contains(summary.ProjectName))
            {
                var suffix = summary.Id.Length > 6 ? summary.Id[^6..] : summary.Id;
                sessions[i] = (summary with { ProjectName = $"{summary.ProjectName} · {suffix}" }, rollout);
            }
        }

        AgentSessionDetail? selected = null;
        if (selectedSessionId is not null && sessions.FirstOrDefault(s => s.Summary.Id == selectedSessionId) is { Rollout: { } chosen })
        {
            selected = new AgentSessionDetail(selectedSessionId, chosen.CurrentTask, chosen.TaskStartedAt,
                [.. chosen.Feed], [.. chosen.Tasks], chosen.Model, chosen.ContextPercent, chosen.TaskFinishedAt);
        }

        var quotas = sessions.Select(s => s.Rollout.Quotas).Where(q => q is not null).MaxBy(q => q!.CapturedAt);
        Forget(tracked);
        return new AgentSnapshot(true, sessions.Select(s => s.Summary).ToList(), selected, quotas);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _projectNames.Clear();
        _files.Clear();
        _recency.Clear();
        _resumed.Clear();
        _lastDeepScan = null;
    }

    /// <summary>
    /// Les transcriptions à suivre : celles actives depuis moins de 2 h (date Windows ou horodatages JSONL), par date
    /// décroissante (128 candidats au plus, pour retenir 12 conversations après exclusion des sous-agents). À chaque
    /// relevé : les dossiers des 3 derniers jours (date locale), les conversations déjà suivies et les conversations
    /// reprises que le dernier parcours complet a trouvées. Au premier relevé, puis dès qu'une minute au moins s'est
    /// écoulée (ou que l'horloge a reculé), tous les dossiers de dates sont parcourus, chaque fichier étant jugé sur sa
    /// fraîcheur réelle : une ancienne conversation reprise est retrouvée au plus tard à ce parcours. Le contenu relu pour
    /// juger la fraîcheur est borné et mis en cache.
    /// </summary>
    private List<Candidate> Candidates(DateTimeOffset now)
    {
        var found = new List<Candidate>();
        if (_lastDeepScan is not { } last || now < last || now - last >= DeepScanInterval)
        {
            ScanAll(now, found);
        }
        else
        {
            ScanRecent(now, found);
        }

        // Le quota porte sur les conversations, pas sur les sous-agents plus récents qui les masqueraient.
        return found.OrderByDescending(c => c.WrittenAt).ThenBy(c => c.File.FullName, StringComparer.Ordinal).Take(MaxCandidates).ToList();
    }

    /// <summary>
    /// Parcours complet : tous les dossiers de dates. Retient les transcriptions actives comme conversations reprises
    /// (surveillées ensuite à chaque relevé) et ne garde en cache que les fichiers vus. Le cache n'est purgé qu'ici :
    /// un relevé rapide ne voit pas les vieux fichiers, il ne doit pas les faire relire au parcours suivant.
    /// </summary>
    private void ScanAll(DateTimeOffset now, List<Candidate> found)
    {
        _lastDeepScan = now;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _resumed.Clear();
        try
        {
            foreach (var file in new DirectoryInfo(_sessionsDir).EnumerateFiles("rollout-*.jsonl", DeepOptions))
            {
                seen.Add(file.FullName);
                if (Consider(file, now) is { } candidate)
                {
                    found.Add(candidate);
                    _resumed.Add(file.FullName);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dossier absent ou devenu illisible : garder les candidats déjà découverts.
        }
        _recency.ForgetExcept(seen);
    }

    /// <summary>
    /// Relevé rapide : les dossiers des 3 derniers jours (date locale), plus les conversations reprises et celles déjà
    /// suivies. Un fichier de ces dossiers qui n'est ni suivi ni repris n'est ouvert que si la date de l'énumération (gratuite)
    /// est récente : s'il est récent mais que Windows garde une date périmée, le prochain parcours complet le retrouve.
    /// Les autres dossiers ne sont pas ouverts avant ce parcours.
    /// </summary>
    private void ScanRecent(DateTimeOffset now, List<Candidate> found)
    {
        var today = TimeZoneInfo.ConvertTime(now, time.LocalTimeZone).Date;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var back = 0; back < DaysScanned; back++)
        {
            var day = today.AddDays(-back);
            var dir = Path.Combine(_sessionsDir, day.Year.ToString("0000", CultureInfo.InvariantCulture),
                day.Month.ToString("00", CultureInfo.InvariantCulture), day.Day.ToString("00", CultureInfo.InvariantCulture));
            try
            {
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl", DayOptions))
                {
                    // Ni suivi ni repris : la date de l'énumération suffit à écarter un fichier ancien sans l'ouvrir.
                    if (listed.Add(file.FullName)
                        && (_files.ContainsKey(file.FullName) || _resumed.Contains(file.FullName)
                            || now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < MaxAge)
                        && Consider(file, now) is { } candidate)
                    {
                        found.Add(candidate);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Dossier du jour absent ou illisible : on passe au précédent.
            }
        }

        foreach (var path in _resumed.Concat(_files.Keys))
        {
            if (listed.Add(path) && Consider(new FileInfo(path), now) is { } candidate)
            {
                found.Add(candidate);
            }
        }
    }

    /// <summary>La transcription si elle est active depuis moins de 2 h (date Windows ou horodatages JSONL), sinon <c>null</c>.</summary>
    private Candidate? Consider(FileInfo file, DateTimeOffset now) =>
        file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        && _recency.Read(file) is { } writtenAt && now - writtenAt < MaxAge
            ? new Candidate(file, writtenAt)
            : null;

    /// <summary>
    /// Applique les lignes nouvelles de la transcription. Après un redémarrage de la lecture, l'état est remis à zéro et
    /// la première ligne du fichier (que la fin peut omettre) est appliquée avant les autres ; si on n'a pas pu la lire
    /// (verrou passager, ligne pas encore complète), on réessaie à chaque relevé tant que la session n'a pas d'identifiant.
    /// Une ligne ou un fichier qui pose problème ne fait perdre que lui-même.
    /// </summary>
    private CodexRollout Update(string path)
    {
        if (!_files.TryGetValue(path, out var state))
        {
            state = new FileState(path);
            _files[path] = state;
        }

        try
        {
            var lines = state.Tail.ReadNewLines();
            var restarted = state.Tail.Restarted;
            if (restarted)
            {
                state.Rollout.Reset();
                state.HeadPending = true;
            }

            if (state.HeadPending && state.Rollout.SessionId is null && HeadReader(path) is { } head)
            {
                state.HeadPending = false;
                // Si la fin recouvre tout le fichier, la première ligne en fait déjà partie.
                if (!(restarted && lines.Count > 0 && lines[0] == head))
                {
                    ApplyLine(state.Rollout, head);
                }
            }

            foreach (var line in lines)
            {
                ApplyLine(state.Rollout, line);
            }

            if (restarted && !state.Rollout.IsInternal && state.Rollout.Feed.Count == 0
                && state.Tail.Position > 512 * 1024)
            {
                state.Rollout.RestoreConversation(CodexConversationHistory.Read(path, state.Tail.Position));
            }

            if (restarted && state.Rollout.Model is null && !state.Rollout.IsInternal)
            {
                state.Rollout.RestoreModel(CodexModelHistory.ReadLatest(path));
            }

            if (state.Rollout.SessionId is not null)
            {
                state.HeadPending = false;
            }
        }
        catch (Exception)
        {
            // Fichier verrouillé, disparu ou inattendu : on garde ce qu'on sait, et on réessaie au prochain relevé.
        }

        return state.Rollout;
    }

    private static void ApplyLine(CodexRollout rollout, string line)
    {
        try
        {
            rollout.Apply(line);
        }
        catch (Exception)
        {
            // Une ligne inutilisable ne doit pas faire perdre celles qui suivent.
        }
    }

    /// <summary>
    /// La première ligne du fichier (64 Kio au plus). <c>null</c> si elle est absente, incomplète ou illisible pour
    /// l'instant : on réessaiera. Vide si elle dépasse 64 Kio (inutile de réessayer).
    /// </summary>
    internal static string? ReadFirstLine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (end < 0)
            {
                return read == buffer.Length ? "" : null;
            }

            var length = end > 0 && buffer[end - 1] == (byte)'\r' ? end - 1 : end;
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Les lignes de la fin comptent plus que la première : on s'en passe.
            return null;
        }
    }

    private string ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "Codex";
        }

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

    /// <summary>Oublie l'état des transcriptions qui ne sont plus suivies.</summary>
    private void Forget(List<Candidate> tracked)
    {
        var paths = tracked.Select(c => c.File.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var path in _files.Keys.Where(path => !paths.Contains(path)).ToList())
        {
            _files.Remove(path);
        }
    }
}
