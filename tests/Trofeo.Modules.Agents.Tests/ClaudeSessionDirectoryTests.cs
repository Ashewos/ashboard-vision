namespace Trofeo.Modules.Agents.Tests;

public sealed class ClaudeSessionDirectoryTests : IDisposable
{
    private const string RealSession =
        """{"pid":89,"sessionId":"801406b2-bd1d-5e23-bf01-23241b27c5f1","cwd":"/home/user/trofeo-HB-dashboard","startedAt":1792922150984,"procStart":"280","version":"2.1.310","peerProtocol":1,"peerFeatures":["notify_idle"],"kind":"interactive","entrypoint":"remote_desktop","pidDomain":"linux:…","messagingSocketPath":"/tmp/cc-socks/89.sock","name":"trofeo-hb-dashboard-61","nameSource":"derived","nameSince":1792922150984,"updatedAt":1792922247343,"status":"busy","statusUpdatedAt":1792922247343}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "trofeo-claude-dir-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<int> _alive = [];

    public ClaudeSessionDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeSessionDirectory NewDirectory() => new(_root, _alive.Contains);

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_root, name), json);

    private static string Session(int pid, string sessionId, string status = "busy", long? statusUpdatedAt = 1792922247343,
        string? kind = "interactive") =>
        "{\"pid\":" + pid + ",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"/work/app\",\"startedAt\":1792922150984,"
        + (kind is null ? "" : "\"kind\":\"" + kind + "\",")
        + "\"status\":\"" + status + "\""
        + (statusUpdatedAt is null ? "" : ",\"statusUpdatedAt\":" + statusUpdatedAt) + "}";

    [Fact]
    public void Directory_ReadsLiveSessions()
    {
        _alive.Add(89);
        Write("89.json", RealSession);

        var sessions = NewDirectory().Read();

        var session = Assert.Single(sessions);
        Assert.Equal(89, session.Pid);
        Assert.Equal("801406b2-bd1d-5e23-bf01-23241b27c5f1", session.SessionId);
        Assert.Equal("/home/user/trofeo-HB-dashboard", session.Cwd);
        Assert.Equal("busy", session.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792922150984), session.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792922247343), session.LastActivityAt);
        Assert.Equal("interactive", session.Kind);
    }

    [Fact]
    public void Directory_LastActivity_FallsBackToUpdatedAtThenStartedAt()
    {
        _alive.UnionWith([1, 2]);
        Write("1.json", """{"pid":1,"sessionId":"a","cwd":"/w","startedAt":1000,"updatedAt":5000,"status":"idle"}""");
        Write("2.json", """{"pid":2,"sessionId":"b","cwd":"/w","startedAt":1000,"status":"idle"}""");

        var sessions = NewDirectory().Read().ToDictionary(s => s.SessionId);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(5000), sessions["a"].LastActivityAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), sessions["b"].LastActivityAt);
        Assert.Null(sessions["b"].Kind);
    }

    [Fact]
    public void Directory_DeadProcess_IsHidden()
    {
        _alive.Add(1);
        Write("1.json", Session(1, "vivante"));
        Write("2.json", Session(2, "morte"));

        var sessions = NewDirectory().Read();

        Assert.Equal(["vivante"], sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Directory_DeadProcess_NotRecheckedWhileSamePid_ButRecheckedForAnotherPid()
    {
        var calls = new List<int>();
        var directory = new ClaudeSessionDirectory(_root, pid => { calls.Add(pid); return false; });
        Write("5.json", Session(5, "orpheline"));

        directory.Read();
        directory.Read();
        Assert.Equal([5], calls);

        // Le fichier change mais porte le même numéro de processus, déjà vu mort : pas de nouvelle vérification.
        Write("5.json", Session(5, "orpheline", statusUpdatedAt: 1792922999999));
        directory.Read();
        Assert.Equal([5], calls);

        // Un autre numéro de processus : on vérifie.
        Write("5.json", Session(6, "orpheline"));
        directory.Read();
        Assert.Equal([5, 6], calls);
    }

    [Fact]
    public void Directory_ReusedPidNewSession_IsRechecked()
    {
        var directory = NewDirectory();
        Write("5.json", Session(5, "ancienne"));
        Assert.Empty(directory.Read()); // le processus 5 est vu mort

        // Windows redonne le numéro 5 à un nouveau Claude Code, qui réécrit le même fichier avec une autre session.
        _alive.Add(5);
        Write("5.json", Session(5, "nouvelle"));

        Assert.Equal(["nouvelle"], directory.Read().Select(s => s.SessionId));
    }

    [Fact]
    public void Directory_ChangeOfSameSizeAndSameWriteTime_IsSeen()
    {
        _alive.Add(5);
        var path = Path.Combine(_root, "5.json");
        var when = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        Write("5.json", Session(5, "s", status: "busy"));
        File.SetLastWriteTimeUtc(path, when);
        var directory = NewDirectory();
        Assert.Equal("busy", Assert.Single(directory.Read()).Status);

        // « idle » et « busy » ont la même longueur ; on force la même date d'écriture.
        Write("5.json", Session(5, "s", status: "idle"));
        File.SetLastWriteTimeUtc(path, when);

        Assert.Equal("idle", Assert.Single(directory.Read()).Status);
    }

    [Fact]
    public void Directory_LiveProcess_CheckedEveryRead_ThenHiddenOnceDead()
    {
        var calls = 0;
        var alive = true;
        var directory = new ClaudeSessionDirectory(_root, _ => { calls++; return alive; });
        Write("5.json", Session(5, "vivante"));

        Assert.Single(directory.Read());
        Assert.Single(directory.Read());
        Assert.Equal(2, calls);

        alive = false;
        Assert.Empty(directory.Read());
    }

    [Fact]
    public void Directory_ProcessCheckThatThrows_HidesSessionButIsRetried()
    {
        var fail = true;
        var calls = 0;
        var directory = new ClaudeSessionDirectory(_root, _ => { calls++; return fail ? throw new InvalidOperationException() : true; });
        Write("5.json", Session(5, "capricieuse"));

        Assert.Empty(directory.Read());
        fail = false;

        Assert.Single(directory.Read());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Directory_BackgroundKind_IsHidden()
    {
        _alive.UnionWith([1, 2, 3]);
        Write("1.json", Session(1, "interactive-one", kind: "interactive"));
        Write("2.json", Session(2, "sans-kind", kind: null));
        Write("3.json", Session(3, "fond", kind: "background"));

        var sessions = NewDirectory().Read();

        Assert.Equal(["interactive-one", "sans-kind"], sessions.Select(s => s.SessionId).Order());
    }

    [Fact]
    public void Directory_PartialFile_KeepsLastGoodRead()
    {
        _alive.Add(89);
        Write("89.json", RealSession);
        var directory = NewDirectory();
        Assert.Single(directory.Read());

        // Claude Code est en train de réécrire le fichier : on n'en voit que le début.
        Write("89.json", RealSession[..60]);
        var during = directory.Read();

        Assert.Equal("busy", Assert.Single(during).Status);

        // Une fois le fichier complet, la nouvelle lecture remplace l'ancienne.
        Write("89.json", RealSession.Replace("\"status\":\"busy\"", "\"status\":\"waiting\"") + " ");
        Assert.Equal("waiting", Assert.Single(directory.Read()).Status);
    }

    [Fact]
    public void Directory_DeletedFile_DisappearsAndIsForgotten()
    {
        _alive.Add(89);
        Write("89.json", RealSession);
        var directory = NewDirectory();
        Assert.Single(directory.Read());

        File.Delete(Path.Combine(_root, "89.json"));

        Assert.Empty(directory.Read());
    }

    [Fact]
    public void Directory_NeverReadFileIsUnreadable_IsIgnored()
    {
        _alive.Add(1);
        Write("1.json", "{ pas du json");
        Write("2.json", """{"pid":2,"cwd":"/w"}""");

        Assert.Empty(NewDirectory().Read());
    }

    [Fact]
    public void Directory_UnsafeSessionId_IsIgnored()
    {
        _alive.Add(1);
        Write("1.json", Session(1, "../../etc"));

        Assert.Empty(NewDirectory().Read());
    }

    [Fact]
    public void Directory_DuplicateSession_KeepsNewest()
    {
        _alive.UnionWith([1, 2]);
        Write("1.json", Session(1, "meme-session", status: "idle", statusUpdatedAt: 2000));
        Write("2.json", Session(2, "meme-session", status: "busy", statusUpdatedAt: 9000));

        var sessions = NewDirectory().Read();

        var session = Assert.Single(sessions);
        Assert.Equal(2, session.Pid);
        Assert.Equal("busy", session.Status);
    }

    [Fact]
    public void Directory_MissingFolder_IsEmpty()
    {
        var directory = new ClaudeSessionDirectory(Path.Combine(_root, "absent"), _ => true);

        Assert.Empty(directory.Read());
    }

    [Fact]
    public void Locate_EncodedPath()
    {
        var projects = Path.Combine(_root, "projects");
        var folder = Path.Combine(projects, "C--Users-alexl-trofeo-HB-dashboard");
        Directory.CreateDirectory(folder);
        var transcript = Path.Combine(folder, "abc-123.jsonl");
        File.WriteAllText(transcript, "");

        var found = TranscriptLocator.Find(projects, @"C:\Users\alexl\trofeo-HB-dashboard", "abc-123");

        Assert.Equal(transcript, found);
    }

    [Fact]
    public void Locate_EncodingMismatch_FallsBackToSearch()
    {
        var projects = Path.Combine(_root, "projects");
        var folder = Path.Combine(projects, "un-autre-nom-de-dossier");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(projects, "vide"));
        var transcript = Path.Combine(folder, "abc-123.jsonl");
        File.WriteAllText(transcript, "");

        var found = TranscriptLocator.Find(projects, @"C:\Users\Élodie\Mes projets\app", "abc-123");

        Assert.Equal(transcript, found);
    }

    [Fact]
    public void Locate_NothingFound_OrUnsafeId_IsNull()
    {
        var projects = Path.Combine(_root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "x"));
        File.WriteAllText(Path.Combine(projects, "x", "secret.jsonl"), "");

        Assert.Null(TranscriptLocator.Find(projects, "/w", "absent"));
        Assert.Null(TranscriptLocator.Find(projects, "/w", "../x/secret"));
        Assert.Null(TranscriptLocator.Find(Path.Combine(_root, "pas-de-projets"), "/w", "absent"));
    }

    private static StatusLineRecord Capture(DateTimeOffset at, long? window = null, double? percent = null) =>
        new(at, "s", null, window, percent, null, null);

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Context_PrefersFreshCapture()
    {
        var percent = ContextWindow.Percent(100_000, "claude-opus-5-5", Capture(Now.AddSeconds(-10), percent: 42.5), Now);

        Assert.Equal(42.5, percent);
    }

    [Fact]
    public void Context_StaleCapture_FallsBackToTokens()
    {
        var percent = ContextWindow.Percent(100_000, "claude-opus-5-5", Capture(Now.AddSeconds(-120), percent: 42.5), Now);

        Assert.Equal(50, percent);
    }

    [Fact]
    public void Context_UsesCapturedWindowSize()
    {
        var percent = ContextWindow.Percent(100_000, "claude-opus-5-5", Capture(Now.AddMinutes(-30), window: 400_000), Now);

        Assert.Equal(25, percent);
    }

    [Fact]
    public void Context_Over200k_Assumes1M()
    {
        Assert.Equal(30, ContextWindow.Percent(300_000, "claude-opus-5-5", null, Now));
    }

    [Fact]
    public void Context_1mModel_Assumes1M()
    {
        Assert.Equal(10, ContextWindow.Percent(100_000, "claude-opus-5-5[1m]", null, Now));
    }

    [Fact]
    public void Context_DefaultWindowIs200k()
    {
        Assert.Equal(85, ContextWindow.Percent(170_000, null, null, Now));
    }

    [Fact]
    public void Context_KnownWindowForSameModel_BeatsTheRules()
    {
        // Une autre session a relevé 1 000 000 pour ce modèle : 100 000 jetons = 10 %, pas 50 %.
        Assert.Equal(10, ContextWindow.Percent(100_000, "claude-opus-5-5", null, Now, knownWindowForModel: 1_000_000));
        // La capture de la session reste prioritaire quand elle donne sa taille.
        Assert.Equal(25, ContextWindow.Percent(100_000, "claude-opus-5-5", Capture(Now.AddMinutes(-30), window: 400_000), Now,
            knownWindowForModel: 1_000_000));
        // Sans fenêtre connue, les règles habituelles.
        Assert.Equal(50, ContextWindow.Percent(100_000, "claude-opus-5-5", null, Now, knownWindowForModel: null));
    }

    [Fact]
    public void Context_IsClampedTo0_100()
    {
        Assert.Equal(100, ContextWindow.Percent(150_000, null, Capture(Now.AddMinutes(-5), window: 100_000), Now));
        Assert.Equal(100, ContextWindow.Percent(1, null, Capture(Now, percent: 140), Now));
        Assert.Equal(0, ContextWindow.Percent(1, null, Capture(Now, percent: -3), Now));
    }

    [Fact]
    public void Context_NoTokens_Null()
    {
        Assert.Null(ContextWindow.Percent(null, "claude-opus-5-5", null, Now));
        Assert.Null(ContextWindow.Percent(null, "claude-opus-5-5", Capture(Now.AddMinutes(-5), window: 1_000_000), Now));
    }
}
