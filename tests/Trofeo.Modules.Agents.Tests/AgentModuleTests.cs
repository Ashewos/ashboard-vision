using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents.Tests;

public sealed class AgentModuleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string key) => null;
        public void Set(string key, string? value) { }
    }

    private sealed class Rig : IDisposable
    {
        public required AgentModule Module { get; init; }
        public required FakeAgentSource Source { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required FakeModuleLog Log { get; init; }

        /// <summary>Les sondages immédiats demandés par les commandes, que le test lance quand il veut.</summary>
        public required List<Action> Background { get; init; }

        public int Invalidated { get; set; }
        public int StatusChanged { get; set; }

        public void Dispose() => Module.Dispose();
    }

    private static Rig Create(AgentSettings? settings = null, AgentFlavor? flavor = null, FakeAgentSource? source = null)
    {
        source ??= new FakeAgentSource();
        var time = new FakeTimeProvider(Start);
        var log = new FakeModuleLog();
        var background = new List<Action>();
        var module = new AgentModule(
            new ClaudeCodeFactory(_ => source).Descriptor,
            flavor ?? new AgentFlavor("Aucune session Claude Code ouverte", "Claude Code n'est pas installé sur ce PC", ClaudeMascot.Definition),
            source, settings ?? new AgentSettings(), time, log, new Random(7))
        {
            RunInBackground = background.Add,
        };
        var rig = new Rig { Module = module, Source = source, Time = time, Log = log, Background = background };
        module.Invalidated += (_, _) => rig.Invalidated++;
        module.StatusChanged += (_, _) => rig.StatusChanged++;
        return rig;
    }

    private static AgentSessionSummary Session(string id, AgentActivity activity, int minutesAgo = 5, string? name = null) =>
        new(id, name ?? "projet-" + id, activity, Start.AddHours(-2), Start.AddMinutes(-minutesAgo));

    private static AgentSnapshot Snapshot(IReadOnlyList<AgentSessionSummary> sessions, AgentSessionDetail? detail = null,
        AgentQuotas? quotas = null, bool installed = true) =>
        new(installed, sessions, detail, quotas);

    private static AgentSessionDetail Detail(string id, string? task = "Écrire les tests", string? model = "claude-opus-5-5",
        double? context = 42, DateTimeOffset? startedAt = null, IReadOnlyList<FeedEntry>? feed = null, IReadOnlyList<AgentTask>? tasks = null) =>
        new(id, task, startedAt ?? Start.AddMinutes(-12), feed ?? [new FeedEntry("Edit", "src/Foo.cs", 12, 3)], tasks ?? [], model, context);

    private static async Task<Rig> Started(params AgentSnapshot[] results)
    {
        var rig = Create();
        foreach (var result in results)
        {
            rig.Source.Enqueue(result);
        }

        await rig.Module.StartAsync(CancellationToken.None);
        return rig;
    }

    /// <summary>Un module non démarré (aucun minuteur) dont le premier sondage est fait à la main : le temps peut avancer sans sondage de fond.</summary>
    private static Rig Primed(params AgentSnapshot[] results)
    {
        var rig = Create();
        foreach (var result in results)
        {
            rig.Source.Enqueue(result);
        }

        rig.Module.PollNow();
        return rig;
    }

    private static AgentSnapshot ThreeSessions(AgentActivity a = AgentActivity.Idle, AgentActivity b = AgentActivity.Idle, AgentActivity c = AgentActivity.Idle,
        int minutesA = 30, int minutesB = 10, int minutesC = 20) =>
        Snapshot([Session("a", a, minutesA), Session("b", b, minutesB), Session("c", c, minutesC)]);

    // --- Sélection ---

    [Fact]
    public async Task Select_DefaultsToMostRecentActivity()
    {
        using var rig = await Started(ThreeSessions());

        Assert.Equal("b", rig.Module.SelectedSessionId);
        Assert.Equal(3, rig.Module.Snapshot!.Sessions.Count);
    }

    [Fact]
    public async Task Select_FirstPollAsksForNoSession_ThenForTheSelectedOne()
    {
        using var rig = await Started(ThreeSessions());

        rig.Module.PollNow();

        Assert.Equal([null, "b"], rig.Source.SelectedIds);
    }

    [Fact]
    public async Task Select_VanishedSession_FallsBackToMostRecentActivity()
    {
        using var rig = await Started(
            ThreeSessions(),
            Snapshot([Session("a", AgentActivity.Idle, 30), Session("c", AgentActivity.Idle, 20)]));
        Assert.Equal("b", rig.Module.SelectedSessionId);

        rig.Module.PollNow();

        Assert.Equal("c", rig.Module.SelectedSessionId);
    }

    [Fact]
    public async Task Select_NoSessions_NoSelection()
    {
        using var rig = await Started(Snapshot([]));

        Assert.Null(rig.Module.SelectedSessionId);
        Assert.Equal(MascotState.Idle, rig.Module.MascotState);
    }

    [Fact]
    public async Task Commands_NextPreviousWrap()
    {
        using var rig = await Started(ThreeSessions()); // sélection : b (rang 1)

        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab)));
        Assert.Equal("c", rig.Module.SelectedSessionId);
        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab)));
        Assert.Equal("a", rig.Module.SelectedSessionId);
        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.PreviousTab)));
        Assert.Equal("c", rig.Module.SelectedSessionId);
        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.PreviousTab)));
        Assert.Equal("b", rig.Module.SelectedSessionId);
        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 0)));
        Assert.Equal("a", rig.Module.SelectedSessionId);
        Assert.True(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.PreviousTab)));
        Assert.Equal("c", rig.Module.SelectedSessionId);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(-1)]
    public async Task Command_SelectOutOfRange_ReturnsFalse(int index)
    {
        using var rig = await Started(ThreeSessions());
        var invalidated = rig.Invalidated;

        Assert.False(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, index)));

        Assert.Equal("b", rig.Module.SelectedSessionId);
        Assert.Equal(invalidated, rig.Invalidated);
        Assert.Empty(rig.Background);
    }

    [Fact]
    public void Command_WithoutAnySession_ReturnsFalse()
    {
        using var rig = Create();

        Assert.False(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab)));
        Assert.False(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 0)));
    }

    [Fact]
    public async Task Command_SetsSelectionPassedToNextPoll()
    {
        using var rig = await Started(ThreeSessions());
        rig.Module.PollNow(); // le détail de b est demandé

        rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 2));
        rig.Module.PollNow();

        Assert.Equal([null, "b", "c"], rig.Source.SelectedIds);
        Assert.Equal("c", rig.Module.SelectedSessionId);
    }

    [Fact]
    public async Task Command_InvalidatesAndQueuesAnImmediatePoll()
    {
        using var rig = await Started(ThreeSessions());
        var invalidated = rig.Invalidated;
        var polls = rig.Source.Polls;

        rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab));

        Assert.Equal(invalidated + 1, rig.Invalidated);
        var work = Assert.Single(rig.Background);
        Assert.Equal(polls, rig.Source.Polls); // pas de sondage dans le thread de l'interface
        work();
        Assert.Equal(polls + 1, rig.Source.Polls);
        Assert.Equal("c", rig.Source.SelectedIds[^1]);
    }

    // --- Suivi automatique ---

    [Fact]
    public async Task AutoFollow_SwitchesToNewlyWaiting()
    {
        using var rig = await Started(
            ThreeSessions(),
            ThreeSessions(c: AgentActivity.Waiting));
        Assert.Equal("b", rig.Module.SelectedSessionId);

        rig.Module.PollNow();

        Assert.Equal("c", rig.Module.SelectedSessionId);
    }

    [Fact]
    public async Task AutoFollow_AlreadyWaitingSession_IsNotANewTransition()
    {
        using var rig = await Started(
            ThreeSessions(c: AgentActivity.Waiting),
            ThreeSessions(c: AgentActivity.Waiting));
        // Au premier relevé rien ne « passe » à l'attente : la session la plus récente reste choisie.
        Assert.Equal("b", rig.Module.SelectedSessionId);

        rig.Module.PollNow();

        Assert.Equal("b", rig.Module.SelectedSessionId);
    }

    [Fact]
    public void AutoFollow_SuppressedAfterManualChoice()
    {
        using var rig = Primed(ThreeSessions(), ThreeSessions(c: AgentActivity.Waiting));
        rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 0)); // choix manuel : a
        rig.Time.Advance(TimeSpan.FromSeconds(90));

        rig.Module.PollNow(); // c passe à l'attente, mais le choix manuel date de 90 s

        Assert.Equal("a", rig.Module.SelectedSessionId);

        rig.Time.Advance(TimeSpan.FromSeconds(31)); // 2 min 1 s après le choix
        rig.Source.Enqueue(ThreeSessions(a: AgentActivity.Waiting, b: AgentActivity.Waiting, c: AgentActivity.Waiting));
        rig.Module.PollNow(); // a et b passent à l'attente : le plus récent des deux, b, est suivi

        Assert.Equal("b", rig.Module.SelectedSessionId);
    }

    [Fact]
    public async Task AutoFollow_Disabled_NoSwitch()
    {
        var rig = Create(new AgentSettings(AgentView.Feed, AutoFollow: false));
        using var _ = rig;
        rig.Source.Enqueue(ThreeSessions());
        rig.Source.Enqueue(ThreeSessions(c: AgentActivity.Waiting));
        await rig.Module.StartAsync(CancellationToken.None);

        rig.Module.PollNow();

        Assert.Equal("b", rig.Module.SelectedSessionId);
    }

    // --- Mascotte ---

    [Fact]
    public async Task Mascot_FollowsTheShownSession()
    {
        using var rig = await Started(
            ThreeSessions(b: AgentActivity.Working),
            ThreeSessions(b: AgentActivity.Waiting),
            ThreeSessions(b: AgentActivity.Waiting, c: AgentActivity.Working));
        Assert.Equal(MascotState.Working, rig.Module.MascotState);

        rig.Module.PollNow();
        Assert.Equal(MascotState.Waiting, rig.Module.MascotState);

        rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 2));
        rig.Module.PollNow();
        Assert.Equal(MascotState.Working, rig.Module.MascotState);
    }

    [Fact]
    public async Task Mascot_BusyToIdle_PlaysDone()
    {
        using var rig = await Started(
            ThreeSessions(b: AgentActivity.Working),
            ThreeSessions(b: AgentActivity.Idle),
            ThreeSessions(b: AgentActivity.Idle));
        Assert.Equal(MascotState.Working, rig.Module.MascotState);

        rig.Module.PollNow();
        Assert.Equal(MascotState.Done, rig.Module.MascotState);

        // Le sondage suivant, toujours inactif, n'interrompt pas le clip de fin.
        rig.Module.PollNow();
        Assert.Equal(MascotState.Done, rig.Module.MascotState);
    }

    [Fact]
    public async Task Mascot_SwitchingTabsFromBusyToIdle_DoesNotPlayDone()
    {
        using var rig = await Started(
            ThreeSessions(b: AgentActivity.Working),
            ThreeSessions(b: AgentActivity.Working));
        rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.SelectTab, 0)); // a est inactive

        rig.Module.PollNow();

        Assert.Equal(MascotState.Idle, rig.Module.MascotState);
    }

    // --- État ---

    [Fact]
    public async Task Status_NotInstalled_NotConfiguredWithMessage()
    {
        using var rig = await Started(Snapshot([], installed: false));

        Assert.Equal(ModuleHealth.NotConfigured, rig.Module.Status.Health);
        Assert.Equal("Claude Code n'est pas installé sur ce PC", rig.Module.Status.ActionMessage);
        Assert.Equal(1, rig.StatusChanged);
    }

    [Fact]
    public async Task Status_Installed_Online_AndBecomesInstalled()
    {
        using var rig = await Started(Snapshot([], installed: false), Snapshot([]));
        Assert.Equal(ModuleHealth.NotConfigured, rig.Module.Status.Health);

        rig.Module.PollNow();

        Assert.Equal(new ModuleStatus(ModuleHealth.Online), rig.Module.Status);
        Assert.Equal(2, rig.StatusChanged);
    }

    [Fact]
    public async Task Status_ThreeFailures_Offline_ThenRecovers()
    {
        using var rig = await Started(ThreeSessions());
        Assert.Equal(ModuleHealth.Online, rig.Module.Status.Health);
        var statusChanges = rig.StatusChanged;
        rig.Source.EnqueueFailure(new IOException("disque absent"));
        rig.Source.EnqueueFailure(new IOException("disque absent"));
        rig.Source.EnqueueFailure(new IOException("disque absent"));

        rig.Module.PollNow();
        rig.Module.PollNow();
        Assert.Equal(ModuleHealth.Online, rig.Module.Status.Health);
        Assert.Equal(statusChanges, rig.StatusChanged);
        rig.Module.PollNow();

        Assert.Equal(ModuleHealth.Offline, rig.Module.Status.Health);
        Assert.Equal(statusChanges + 1, rig.StatusChanged);
        Assert.Equal(3, rig.Log.Entries.Count(e => e.StartsWith("Error : ", StringComparison.Ordinal)));
        Assert.Contains("disque absent", rig.Log.Entries[0]);
        Assert.Equal(3, rig.Module.Snapshot!.Sessions.Count); // le dernier relevé est gardé

        rig.Source.Enqueue(ThreeSessions());
        rig.Module.PollNow();

        Assert.Equal(ModuleHealth.Online, rig.Module.Status.Health);
        Assert.Equal(statusChanges + 2, rig.StatusChanged);
    }

    [Fact]
    public async Task Status_FailuresMustBeConsecutive()
    {
        using var rig = await Started(ThreeSessions());
        foreach (var _ in Enumerable.Range(0, 2))
        {
            rig.Source.EnqueueFailure(new IOException("x"));
            rig.Source.EnqueueFailure(new IOException("x"));
            rig.Module.PollNow();
            rig.Module.PollNow();
            rig.Source.Enqueue(ThreeSessions());
            rig.Module.PollNow();
        }

        Assert.Equal(ModuleHealth.Online, rig.Module.Status.Health);
    }

    [Fact]
    public async Task Poll_SourceThrowingAnythingIsAFailure_NeverEscapes()
    {
        using var rig = await Started(ThreeSessions());
        rig.Source.EnqueueFailure(new InvalidOperationException("boum"));

        rig.Module.PollNow();

        Assert.Contains(rig.Log.Entries, e => e.Contains("boum", StringComparison.Ordinal));
    }

    // --- Minuteurs ---

    [Fact]
    public async Task Timer_PollsEverySecond_NeverBeforeStart()
    {
        using var rig = Create();
        rig.Source.Enqueue(ThreeSessions());
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, rig.Source.Polls);

        await rig.Module.StartAsync(CancellationToken.None);
        Assert.Equal(1, rig.Source.Polls);

        rig.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, rig.Source.Polls);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, rig.Source.Polls);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(5, rig.Source.Polls);
    }

    [Fact]
    public async Task Timer_NoOverlappingPolls()
    {
        var source = new BlockingSource();
        var time = new FakeTimeProvider(Start);
        using var module = new AgentModule(new ClaudeCodeFactory(_ => source).Descriptor,
            new AgentFlavor("a", "b", ClaudeMascot.Definition), source, new AgentSettings(), time, new FakeModuleLog(), new Random(1));
        await module.StartAsync(CancellationToken.None);
        source.Block = true;

        var blocked = Task.Run(module.PollNow);
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromSeconds(3)); // trois ticks pendant que le sondage court : tous sautés
        module.PollNow();                       // et un sondage manuel aussi
        source.Release.Set();
        await blocked;

        Assert.Equal(2, source.Calls); // le premier au démarrage, un seul pendant le blocage
        Assert.Equal(1, source.MaxConcurrent);

        source.Block = false;
        time.Advance(TimeSpan.FromSeconds(1)); // la place est libre : le minuteur sonde à nouveau
        Assert.Equal(3, source.Calls);
    }

    private sealed class BlockingSource : IAgentSource
    {
        private int _concurrent;
        private int _calls;
        public bool Block { get; set; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent { get; private set; }
        public List<string?> SelectedIds { get; } = [];

        public AgentSnapshot Poll(string? selectedSessionId, DateTimeOffset now)
        {
            lock (SelectedIds)
            {
                SelectedIds.Add(selectedSessionId);
            }

            var now2 = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, now2);
            Interlocked.Increment(ref _calls);
            try
            {
                if (Block)
                {
                    Entered.Set();
                    Release.Wait(TimeSpan.FromSeconds(10));
                }

                return new AgentSnapshot(true, [Session("a", AgentActivity.Idle, 30), Session("b", AgentActivity.Idle, 10), Session("c", AgentActivity.Idle, 20)], null, null);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Dispose_StopsTimersAndDisposesSource()
    {
        var rig = await Started(ThreeSessions());
        var polls = rig.Source.Polls;

        rig.Module.Dispose();
        rig.Module.Dispose(); // idempotent
        rig.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(polls, rig.Source.Polls);
        Assert.Equal(1, rig.Source.Disposed);
        Assert.False(rig.Module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab)));
    }

    [Fact]
    public async Task Dispose_BeforeStart_StartDoesNothing()
    {
        var rig = Create();
        rig.Module.Dispose();

        await rig.Module.StartAsync(CancellationToken.None);

        Assert.Equal(0, rig.Source.Polls);
        Assert.Equal(1, rig.Source.Disposed);
    }

    [Fact]
    public async Task Command_DuringARunningPoll_IsNotLost_ThePollIsRedoneWithTheNewTab()
    {
        var source = new BlockingSource();
        var time = new FakeTimeProvider(Start);
        var background = new List<Action>();
        using var module = new AgentModule(new ClaudeCodeFactory(_ => source).Descriptor,
            new AgentFlavor("a", "b", ClaudeMascot.Definition), source, new AgentSettings(), time, new FakeModuleLog(), new Random(1))
        {
            RunInBackground = background.Add,
        };
        await module.StartAsync(CancellationToken.None); // sélection : b
        source.Block = true;

        var running = Task.Run(module.PollNow);          // demande « b » et reste bloqué dans la source
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab))); // c
        background.Single()();                           // le sondage immédiat de la commande : sauté, un sondage court déjà
        Assert.Equal(2, source.Calls);
        source.Block = false;
        source.Release.Set();
        await running;

        Assert.Equal(3, source.Calls);                   // refait à la fin du sondage bloqué
        Assert.Equal([null, "b", "c"], source.SelectedIds);
        Assert.Equal(1, source.MaxConcurrent);
    }

    [Fact]
    public async Task Command_WithoutARunningPoll_PollsOnce()
    {
        var source = new BlockingSource();
        var time = new FakeTimeProvider(Start);
        var background = new List<Action>();
        using var module = new AgentModule(new ClaudeCodeFactory(_ => source).Descriptor,
            new AgentFlavor("a", "b", ClaudeMascot.Definition), source, new AgentSettings(), time, new FakeModuleLog(), new Random(1))
        {
            RunInBackground = background.Add,
        };
        await module.StartAsync(CancellationToken.None);

        module.HandleCommand(new ModuleCommand(ModuleCommandKind.NextTab));
        background.Single()();

        Assert.Equal(2, source.Calls); // pas de sondage de trop
        Assert.Equal([null, "c"], source.SelectedIds);
    }

    [Fact]
    public async Task Events_ASubscriberThatThrows_DoesNotStopThePollNorTheWake()
    {
        using var rig = Create();
        rig.Source.Enqueue(Snapshot([Session("a", AgentActivity.Working)], PlainDetail));
        var calls = 0;
        rig.Module.Invalidated += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("abonné fautif");
        };
        rig.Module.StatusChanged += (_, _) => throw new InvalidOperationException("abonné fautif");
        await rig.Module.StartAsync(CancellationToken.None);

        Tick(rig, TimeSpan.FromSeconds(8)); // des sondages et des réveils de la mascotte au travail continuent

        Assert.True(calls > 3, $"seulement {calls} invalidations");
        Assert.Equal(1 + 8, rig.Source.Polls);
        Assert.Contains(rig.Log.Entries, e => e.Contains("abonné fautif", StringComparison.Ordinal));
        Assert.Single(rig.Module.Snapshot!.Sessions);
    }

    [Fact]
    public void Render_OfflineBeforeAnyPoll_DrawsNoSessionTextInsteadOfLoading()
    {
        using var rig = Create();
        for (var i = 0; i < 3; i++)
        {
            rig.Source.EnqueueFailure(new IOException("disque absent"));
            rig.Module.PollNow();
        }

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Equal(ModuleHealth.Offline, rig.Module.Status.Health);
        Assert.Null(rig.Module.Snapshot);
        Assert.Contains("Aucune session Claude Code ouverte", texts);
        Assert.DoesNotContain("Chargement…", texts);
    }

    [Fact]
    public void Render_FailingOnceBeforeAnyPoll_StillSaysLoading()
    {
        using var rig = Create();
        rig.Source.EnqueueFailure(new IOException("disque absent"));
        rig.Module.PollNow();

        Assert.Contains("Chargement…", DrawnTexts(rig.Module, 8, 4));
    }

    // --- Rafraîchissement ---

    [Fact]
    public async Task Invalidated_OnlyWhenSignatureChanges()
    {
        using var rig = await Started(
            Snapshot([Session("a", AgentActivity.Working)], Detail("a")),
            Snapshot([Session("a", AgentActivity.Working, minutesAgo: 0)], Detail("a")), // seule l'heure d'activité change
            Snapshot([Session("a", AgentActivity.Working)], Detail("a", task: "Autre tâche")));
        Assert.Equal(1, rig.Invalidated);

        rig.Module.PollNow();
        Assert.Equal(1, rig.Invalidated);

        rig.Module.PollNow();
        Assert.Equal(2, rig.Invalidated);
    }

    [Fact]
    public void Invalidated_WhenTheMinuteOfTheDurationChanges()
    {
        using var rig = Primed(Snapshot([Session("a", AgentActivity.Working)], Detail("a", startedAt: Start.AddSeconds(-30))));
        Assert.Equal(1, rig.Invalidated);

        rig.Time.Advance(TimeSpan.FromSeconds(20)); // 50 s : encore « < 1 min »
        rig.Module.PollNow();
        Assert.Equal(1, rig.Invalidated);

        rig.Time.Advance(TimeSpan.FromSeconds(11)); // 61 s : « 1 min »
        rig.Module.PollNow();
        Assert.Equal(2, rig.Invalidated);
    }

    [Fact]
    public async Task Invalidated_NotRaisedByAFailedPoll()
    {
        using var rig = await Started(ThreeSessions());
        var invalidated = rig.Invalidated;
        rig.Source.EnqueueFailure(new IOException("x"));

        rig.Module.PollNow();

        Assert.Equal(invalidated, rig.Invalidated);
    }

    [Fact]
    public async Task Invalidated_NaNQuota_DoesNotBreakTheSignature()
    {
        var quotas = new AgentQuotas(new QuotaWindow(double.NaN, null), new QuotaWindow(10, Start.AddDays(1)), Start);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a"), quotas));

        Assert.Empty(rig.Log.Entries);
        Assert.NotNull(rig.Module.Snapshot);
    }

    /// <summary>
    /// Avance l'horloge par petits pas : un seul grand saut fausserait le réarmement du minuteur de réveil (l'horloge factice
    /// est déjà à l'arrivée quand les minuteurs sonnent), ce que le temps réel ne fait pas.
    /// </summary>
    private static void Tick(Rig rig, TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            rig.Time.Advance(step);
        }
    }

    private static readonly AgentSessionDetail PlainDetail = new("a", null, null, [], [], null, null);

    [Fact]
    public async Task Animation_ClipsKeepTheFrameRate_PausesWakeTheModule()
    {
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Working)], PlainDetail));
        // Un clip « travaille » vient de démarrer.
        Assert.True(rig.Module.IsAnimating);
        Assert.Equal(1, rig.Invalidated);

        var sawClip = false;
        var sawPause = false;
        for (var step = 0; step < 400; step++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(50));
            if (rig.Module.IsAnimating)
            {
                sawClip = true;
            }
            else
            {
                sawPause = true;
            }
        }

        // La signature ne change plus (détail sans durée) : seuls les réveils de la mascotte invalident.
        Assert.True(sawClip && sawPause);
        Assert.True(rig.Invalidated > 4, $"seulement {rig.Invalidated} invalidations en 20 s");
    }

    [Fact]
    public async Task Animation_WakeTimerInvalidatesWhenAPauseEnds()
    {
        // Inactive : une pause de 4 à 12 s, puis un clip.
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], PlainDetail));
        Assert.False(rig.Module.IsAnimating);
        Assert.Equal(1, rig.Invalidated);

        Tick(rig, TimeSpan.FromSeconds(3.9));
        Assert.Equal(1, rig.Invalidated); // la pause dure au moins 4 s : rien ne bouge

        Tick(rig, TimeSpan.FromSeconds(8.2));
        Assert.True(rig.Invalidated >= 2, "aucun réveil à la fin de la pause");
    }

    [Fact]
    public async Task Animation_SleepingMascot_ArmsNoTimerAndIsNotAnimating()
    {
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], PlainDetail));

        Tick(rig, TimeSpan.FromMinutes(10.5));

        Assert.False(rig.Module.IsAnimating);
        Assert.True(rig.Module.MascotSleeping);
        var invalidated = rig.Invalidated;
        Tick(rig, TimeSpan.FromMinutes(5));
        Assert.Equal(invalidated, rig.Invalidated);
    }

    // --- Dessin (sélection et textes) ---

    private static SKBitmap RenderOn(AgentModule module, SKRectI rect, GridSize size, float scale, DateTimeOffset? now = null, int margin = 30, Theme? theme = null)
    {
        theme ??= Themes.Classique;
        var bitmap = new SKBitmap(GridGeometry.Width + 2 * margin, GridGeometry.Height + 2 * margin);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(theme.Background);
        var bounds = new SKRect(rect.Left + margin, rect.Top + margin, rect.Right + margin, rect.Bottom + margin);
        module.Render(canvas, new ModuleRenderContext(bounds, size, theme, scale, now ?? Start));
        return bitmap;
    }

    private static int Count(SKBitmap bitmap, SKRectI area, Func<SKColor, bool> predicate)
    {
        var count = 0;
        for (var y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
        {
            for (var x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static List<string> DrawnTexts(AgentModule module, int columns, int rows, float scale = 1f, DateTimeOffset? now = null)
    {
        var texts = new List<string>();
        module.Painter.DrawnTexts = texts;
        var rect = GridGeometry.ModuleRect(0, 0, columns, rows);
        using var bitmap = RenderOn(module, rect, new GridSize(columns, rows), scale, now);
        module.Painter.DrawnTexts = null;
        return texts;
    }

    private static List<FeedEntry> LongFeed() =>
        [.. Enumerable.Range(0, 50).Select(i => new FeedEntry(i % 3 == 0 ? "Edit" : "Bash", $"src/Trofeo.Modules.Agents/Dossier{i}/Un-fichier-au-nom-assez-long.cs", i % 3 == 0 ? i + 1 : null, i % 3 == 0 ? i : null))];

    private static List<AgentTask> ManyTasks() =>
        [.. Enumerable.Range(0, 12).Select(i => new AgentTask($"Tâche numéro {i} avec un intitulé plutôt long pour déborder", (AgentTaskState)(i % 3)))];

    private static AgentSnapshot RichSnapshot(string name = "trofeo-HB-dashboard", bool quotas = true)
    {
        var sessions = new[]
        {
            Session("a", AgentActivity.Working, 1, name),
            Session("b", AgentActivity.Waiting, 5, "un-projet-au-nom-vraiment-trop-long-pour-un-onglet"),
            Session("c", AgentActivity.Idle, 9, "docs"),
            Session("d", AgentActivity.Idle, 40, "api"),
        };
        var q = quotas
            ? new AgentQuotas(new QuotaWindow(42, Start.AddHours(2)), new QuotaWindow(77.5, Start.AddDays(3)), Start)
            : null;
        return Snapshot(sessions, Detail("a", "Écrire les tests de rendu du module et vérifier qu'ils passent sur toutes les tailles",
            feed: LongFeed(), tasks: ManyTasks()), q);
    }

    public static TheoryData<int, int, int, int, float, AgentView> Cases()
    {
        var data = new TheoryData<int, int, int, int, float, AgentView>();
        foreach (var view in new[] { AgentView.Feed, AgentView.Overview })
        {
            foreach (var scale in new[] { 0.85f, 1.0f, 1.2f })
            {
                data.Add(0, 0, 6, 3, scale, view);
                data.Add(10, 1, 6, 3, scale, view);
                data.Add(0, 0, 8, 4, scale, view);
                data.Add(8, 0, 8, 4, scale, view);
                data.Add(0, 0, 10, 4, scale, view);
                data.Add(6, 0, 10, 4, scale, view);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Render_AllSizesAndViews_StayInside(int col, int row, int cols, int rows, float scale, AgentView view)
    {
        var theme = Themes.Classique;
        var rect = GridGeometry.ModuleRect(col, row, cols, rows);
        var inside = new SKRectI(rect.Left, rect.Top, rect.Right, rect.Bottom);
        inside.Offset(30, 30);
        var whole = new SKRectI(0, 0, GridGeometry.Width + 60, GridGeometry.Height + 60);
        var rig = Create(new AgentSettings(view));
        using var _ = rig;
        rig.Source.Enqueue(RichSnapshot());
        await rig.Module.StartAsync(CancellationToken.None);
        rig.Module.PollNow(); // le détail de la session choisie est lu au deuxième sondage

        using var bitmap = RenderOn(rig.Module, rect, new GridSize(cols, rows), scale);

        var total = Count(bitmap, whole, c => c != theme.Background);
        var inner = Count(bitmap, inside, c => c != theme.Background);
        Assert.True(inner > 2000, $"seulement {inner} pixels dessinés");
        Assert.True(total == inner, $"{total - inner} pixels hors du module");
    }

    [Fact]
    public async Task Render_Feed_ShowsNewestFirstAndHeader()
    {
        var feed = new List<FeedEntry> { new("Read", "ancien.cs"), new("Edit", "nouveau.cs", 12, 3) };
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Working)], Detail("a", "Faire le truc", feed: feed)));
        rig.Module.PollNow();

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Faire le truc", texts);
        Assert.Contains("+12", texts);
        Assert.Contains("−3", texts);
        Assert.True(texts.IndexOf("nouveau.cs") < texts.IndexOf("ancien.cs"));
        Assert.Contains("Contexte", texts);
        Assert.Contains("Durée", texts);
        Assert.Contains("Modèle", texts);
        Assert.Contains("42 %", texts);
        Assert.Contains("12 min", texts);
        Assert.Contains("Opus 5.5", texts);
    }

    [Fact]
    public async Task Render_CompletedTaskDurationDoesNotIncludeTimeSinceCompletion()
    {
        var detail = Detail("a", startedAt: Start.AddMinutes(-12)) with { TaskFinishedAt = Start.AddMinutes(-10) };
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], detail));
        rig.Module.PollNow();

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("2 min", texts);
        Assert.DoesNotContain("12 min", texts);
    }

    // --- Fil d'activité : fondu et sens ---

    private const string Oldest = "premier.cs";
    private const string Middle = "milieu.cs";
    private const string Newest = "dernier.cs";

    private static async Task<Rig> StartedWithFeed(AgentSettings settings, IReadOnlyList<FeedEntry> feed)
    {
        var rig = Create(settings);
        rig.Source.Enqueue(Snapshot([Session("a", AgentActivity.Working)], Detail("a", "Faire le truc", feed: feed)));
        await rig.Module.StartAsync(CancellationToken.None);
        rig.Module.PollNow();
        return rig;
    }

    private static List<DrawnRun> DrawnRuns(AgentModule module, int columns, int rows, Theme? theme = null)
    {
        var runs = new List<DrawnRun>();
        module.Painter.DrawnRuns = runs;
        var rect = GridGeometry.ModuleRect(0, 0, columns, rows);
        using var bitmap = RenderOn(module, rect, new GridSize(columns, rows), 1f, theme: theme);
        module.Painter.DrawnRuns = null;
        return runs;
    }

    private static DrawnRun Run(List<DrawnRun> runs, string text) => Assert.Single(runs, r => r.Text == text);

    private static List<FeedEntry> ThreeEntries() => [new("Read", Oldest), new("Read", Middle), new("Edit", Newest)];

    private static int FeedCapacity(int columns, int rows)
    {
        var rect = GridGeometry.ModuleRect(0, 0, columns, rows);
        return AgentLayout.Compute(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), new GridSize(columns, rows), 1f).FeedCapacity;
    }

    [Fact]
    public async Task Render_Feed_DefaultHasNoFade()
    {
        using var rig = await StartedWithFeed(new AgentSettings(), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4);

        Assert.Equal(Themes.Classique.Text, Run(runs, Newest).Color);
        Assert.Equal(Run(runs, Newest).Color, Run(runs, Middle).Color);
        Assert.Equal(Run(runs, Newest).Color, Run(runs, Oldest).Color);
    }

    [Fact]
    public async Task Render_Feed_WithFade_OldLinesArePaler()
    {
        using var rig = await StartedWithFeed(new AgentSettings(FeedFade: true), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4);

        Assert.Equal(Themes.Classique.Text, Run(runs, Newest).Color);
        Assert.True(Run(runs, Middle).Color.Alpha < Run(runs, Newest).Color.Alpha);
        Assert.True(Run(runs, Oldest).Color.Alpha < Run(runs, Middle).Color.Alpha);
    }

    [Fact]
    public async Task Render_Feed_NewestOnTop_KeepsTheCurrentOrder()
    {
        using var rig = await StartedWithFeed(new AgentSettings(), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4);

        var step = Run(runs, Middle).Baseline - Run(runs, Newest).Baseline;
        Assert.True(step > 0f);
        Assert.Equal(step, Run(runs, Oldest).Baseline - Run(runs, Middle).Baseline, 0.01f);
    }

    [Fact]
    public async Task Render_Feed_NewestOnBottom_StacksUpwardsFromTheLastWholeLine()
    {
        var capacity = FeedCapacity(8, 4);
        Assert.True(capacity >= 4);

        // Un fil plus long que le corps : le plus récent occupe la dernière ligne entière, comme la plus ancienne l'occupe en haut.
        var feed = Enumerable.Range(0, capacity + 5).Select(i => new FeedEntry("Read", $"f{i:00}.cs")).ToList();
        using var top = await StartedWithFeed(new AgentSettings(), feed);
        using var bottom = await StartedWithFeed(new AgentSettings(FeedNewest: FeedNewest.Bottom), feed);

        var topRuns = DrawnRuns(top.Module, 8, 4);
        var bottomRuns = DrawnRuns(bottom.Module, 8, 4);

        var newest = $"f{capacity + 4:00}.cs";
        var previous = $"f{capacity + 3:00}.cs";
        var oldestShown = $"f{5:00}.cs";
        var lastLine = Run(topRuns, oldestShown).Baseline;
        Assert.Equal(lastLine, Run(bottomRuns, newest).Baseline, 0.01f);
        Assert.True(Run(bottomRuns, previous).Baseline < Run(bottomRuns, newest).Baseline);
        Assert.Equal(Run(topRuns, newest).Baseline, Run(bottomRuns, oldestShown).Baseline, 0.01f);
    }

    [Fact]
    public async Task Render_Feed_NewestOnBottom_ShortFeedIsGluedToTheBottom()
    {
        using var top = await StartedWithFeed(new AgentSettings(), ThreeEntries());
        using var bottom = await StartedWithFeed(new AgentSettings(FeedNewest: FeedNewest.Bottom), ThreeEntries());
        var firstLine = Run(DrawnRuns(top.Module, 8, 4), Newest).Baseline;
        var lastLine = firstLine + (FeedCapacity(8, 4) - 1) * AgentLayout.FeedLineHeight;

        var runs = DrawnRuns(bottom.Module, 8, 4);

        Assert.Equal(lastLine, Run(runs, Newest).Baseline, 0.01f);
        Assert.True(Run(runs, Middle).Baseline < Run(runs, Newest).Baseline);
        Assert.True(Run(runs, Oldest).Baseline < Run(runs, Middle).Baseline);
        Assert.Equal(AgentLayout.FeedLineHeight, Run(runs, Newest).Baseline - Run(runs, Middle).Baseline, 0.01f);
    }

    [Fact]
    public async Task Render_Feed_NewestOnBottom_WithFade_NewestStaysOpaqueAndAgeGrowsUpwards()
    {
        using var rig = await StartedWithFeed(new AgentSettings(FeedFade: true, FeedNewest: FeedNewest.Bottom), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4);

        Assert.Equal(Themes.Classique.Text, Run(runs, Newest).Color);
        Assert.True(Run(runs, Middle).Color.Alpha < Run(runs, Newest).Color.Alpha);
        Assert.True(Run(runs, Oldest).Color.Alpha < Run(runs, Middle).Color.Alpha);
    }

    [Fact]
    public async Task Render_6Columns_OmitsTheModelField()
    {
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Working)], Detail("a")));
        rig.Module.PollNow();

        var texts = DrawnTexts(rig.Module, 6, 3);

        Assert.Contains("Contexte", texts);
        Assert.Contains("Durée", texts);
        Assert.DoesNotContain("Modèle", texts);
        Assert.DoesNotContain("Opus 5.5", texts);
    }

    [Fact]
    public async Task Render_Overview_ShowsActionAndTasks()
    {
        var feed = new List<FeedEntry> { new("Read", "ancien.cs"), new("Edit", "nouveau.cs", 12, 3) };
        var tasks = new List<AgentTask>
        {
            new("Première tâche", AgentTaskState.Completed),
            new("Deuxième tâche", AgentTaskState.InProgress),
            new("Troisième tâche", AgentTaskState.Pending),
        };
        var rig = Create(new AgentSettings(AgentView.Overview));
        using var _ = rig;
        rig.Source.Enqueue(Snapshot([Session("a", AgentActivity.Working)], Detail("a", feed: feed, tasks: tasks)));
        await rig.Module.StartAsync(CancellationToken.None);
        rig.Module.PollNow();

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Edit nouveau.cs", texts);
        Assert.Contains("Tâches 1/3", texts);
        Assert.Contains("Première tâche", texts);
        Assert.Contains("Deuxième tâche", texts);
        Assert.Contains("Troisième tâche", texts);
        Assert.DoesNotContain("ancien.cs", texts);
    }

    [Fact]
    public async Task Render_Overview_IdleSession_HasNoAction()
    {
        var feed = new List<FeedEntry> { new("Edit", "nouveau.cs", 12, 3) };
        var rig = Create(new AgentSettings(AgentView.Overview));
        using var _ = rig;
        rig.Source.Enqueue(Snapshot([Session("a", AgentActivity.Idle)], Detail("a", feed: feed)));
        await rig.Module.StartAsync(CancellationToken.None);
        rig.Module.PollNow();

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.DoesNotContain(texts, t => t.Contains("nouveau.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Tâches", StringComparison.Ordinal)); // pas de liste
    }

    [Fact]
    public async Task Render_Overflow15Sessions_ShowsPlusN()
    {
        var sessions = Enumerable.Range(0, 15).Select(i => Session("s" + i, AgentActivity.Idle, minutesAgo: i == 2 ? 1 : 30 + i, name: "p" + i)).ToList();
        using var rig = await Started(Snapshot(sessions));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Equal("s2", rig.Module.SelectedSessionId);
        Assert.Contains("+10", texts);
        foreach (var i in new[] { 0, 1, 2, 3, 4 })
        {
            Assert.Contains("p" + i, texts);
        }

        Assert.DoesNotContain("p5", texts);
        Assert.DoesNotContain("+9", texts);
    }

    [Fact]
    public async Task Render_Overflow_SelectedTabBeyondTheFold_StaysVisible()
    {
        var sessions = Enumerable.Range(0, 15).Select(i => Session("s" + i, AgentActivity.Idle, minutesAgo: i == 12 ? 1 : 30 + i, name: "p" + i)).ToList();
        using var rig = await Started(Snapshot(sessions));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Equal("s12", rig.Module.SelectedSessionId);
        Assert.Contains("p12", texts);
        Assert.Contains("+10", texts);
    }

    [Fact]
    public async Task Render_NoOverflow_NoPlusN()
    {
        var sessions = Enumerable.Range(0, 6).Select(i => Session("s" + i, AgentActivity.Idle, name: "p" + i)).ToList();
        using var rig = await Started(Snapshot(sessions));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.DoesNotContain(texts, t => t.StartsWith('+'));
        Assert.Contains("p5", texts);
    }

    [Fact]
    public async Task Render_NoSessions_DrawsMessage()
    {
        using var rig = await Started(Snapshot([]));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Aucune session Claude Code ouverte", texts);
        Assert.DoesNotContain("Chargement…", texts);
    }

    [Fact]
    public async Task Render_NotInstalled_DrawsActionMessageInsteadOfNoSession()
    {
        using var rig = await Started(Snapshot([], installed: false));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Claude Code n'est pas installé sur ce PC", texts);
        Assert.DoesNotContain("Aucune session Claude Code ouverte", texts);
    }

    [Fact]
    public async Task Render_CodexFlavor_UsesItsOwnTexts()
    {
        var rig = Create(flavor: new AgentFlavor("Aucune session Codex ouverte", "Codex n'est pas installé sur ce PC", CodexMascot.Definition));
        using var _ = rig;
        rig.Source.Enqueue(Snapshot([]));
        await rig.Module.StartAsync(CancellationToken.None);

        Assert.Contains("Aucune session Codex ouverte", DrawnTexts(rig.Module, 8, 4));
    }

    [Fact]
    public void Render_BeforeFirstPoll_DrawsLoading()
    {
        using var rig = Create();

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Chargement…", texts);
    }

    [Fact]
    public async Task Render_DetailNotReadYet_DrawsLoading()
    {
        using var rig = await Started(ThreeSessions()); // le détail de b n'a pas encore été demandé

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Chargement…", texts);
        Assert.Contains("projet-a", texts);
    }

    [Fact]
    public async Task Render_NoQuotas_DrawsUnavailable()
    {
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a")));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("Quotas indisponibles", texts);
        Assert.DoesNotContain("5 h", texts);
        Assert.DoesNotContain("Hebdo", texts);
    }

    [Fact]
    public async Task Render_Quotas_DrawsBothWindows()
    {
        var quotas = new AgentQuotas(new QuotaWindow(42, Start.AddHours(2)), new QuotaWindow(77.5, Start.AddDays(3)), Start);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a"), quotas));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.DoesNotContain("Quotas indisponibles", texts);
        Assert.Contains("5 h", texts);
        Assert.Contains("Hebdo", texts);
        Assert.Contains("42 %", texts);
        Assert.Contains("78 %", texts);
        Assert.Contains(texts, t => t.StartsWith("reset ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Render_ExpiredQuota_ShowsDashAndResetPassed()
    {
        var quotas = new AgentQuotas(new QuotaWindow(88, Start.AddMinutes(-1)), new QuotaWindow(10, Start.AddDays(2)), Start);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a", context: null), quotas));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("reset passé", texts);
        Assert.DoesNotContain("88 %", texts);
        Assert.Contains("10 %", texts);
    }

    [Fact]
    public async Task Render_NaNQuota_NeverShowsNaN()
    {
        var quotas = new AgentQuotas(new QuotaWindow(double.NaN, Start.AddHours(1)), null, Start);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a", context: double.NaN), quotas));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.DoesNotContain(texts, t => t.Contains("NaN", StringComparison.Ordinal));
        Assert.DoesNotContain("Quotas indisponibles", texts);
    }

    [Fact]
    public async Task Render_QuotaWithoutResetTime_ShowsNoResetText()
    {
        var quotas = new AgentQuotas(new QuotaWindow(42, null), new QuotaWindow(10, null), Start);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], Detail("a"), quotas));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Contains("42 %", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Render_NoDetailValues_ShowDashes()
    {
        var detail = new AgentSessionDetail("a", null, null, [], [], null, null);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle)], detail));

        var texts = DrawnTexts(rig.Module, 8, 4);

        Assert.Equal(3, texts.Count(t => t == "—"));
    }

    [Fact]
    public async Task Render_JapaneseProjectName_StaysInside()
    {
        using var rig = await Started(Snapshot(
            [Session("a", AgentActivity.Working, name: "日本語のとても長いプロジェクト名前を表示するテスト"), Session("b", AgentActivity.Idle, name: "🚀 ロケット")],
            Detail("a", task: "日本語のタスク：テストを書いてください")));
        rig.Module.PollNow();
        var theme = Themes.Classique;
        var rect = GridGeometry.ModuleRect(0, 0, 6, 3);
        var inside = new SKRectI(rect.Left + 30, rect.Top + 30, rect.Right + 30, rect.Bottom + 30);

        using var bitmap = RenderOn(rig.Module, rect, new GridSize(6, 3), 1.2f);

        var total = Count(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height), c => c != theme.Background);
        Assert.Equal(Count(bitmap, inside, c => c != theme.Background), total);
        Assert.True(total > 1000);
    }

    [Fact]
    public async Task Render_ShownTab_HasAHighlightAndAccentStripe()
    {
        var theme = Themes.Classique;
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle, 30), Session("b", AgentActivity.Idle, 1)]));
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);

        using var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), 1f);

        // Marge 16 px, onglet de 46 px de haut, 6 px d'écart : le second onglet (b, affiché) commence à 52 px du haut.
        var left = rect.Left + 30 + 16;
        var top = rect.Top + 30 + 16;
        Assert.Equal(theme.Accent, bitmap.GetPixel(left + 1, top + 52 + 23));
        Assert.NotEqual(theme.Accent, bitmap.GetPixel(left + 1, top + 23)); // le premier n'est pas affiché
        Assert.NotEqual(bitmap.GetPixel(left + 190, top + 4), bitmap.GetPixel(left + 190, top + 52 + 4)); // fond du second plus clair
        Assert.Equal(theme.Background, bitmap.GetPixel(left + 190, top + 4));
    }

    /// <summary>
    /// Les empreintes ont été relevées sous Windows x64 (le dessin des lettres dépend de la plateforme) : ailleurs, le test est
    /// ignoré, pas en échec.
    /// </summary>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Empreintes relevées sous Windows x64 seulement.";
        }
    }

    /// <summary>
    /// Le rendu du module (onglet choisi compris) dans les trois autres thèmes, relevé sur le code d'avant la tâche 4 du plan 9
    /// (commit <c>f79f3da</c>), quand <c>DrawTab</c> dessinait encore lui-même l'onglet choisi. SHA-256 des pixels en RGBA.
    /// </summary>
    private static readonly Dictionary<string, string> TabReference = new()
    {
        ["classique/1"] = "643E99904CA0E5F78EDF569AA2792208717B5F448ED70952201121A2B6E431B7",
        ["classique/1.25"] = "AA4DD269ED76A31E6A2068C04F4534E4D3B121BD564DC7BF46A7448DB6D39E54",
        ["liquid-glass/1"] = "E1C644C14B83055E9EC4914F4051C995D7E81BF1600771B831A3891B59BBDCD7",
        ["liquid-glass/1.25"] = "463451E9D31B423117ABB81F89DAE34438EF19467C3BFA2FB8BB39485CE9EAED",
        ["pixel/1"] = "768439E31CBE15A22749EE95E0913886370E652CF10791048E2A95B7A25A7A52",
        ["pixel/1.25"] = "DB87BF74023ED8A04ABDDF9A24C660CF30B75A7FA8BC2D6EC21BCF3D13355D68",
    };

    [WindowsFact]
    public async Task Tabs_OtherThemes_Unchanged()
    {
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Idle, 30), Session("b", AgentActivity.Working, 1), Session("c", AgentActivity.Waiting, 40)]));
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);
        var actual = new SortedDictionary<string, string>();
        foreach (var theme in new[] { Themes.Classique, Themes.LiquidGlass.Theme, Themes.Pixel.Theme })
        {
            foreach (var scale in new[] { 1f, 1.25f })
            {
                using var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), scale, theme: theme);
                using var rgba = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(rgba))
                using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                {
                    canvas.DrawBitmap(bitmap, 0, 0, paint);
                }
                actual[FormattableString.Invariant($"{theme.Id}/{scale}")] =
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rgba.GetPixelSpan()));

                // L'onglet choisi est bien dans l'image : son trait, à gauche du second onglet.
                var marker = theme.Shape == ThemeShape.Pixel ? theme.Highlight : theme.Accent;
                Assert.Equal(marker, bitmap.GetPixel(rect.Left + 30 + (int)(16 * scale) + 1, rect.Top + 30 + (int)((16 + 52 + 23) * scale)));
            }
        }

        var lines = string.Join("\n", actual.Select(p => $"        [\"{p.Key}\"] = \"{p.Value}\","));
        Assert.True(
            actual.Count == TabReference.Count && actual.All(p => TabReference.TryGetValue(p.Key, out var expected) && expected == p.Value),
            "Le rendu du module a changé dans un autre thème. Empreintes obtenues :\n" + lines);
    }

    [Fact]
    public async Task Render_TabDots_UseTheStateColors()
    {
        var theme = Themes.Classique;
        using var rig = await Started(Snapshot([
            Session("a", AgentActivity.Working, 1), Session("b", AgentActivity.Waiting, 2), Session("c", AgentActivity.Idle, 3)]));
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);

        using var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), 1f);

        var area = new SKRectI(rect.Left + 30, rect.Top + 30, rect.Left + 30 + 40, rect.Top + 30 + 200);
        Assert.True(Count(bitmap, area, c => c == SKColor.Parse("#E8875B")) > 20);
        Assert.True(Count(bitmap, area, c => c == theme.Warning) > 20);
        Assert.True(Count(bitmap, area, c => c == theme.TextMuted) > 20);
    }

    // --- Fabriques ---

    [Fact]
    public void Factories_Descriptors()
    {
        var claude = new ClaudeCodeFactory(_ => new FakeAgentSource()).Descriptor;
        var codex = new CodexFactory(_ => new FakeAgentSource()).Descriptor;

        Assert.Equal("claudecode", claude.TypeId);
        Assert.Equal("Claude Code", claude.DisplayName);
        Assert.Equal("codex", codex.TypeId);
        Assert.Equal("Codex", codex.DisplayName);
        foreach (var d in new[] { claude, codex })
        {
            Assert.Equal(new GridSize(6, 3), d.MinSize);
            Assert.Equal(new GridSize(6, 3), d.Presets[SizePreset.Compact]);
            Assert.Equal(new GridSize(8, 4), d.Presets[SizePreset.Ideal]);
            Assert.Equal(new GridSize(10, 4), d.Presets[SizePreset.Large]);
        }
    }

    [Fact]
    public async Task Factories_Create_BuildModulesWithTheirFlavorAndSettings()
    {
        var sources = new List<FakeAgentSource>();
        IAgentSource Make(ModuleContext _)
        {
            var source = new FakeAgentSource();
            source.Enqueue(Snapshot([]));
            sources.Add(source);
            return source;
        }

        using var settings = JsonDocument.Parse("""{"view":"overview","autoFollow":false}""");
        var context = new ModuleContext(Guid.NewGuid(), settings.RootElement.Clone(), new FakeTimeProvider(Start), new FakeModuleLog(), new NoSecrets());

        using var claude = (AgentModule)new ClaudeCodeFactory(Make).Create(context);
        using var codex = (AgentModule)new CodexFactory(Make).Create(context);
        await claude.StartAsync(CancellationToken.None);
        await codex.StartAsync(CancellationToken.None);

        Assert.Equal("claudecode", claude.Descriptor.TypeId);
        Assert.Equal("codex", codex.Descriptor.TypeId);
        Assert.Contains("Aucune session Claude Code ouverte", DrawnTexts(claude, 8, 4));
        Assert.Contains("Aucune session Codex ouverte", DrawnTexts(codex, 8, 4));
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public void Factories_CreateProduction_BuildModules()
    {
        // Les modules ne sont pas démarrés : aucune lecture de disque (les dossiers de l'outil ne sont pas touchés).
        var context = new ModuleContext(Guid.NewGuid(), default, TimeProvider.System, new FakeModuleLog(), new NoSecrets());

        using var claude = ClaudeCodeFactory.CreateProduction().Create(context);
        using var codex = CodexFactory.CreateProduction().Create(context);

        Assert.Equal("claudecode", claude.Descriptor.TypeId);
        Assert.Equal("codex", codex.Descriptor.TypeId);
        Assert.IsType<AgentModule>(claude);
        Assert.IsType<AgentModule>(codex);
        Assert.Equal(ModuleHealth.Online, claude.Status.Health);
    }

    // --- Thème : onglet actif, points, titre, fil, barres ---

    private static readonly SKColor TestHighlight = new(0xFF, 0x00, 0xFF);
    private static readonly SKColor TestSurface = new(0x20, 0x30, 0x40);
    private static readonly SKColor TestTrack = new(0x00, 0x80, 0x00);

    private static Theme ThemeOf(ThemeShape shape) => Themes.Classique with
    {
        Shape = shape, Highlight = TestHighlight, Surface = TestSurface, Track = TestTrack,
    };

    /// <summary>Un seul onglet (donc le premier, actif) : le rectangle de l'onglet dans le bitmap rendu sur (8 × 4).</summary>
    private static async Task<(SKBitmap Bitmap, SKRect Tab)> RenderActiveTab(Theme theme, AgentActivity activity = AgentActivity.Idle)
    {
        var rig = await Started(Snapshot([Session("a", activity)], Detail("a")));
        rig.Module.PollNow();
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);
        var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), 1f, theme: theme);
        rig.Dispose();
        var layout = AgentLayout.Compute(new SKRect(rect.Left + 30, rect.Top + 30, rect.Right + 30, rect.Bottom + 30), new GridSize(8, 4), 1f);
        var tab = new SKRect(layout.Tabs.Left, layout.Tabs.Top, layout.Tabs.Right, layout.Tabs.Top + AgentLayout.TabHeight);
        return (bitmap, tab);
    }

    [Fact]
    public async Task Agents_PixelTheme_ActiveTabHasHighlightMarker()
    {
        var (bitmap, tab) = await RenderActiveTab(ThemeOf(ThemeShape.Pixel));
        using var _ = bitmap;
        var y = (int)tab.MidY - 12;

        // Trait de 4 px dans la couleur de mise en avant (x = 0 à 3), puis le fond de l'onglet.
        for (var dx = 0; dx < 4; dx++)
        {
            Assert.Equal(TestHighlight, bitmap.GetPixel((int)tab.Left + dx, y));
        }

        Assert.Equal(TestSurface, bitmap.GetPixel((int)tab.Left + 4, y));
        Assert.Equal(TestSurface, bitmap.GetPixel((int)tab.Left + 8, (int)tab.Top + 1));
        Assert.Equal(TestHighlight, bitmap.GetPixel((int)tab.Left + 3, (int)tab.Top));
        Assert.Equal(TestHighlight, bitmap.GetPixel((int)tab.Left + 3, (int)tab.Bottom - 1));
    }

    [Fact]
    public async Task Agents_RoundedTheme_ActiveTabKeepsThreePixelAccentMarker()
    {
        var theme = ThemeOf(ThemeShape.Rounded);
        var (bitmap, tab) = await RenderActiveTab(theme);
        using var _ = bitmap;
        var y = (int)tab.MidY - 12;

        for (var dx = 0; dx < 3; dx++)
        {
            Assert.Equal(theme.Accent, bitmap.GetPixel((int)tab.Left + dx, y));
        }

        Assert.Equal(TestSurface, bitmap.GetPixel((int)tab.Left + 3, y));
    }

    [Fact]
    public async Task Agents_PixelTheme_StatusDotIsSquare_RoundedIsRound()
    {
        var pixel = ThemeOf(ThemeShape.Pixel);
        var rounded = ThemeOf(ThemeShape.Rounded);
        var (pixelBitmap, tab) = await RenderActiveTab(pixel);
        var (roundedBitmap, _) = await RenderActiveTab(rounded);
        using var _1 = pixelBitmap;
        using var _2 = roundedBitmap;

        // Coin du carré de 12 px centré sur (gauche + 18, milieu) ; un disque de même diamètre n'y arrive pas.
        var x = (int)tab.Left + 18 - 6;
        var y = (int)tab.MidY - 6;
        Assert.Equal(pixel.TextMuted, pixelBitmap.GetPixel(x, y));
        Assert.Equal(pixel.TextMuted, pixelBitmap.GetPixel(x + 11, y + 11));
        Assert.NotEqual(rounded.TextMuted, roundedBitmap.GetPixel(x, y));
    }

    [Fact]
    public async Task Agents_PixelTheme_ActiveTabNameIsHighlight_RoundedKeepsText()
    {
        foreach (var (shape, expected) in new[] { (ThemeShape.Pixel, TestHighlight), (ThemeShape.Rounded, Themes.Classique.Text) })
        {
            using var rig = await StartedWithFeed(new AgentSettings(), ThreeEntries());

            var runs = DrawnRuns(rig.Module, 8, 4, ThemeOf(shape));

            Assert.Equal(expected, Run(runs, "projet-a").Color);
        }
    }

    [Fact]
    public async Task Agents_TaskTitleUsesHighlight()
    {
        using var rig = await StartedWithFeed(new AgentSettings(), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4, ThemeOf(ThemeShape.Rounded));

        Assert.Equal(TestHighlight, Run(runs, "Faire le truc").Color);
    }

    [Fact]
    public async Task Agents_FeedUsesFeedTypeface()
    {
        // Pixel porte désormais VT323 aux deux endroits : on remet Pixelify Sans au thème pour que les deux polices diffèrent.
        var theme = Themes.Pixel.Theme with { Typeface = EmbeddedFonts.Find("Pixelify Sans")!, TypefaceScale = 1f };
        Assert.NotSame(theme.Typeface, theme.FeedTypeface);
        using var rig = await StartedWithFeed(new AgentSettings(), [new FeedEntry("Edit", "dernier.cs", 12, 3)]);

        var runs = DrawnRuns(rig.Module, 8, 4, theme);

        var tool = Run(runs, "Edit");
        var target = Run(runs, "dernier.cs");
        Assert.Same(theme.FeedTypeface, tool.Typeface);
        Assert.Same(theme.FeedTypeface, target.Typeface);
        Assert.Equal(22f * theme.FeedScale, tool.Size, 0.01f);
        Assert.Equal(22f * theme.FeedScale, target.Size, 0.01f);

        // Le reste (titre, compteurs, onglets) garde la police du thème.
        Assert.Same(theme.Typeface, Run(runs, "Faire le truc").Typeface);
        Assert.Same(theme.Typeface, Run(runs, "+12").Typeface);
        Assert.Same(theme.Typeface, Run(runs, "projet-a").Typeface);
    }

    [Fact]
    public async Task Agents_TypefaceScale_EnlargesTextButNotTheFeed()
    {
        var plain = Themes.Pixel.Theme with { TypefaceScale = 1f, FeedScale = 1f };
        var enlarged = plain with { TypefaceScale = 1.5f };
        using var rig = await StartedWithFeed(new AgentSettings(), [new FeedEntry("Edit", "dernier.cs", 12, 3)]);

        var before = DrawnRuns(rig.Module, 8, 4, plain);
        var after = DrawnRuns(rig.Module, 8, 4, enlarged);

        // Titre et compteurs grossissent ; le fil suit FeedScale seul (TypefaceScale ne s'y ajoute pas).
        Assert.Equal(Run(before, "Faire le truc").Size * 1.5f, Run(after, "Faire le truc").Size, 0.01f);
        Assert.Equal(Run(before, "+12").Size * 1.5f, Run(after, "+12").Size, 0.01f);
        Assert.Equal(Run(before, "Edit").Size, Run(after, "Edit").Size, 0.01f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task Agents_FeedScaleNotUsable_FallsBackToOne(float scale)
    {
        var theme = Themes.Classique with { FeedScale = scale };
        using var rig = await StartedWithFeed(new AgentSettings(), [new FeedEntry("Edit", "dernier.cs")]);

        var runs = DrawnRuns(rig.Module, 8, 4, theme);

        Assert.Equal(22f, Run(runs, "dernier.cs").Size, 0.01f);
    }

    [Theory]
    [InlineData(1.24f)]
    [InlineData(2.5f)]
    public async Task Agents_FeedWithBigFont_StaysInsideItsRow(float feedScale)
    {
        var theme = Themes.Pixel.Theme with { FeedScale = feedScale };
        using var rig = await StartedWithFeed(new AgentSettings(), ThreeEntries());

        var runs = DrawnRuns(rig.Module, 8, 4, theme);

        // Chaque ligne du fil occupe [haut, haut + 34] ; la boîte du texte (montée + descente) y reste entière.
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);
        var layout = AgentLayout.Compute(new SKRect(rect.Left + 30, rect.Top + 30, rect.Right + 30, rect.Bottom + 30), new GridSize(8, 4), 1f);
        // Le plus récent est en haut (ligne 0), puis le milieu (1), puis le plus ancien (2) ; l'outil et la cible partagent la ligne.
        var lines = new[] { (Newest, 0), (Middle, 1), (Oldest, 2) };
        foreach (var (target, slot) in lines)
        {
            var rowTop = layout.Body.Top + slot * AgentLayout.FeedLineHeight;
            var row = runs.Where(r => Math.Abs(r.Baseline - Run(runs, target).Baseline) < 0.01f).ToList();
            Assert.Equal(2, row.Count);
            foreach (var run in row)
            {
                using var font = new SKFont(run.Typeface, run.Size);
                Assert.True(run.Baseline + font.Metrics.Ascent >= rowTop - 0.01f, $"{run.Text} dépasse en haut");
                Assert.True(run.Baseline + font.Metrics.Descent <= rowTop + AgentLayout.FeedLineHeight + 0.01f, $"{run.Text} dépasse en bas");
            }
        }
    }

    [Fact]
    public async Task Agents_PixelTheme_QuotaBarIsSegmentedAndTaller()
    {
        var theme = ThemeOf(ThemeShape.Pixel);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Working)], Detail("a"),
            new AgentQuotas(new QuotaWindow(42, Start.AddHours(2)), new QuotaWindow(77.5, Start.AddDays(3)), Start)));
        rig.Module.PollNow();
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);
        using var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), 1f, theme: theme);
        var layout = AgentLayout.Compute(new SKRect(rect.Left + 30, rect.Top + 30, rect.Right + 30, rect.Bottom + 30), new GridSize(8, 4), 1f);

        // Pistes à la couleur Track (rien d'autre ne l'emploie en bas de l'écran) : 12 px de haut, centrées sur la ligne de 8 px.
        var bar = layout.Bar;
        var xs = Enumerable.Range((int)bar.Left, (int)bar.Width).Where(x => Enumerable.Range((int)bar.Top, (int)bar.Height).Any(y => bitmap.GetPixel(x, y) == TestTrack)).ToList();
        Assert.NotEmpty(xs);
        var column = xs[0];
        var ys = Enumerable.Range((int)bar.Top, (int)bar.Height).Where(y => bitmap.GetPixel(column, y) == TestTrack).ToList();
        Assert.Equal(12, ys.Count);
        Assert.Equal((int)MathF.Round(bar.Top + 27f + 4f - 6f), ys[0]);

        // Segmentée : des interruptions le long de la barre.
        var row = ys[5];
        var runs = 0;
        var inside = false;
        for (var x = (int)bar.Left; x < (int)bar.Right; x++)
        {
            var c = bitmap.GetPixel(x, row);
            var on = c == TestTrack || c == theme.Accent;
            if (on && !inside) runs++;
            inside = on;
        }

        Assert.True(runs > 5, $"{runs} segments seulement");
    }

    [Fact]
    public async Task Agents_ClassiqueQuotaBar_UsesThemeTrackRounded()
    {
        var theme = ThemeOf(ThemeShape.Rounded);
        using var rig = await Started(Snapshot([Session("a", AgentActivity.Working)], Detail("a"),
            new AgentQuotas(new QuotaWindow(0, Start.AddHours(2)), new QuotaWindow(0, Start.AddDays(3)), Start)));
        rig.Module.PollNow();
        var rect = GridGeometry.ModuleRect(0, 0, 8, 4);
        using var bitmap = RenderOn(rig.Module, rect, new GridSize(8, 4), 1f, theme: theme);
        var layout = AgentLayout.Compute(new SKRect(rect.Left + 30, rect.Top + 30, rect.Right + 30, rect.Bottom + 30), new GridSize(8, 4), 1f);

        var bar = layout.Bar;
        var x = (int)bar.Right - 40;
        var y = (int)(bar.Top + 27f + 4f);

        Assert.Equal(TestTrack, bitmap.GetPixel(x, y));
    }
}
