using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Un hôte de module est un processus qu'on ne croit pas : il ne doit pouvoir imposer au service ni un travail ni une mémoire sans
/// borne (images, données, secrets, états, réponses non lues), et le service ne doit lever aucun événement sous son propre verrou.
/// </summary>
[Collection("Hosts")]
public sealed class SupervisorBoundsTests : IAsyncLifetime
{
    private readonly ModuleSandbox _folders = new();
    private readonly List<SupervisorRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        _folders.Dispose();
    }

    private (SupervisorRig Rig, FakeHostFactory Factory, string Folder) Fake(
        IReadOnlyList<ModulePermission>? permissions = null, ISecretStore? secrets = null)
    {
        var folder = _folders.SmallModule();
        var manifest = ModuleSandbox.Manifest(entry: "module.dll", permissions: permissions);
        var factory = new FakeHostFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(folder, manifest), factory, secrets: secrets);
        _rigs.Add(rig);
        return (rig, factory, folder);
    }

    private static RemoteModule OpenAndSize(SupervisorRig rig)
    {
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        return module;
    }

    private static bool Draws(SupervisorRig rig, RemoteModule module)
    {
        using var bitmap = rig.Draw(module);
        return bitmap.Pixels.Any(pixel => pixel.Alpha != 0);
    }

    // --- Un hôte qui inonde ----------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Flood_Frames_AreAllAcknowledgedButMostAreNotCopied()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        host.PaintBuffer(open, 0, SKColors.Red);
        var invalidated = 0;
        module.Invalidated += (_, _) => Interlocked.Increment(ref invalidated);

        for (var i = 0; i < 1000; i++)
        {
            await host.SendAsync(new Frame(open.InstanceId, 0, true));
        }

        await TestWait.UntilAsync(() => host.Of<FrameDone>().Count() == 1000, null, () => $"{host.Of<FrameDone>().Count()} accusés");
        Assert.InRange(invalidated, 1, 40); // un petit salve, puis le débit permis : pas 1000 copies de 3,5 Mo
        Assert.True(Draws(rig, module));
        Assert.True(module.IsAnimating); // même une image non copiée dit que le module est animé
        Assert.False(host.HasExited);
        Assert.Empty(rig.SetAsides);
    }

    [WindowsHostFact]
    public async Task Flood_DataRequests_AreBoundedAndEveryOneIsAnswered()
    {
        var (rig, factory, _) = Fake([ModulePermission.Sensors]);
        rig.Data.Delay = TimeSpan.FromMilliseconds(50);
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        for (var i = 1; i <= 300; i++)
        {
            await host.SendAsync(new DataGet(i, HostPayloads.SensorsKind));
        }

        await TestWait.UntilAsync(() => host.Of<DataValue>().Count() == 300, null, () => $"{host.Of<DataValue>().Count()} réponses");
        var unavailable = host.Of<DataValue>().Count(value => value.Status == "unavailable");
        Assert.True(unavailable >= 100, $"Au-delà de la file, les demandes doivent être refusées ({unavailable} refusées).");
        Assert.True(rig.Data.SensorCalls < 200, $"Le service ne doit pas lire 300 fois ({rig.Data.SensorCalls}).");
        Assert.False(host.HasExited);
    }

    [WindowsHostFact]
    public async Task Flood_Secrets_AreBoundedAndEveryOneIsAnswered()
    {
        var slow = new SlowSecretStore();
        var (rig, factory, _) = Fake(secrets: slow);
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        for (var i = 1; i <= 400; i++)
        {
            await host.SendAsync(new SecretGet(i, "jeton"));
        }

        await TestWait.UntilAsync(() => host.Of<SecretValue>().Count() == 400, null, () => $"{host.Of<SecretValue>().Count()} réponses");
        Assert.True(slow.Calls < 200, $"Le coffre ne doit pas être lu 400 fois ({slow.Calls}).");
        Assert.False(host.HasExited);
    }

    [WindowsHostFact]
    public async Task Flood_StatusChanges_AreRateLimited_AndTheLastOneWins()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        var changes = 0;
        module.StatusChanged += (_, _) => Interlocked.Increment(ref changes);

        for (var i = 0; i < 500; i++)
        {
            await host.SendAsync(new Status(open.InstanceId, i % 2 == 0 ? ModuleHealth.Offline : ModuleHealth.NotConfigured, "état " + i));
        }

        await host.SendAsync(new Status(open.InstanceId, ModuleHealth.NotConfigured, "le dernier"));
        await TestWait.UntilAsync(() => module.Status.ActionMessage is { } message && message.StartsWith("état", StringComparison.Ordinal));
        await Task.Delay(300); // le temps que la boucle de lecture ait tout lu
        Assert.InRange(Volatile.Read(ref changes), 1, 10);

        rig.Time.Advance(TimeSpan.FromSeconds(1.5)); // la fenêtre se referme : l'état en attente (le dernier) est appliqué
        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(ModuleHealth.NotConfigured, "le dernier"), null, () => module.Status.ToString());
    }

    [WindowsHostFact]
    public async Task Flood_RepliesTheHostDoesNotRead_KillTheHostAndCountAsAStop()
    {
        var (rig, factory, _) = Fake();
        factory.ReadInput = false; // un hôte qui n'écoute plus ce que le service lui écrit
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();

        // Chaque demande refusée vaut une réponse au service : sans borne, la file d'envoi grossit à mesure.
        try
        {
            for (var i = 1; i <= 8000 && !host.Killed; i++)
            {
                await host.SendAsync(new LocalConnect(i, Guid.Empty, 1));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // l'hôte a été arrêté pendant qu'on écrivait : c'est ce qu'on attend
        }

        await TestWait.UntilAsync(() => host.Killed, null, () => "L'hôte n'a pas été arrêté");
        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText));
        Assert.Contains(rig.Log.Entries, line => line.Contains("ne lit plus", StringComparison.Ordinal));
    }

    // --- Événements hors du verrou ---------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Events_AreNotRaisedUnderTheSupervisorLock()
    {
        var (rig, factory, _) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        var stuck = false;
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.StatusChanged += (_, _) =>
        {
            // Un abonné qui rappelle le superviseur depuis un autre fil et l'attend : sous le verrou du superviseur, ce serait une impasse.
            var probe = Task.Run(() => rig.Supervisor.ActiveInstances);
            if (!((IAsyncResult)probe).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
            {
                stuck = true;
            }

            handled.TrySetResult();
        };

        host.Exit(1);

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(stuck);
    }

    // --- Verrou de fichiers occupé ---------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task FilesBusy_RetriesLaterWithoutCountingAsAStop()
    {
        var (rig, factory, folder) = Fake();
        var held = new FileStream(Path.Combine(folder, "module.dll"), FileMode.Open, FileAccess.Write, FileShare.None); // un antivirus, OneDrive…
        try
        {
            var module = rig.Open();
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var expected = attempt;
                await TestWait.UntilAsync(() => rig.Log.Entries.Count(line => line.Contains("indisponibles", StringComparison.Ordinal)) >= expected, null,
                    () => string.Join(" | ", rig.Log.Entries));
                rig.Time.Advance(ModuleHostSupervisor.FilesBusyRetry + TimeSpan.FromSeconds(0.1));
            }

            Assert.Equal(0, factory.Count);
            Assert.Empty(rig.SetAsides); // cinq essais ratés : aucune mise de côté
            Assert.Equal(ModuleHealth.Offline, module.Status.Health);
            Assert.Equal(ModuleHostSupervisor.FilesBusyText, module.Status.ActionMessage);

            // La 5e avance a pu lancer un 6e essai sur le pool avant que les fichiers ne soient libres : il échoue et programme le suivant.
            // On libère, puis on avance l'horloge tant que l'hôte n'est pas parti, pour ne dépendre de l'ordre d'aucun de ces essais.
            held.Dispose();
            await TestWait.UntilAsync(() =>
            {
                if (factory.Count == 0)
                {
                    rig.Time.Advance(ModuleHostSupervisor.FilesBusyRetry + TimeSpan.FromSeconds(0.1));
                }

                return factory.Count >= 1;
            }, null, () => string.Join(" | ", rig.Log.Entries));
            await factory.WaitForStartAsync();
            await TestWait.UntilAsync(() => module.Status.Health == ModuleHealth.Online);
        }
        finally
        {
            held.Dispose();
        }
    }

    // --- Armement du délai de réponse -------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task AFrameThatAnswersAtOnce_NeverLeavesTheCaseWaiting()
    {
        var (rig, factory, _) = Fake();
        factory.OnMessage = (host, message) =>
        {
            if (message is Open open)
            {
                host.PaintBuffer(open, 0, SKColors.Blue);
                host.SendAsync(new Frame(open.InstanceId, 0, false)).GetAwaiter().GetResult(); // la réponse arrive dès l'ouverture
            }
        };
        var modules = Enumerable.Range(0, 20).Select(_ => OpenAndSize(rig)).ToList();
        await TestWait.UntilAsync(() => modules.All(module => Draws(rig, module)));

        rig.Time.Advance(TimeSpan.FromSeconds(11));

        // Une image est arrivée pour chaque case : aucune ne doit croire qu'elle attend encore.
        Assert.All(modules, module => Assert.Equal(ModuleHealth.Online, module.Status.Health));
    }

    // --- Arrêt d'un hôte qui ignore Shutdown ---------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task HostIgnoringShutdown_IsKilledAfterTheGrace()
    {
        var (rig, factory, _) = Fake();
        rig.Supervisor.ShutdownGrace = TimeSpan.FromSeconds(2); // la minuterie du tueur est réelle : assez longue pour ne pas devancer l'attente
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        module.Dispose(); // la dernière case se ferme : Shutdown, que cet hôte ignore

        await host.WaitAsync<Shutdown>();
        await TestWait.UntilAsync(() => host.Killed, TimeSpan.FromSeconds(10), () => "Jamais tué");
        await TestWait.UntilAsync(() => host.HasExited);
        Assert.Empty(rig.SetAsides); // un arrêt voulu n'est pas une panne
    }

    [WindowsHostFact]
    public async Task Dispose_HostIgnoringShutdown_IsKilledAfterTheGrace()
    {
        var (rig, factory, _) = Fake();
        rig.Supervisor.ShutdownGrace = TimeSpan.FromSeconds(2);
        rig.Supervisor.DisposeGrace = TimeSpan.FromSeconds(2);
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(host.Killed);
        Assert.True(host.HasExited);
    }

    [WindowsHostFact]
    public async Task Dispose_AfterTheLastCaseClosed_WaitsForTheStoppingHost_AndReleasesTheFiles()
    {
        var (rig, factory, folder) = Fake();
        var module = OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        module.Dispose(); // la dernière case : Shutdown, que cet hôte ignore ; le superviseur n'a plus de session
        await host.WaitAsync<Shutdown>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Rendu après {clock.Elapsed} : le superviseur a attendu le délai de 5 s de l'hôte qui s'arrête.");
        Assert.True(host.Killed);
        Directory.Delete(folder, true); // lève si un fichier du module est encore tenu
    }

    [WindowsHostFact]
    public async Task Dispose_WhileAnOlderHostIsStillFinishing_WaitsForItToo()
    {
        var folder = _folders.SmallModule();
        var manifest = ModuleSandbox.Manifest(entry: "module.dll");
        var inner = new FakeHostFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(folder, manifest), new SlowExitHostFactory(inner, TimeSpan.FromMilliseconds(1500)));
        _rigs.Add(rig);
        rig.Supervisor.ShutdownGrace = TimeSpan.FromMilliseconds(50);
        rig.Supervisor.DisposeGrace = TimeSpan.FromMilliseconds(100);
        OpenAndSize(rig);
        await (await inner.WaitForStartAsync()).WaitAsync<Hello>();

        await rig.Supervisor.RestartAsync(); // l'ancien hôte est tué mais met 1,5 s à finir : on n'a attendu que 50 ms
        await (await inner.WaitForStartAsync(2)).WaitAsync<Hello>();
        rig.Supervisor.ShutdownGrace = TimeSpan.FromSeconds(5); // le délai court ne servait qu'à rendre la main à RestartAsync
        await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Directory.Delete(folder, true); // lève si l'ancien hôte tient encore un fichier
    }

    // --- Texte venu de l'hôte ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a\u2028b\u2029c", "abc")]
    [InlineData("a\u200Bb\u200Cc\u200Dd\u2060e\uFEFFf", "abcdef")]
    [InlineData("tab\there", "tab here")]
    [InlineData("seul\uD800seul", "seulseul")]
    public void HostText_RemovesInvisibleAndLineBreakingCharacters(string raw, string expected) =>
        Assert.Equal(expected, HostText.Clean(raw, 100));

    [Fact]
    public void HostText_NeverCutsASurrogatePair()
    {
        var emoji = "\U0001F600";

        var cut = HostText.Clean("ab" + emoji + "cd", 3); // la place pour « ab » et la moitié d'un emoji

        Assert.Equal("ab", cut);
        Assert.Equal("ab" + emoji, HostText.Clean("ab" + emoji + "cd", 4));
    }

    [WindowsHostFact]
    public async Task HostLog_LineLength_IsBounded()
    {
        var (rig, factory, _) = Fake();
        OpenAndSize(rig);
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        await host.SendAsync(new Log("info", new string('x', 2000)));

        await TestWait.UntilAsync(() => rig.Log.Entries.Any(line => line.Contains("xxxx", StringComparison.Ordinal)));
        Assert.All(rig.Log.Entries.Where(line => line.Contains("xxxx", StringComparison.Ordinal)), line => Assert.True(line.Length <= 520, $"{line.Length} caractères"));
    }
}
