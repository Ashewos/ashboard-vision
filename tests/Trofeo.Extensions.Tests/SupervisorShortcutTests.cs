using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Le service transmet un raccourci aux cases choisies, jamais au-delà de 5 appuis par seconde et par module, et seulement s'il est
/// déclaré par le manifeste ; un module qui ne tourne pas, ou qui est mis de côté, ne reçoit rien.
/// </summary>
[Collection("Hosts")]
public sealed class SupervisorShortcutTests : IAsyncLifetime
{
    private const string Refresh = "refresh";

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

    private (SupervisorRig Rig, FakeHostFactory Factory) Fake()
    {
        var manifest = ModuleSandbox.Manifest(entry: "module.dll") with
        {
            Shortcuts = [new ModuleShortcut(Refresh, "Rafraîchir", null), new ModuleShortcut("Open", "Ouvrir", null)],
        };
        var factory = new FakeHostFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SmallModule(), manifest), factory);
        _rigs.Add(rig);
        return (rig, factory);
    }

    /// <summary>Ouvre une case de cette instance, la dessine (ce qui ouvre son instance dans l'hôte) et rend la case.</summary>
    private static RemoteModule OpenAndSize(SupervisorRig rig, Guid instance)
    {
        var module = rig.Open(instance);
        using (rig.Draw(module))
        {
        }

        return module;
    }

    /// <summary>Le superviseur compte l'hôte comme installé quand il a reçu <c>Hello</c> ; l'instance est ouverte juste après.</summary>
    private static async Task<FakeHostProcess> HostWithOpenAsync(FakeHostFactory factory, int opens)
    {
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        await TestWait.UntilAsync(() => host.Of<Open>().Count() == opens);
        return host;
    }

    [WindowsHostFact]
    public async Task SendShortcut_GoesToTheGivenInstancesOnly()
    {
        var (rig, factory) = Fake();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        OpenAndSize(rig, a);
        var host = await HostWithOpenAsync(factory, 1);
        var openA = host.Of<Open>().Single();
        OpenAndSize(rig, b);
        await TestWait.UntilAsync(() => host.Of<Open>().Count() == 2);
        var openB = host.Of<Open>().Single(o => o.InstanceId != openA.InstanceId);
        OpenAndSize(rig, c);
        await TestWait.UntilAsync(() => host.Of<Open>().Count() == 3);
        var openC = host.Of<Open>().Single(o => o.InstanceId != openA.InstanceId && o.InstanceId != openB.InstanceId);

        Assert.True(rig.Supervisor.SendShortcut(Refresh, [a, c, Guid.NewGuid()])); // une instance inconnue est ignorée

        await TestWait.UntilAsync(() => host.Of<Shortcut>().Count() == 2);
        await Task.Delay(100);
        Assert.Equal(
            new[] { openA.InstanceId, openC.InstanceId }.Order(),
            host.Of<Shortcut>().Select(s => s.InstanceId).Order());
        Assert.All(host.Of<Shortcut>(), s => Assert.Equal(Refresh, s.ShortcutId));
        Assert.DoesNotContain(host.Of<Shortcut>(), s => s.InstanceId == openB.InstanceId);
    }

    [WindowsHostFact]
    public async Task SendShortcut_SixthInOneSecond_IsDropped()
    {
        var (rig, factory) = Fake();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        OpenAndSize(rig, a);
        OpenAndSize(rig, b);
        var host = await HostWithOpenAsync(factory, 2);

        // Cinq appuis sur deux cases chacun : le décompte est par appui, pas par case.
        for (var press = 0; press < ModuleHostSupervisor.MaxShortcutsPerSecond; press++)
        {
            Assert.True(rig.Supervisor.SendShortcut(Refresh, [a, b]));
            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.False(rig.Supervisor.SendShortcut(Refresh, [a, b])); // 0,5 s après le premier : le sixième est jeté
        await TestWait.UntilAsync(() => host.Of<Shortcut>().Count() == 10);
        await Task.Delay(100);
        Assert.Equal(10, host.Of<Shortcut>().Count());

        rig.Time.Advance(TimeSpan.FromMilliseconds(600)); // le premier a plus d'une seconde : une place se libère
        Assert.True(rig.Supervisor.SendShortcut(Refresh, [a]));
        await TestWait.UntilAsync(() => host.Of<Shortcut>().Count() == 11);
    }

    [WindowsHostFact]
    public async Task SendShortcut_HostNotRunning_IsDroppedAndReturnsFalse()
    {
        var (rig, factory) = Fake();

        Assert.False(rig.Supervisor.SendShortcut(Refresh, [Guid.NewGuid()])); // aucune case : aucun hôte

        Assert.True(await TestWait.StaysFalseAsync(() => factory.Count > 0, TimeSpan.FromMilliseconds(200)));
    }

    [WindowsHostFact]
    public async Task SendShortcut_AfterTheHostStopped_ReturnsFalse()
    {
        var (rig, factory) = Fake();
        var instance = Guid.NewGuid();
        var module = OpenAndSize(rig, instance);
        var host = await HostWithOpenAsync(factory, 1);

        host.Exit(1);
        await TestWait.UntilAsync(() => module.Status.ActionMessage == ModuleHostSupervisor.HostStoppedText);

        Assert.False(rig.Supervisor.SendShortcut(Refresh, [instance]));
        Assert.Empty(host.Of<Shortcut>());
    }

    [WindowsHostFact]
    public async Task SendShortcut_SetAsideModule_DoesNothing()
    {
        var (rig, factory) = Fake();
        var instance = Guid.NewGuid();
        var module = OpenAndSize(rig, instance);

        // Trois arrêts en dix minutes : le module est mis de côté.
        var hosts = new List<FakeHostProcess>();
        for (var crash = 1; crash <= 3; crash++)
        {
            var host = await factory.WaitForStartAsync(crash);
            hosts.Add(host);
            await host.WaitAsync<Hello>();
            host.Exit(1);
            if (crash < 3)
            {
                await TestWait.UntilAsync(() => module.Status.ActionMessage == ModuleHostSupervisor.HostStoppedText);
                rig.Time.Advance(TimeSpan.FromSeconds(crash * 2.1));
            }
        }

        await TestWait.UntilAsync(() => rig.SetAsides.Count == 1);

        Assert.False(rig.Supervisor.SendShortcut(Refresh, [instance]));
        Assert.All(hosts, host => Assert.Empty(host.Of<Shortcut>()));
    }

    [WindowsHostFact]
    public async Task SendShortcut_UndeclaredId_IsDropped()
    {
        var (rig, factory) = Fake();
        var instance = Guid.NewGuid();
        OpenAndSize(rig, instance);
        var host = await HostWithOpenAsync(factory, 1);

        Assert.False(rig.Supervisor.SendShortcut("unknown", [instance]));
        Assert.False(rig.Supervisor.SendShortcut("REFRESH", [instance])); // comparé tel quel, majuscules comprises
        Assert.True(rig.Supervisor.SendShortcut("Open", [instance])); // déclaré : passe, et ces refus n'ont pas compté

        await TestWait.UntilAsync(() => host.Of<Shortcut>().Count() == 1);
        await Task.Delay(100);
        Assert.Equal("Open", Assert.Single(host.Of<Shortcut>()).ShortcutId);
    }
}
