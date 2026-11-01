using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Sandbox = Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Le superviseur d'un module : un hôte pour toutes les cases, lancé à la première et arrêté avec la dernière, relancé après une
/// panne, mis de côté à la troisième, qui verrouille les fichiers pendant qu'il tourne. Presque tous les tests jouent l'hôte à la main
/// (un faux processus) pour maîtriser chaque message ; ceux qui touchent au vrai hôte le disent.
/// </summary>
[Collection("Hosts")]
public sealed class SupervisorTests : IAsyncLifetime
{
    private static readonly ModulePermission[] SensorsAndInternet = [ModulePermission.Sensors, ModulePermission.Internet];

    private readonly ModuleSandbox _folders = new();
    private readonly List<SupervisorRig> _rigs = [];
    private readonly List<string> _profiles = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        foreach (var id in _profiles)
        {
            Sandbox.AppContainerProfile.Delete(id);
        }

        _folders.Dispose();
    }

    private (SupervisorRig Rig, FakeHostFactory Factory, string Folder) Fake(
        IReadOnlyList<ModulePermission>? declared = null, IReadOnlyList<ModulePermission>? granted = null, bool dev = false, string? hash = null,
        params SettingField[] fields)
    {
        var folder = _folders.SmallModule();
        var manifest = ModuleSandbox.Manifest(entry: "module.dll", permissions: declared, fields: fields);
        var entry = dev ? ModuleSandbox.DevFolder(folder, manifest) : ModuleSandbox.Ready(folder, manifest, granted ?? declared, hash);
        var factory = new FakeHostFactory();
        var rig = new SupervisorRig(entry, factory, granted ?? declared);
        _rigs.Add(rig);
        return (rig, factory, folder);
    }

    /// <summary>Ouvre une case, la dessine une fois (ce qui ouvre son instance dans l'hôte) et rend la case.</summary>
    private static RemoteModule OpenAndSize(SupervisorRig rig, string settings = "{}")
    {
        var module = rig.Open(settings: settings);
        using (rig.Draw(module))
        {
        }

        return module;
    }

    private static async Task WaitForStatusAsync(RemoteModule module, ModuleHealth health, string? message) =>
        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(health, message), null,
            () => $"État actuel : {module.Status}");

    /// <summary>
    /// L'hôte (déjà installé : il a reçu <c>Hello</c>) s'arrête ; rend la main quand le superviseur l'a compté et l'a dit à la case
    /// (relance programmée, ou mise de côté).
    /// </summary>
    private static async Task<FakeHostProcess> CrashAsync(FakeHostProcess host, RemoteModule module, int code = 1)
    {
        await host.WaitAsync<Hello>();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            var message = module.Status.ActionMessage;
            if (module.Status.Health == ModuleHealth.Offline && (message == ModuleHostSupervisor.HostStoppedText || message?.StartsWith("Mis de côté", StringComparison.Ordinal) == true))
            {
                announced.TrySetResult();
            }
        }

        module.StatusChanged += Check;
        try
        {
            host.Exit(code);
            await announced.Task.WaitAsync(TestWait.Patience);
        }
        finally
        {
            module.StatusChanged -= Check;
        }

        return host;
    }

    // --- Coût nul, partage, arrêt -----------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task NoOpenInstance_NoProcessStarted()
    {
        var (rig, factory, _) = Fake();

        rig.Supervisor.SetMaxFps(30);
        rig.Supervisor.SetAppearance(ModuleSandbox.Json("""{"Theme":"classique"}"""));

        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 0, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, rig.Supervisor.ActiveInstances);
        Assert.Null(rig.Supervisor.Usage);
    }

    [WindowsHostFact]
    public async Task FirstOpen_StartsOneHostSharedByEveryInstance_WithHelloFirst()
    {
        var (rig, factory, folder) = Fake(declared: SensorsAndInternet, granted: [ModulePermission.Internet, ModulePermission.ThisComputer]);
        rig.Supervisor.SetMaxFps(25);

        var first = OpenAndSize(rig);
        var second = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>(open => true);
        await TestWait.UntilAsync(() => host.Of<Open>().Count() == 2);

        Assert.Equal(1, factory.Count);
        Assert.Equal(2, rig.Supervisor.ActiveInstances);
        var hello = Assert.IsType<Hello>(host.Received[0]);
        Assert.Equal((HostMessage.ProtocolVersion, folder, "module.dll", "test.sample", 25), (hello.Protocol, hello.ModuleDirectory, hello.Entry, hello.TypeId, hello.MaxFps));
        Assert.Equal([ModulePermission.Internet], hello.Granted); // seule la permission déclarée ET accordée
        Assert.Matches(@"^--in \d+ --out \d+$", host.Start.Arguments);
        Assert.Equal(2, host.Start.InheritedHandles.Count);
        Assert.Equal("test.sample", host.Start.ModuleId);
        Assert.Equal(folder, host.Start.ModuleDirectory);
        Assert.Equal(2, host.Of<Open>().Select(open => open.InstanceId).Distinct().Count());
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [WindowsHostFact]
    public async Task LastInstanceClosed_StopsHost()
    {
        var factory = new PlainHostProcessFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SampleCopy(), ModuleSandbox.Manifest()), factory);
        _rigs.Add(rig);
        var first = OpenAndSize(rig);
        var second = OpenAndSize(rig);
        await TestWait.UntilAsync(() => Draws(rig, first) && Draws(rig, second));
        var host = factory.Last;

        first.Dispose();
        Assert.True(await TestWait.StaysFalseAsync(() => host.HasExited, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(1, rig.Supervisor.ActiveInstances);

        second.Dispose();
        var code = await host.Exited.WaitAsync(TestWait.Patience);
        Assert.Equal(0, code); // il a fini de lui-même sur Shutdown, sans être tué
        Assert.Equal(0, rig.Supervisor.ActiveInstances);
        Assert.Null(rig.Supervisor.Usage);

        // Une case posée plus tard relance un hôte tout neuf.
        var third = OpenAndSize(rig);
        await TestWait.UntilAsync(() => factory.Count == 2);
        await TestWait.UntilAsync(() => Draws(rig, third));
    }

    [WindowsHostFact]
    public async Task Dispose_AsksTheHostToShutDown()
    {
        var (rig, factory, _) = Fake();
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();
        _ = host.WaitAsync<Shutdown>().ContinueWith(_ => host.Exit(0), TaskScheduler.Default);

        await rig.Supervisor.DisposeAsync();

        Assert.True(host.HasExited);
        Assert.False(host.Killed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await Task.FromResult(rig.Supervisor.Open(
            new ModuleContext(Guid.NewGuid(), ModuleSandbox.Json(), rig.Time, rig.Log, rig.Secrets))));
    }

    [WindowsHostFact]
    public async Task Usage_ReadsTheRunningHost()
    {
        var (rig, factory, _) = Fake();
        factory.Configure = process => process.Usage = new JobUsage(TimeSpan.FromSeconds(3), 42_000_000, false);
        var module = OpenAndSize(rig);

        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await TestWait.UntilAsync(() => rig.Supervisor.Usage is not null);
        Assert.Equal(new JobUsage(TimeSpan.FromSeconds(3), 42_000_000, false), rig.Supervisor.Usage);
        module.Dispose();
    }

    // --- Pannes ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(40, 60)]
    public void Backoff_FollowsTheDocumentedSteps(int consecutiveStops, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ModuleHostSupervisor.BackoffFor(consecutiveStops));

    [WindowsHostFact]
    public async Task Crash_RestartsWithBackoff()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var first = await factory.WaitForStartAsync();
        var open = await first.WaitAsync<Open>();

        await CrashAsync(first, module);
        Assert.Equal(new ModuleStatus(ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText), module.Status);

        // Première relance : après 1 s.
        rig.Time.Advance(TimeSpan.FromSeconds(0.9));
        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 1, TimeSpan.FromMilliseconds(200)));
        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        var second = await factory.WaitForStartAsync(2);

        // La case est rouverte (même instance, mêmes images partagées) et retrouve son état.
        var reopened = await second.WaitAsync<Open>();
        Assert.Equal(open.InstanceId, reopened.InstanceId);
        await WaitForStatusAsync(module, ModuleHealth.Online, null);

        // Deuxième relance : après 2 s.
        await CrashAsync(second, module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 2, TimeSpan.FromMilliseconds(200)));
        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        await factory.WaitForStartAsync(3);
        Assert.Empty(rig.SetAsides);
    }

    [WindowsHostFact]
    public async Task ThreeCrashesInTenMinutes_SetsAside()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        const string reason = "Mis de côté : le module s'est arrêté 3 fois en 10 minutes.";

        await CrashAsync(await factory.WaitForStartAsync(), module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await CrashAsync(await factory.WaitForStartAsync(2), module);
        await WaitForStatusAsync(module, ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText);
        rig.Time.Advance(TimeSpan.FromSeconds(2.1));
        var third = await factory.WaitForStartAsync(3);
        Assert.Empty(rig.SetAsides);
        await CrashAsync(third, module);

        await WaitForStatusAsync(module, ModuleHealth.Offline, reason);
        var aside = Assert.Single(rig.SetAsides);
        Assert.Equal(("test.sample", reason), aside);

        // Plus aucune relance, ni plus tard, ni pour une case posée ensuite : elle dit pourquoi.
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 3, TimeSpan.FromMilliseconds(300)));
        var late = rig.Open();
        Assert.Equal(new ModuleStatus(ModuleHealth.Offline, reason), late.Status);
        Assert.Equal(3, factory.Count);
    }

    [WindowsHostFact]
    public async Task CrashesSpreadOverMoreThanTenMinutes_AreNotSetAside()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);

        await CrashAsync(await factory.WaitForStartAsync(), module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        var second = await factory.WaitForStartAsync(2);
        rig.Time.Advance(TimeSpan.FromMinutes(11)); // l'hôte a tenu : la série repart de zéro
        await CrashAsync(second, module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1)); // 1 s encore, pas 2
        var third = await factory.WaitForStartAsync(3);
        await CrashAsync(third, module);
        rig.Time.Advance(TimeSpan.FromSeconds(2.1));
        await factory.WaitForStartAsync(4);

        Assert.Empty(rig.SetAsides);
    }

    [WindowsHostFact]
    public async Task MemoryLimitHit_WhenTheHostDied_IsCitedInTheReason()
    {
        var (rig, factory, _) = Fake();
        factory.Configure = process => process.Usage = new JobUsage(TimeSpan.Zero, 511L * 1024 * 1024, true);
        var module = OpenAndSize(rig);

        await CrashAsync(await factory.WaitForStartAsync(), module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await CrashAsync(await factory.WaitForStartAsync(2), module);
        rig.Time.Advance(TimeSpan.FromSeconds(2.1));
        await CrashAsync(await factory.WaitForStartAsync(3), module);

        await TestWait.UntilAsync(() => rig.SetAsides.Count == 1);
        Assert.Contains("512 Mo dépassés", rig.SetAsides[0].Reason);
    }

    [WindowsHostFact]
    public async Task Failure_FromTheHost_KillsItAndIsCitedWhenSetAside()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var host = await factory.WaitForStartAsync(attempt);
            await host.SendAsync(new Failure("Deux classes de module\ten trop."));
            await TestWait.UntilAsync(() => host.Killed);
            await TestWait.UntilAsync(() => module.Status.Health == ModuleHealth.Offline);
            rig.Time.Advance(TimeSpan.FromSeconds(attempt * 2));
        }

        await TestWait.UntilAsync(() => rig.SetAsides.Count == 1);
        Assert.Contains("Dernière erreur : Deux classes de module en trop.", rig.SetAsides[0].Reason);
    }

    [WindowsHostFact]
    public async Task LaunchFailure_CountsAsAStopAndRetries()
    {
        var (rig, factory, _) = Fake();
        var attempts = 0;
        factory.OnStart = _ =>
        {
            if (Interlocked.Increment(ref attempts) <= 1)
            {
                throw new IOException("verrou d'un antivirus");
            }
        };
        var module = OpenAndSize(rig);

        await WaitForStatusAsync(module, ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await factory.WaitForStartAsync();

        await WaitForStatusAsync(module, ModuleHealth.Online, null);
        Assert.Equal(2, attempts);
    }

    [WindowsHostFact]
    public async Task RestartAsync_RelaunchesTheHost_AndClearsTheSetAside()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var first = await factory.WaitForStartAsync();
        await first.WaitAsync<Open>();

        await rig.Supervisor.RestartAsync();

        Assert.True(first.Killed);
        var second = await factory.WaitForStartAsync(2);
        await second.WaitAsync<Open>();
        Assert.Empty(rig.SetAsides); // un redémarrage voulu n'est pas une panne

        // Mis de côté : le redémarrage à la main le remet en route.
        await CrashAsync(second, module);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await CrashAsync(await factory.WaitForStartAsync(3), module);
        rig.Time.Advance(TimeSpan.FromSeconds(2.1));
        await CrashAsync(await factory.WaitForStartAsync(4), module);
        await TestWait.UntilAsync(() => rig.SetAsides.Count == 1);

        await rig.Supervisor.RestartAsync();

        await factory.WaitForStartAsync(5);
        await WaitForStatusAsync(module, ModuleHealth.Online, null);
    }

    [WindowsHostFact]
    public async Task CloseBeforeTheHostIsInstalled_KillsTheLateHostAndFreesTheFiles()
    {
        var (rig, factory, folder) = Fake();
        factory.OnStart = _ => Thread.Sleep(300); // un lancement lent
        var module = rig.Open();

        module.Dispose(); // la case se ferme avant la fin du lancement

        var host = await factory.WaitForStartAsync();
        await TestWait.UntilAsync(() => host.Killed);
        Assert.Equal(0, rig.Supervisor.ActiveInstances);
        Assert.Empty(host.Received); // il n'a même pas reçu Hello
        await TestWait.UntilAsync(() =>
        {
            try
            {
                File.AppendAllText(Path.Combine(folder, "module.dll"), "");
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        });
        Assert.Empty(rig.SetAsides);
    }

    [WindowsHostFact]
    public async Task OpenAgainWhileTheHostIsStopping_StartsAFreshHost()
    {
        var (rig, factory, _) = Fake();
        var first = OpenAndSize(rig);
        var oldHost = await factory.WaitForStartAsync();
        await oldHost.WaitAsync<Open>();

        first.Dispose(); // l'ancien hôte reçoit Shutdown mais ne s'arrête pas tout de suite
        var second = OpenAndSize(rig);

        var newHost = await factory.WaitForStartAsync(2);
        await newHost.WaitAsync<Open>();
        await oldHost.WaitAsync<Shutdown>();
        Assert.False(oldHost.HasExited);
        Assert.Equal(1, rig.Supervisor.ActiveInstances);
        Assert.NotNull(second);
        oldHost.Exit(0);
        await Task.Delay(100);
        Assert.Empty(rig.SetAsides);
        Assert.Equal(ModuleHealth.Online, second.Status.Health); // l'arrêt voulu de l'ancien hôte n'est pas une panne de la nouvelle case
    }

    // --- Fichiers ----------------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Start_FilesChangedSinceScan_DoesNotLaunch()
    {
        var (rig, factory, folder) = Fake();
        File.AppendAllText(Path.Combine(folder, "module.dll"), " modifié après la validation");

        var module = rig.Open();
        using var bitmap = rig.Draw(module);

        await WaitForStatusAsync(module, ModuleHealth.Offline, ModuleHostSupervisor.FilesChangedText);
        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 0, TimeSpan.FromMilliseconds(300)));
        using var drawn = rig.Draw(module);
        Assert.False(IsEmpty(drawn)); // le message est dessiné
        Assert.Equal(ModuleHostSupervisor.FilesChangedText, rig.Open().Status.ActionMessage);
        Assert.Empty(rig.SetAsides);
        File.AppendAllText(Path.Combine(folder, "module.dll"), "x"); // et aucun fichier n'est resté verrouillé
    }

    [WindowsHostFact]
    public async Task FilesLockedWhileHostRuns_WriteAndDeleteFail()
    {
        var (rig, factory, folder) = Fake();
        OpenAndSize(rig);
        await factory.WaitForStartAsync();
        var file = Path.Combine(folder, "module.dll");
        var nested = Path.Combine(folder, "sub", "data.txt");

        Assert.Throws<IOException>(() => File.OpenWrite(file));
        Assert.Throws<IOException>(() => File.AppendAllText(nested, "x"));
        Assert.Throws<IOException>(() => File.Delete(file));
        Assert.Throws<IOException>(() => File.Move(nested, nested + ".old"));
        Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-renamed"));
        Assert.Equal("des données", File.ReadAllText(nested)); // la lecture reste permise
    }

    [WindowsHostFact]
    public async Task HostExited_FilesUnlocked()
    {
        var (rig, factory, folder) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var file = Path.Combine(folder, "module.dll");
        Assert.Throws<IOException>(() => File.OpenWrite(file));

        await CrashAsync(host, module);

        await TestWait.UntilAsync(() =>
        {
            try
            {
                File.AppendAllText(file, "");
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        });

        // Relancé, l'hôte reprend le verrou.
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await factory.WaitForStartAsync(2);
        Assert.Throws<IOException>(() => File.OpenWrite(file));
    }

    [WindowsHostFact]
    public async Task DevFolder_LaunchesWithoutHashCheckOrLock()
    {
        var (rig, factory, folder) = Fake(dev: true);
        OpenAndSize(rig);

        await factory.WaitForStartAsync();

        File.AppendAllText(Path.Combine(folder, "module.dll"), " recompilé"); // le créateur peut recompiler pendant que ça tourne
    }

    [WindowsHostFact]
    public async Task ModuleNotApproved_NeverStartsAHost()
    {
        var folder = _folders.SmallModule();
        var manifest = ModuleSandbox.Manifest(entry: "module.dll");
        var factory = new FakeHostFactory();
        var needsApproval = new CommunityEntry(manifest.Id, folder, manifest, CommunityState.NeedsApproval, "Ce module n'a pas encore été validé.", null, false);
        var rig = new SupervisorRig(needsApproval, factory);
        _rigs.Add(rig);

        var module = rig.Open();

        await WaitForStatusAsync(module, ModuleHealth.Offline, "À valider dans la fenêtre de réglage");
        Assert.Equal(0, factory.Count);
    }

    // --- Ce que l'hôte écrit est hostile -------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Frame_WithBufferOutOfRange_OrUnknownInstance_IsIgnored()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();

        await host.SendAsync(new Frame(open.InstanceId, 2, false));
        await host.SendAsync(new Frame(open.InstanceId, -1, false));
        await host.SendAsync(new Frame(Guid.NewGuid(), 0, false));
        await host.SendFrameAsync(open, 0, SKColors.Red);

        await host.WaitAsync<FrameDone>();
        await TestWait.UntilAsync(() => Draws(rig, module));
        Assert.Single(host.Of<FrameDone>()); // seule la bonne image est acquittée
        Assert.False(host.HasExited);
        Assert.Empty(rig.SetAsides);
    }

    [WindowsHostFact]
    public async Task MessagesOfTheWrongDirection_AreIgnored()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();

        await host.SendAsync(new Hello("1.0", "x", "x", "x", [], "x", ModuleSandbox.Json(), 30));
        await host.SendAsync(open with { InstanceId = Guid.NewGuid() });
        await host.SendAsync(new FrameDone(open.InstanceId, 0));
        await host.SendAsync(new LocalConnected(1, 1, 1));
        await host.SendAsync(new Shutdown());
        await host.SendAsync(new Status(open.InstanceId, ModuleHealth.NotConfigured, "À régler"));

        await WaitForStatusAsync(module, ModuleHealth.NotConfigured, "À régler");
        Assert.False(host.HasExited);
        Assert.Equal(1, factory.Count);
    }

    [WindowsHostFact]
    public async Task UnreadableStream_KillsTheHostAndCountsAsAStop()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        host.WriteRaw([0, 0, 0, 0]); // une trame vide : le protocole n'en connaît pas

        await TestWait.UntilAsync(() => host.Killed);
        await WaitForStatusAsync(module, ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await factory.WaitForStartAsync(2);
    }

    [WindowsHostFact]
    public async Task HostStatusText_IsCleanedBeforeItReachesTheScreen()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();

        await host.SendAsync(new Status(open.InstanceId, ModuleHealth.Offline, "Ligne un\r\nLigne\u0007 deux‮!"));

        await WaitForStatusAsync(module, ModuleHealth.Offline, "Ligne un  Ligne deux!");
    }

    [WindowsHostFact]
    public async Task Hello_ThenAppearanceAndLimits_AreRelayedToARunningHost()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        await host.SendFrameAsync(open, 0, SKColors.Red);
        await TestWait.UntilAsync(() => Draws(rig, module));

        rig.Supervisor.SetMaxFps(30);
        rig.Supervisor.SetMaxFps(30);
        rig.Supervisor.SetAppearance(ModuleSandbox.Json("""{"Theme":"pixel"}"""));
        rig.Supervisor.SetAppearance(default); // une apparence absente retombe sur celle par défaut, sans exception

        Assert.Equal(30, (await host.WaitAsync<Limits>()).MaxFps);
        var changed = await host.WaitAsync<AppearanceChanged>();
        Assert.Equal("pixel", changed.Appearance.GetProperty("Theme").GetString());
        Assert.Single(host.Of<Limits>()); // un plafond inchangé n'est pas renvoyé

        // L'hôte doit redessiner après un changement d'apparence : sans image au bout de 10 s, la case le dit.
        rig.Time.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(new ModuleStatus(ModuleHealth.Offline, RemoteModule.NotRespondingText), module.Status);
    }

    // --- Données, secrets ------------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task DataGet_NeedsThePermission_AndAnswersWithTheSharedFormat()
    {
        var (rig, factory, _) = Fake(declared: [ModulePermission.Sensors, ModulePermission.NowPlaying], granted: [ModulePermission.Sensors]);
        rig.Data.NowPlayingResult = new DataResult<NowPlayingInfo>(DataStatus.Ok, new NowPlayingInfo("Titre", "Artiste", "App", TimeSpan.Zero, TimeSpan.FromMinutes(3), true, null));
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await host.SendAsync(new DataGet(1, HostPayloads.SensorsKind));
        await host.SendAsync(new DataGet(2, HostPayloads.NowPlayingKind));
        await host.SendAsync(new DataGet(3, "autre"));

        var sensors = await host.WaitAsync<DataValue>(value => value.Id == 1);
        Assert.Equal("ok", sensors.Status);
        Assert.Equal(12.5, HostPayloads.ReadSensors(sensors.Value!.Value)!.Values["cpu.load"]);
        Assert.Equal("denied", (await host.WaitAsync<DataValue>(value => value.Id == 2)).Status); // non accordée : refusé, sans lire
        Assert.Equal("unavailable", (await host.WaitAsync<DataValue>(value => value.Id == 3)).Status);
    }

    [WindowsHostFact]
    public async Task DataGet_NothingAvailable_IsUnavailable_AndPermissionGatesTheReading()
    {
        var (rig, factory, _) = Fake(declared: [ModulePermission.Sensors], granted: []);
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await host.SendAsync(new DataGet(1, HostPayloads.SensorsKind));

        Assert.Equal("denied", (await host.WaitAsync<DataValue>()).Status);
        Assert.Equal(0, rig.Data.SensorCalls);
    }

    [WindowsHostFact]
    public async Task Secrets_ReachTheModuleVault()
    {
        var (rig, factory, _) = Fake();
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await host.SendAsync(new SecretSet(1, "jeton", "v1"));
        await host.WaitAsync<SecretValue>(value => value.Id == 1);
        await host.SendAsync(new SecretGet(2, "jeton"));
        await host.SendAsync(new SecretGet(3, "absent"));
        await host.SendAsync(new SecretSet(4, "jeton", null));
        await host.WaitAsync<SecretValue>(value => value.Id == 4);
        await host.SendAsync(new SecretSet(5, new string('k', 500), "x")); // clé démesurée : ignorée, mais l'hôte est servi

        Assert.Equal("v1", (await host.WaitAsync<SecretValue>(value => value.Id == 2)).Value);
        Assert.Null((await host.WaitAsync<SecretValue>(value => value.Id == 3)).Value);
        await host.WaitAsync<SecretValue>(value => value.Id == 5);
        Assert.Null(rig.Secrets.Get("jeton"));
        Assert.Null(rig.Secrets.Get(new string('k', 500)));
    }

    // --- Réglages : recherches et actions --------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Factory_WithoutAHost_SaysTheModuleIsStopped()
    {
        var (rig, _, _) = Fake(fields: [new TextField("nom", "Nom")]);
        var factory = new RemoteModuleFactory(ModuleSandbox.Ready(_folders.SmallModule(), ModuleSandbox.Manifest(entry: "module.dll", fields: [new TextField("nom", "Nom")])), rig.Supervisor);

        Assert.Empty(await factory.LookupAsync("champ", "q", CancellationToken.None));
        var result = await factory.RunActionAsync("tester", ModuleSandbox.Json(), new Trofeo.TestDoubles.FakeSecretStore(), CancellationToken.None);

        Assert.Equal(new ActionResult(false, "Module arrêté."), result);
        Assert.Equal("test.sample", factory.Descriptor.TypeId);
        Assert.Equal("nom", Assert.Single(factory.Schema.Fields).Key);
    }

    [WindowsHostFact]
    public async Task Factory_LookupAndAction_AreRelayedToTheHost()
    {
        var (rig, hostFactory, folder) = Fake();
        var entry = ModuleSandbox.Ready(folder, ModuleSandbox.Manifest(entry: "module.dll"));
        var factory = new RemoteModuleFactory(entry, rig.Supervisor);
        var module = factory.Create(new ModuleContext(Guid.NewGuid(), ModuleSandbox.Json(), rig.Time, rig.Log, rig.Secrets));
        var host = await hostFactory.WaitForStartAsync();
        await host.WaitAsync<Hello>(); // l'hôte est installé : les demandes lui sont relayées

        var lookup = factory.LookupAsync("entity", "salon", CancellationToken.None);
        var request = await host.WaitAsync<Lookup>();
        await host.SendAsync(new LookupReply(request.Id, [new LookupResult("Salon\n", ModuleSandbox.Json("\"light.salon\""))]));
        var found = Assert.Single(await lookup);
        Assert.Equal(("entity", "salon"), (request.FieldKey, request.Query));
        Assert.Equal("Salon", found.Label);
        Assert.Equal("light.salon", found.Value.GetString());

        var action = factory.RunActionAsync("tester", ModuleSandbox.Json("""{"hote":"x"}"""), rig.Secrets, CancellationToken.None);
        var run = await host.WaitAsync<RunAction>();
        Assert.Equal(("tester", "x"), (run.ActionKey, run.Settings.GetProperty("hote").GetString()));
        Assert.NotEqual(Guid.Empty, run.InstanceId);
        await host.SendAsync(new ActionReply(run.Id, new ActionResult(true, "Connecté.")));
        Assert.Equal(new ActionResult(true, "Connecté."), await action);

        // L'hôte s'arrête pendant une action : la réponse est « Module arrêté. ».
        var interrupted = factory.RunActionAsync("tester", ModuleSandbox.Json(), rig.Secrets, CancellationToken.None);
        await host.WaitAsync<RunAction>(a => a.Id != run.Id);
        host.Exit(1);
        Assert.Equal(new ActionResult(false, "Module arrêté."), await interrupted);
        module.Dispose();
    }


    /// <summary>
    /// Le chemin de production, une fois : l'hôte lancé par <see cref="SandboxedHostProcessFactory"/> (AppContainer et job object du
    /// module), les fichiers du module verrouillés, la section d'images dupliquée dans le processus enfermé.
    /// </summary>
    [WindowsSandboxFact]
    [Trait("Windows", "Sandbox")]
    public async Task Production_SandboxedHost_DrawsAFrameWithTheFilesLocked()
    {
        var id = "test.sup-" + Guid.NewGuid().ToString("N")[..12];
        _profiles.Add(id);
        var hostFolder = _folders.NewFolder();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "host"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(hostFolder, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "host"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Sandbox.AppContainerProfile.Ensure(id).GrantRead(hostFolder); // en production, Program Files est lisible par tous les conteneurs
        var folder = _folders.SampleCopy();
        var entry = ModuleSandbox.Ready(folder, ModuleSandbox.Manifest(id));
        var rig = new SupervisorRig(entry, new SandboxedHostProcessFactory(), hostExecutable: Path.Combine(hostFolder, "Trofeo.ModuleHost.exe"));
        _rigs.Add(rig);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        await TestWait.UntilAsync(() => Draws(rig, module), TimeSpan.FromSeconds(60), () => string.Join(" | ", rig.Log.Entries) + " " + module.Status);

        using var bitmap = rig.Draw(module);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(10 + 10, 10 + 24));
        Assert.Throws<IOException>(() => File.OpenWrite(Path.Combine(folder, ModuleSandbox.SampleEntry)));
        var usage = rig.Supervisor.Usage;
        Assert.NotNull(usage);
        Assert.True(usage.Value.PeakProcessMemory > 0);
        Assert.False(usage.Value.MemoryLimitHit);

        module.Dispose();
        await TestWait.UntilAsync(() => rig.Supervisor.Usage is null);
        await TestWait.UntilAsync(() =>
        {
            try
            {
                File.AppendAllText(Path.Combine(folder, ModuleSandbox.SampleEntry), "");
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        });
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.Pixels.All(pixel => pixel.Alpha == 0);

    /// <summary>Dessine la case et dit si quelque chose est apparu.</summary>
    private static bool Draws(SupervisorRig rig, RemoteModule module)
    {
        using var bitmap = rig.Draw(module);
        return !IsEmpty(bitmap);
    }
}

