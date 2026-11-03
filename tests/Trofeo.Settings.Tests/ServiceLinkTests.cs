using System.Collections.Concurrent;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

public class ServiceLinkTests
{
    private static string NewPipeName() => "trofeo-settings-test-" + Guid.NewGuid().ToString("N");

    private static ServiceLink NewLink(string pipe) => new(
        pipe, retryDelay: TimeSpan.FromMilliseconds(60), connectTimeout: TimeSpan.FromMilliseconds(150),
        statusInterval: TimeSpan.FromMilliseconds(80));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Délai dépassé : " + what);
            await Task.Delay(15);
        }
    }

    private sealed class Handler : IControlHandler
    {
        public readonly ConcurrentQueue<int> Subscriptions = new();

        /// <summary>La version du protocole annoncée à la connexion.</summary>
        public int Protocol { get; init; } = ControlPipe.ProtocolVersion;

        public Welcome Hello() => new("1.0.0", Protocol, HelloTypes);

        /// <summary>Les types annoncés à la connexion.</summary>
        public IReadOnlyList<ModuleTypeInfo> HelloTypes { get; init; } = [];

        /// <summary>Les types que le service connaît maintenant : une installation ou un retrait les change.</summary>
        public volatile IReadOnlyList<ModuleTypeInfo> CurrentTypes = [];

        public int TypesCalls;
        public readonly ConcurrentQueue<string> ModuleCalls = new();

        public IReadOnlyList<ModuleTypeInfo> GetTypes()
        {
            Interlocked.Increment(ref TypesCalls);
            return CurrentTypes;
        }

        public static ModuleTypeInfo CommunityType(string id) =>
            new(id, "Démo", new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), false, SettingsSchema.Empty,
                new CommunityTypeInfo("Alex", "Démo", "1.0.0", [ModulePermission.Internet], [ModulePermission.Internet]));

        private ActionResult Changing(string call)
        {
            ModuleCalls.Enqueue(call);
            CurrentTypes = [.. CurrentTypes, CommunityType("alex.t" + CurrentTypes.Count)];
            return new ActionResult(true, "Fait.");
        }

        public CommunityOverview GetCommunity()
        {
            ModuleCalls.Enqueue("community");
            return new CommunityOverview([], @"C:\Modules", false, [], false);
        }

        public PackageInfo InspectPackage(string path)
        {
            ModuleCalls.Enqueue("inspect " + path);
            return new PackageInfo("alex.demo", "Démo", "Alex", "1.0.0", "Démo", [], [], null, null);
        }

        public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
            Task.FromResult(Changing("install " + path));

        public ActionResult ApproveModule(string id, IReadOnlyList<ModulePermission> granted) => Changing("approve " + id);

        public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
            Task.FromResult(Changing("permissions " + id));

        public Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct) => Task.FromResult(Changing("uninstall " + id));

        public Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct) => Task.FromResult(Changing("relaunch " + id));

        public ActionResult SetModulesRoot(string path) => Changing("root " + path);

        public ActionResult SetDeveloperMode(bool on) => Changing("dev " + on);

        public ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted) => Changing("addDev " + path);

        public ActionResult RemoveDevFolder(string path) => Changing("removeDev " + path);

        public Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct) => Task.FromResult(Changing("reloadDev " + id));

        public Task CheckModuleUpdatesAsync(CancellationToken ct)
        {
            ModuleCalls.Enqueue("checkModuleUpdates");
            return Task.CompletedTask;
        }

        public Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id, CancellationToken ct)
        {
            ModuleCalls.Enqueue("prepare " + id);
            return Task.FromResult(new ModuleUpdatePreparation(true, null, "1.3.0", "Notes.", [ModulePermission.Internet], []));
        }

        public void CancelModuleUpdate(string id) => ModuleCalls.Enqueue("cancel " + id);

        public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
            Task.FromResult(Changing("update " + id));

        public IReadOnlyList<ModuleConsumption> GetConsumption()
        {
            ModuleCalls.Enqueue("consumption");
            return [new ModuleConsumption("service", "Service", false, 0.3, 50_000_000, false)];
        }

        public ServiceStatus GetStatus() => new(
            new ScreenInfo(LinkScreenState.Ready, null), false, [],
            new ProcessInfo(1, 2, 0), new FrameInfo(0, 0, 0), new SensorInfo(false, false, false));

        public Func<CancellationToken, Task<Reloaded>> OnReload { get; set; } = _ => Task.FromResult(new Reloaded(true, null, []));

        public Task<Reloaded> ReloadAsync(CancellationToken ct) => OnReload(ct);
        public Task<bool> RestartModuleAsync(Guid instanceId, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LookupResult>>([]);

        public Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey, CancellationToken ct) =>
            Task.FromResult(new ActionResult(true, "ok"));

        /// <summary>Si posé, la prochaine lecture des mises à jour rend l'état d'avant puis attend <see cref="ReleaseStale"/> : une lecture périmée.</summary>
        public volatile bool StallNextRead;
        public readonly ManualResetEventSlim Stalled = new(false);
        public readonly ManualResetEventSlim ReleaseStale = new(false);
        public volatile UpdateInfo Update = new(LinkUpdateState.Unknown, null, false, null, null, LinkUpdateChannel.Stable, false);
        public readonly ConcurrentQueue<string> UpdateCalls = new();

        public UpdateInfo GetUpdate()
        {
            var current = Update;
            if (StallNextRead)
            {
                StallNextRead = false;
                Stalled.Set();
                ReleaseStale.Wait(TimeSpan.FromSeconds(15));
            }

            return current;
        }

        public Task CheckUpdatesAsync(CancellationToken ct)
        {
            UpdateCalls.Enqueue("check");
            Update = new UpdateInfo(LinkUpdateState.Available, "0.9.0", false, null, "La version 0.9.0 est disponible.", Update.Channel, Update.WaitForNextStable);
            return Task.CompletedTask;
        }

        public Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken ct)
        {
            UpdateCalls.Enqueue($"prefs {channel} {waitForNextStable}");
            Update = Update with { Channel = channel, WaitForNextStable = waitForNextStable };
            return Task.CompletedTask;
        }

        public Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken ct)
        {
            UpdateCalls.Enqueue($"install {confirmDowngrade}");
            return Task.FromResult(new ActionResult(false, "Mise à jour annulée : l'autorisation n'a pas été donnée."));
        }

        public IDisposable SubscribePreview(int maxFps, Action<PreviewFrame> onFrame)
        {
            Subscriptions.Enqueue(maxFps);
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task Link_ServiceRestarts_Reconnects()
    {
        var pipe = NewPipeName();
        var server = new ControlServer(pipe, new Handler());
        server.Start();
        await using var link = NewLink(pipe);
        var history = new ConcurrentQueue<LinkState>();
        link.Changed += () => history.Enqueue(link.State);

        await WaitUntilAsync(() => link.State == LinkState.Connected, "première connexion");
        Assert.NotNull(link.Welcome);
        await WaitUntilAsync(() => link.Status is not null, "premier état");

        // Le service s'arrête : la fenêtre repasse à « Connecting » sans exception.
        server.Dispose();
        await WaitUntilAsync(() => history.Contains(LinkState.Connecting), "passage à Connecting");

        // Il revient : la fenêtre se reconnecte seule.
        using var again = new ControlServer(pipe, new Handler());
        again.Start();
        await WaitUntilAsync(() => link.State == LinkState.Connected, "reconnexion");
        Assert.NotNull(link.Welcome);
    }

    [Fact]
    public async Task Link_ReadsTheUpdates_AndSendsTheRequests()
    {
        var pipe = NewPipeName();
        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await using var link = NewLink(pipe);

        await WaitUntilAsync(() => link.Updates is not null, "premier état des mises à jour");
        Assert.Equal(LinkUpdateState.Unknown, link.Updates!.State);

        // La recherche et le choix du canal rendent la main avec l'état déjà à jour (sans attendre la relecture suivante).
        await link.CheckUpdatesAsync();
        Assert.Equal(LinkUpdateState.Available, link.Updates!.State);
        await link.SetUpdatePreferencesAsync(LinkUpdateChannel.Beta, waitForNextStable: false);
        Assert.Equal(LinkUpdateChannel.Beta, link.Updates!.Channel);

        var result = await link.InstallUpdateAsync(confirmDowngrade: true);
        Assert.False(result.Ok);
        Assert.Equal("Mise à jour annulée : l'autorisation n'a pas été donnée.", result.Message);
        Assert.Equal(["check", "prefs Beta False", "install True"], handler.UpdateCalls.ToArray());

        // L'état change côté service : la relecture de chaque seconde le rapporte.
        handler.Update = handler.Update with { State = LinkUpdateState.UpToDate };
        await WaitUntilAsync(() => link.Updates!.State == LinkUpdateState.UpToDate, "relecture des mises à jour");
    }

    [Fact]
    public async Task Types_StartAsTheWelcomeOnes_AndAreNullWhenNotConnected()
    {
        var pipe = NewPipeName();
        var link = NewLink(pipe);
        await using var _ = link;
        Assert.Null(link.Types);
        var handler = new Handler { HelloTypes = [Handler.CommunityType("alex.demo")] };
        var server = new ControlServer(pipe, handler);
        server.Start();

        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");

        Assert.Equal(["alex.demo"], link.Types!.Select(t => t.TypeId));

        server.Dispose();
        await WaitUntilAsync(() => link.State != LinkState.Connected, "coupure");
        Assert.Null(link.Types);
    }

    [Fact]
    public async Task Types_RefreshedAfterInstall()
    {
        var pipe = NewPipeName();
        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await using var link = NewLink(pipe);
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");
        Assert.Empty(link.Types!);
        var changes = 0;
        link.Changed += () => Interlocked.Increment(ref changes);

        var result = await link.InstallPackageAsync(@"C:\a.avmod", [ModulePermission.Internet]);

        Assert.True(result.Ok);
        Assert.Equal(["alex.t0"], link.Types!.Select(t => t.TypeId)); // relus sans attendre quoi que ce soit
        Assert.NotNull(link.Types![0].Community);
        Assert.True(Volatile.Read(ref changes) > 0);
        Assert.Equal(["install C:\\a.avmod"], handler.ModuleCalls.ToArray());
    }

    [Fact]
    public async Task Types_ReadAgainAfterEveryChange_ButNotAfterAReadOnlyRequest()
    {
        var pipe = NewPipeName();
        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await using var link = NewLink(pipe);
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");
        var granted = new[] { ModulePermission.Internet };

        var changes = new Func<Task<ActionResult>>[]
        {
            () => link.InstallPackageAsync("a", granted),
            () => link.ApproveModuleAsync("alex.demo", granted),
            () => link.SetModulePermissionsAsync("alex.demo", granted),
            () => link.UninstallModuleAsync("alex.demo"),
            () => link.RelaunchModuleAsync("alex.demo"),
            () => link.SetModulesRootAsync("D:"),
            () => link.SetDeveloperModeAsync(true),
            () => link.AddDevFolderAsync("C:", granted),
            () => link.RemoveDevFolderAsync("C:"),
            () => link.ReloadDevModuleAsync("alex.demo"),
            () => link.InstallModuleUpdateAsync("alex.demo", granted),
        };
        foreach (var change in changes)
        {
            var before = Volatile.Read(ref handler.TypesCalls);
            Assert.True((await change()).Ok);
            Assert.Equal(before + 1, Volatile.Read(ref handler.TypesCalls));
        }

        Assert.Equal(changes.Length, link.Types!.Count);

        var calls = Volatile.Read(ref handler.TypesCalls);
        Assert.Equal(@"C:\Modules", (await link.GetCommunityAsync()).ModulesRoot);
        Assert.Equal("alex.demo", (await link.InspectPackageAsync("x.avmod")).Id);
        await link.CheckModuleUpdatesAsync();
        Assert.Equal("service", Assert.Single(await link.GetConsumptionAsync()).Id);
        // Préparer une mise à jour (télécharger, lire) et l'abandonner ne changent encore aucun module : rien à relire.
        var preparation = await link.PrepareModuleUpdateAsync("alex.demo");
        await link.CancelModuleUpdateAsync("alex.demo");
        Assert.True(preparation.Ok);
        Assert.Equal("1.3.0", preparation.Version);
        Assert.Equal(calls, Volatile.Read(ref handler.TypesCalls));
        Assert.Contains("prepare alex.demo", handler.ModuleCalls);
        Assert.Contains("cancel alex.demo", handler.ModuleCalls);
    }

    [Fact]
    public async Task ModuleRequests_ServiceAbsent_ThrowControlException()
    {
        await using var link = NewLink(NewPipeName());

        await Assert.ThrowsAsync<ControlException>(() => link.InstallPackageAsync("a", []));
        await Assert.ThrowsAsync<ControlException>(() => link.GetCommunityAsync());
        await Assert.ThrowsAsync<ControlException>(() => link.GetConsumptionAsync());
        await Assert.ThrowsAsync<ControlException>(() => link.PrepareModuleUpdateAsync("alex.demo"));
        await Assert.ThrowsAsync<ControlException>(() => link.CancelModuleUpdateAsync("alex.demo"));
    }

    [Fact]
    public async Task Link_StaleUpdateRead_DoesNotOverwriteANewerOne()
    {
        var pipe = NewPipeName();
        var handler = new Handler { StallNextRead = true };
        using var server = new ControlServer(pipe, handler);
        server.Start();
        // Relecture de la boucle toutes les 30 s : la lecture périmée est la seule qui puisse écraser.
        await using var link = new ServiceLink(
            pipe, retryDelay: TimeSpan.FromMilliseconds(60), connectTimeout: TimeSpan.FromMilliseconds(150), statusInterval: TimeSpan.FromSeconds(30));

        // La première lecture (Stable) est partie, mais n'est pas encore arrivée.
        Assert.True(handler.Stalled.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        await link.SetUpdatePreferencesAsync(LinkUpdateChannel.Beta, waitForNextStable: false);
        Assert.Equal(LinkUpdateChannel.Beta, link.Updates!.Channel);

        handler.ReleaseStale.Set();
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");

        Assert.Equal(LinkUpdateChannel.Beta, link.Updates!.Channel); // la lecture périmée (Stable) est écartée
    }

    [Fact]
    public async Task Link_ThreeFailures_IsServiceStopped()
    {
        var pipe = NewPipeName();
        await using var link = NewLink(pipe);
        Assert.Equal(LinkState.Connecting, link.State);

        await WaitUntilAsync(() => link.State == LinkState.ServiceStopped, "arrêt constaté");

        // Le service arrive plus tard : la fenêtre s'en aperçoit.
        using var server = new ControlServer(pipe, new Handler());
        server.Start();
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion tardive");
    }

    [Fact]
    public async Task Link_DifferentProtocol_IsVersionMismatch_NotServiceStopped()
    {
        var pipe = NewPipeName();
        var history = new ConcurrentQueue<LinkState>();
        using var server = new ControlServer(pipe, new Handler { Protocol = ControlPipe.ProtocolVersion + 1 });
        server.Start();
        await using var link = NewLink(pipe);
        link.Changed += () => history.Enqueue(link.State);

        await WaitUntilAsync(() => link.State == LinkState.VersionMismatch, "versions différentes constatées");

        // Plusieurs tentatives plus tard, on n'a jamais dit « service arrêté » : il tourne, c'est la version qui diffère.
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(LinkState.VersionMismatch, link.State);
        Assert.DoesNotContain(LinkState.ServiceStopped, history);
        Assert.Null(link.Welcome);
    }

    [Fact]
    public async Task Link_Subscribes4FpsOr12()
    {
        var pipe = NewPipeName();
        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await using var link = NewLink(pipe);

        await WaitUntilAsync(() => handler.Subscriptions.Count >= 1, "abonnement");
        Assert.Equal(4, handler.Subscriptions.Last());

        link.SetRealTimePreview(true);
        await WaitUntilAsync(() => handler.Subscriptions.Last() == 12, "aperçu à 12 i/s");

        link.SetRealTimePreview(false);
        await WaitUntilAsync(() => handler.Subscriptions.Last() == 4, "retour à 4 i/s");
    }

    [Fact]
    public async Task Link_RealTimeChosenBeforeConnection_SubscribesAt12()
    {
        var pipe = NewPipeName();
        await using var link = NewLink(pipe);
        link.SetRealTimePreview(true);

        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await WaitUntilAsync(() => handler.Subscriptions.Count >= 1, "abonnement");
        Assert.Equal(12, handler.Subscriptions.First());
    }

    /// <summary>Un « fil de l'interface » de pacotille : les envois s'accumulent jusqu'à ce que le test les exécute.</summary>
    private sealed class StalledUi
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public int Pending => _queue.Count;
        public void Post(Action action) => _queue.Enqueue(action);

        public void Pump()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }
    }

    private static ServiceLink NewLink(string pipe, StalledUi ui) => new(
        pipe, retryDelay: TimeSpan.FromMilliseconds(60), connectTimeout: TimeSpan.FromMilliseconds(150),
        statusInterval: TimeSpan.FromMilliseconds(80), post: ui.Post);

    /// <summary>Une liaison qui n'aura rien à dire pendant le test : un seul échec de connexion, puis une longue attente.</summary>
    private static ServiceLink NewSilentLink(StalledUi ui) => new(
        NewPipeName(), retryDelay: TimeSpan.FromHours(1), connectTimeout: TimeSpan.FromMilliseconds(50), post: ui.Post);

    [Fact]
    public async Task Link_Events_AreRaisedThroughThePoster_NeverInline()
    {
        var pipe = NewPipeName();
        using var server = new ControlServer(pipe, new Handler());
        server.Start();
        var ui = new StalledUi();
        await using var link = NewLink(pipe, ui);
        var changedOn = new ConcurrentQueue<int>();
        link.Changed += () => changedOn.Enqueue(Environment.CurrentManagedThreadId);

        // La liaison est connectée côté pool, mais rien n'est arrivé « à l'interface » tant que le test ne passe pas la main.
        await WaitUntilAsync(() => ui.Pending > 0, "premier envoi vers l'interface");
        Assert.Equal(LinkState.Connecting, link.State);
        Assert.Empty(changedOn);

        ui.Pump();
        Assert.Equal(LinkState.Connected, link.State);
        Assert.All(changedOn, id => Assert.Equal(Environment.CurrentManagedThreadId, id));
        Assert.NotEmpty(changedOn);
    }

    [Fact]
    public async Task Frames_Coalesced_WhenUiStalls()
    {
        var ui = new StalledUi();
        await using var link = NewSilentLink(ui);
        var received = new List<(PreviewFrame Frame, int Thread)>();
        link.FrameReceived += frame => received.Add((frame, Environment.CurrentManagedThreadId));

        var frames = Enumerable.Range(0, 100).Select(i => new PreviewFrame([(byte)i], 180)).ToArray();
        await Task.Run(() =>
        {
            foreach (var frame in frames)
                link.ReceiveFrame(frame);
        }, TestContext.Current.CancellationToken);

        // Cent images pendant que l'interface est bloquée : un seul envoi en attente, et c'est la dernière image qui part.
        Assert.Equal(1, ui.Pending);
        Assert.Empty(received);
        ui.Pump();
        var delivered = Assert.Single(received);
        Assert.Same(frames[^1], delivered.Frame);
        Assert.Equal(Environment.CurrentManagedThreadId, delivered.Thread);

        // L'interface a repris : l'image suivante repart tout de suite.
        link.ReceiveFrame(frames[0]);
        Assert.Equal(1, ui.Pending);
        ui.Pump();
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task Frames_AfterDispose_AreNotPosted()
    {
        var ui = new StalledUi();
        var link = NewSilentLink(ui);
        var received = 0;
        link.FrameReceived += _ => received++;

        link.ReceiveFrame(new PreviewFrame([1], 0));
        Assert.Equal(1, ui.Pending);
        await link.DisposeAsync();

        link.ReceiveFrame(new PreviewFrame([2], 0));
        Assert.Equal(1, ui.Pending);   // pas de nouvel envoi
        ui.Pump();                     // et celui qui attendait ne livre plus rien
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task Link_Paused_SubscribesAtZeroThenBack()
    {
        var pipe = NewPipeName();
        var handler = new Handler();
        using var server = new ControlServer(pipe, handler);
        server.Start();
        await using var link = NewLink(pipe);
        await WaitUntilAsync(() => handler.Subscriptions.Count >= 1, "abonnement");

        link.SetPreviewPaused(true);
        // L'arrêt (0 i/s) désabonne côté service : on le voit à l'abonnement suivant.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        link.SetRealTimePreview(true);   // choisi pendant la pause : rien n'est demandé tant qu'elle dure
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([4], handler.Subscriptions.ToArray());

        link.SetPreviewPaused(false);
        await WaitUntilAsync(() => handler.Subscriptions.Count >= 2, "reprise de l'aperçu");
        Assert.Equal(12, handler.Subscriptions.Last());
    }

    [Fact]
    public async Task Link_Disposed_RequestsFailWithLinkCut()
    {
        var pipe = NewPipeName();
        using var server = new ControlServer(pipe, new Handler());
        server.Start();
        var link = NewLink(pipe);
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");
        await link.DisposeAsync();

        foreach (var request in new Func<Task>[]
                 {
                     () => link.ReloadAsync(),
                     () => link.LookupAsync("clock", "city", "Ly"),
                     () => link.RunActionAsync(Guid.NewGuid(), "x"),
                     () => link.RestartModuleAsync(Guid.NewGuid()),
                 })
        {
            var ex = await Assert.ThrowsAsync<ControlException>(request);
            Assert.Equal("Le lien avec le service est coupé.", ex.Message);
        }
    }

    [Fact]
    public async Task Link_DisposedDuringRequest_FailsWithLinkCut_AndSaveReportsIt()
    {
        var pipe = NewPipeName();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler
        {
            OnReload = async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new Reloaded(true, null, []);
            },
        };
        using var server = new ControlServer(pipe, handler);
        server.Start();
        var link = NewLink(pipe);
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");

        // La fenêtre se ferme pendant que le service travaille : l'appelant reçoit une erreur française, pas une annulation.
        var dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Trofeo.Core.Layout.LayoutStore(Path.Combine(dir, "layout.json"));
            store.Save(Trofeo.Core.Layout.DashboardLayout.Default);
            var document = new LayoutDocument(store, link);
            document.Load();

            var saving = document.SaveAsync(Trofeo.Core.Layout.DashboardLayout.Default with { MaxFps = 6 });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await link.DisposeAsync();

            var outcome = await saving;
            Assert.True(outcome.Saved);
            Assert.Equal("Le lien avec le service est coupé.", outcome.Message);

            // Et une sauvegarde après la fermeture le dit aussi, sans exception.
            var after = await document.SaveAsync(Trofeo.Core.Layout.DashboardLayout.Default with { MaxFps = 7 });
            Assert.True(after.Saved);
            Assert.Equal("Le lien avec le service est coupé.", after.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Link_NotConnected_RequestsFailWithFrenchMessage()
    {
        await using var link = NewLink(NewPipeName());
        var ex = await Assert.ThrowsAsync<ControlException>(() => link.ReloadAsync());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Request_TimeoutFromTheService_BecomesALostLinkControlException()
    {
        var pipe = NewPipeName();
        using var server = new ControlServer(pipe, new Handler());
        server.Start();
        var link = NewLink(pipe);
        await WaitUntilAsync(() => link.State == LinkState.Connected, "connexion");

        // Le service n'a pas répondu dans le délai de la requête (20 s pour un redémarrage) : l'appelant reçoit une erreur française.
        var ex = await Assert.ThrowsAsync<ControlException>(() =>
            link.RequestAsync<bool>((_, _) => throw new TimeoutException("Le service n'a pas répondu à « restart » dans le délai.")));

        Assert.Equal(Texts.LinkLost, ex.Message);
        Assert.Equal("Le service d'affichage ne répond pas.", ex.Message);
        await link.DisposeAsync();
    }
}
