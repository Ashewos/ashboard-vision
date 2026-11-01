using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Trofeo.Modules.Agents.Tests;

public sealed partial class ClaudeSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly long StartMs = Now.AddHours(-2).ToUnixTimeMilliseconds();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "trofeo-claude-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _work;
    private readonly HashSet<int> _alive = [];

    public ClaudeSourceTests()
    {
        _home = Path.Combine(_root, "claude-home");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeSource NewSource() => new(_home, _alive.Contains);

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Crée un dossier de travail et le fichier de session (vivant) correspondant ; rend le dossier.</summary>
    private string AddSession(int pid, string id, string project, string status, long startedAtMs, string? waitingFor = null)
    {
        var cwd = Path.Combine(_work, project);
        Directory.CreateDirectory(cwd);
        _alive.Add(pid);
        var waiting = waitingFor is null ? "" : ",\"waitingFor\":\"" + waitingFor + "\"";
        File.WriteAllText(Path.Combine(_home, "sessions", pid + ".json"),
            "{\"pid\":" + pid + ",\"sessionId\":\"" + id + "\",\"cwd\":" + JsonSerializer.Serialize(cwd)
            + ",\"startedAt\":" + startedAtMs + ",\"kind\":\"interactive\",\"status\":\"" + status
            + "\",\"statusUpdatedAt\":" + (startedAtMs + 5000) + waiting + "}");
        return cwd;
    }

    private string TranscriptPath(string cwd, string id)
    {
        var folder = Path.Combine(_home, "projects", NonAlphanumeric().Replace(cwd, "-"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, id + ".jsonl");
    }

    private static string Prompt(string text) =>
        $$"""{"isSidechain":false,"type":"user","message":{"role":"user","content":{{JsonSerializer.Serialize(text)}}},"timestamp":"2026-10-25T10:30:00Z"}""";

    private static string ReadCall(string toolId, string file) =>
        $$$"""{"isSidechain":false,"type":"assistant","message":{"model":"claude-opus-5-5","role":"assistant","content":[{"type":"tool_use","id":"{{{toolId}}}","name":"Read","input":{"file_path":{{{JsonSerializer.Serialize(file)}}}}}],"usage":{"input_tokens":2,"cache_creation_input_tokens":782,"cache_read_input_tokens":169665,"output_tokens":353}},"timestamp":"2026-10-25T10:30:05Z"}""";

    private void WriteQuotaCapture(string sessionId, DateTimeOffset at, double fiveHour, double sevenDay, string? dir = null)
    {
        var record = new StatusLineRecord(at, sessionId, "claude-opus-5-5", null, null,
            new CapturedQuota(fiveHour, at.AddHours(3)), new CapturedQuota(sevenDay, at.AddDays(4)));
        CaptureFiles.Write(dir ?? ClaudeHome.StatusLineDir(_home), record);
    }

    [Fact]
    public void Source_Poll_BuildsSnapshot()
    {
        var cwdA = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        AddSession(11, "sess-b", "projet-b", "waiting", StartMs + 60_000, waitingFor: "permission");
        File.WriteAllLines(TranscriptPath(cwdA, "sess-a"), [Prompt("Écris le plan 3"), ReadCall("t1", "/src/Foo.cs")]);
        var tasks = Path.Combine(_home, "tasks", "sess-a");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "1.json"), """{"id":"1","subject":"Écrire","status":"in_progress","activeForm":"Écriture"}""");
        WriteQuotaCapture("sess-a", Now.AddSeconds(-30), 31, 12);
        using var source = NewSource();

        var snapshot = source.Poll("sess-a", Now);

        Assert.True(snapshot.Installed);
        Assert.Equal(["sess-a", "sess-b"], snapshot.Sessions.Select(s => s.Id));
        Assert.Equal(["projet-a", "projet-b"], snapshot.Sessions.Select(s => s.ProjectName));
        Assert.Equal([AgentActivity.Working, AgentActivity.Waiting], snapshot.Sessions.Select(s => s.Activity));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StartMs), snapshot.Sessions[0].StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StartMs + 5000), snapshot.Sessions[0].LastActivityAt);

        var detail = snapshot.Selected!;
        Assert.Equal("sess-a", detail.Id);
        Assert.Equal("Écris le plan 3", detail.CurrentTask);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T10:30:00Z"), detail.TaskStartedAt);
        Assert.Equal([new FeedEntry("Read", "Foo.cs")], detail.Feed);
        Assert.Equal("claude-opus-5-5", detail.Model);
        Assert.Equal(170_449 / 200_000d * 100, detail.ContextPercent!.Value, 3);
        Assert.Equal([new AgentTask("Écriture", AgentTaskState.InProgress)], detail.Tasks);

        var quotas = snapshot.Quotas!;
        Assert.Equal(31, quotas.FiveHour!.UsedPercent);
        Assert.Equal(Now.AddSeconds(-30).AddHours(3), quotas.FiveHour.ResetsAt);
        Assert.Equal(12, quotas.Weekly!.UsedPercent);
        Assert.Equal(Now.AddSeconds(-30), quotas.CapturedAt);
    }

    [Fact]
    public void Source_Poll_SessionsSortedByStart_NoSelection_NoDetail()
    {
        AddSession(10, "sess-late", "p1", "idle", StartMs + 1000);
        AddSession(11, "sess-early", "p2", "inconnu", StartMs);
        using var source = NewSource();

        var snapshot = source.Poll(null, Now);

        Assert.Equal(["sess-early", "sess-late"], snapshot.Sessions.Select(s => s.Id));
        Assert.All(snapshot.Sessions, s => Assert.Equal(AgentActivity.Idle, s.Activity));
        Assert.Null(snapshot.Selected);
        Assert.Null(snapshot.Quotas);
        Assert.Null(source.Poll("absente", Now).Selected);
    }

    [Fact]
    public void Source_Poll_UsesFreshCaptureForContext_AndFallsBackToItsModel()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Salut")]);
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddSeconds(-5), "sess-a", "claude-sonnet-5-5", 1_000_000, 42.5, null, null));
        using var source = NewSource();

        var detail = source.Poll("sess-a", Now).Selected!;

        Assert.Equal(42.5, detail.ContextPercent);
        Assert.Equal("claude-sonnet-5-5", detail.Model);
    }

    [Fact]
    public void Source_Poll_AppendsOnlyNewLines()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        var transcript = TranscriptPath(cwd, "sess-a");
        File.WriteAllLines(transcript, [Prompt("Premier"), ReadCall("t1", "/src/A.cs")]);
        using var source = NewSource();
        Assert.Equal([new FeedEntry("Read", "A.cs")], source.Poll("sess-a", Now).Selected!.Feed);

        File.AppendAllText(transcript, ReadCall("t2", "/src/B.cs") + "\n", Encoding.UTF8);
        var detail = source.Poll("sess-a", Now.AddSeconds(1)).Selected!;

        Assert.Equal([new FeedEntry("Read", "A.cs"), new FeedEntry("Read", "B.cs")], detail.Feed);
        Assert.Equal("Premier", detail.CurrentTask);

        // Sans ajout, rien ne se double.
        Assert.Equal(2, source.Poll("sess-a", Now.AddSeconds(2)).Selected!.Feed.Count);
    }

    [Fact]
    public void Source_Poll_SnapshotFeedIsACopy()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        var transcript = TranscriptPath(cwd, "sess-a");
        File.WriteAllLines(transcript, [ReadCall("t1", "/src/A.cs")]);
        using var source = NewSource();
        var first = source.Poll("sess-a", Now).Selected!;

        File.AppendAllText(transcript, ReadCall("t2", "/src/B.cs") + "\n");
        source.Poll("sess-a", Now.AddSeconds(1));

        Assert.Single(first.Feed);
    }

    [Fact]
    public void Source_Poll_TruncatedTranscript_StartsOver()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        var transcript = TranscriptPath(cwd, "sess-a");
        File.WriteAllLines(transcript, [Prompt("Ancienne demande"), ReadCall("t1", "/src/Vieux.cs"), ReadCall("t2", "/src/Vieux2.cs")]);
        using var source = NewSource();
        Assert.Equal(2, source.Poll("sess-a", Now).Selected!.Feed.Count);

        File.WriteAllLines(transcript, [Prompt("Nouvelle"), ReadCall("t9", "/src/Neuf.cs")]);
        var detail = source.Poll("sess-a", Now.AddSeconds(1)).Selected!;

        Assert.Equal("Nouvelle", detail.CurrentTask);
        Assert.Equal([new FeedEntry("Read", "Neuf.cs")], detail.Feed);
    }

    [Fact]
    public void Source_Poll_TasksFallBackToTodosThenEmpty()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        const string todoWrite = """{"isSidechain":false,"type":"assistant","message":{"model":"m","role":"assistant","content":[{"type":"tool_use","id":"w","name":"TodoWrite","input":{"todos":[{"content":"Lire","status":"pending","activeForm":"Lecture"}]}}]},"timestamp":"2026-10-25T10:30:05Z"}""";
        var transcript = TranscriptPath(cwd, "sess-a");
        File.WriteAllLines(transcript, [Prompt("Salut")]);
        using var source = NewSource();
        Assert.Empty(source.Poll("sess-a", Now).Selected!.Tasks);

        File.AppendAllText(transcript, todoWrite + "\n");

        Assert.Equal([new AgentTask("Lire", AgentTaskState.Pending)], source.Poll("sess-a", Now.AddSeconds(1)).Selected!.Tasks);

        // Un dossier de tâches présent, même vide, a priorité sur les Todos.
        Directory.CreateDirectory(Path.Combine(_home, "tasks", "sess-a"));
        Assert.Empty(source.Poll("sess-a", Now.AddSeconds(2)).Selected!.Tasks);
    }

    [Fact]
    public void Source_Poll_NoTranscriptYet_StillGivesDetail()
    {
        AddSession(10, "sess-a", "projet-a", "idle", StartMs);
        using var source = NewSource();

        var detail = source.Poll("sess-a", Now).Selected!;

        Assert.Equal("sess-a", detail.Id);
        Assert.Null(detail.CurrentTask);
        Assert.Empty(detail.Feed);
        Assert.Null(detail.ContextPercent);
    }

    [Fact]
    public void Source_Poll_SessionWhoseProcessCheckFails_DoesNotBreakOthers()
    {
        AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        AddSession(11, "sess-b", "projet-b", "busy", StartMs + 1);
        using var source = new ClaudeSource(_home, pid => pid == 11 ? throw new InvalidOperationException("boum") : _alive.Contains(pid));

        var snapshot = source.Poll("sess-a", Now);

        Assert.Equal(["sess-a"], snapshot.Sessions.Select(s => s.Id));
        Assert.NotNull(snapshot.Selected);
    }

    [Fact]
    public void Source_Poll_TaskFailure_DoesNotBreakPoll_OtherSessionStaysCorrect()
    {
        var cwdA = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        var cwdB = AddSession(11, "sess-b", "projet-b", "busy", StartMs + 1);
        File.WriteAllLines(TranscriptPath(cwdA, "sess-a"), [Prompt("Demande A")]);
        File.WriteAllLines(TranscriptPath(cwdB, "sess-b"), [Prompt("Demande B")]);
        Directory.CreateDirectory(Path.Combine(_home, "tasks", "sess-a"));
        Directory.CreateDirectory(Path.Combine(_home, "tasks", "sess-b"));
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddMinutes(-5), "sess-a", "claude-sonnet-5-5", null, null, null, null));
        using var source = new ClaudeSource(_home, _alive.Contains)
        {
            TaskListReader = dir => dir.EndsWith("sess-a", StringComparison.Ordinal)
                ? throw new InvalidOperationException("boum")
                : [new AgentTask("Tâche B", AgentTaskState.Pending)],
        };

        var failing = source.Poll("sess-a", Now);
        var healthy = source.Poll("sess-b", Now);

        Assert.Equal(["sess-a", "sess-b"], failing.Sessions.Select(s => s.Id));
        Assert.Equal("sess-a", failing.Selected!.Id);
        Assert.Null(failing.Selected.CurrentTask);
        Assert.Equal("claude-sonnet-5-5", failing.Selected.Model);
        Assert.Equal("Demande B", healthy.Selected!.CurrentTask);
        Assert.Equal([new AgentTask("Tâche B", AgentTaskState.Pending)], healthy.Selected.Tasks);
    }

    [Fact]
    public void Source_Poll_LateTranscript_FoundOnlyAfterFiveSeconds()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        using var source = NewSource();
        Assert.Null(source.Poll("sess-a", Now).Selected!.CurrentTask);

        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Arrivée tardive")]);

        Assert.Null(source.Poll("sess-a", Now.AddSeconds(4)).Selected!.CurrentTask);
        Assert.Equal("Arrivée tardive", source.Poll("sess-a", Now.AddSeconds(5)).Selected!.CurrentTask);
    }

    [Fact]
    public void Source_Poll_UnchangedTaskFolder_NotReread_ChangedOneIs()
    {
        AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        var tasks = Path.Combine(_home, "tasks", "sess-a");
        Directory.CreateDirectory(tasks);
        var file = Path.Combine(tasks, "1.json");
        File.WriteAllText(file, """{"id":"1","subject":"Premier","status":"pending"}""");
        var reads = 0;
        using var source = new ClaudeSource(_home, _alive.Contains)
        {
            TaskListReader = dir => { reads++; return ClaudeTaskList.Read(dir); },
        };

        Assert.Equal("Premier", Assert.Single(source.Poll("sess-a", Now).Selected!.Tasks).Text);
        source.Poll("sess-a", Now.AddSeconds(1));
        source.Poll("sess-a", Now.AddSeconds(2));
        Assert.Equal(1, reads);

        File.WriteAllText(file, """{"id":"1","subject":"Second!","status":"pending"}""");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("Second!", Assert.Single(source.Poll("sess-a", Now.AddSeconds(3)).Selected!.Tasks).Text);
        Assert.Equal(2, reads);

        File.WriteAllText(Path.Combine(tasks, "2.json"), """{"id":"2","subject":"Nouvelle","status":"pending"}""");
        Assert.Equal(2, source.Poll("sess-a", Now.AddSeconds(4)).Selected!.Tasks.Count);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void Source_Poll_UnchangedCapture_NotReread_ChangedOneIs()
    {
        WriteQuotaCapture("sess-x", Now.AddMinutes(-1), 31, 12);
        var path = Path.Combine(ClaudeHome.StatusLineDir(_home), "sess-x.json");
        var written = File.GetLastWriteTimeUtc(path);
        using var source = NewSource();
        Assert.Equal(31, source.Poll(null, Now).Quotas!.FiveHour!.UsedPercent);

        // Même taille, même date d'écriture, mais contenu invalide : si le fichier était relu, la capture disparaîtrait.
        var bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(31, source.Poll(null, Now.AddSeconds(1)).Quotas!.FiveHour!.UsedPercent);

        // Fichier modifié (nouvelle taille) : relu.
        WriteQuotaCapture("sess-x", Now.AddSeconds(-5), 7.25, 12);
        Assert.Equal(7.25, source.Poll(null, Now.AddSeconds(2)).Quotas!.FiveHour!.UsedPercent);
    }

    [Fact]
    public void Source_Poll_CaptureAges_JudgedAtEachPoll()
    {
        WriteQuotaCapture("sess-x", Now.AddDays(-7), 31, 12);
        using var source = NewSource();
        Assert.NotNull(source.Poll(null, Now).Quotas);

        Assert.Null(source.Poll(null, Now.AddDays(2)).Quotas);
    }

    [Fact]
    public void Source_Poll_DeletedCapture_Forgotten()
    {
        WriteQuotaCapture("sess-x", Now.AddMinutes(-1), 31, 12);
        using var source = NewSource();
        Assert.NotNull(source.Poll(null, Now).Quotas);

        File.Delete(Path.Combine(ClaudeHome.StatusLineDir(_home), "sess-x.json"));

        Assert.Null(source.Poll(null, Now.AddSeconds(1)).Quotas);
    }

    [Fact]
    public void Source_QuotasFromAnySession()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Salut")]);
        WriteQuotaCapture("sess-ancienne", Now.AddDays(-3), 80, 70);
        WriteQuotaCapture("sess-autre", Now.AddMinutes(-2), 5, 6);
        using var source = NewSource();

        var snapshot = source.Poll("sess-a", Now);

        Assert.Equal("sess-a", snapshot.Selected!.Id);
        Assert.Equal(5, snapshot.Quotas!.FiveHour!.UsedPercent);
        Assert.Equal(6, snapshot.Quotas.Weekly!.UsedPercent);
        Assert.Equal(Now.AddMinutes(-2), snapshot.Quotas.CapturedAt);
    }

    [Fact]
    public void Source_Quotas_AreMergedWindowByWindow()
    {
        var dir = ClaudeHome.StatusLineDir(_home);
        CaptureFiles.Write(dir, new StatusLineRecord(Now.AddMinutes(-1), "sess-recente", "m", null, null,
            new CapturedQuota(40, Now.AddHours(2)), null));
        CaptureFiles.Write(dir, new StatusLineRecord(Now.AddMinutes(-10), "sess-ancienne", "m", null, null,
            new CapturedQuota(10, Now.AddHours(1)), new CapturedQuota(60, Now.AddDays(3))));
        using var source = NewSource();

        var quotas = source.Poll(null, Now).Quotas!;

        Assert.Equal(40, quotas.FiveHour!.UsedPercent); // la plus récente qui porte la fenêtre de 5 h
        Assert.Equal(60, quotas.Weekly!.UsedPercent); // la plus récente qui porte la fenêtre hebdomadaire
        Assert.Equal(Now.AddDays(3), quotas.Weekly.ResetsAt);
        Assert.Equal(Now.AddMinutes(-1), quotas.CapturedAt);
    }

    [Fact]
    public void Source_Context_UsesWindowCapturedByAnotherSessionForSameModel()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Salut"), ReadCall("t1", "/src/Foo.cs")]);
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddMinutes(-20), "sess-autre", "claude-opus-5-5", 1_000_000, 3, null, null));
        using var source = NewSource();

        var detail = source.Poll("sess-a", Now).Selected!;

        Assert.Equal(170_449 / 1_000_000d * 100, detail.ContextPercent!.Value, 3);
    }

    [Fact]
    public void ContextWindow_CaptureModelWithSuffix_MatchesTranscriptModel()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Salut"), ReadCall("t1", "/src/Foo.cs")]);
        // La barre d'état rapporte l'identifiant avec son suffixe, la transcription sans.
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddMinutes(-20), "sess-autre", "claude-opus-5-5[1m]", 1_000_000, 3, null, null));
        using var source = NewSource();

        var detail = source.Poll("sess-a", Now).Selected!;

        Assert.Equal(170_449 / 1_000_000d * 100, detail.ContextPercent!.Value, 3);
    }

    [Fact]
    public void Source_Context_IgnoresWindowCapturedForAnotherModel()
    {
        var cwd = AddSession(10, "sess-a", "projet-a", "busy", StartMs);
        File.WriteAllLines(TranscriptPath(cwd, "sess-a"), [Prompt("Salut"), ReadCall("t1", "/src/Foo.cs")]);
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddMinutes(-20), "sess-autre", "claude-sonnet-5-5", 1_000_000, 3, null, null));
        using var source = NewSource();

        var detail = source.Poll("sess-a", Now).Selected!;

        Assert.Equal(170_449 / 200_000d * 100, detail.ContextPercent!.Value, 3);
    }

    [Fact]
    public void Source_Quotas_SkipsNewerCaptureWithoutQuota_AndTooOldOnes()
    {
        CaptureFiles.Write(ClaudeHome.StatusLineDir(_home),
            new StatusLineRecord(Now.AddSeconds(-1), "sess-sans-quota", "m", null, 10, null, null));
        WriteQuotaCapture("sess-vieille", Now.AddDays(-9), 99, 99);
        using var source = NewSource();
        Assert.Null(source.Poll(null, Now).Quotas);

        WriteQuotaCapture("sess-recente", Now.AddDays(-5), 20, 30);

        var quotas = source.Poll(null, Now.AddSeconds(1)).Quotas!;
        Assert.Equal(20, quotas.FiveHour!.UsedPercent);
    }

    [Fact]
    public void Source_Housekeeping_DeletesOldCapturesAtMostOncePerHour()
    {
        WriteQuotaCapture("sess-vieille", Now.AddDays(-9), 1, 1);
        var oldFile = Path.Combine(ClaudeHome.StatusLineDir(_home), "sess-vieille.json");
        using var source = NewSource();

        source.Poll(null, Now);
        Assert.False(File.Exists(oldFile));

        WriteQuotaCapture("sess-vieille", Now.AddDays(-9), 1, 1);
        source.Poll(null, Now.AddMinutes(30));
        Assert.True(File.Exists(oldFile));

        source.Poll(null, Now.AddMinutes(61));
        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void Captures_ReadFromNewThenLegacyFolder()
    {
        var legacy = ClaudeHome.LegacyStatusLineDir(_home);
        WriteQuotaCapture("sess-ancienne", Now.AddDays(-2), 10, 20, legacy); // posée par l'ancienne barre d'état
        using var source = NewSource();

        // Seul l'ancien dossier existe : ses captures de moins de 8 jours comptent encore.
        var quotas = source.Poll(null, Now).Quotas!;
        Assert.Equal(10, quotas.FiveHour!.UsedPercent);
        Assert.Equal(20, quotas.Weekly!.UsedPercent);

        // Le mod écrit dans le nouveau dossier : sa capture, plus récente, passe devant.
        WriteQuotaCapture("sess-nouvelle", Now.AddMinutes(-1), 30, 40);
        quotas = source.Poll(null, Now).Quotas!;
        Assert.Equal(30, quotas.FiveHour!.UsedPercent);
        Assert.Equal(40, quotas.Weekly!.UsedPercent);
        Assert.Equal(Now.AddMinutes(-1), quotas.CapturedAt);
    }

    [Fact]
    public void Captures_LegacyFolder_IgnoresCapturesOfEightDaysOrMore()
    {
        WriteQuotaCapture("sess-trop-vieille", Now.AddDays(-9), 99, 99, ClaudeHome.LegacyStatusLineDir(_home));
        using var source = NewSource();

        Assert.Null(source.Poll(null, Now).Quotas);
    }

    [Fact]
    public void Captures_SameSessionInBothFolders_TheNewFolderWins()
    {
        WriteQuotaCapture("sess-a", Now.AddMinutes(-30), 11, 12, ClaudeHome.LegacyStatusLineDir(_home));
        WriteQuotaCapture("sess-a", Now.AddMinutes(-5), 55, 56);
        using var source = NewSource();

        var quotas = source.Poll(null, Now).Quotas!;

        Assert.Equal(55, quotas.FiveHour!.UsedPercent);
        Assert.Equal(56, quotas.Weekly!.UsedPercent);
    }

    [Fact]
    public void Source_Housekeeping_AlsoCleansTheLegacyFolder()
    {
        WriteQuotaCapture("sess-vieille", Now.AddDays(-9), 1, 1, ClaudeHome.LegacyStatusLineDir(_home));
        var oldFile = Path.Combine(ClaudeHome.LegacyStatusLineDir(_home), "sess-vieille.json");
        using var source = NewSource();

        source.Poll(null, Now);

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void Source_NoClaudeHome_NotInstalled()
    {
        using var source = new ClaudeSource(Path.Combine(_root, "n-existe-pas"), _ => true);

        var snapshot = source.Poll(null, Now);

        Assert.False(snapshot.Installed);
        Assert.Empty(snapshot.Sessions);
        Assert.Null(snapshot.Selected);
        Assert.Null(snapshot.Quotas);
    }

    [Fact]
    public void Source_HomeWithoutSessionsFolder_InstalledButEmpty()
    {
        Directory.Delete(Path.Combine(_home, "sessions"));
        using var source = NewSource();

        var snapshot = source.Poll(null, Now);

        Assert.True(snapshot.Installed);
        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void Source_IsProcessAlive_TrueForSelf_FalseForNoSuchPid()
    {
        Assert.True(ClaudeSource.IsProcessAlive(Environment.ProcessId));
        Assert.False(ClaudeSource.IsProcessAlive(int.MaxValue));
        Assert.False(ClaudeSource.IsProcessAlive(-5));
    }
}
