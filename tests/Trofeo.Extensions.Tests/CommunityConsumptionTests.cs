using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Tests;

// La consommation des modules vue du service : rien tant qu'aucun hôte ne tourne, un relevé par minute ensuite, et toutes les 5 s
// seulement pendant que la fenêtre de réglage la lit.
[Collection("Hosts")]
public sealed class CommunityConsumptionTests : IAsyncLifetime
{
    private const long MiB = 1024 * 1024;
    private static readonly ModulePermission[] Internet = [ModulePermission.Internet];
    private static readonly ProcessInfo Service = new(90 * MiB, 130 * MiB, 0.4);

    private readonly List<CommunityRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    private async Task<CommunityRig> InstalledAsync()
    {
        var rig = new CommunityRig();
        _rigs.Add(rig);
        var result = await rig.Community.InstallAsync(rig.Package(), Internet, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        return rig;
    }

    /// <summary>Pose une case : l'hôte est lancé (faux) et installé quand on rend la main.</summary>
    private static async Task<FakeHostProcess> StartHostAsync(CommunityRig rig)
    {
        rig.Open("alex.demo");
        var host = await rig.Launcher.WaitForStartAsync();
        await host.WaitAsync<Hello>(); // le superviseur l'a installé
        return host;
    }

    [WindowsHostFact]
    public async Task NoHostRunning_OnlyTheServiceRow_AndNothingIsMeasured()
    {
        var rig = await InstalledAsync();

        var rows = rig.Community.Consumption(Service);
        rig.Time.Advance(TimeSpan.FromHours(1));
        var later = rig.Community.Consumption(Service);

        var original = Assert.Single(rows);
        Assert.Equal(("Modules d'origine (dans le service)", false, 90 * MiB, 0.4), (original.DisplayName, original.IsCommunity, original.MemoryBytes, original.CpuPercent));
        Assert.Single(later);
        Assert.Equal(0, rig.Launcher.Count); // ni hôte, ni relevé
    }

    [WindowsHostFact]
    public async Task RunningHost_AppearsAfterAMinute_WithItsAverageAndTheHighMark()
    {
        var rig = await InstalledAsync();
        var host = await StartHostAsync(rig);
        host.PrivateBytes = 80 * MiB;
        host.Usage = new JobUsage(TimeSpan.FromSeconds(60 * Environment.ProcessorCount * 0.05), 0, false); // 5 % de la machine pendant la minute

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        var row = rig.Community.Consumption(Service).Single(r => r.IsCommunity);

        Assert.Equal(("alex.demo", "Module de test", 80 * MiB, true), (row.Id, row.DisplayName, row.MemoryBytes, row.High));
        Assert.Equal(5.0, row.CpuPercent, 1);
    }

    [WindowsHostFact]
    public async Task RunningHost_QuietForAMinute_IsNotHigh()
    {
        var rig = await InstalledAsync();
        var host = await StartHostAsync(rig);
        host.PrivateBytes = 40 * MiB;
        host.Usage = new JobUsage(TimeSpan.FromSeconds(60 * Environment.ProcessorCount * 0.002), 0, false);

        rig.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(rig.Community.Consumption(Service).Single(r => r.IsCommunity).High);
    }

    [WindowsHostFact]
    public async Task WindowNotReading_OneMeasurePerMinute_ThenEveryFiveSecondsWhileItReads()
    {
        var rig = await InstalledAsync();
        var host = await StartHostAsync(rig);
        host.PrivateBytes = 40 * MiB;
        host.Usage = new JobUsage(TimeSpan.Zero, 0, false);
        Assert.Equal(0, host.PrivateReads);

        rig.Time.AdvanceInSteps(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1));
        Assert.Equal(3, host.PrivateReads); // un par minute, personne ne regarde

        rig.Community.Consumption(Service); // la fenêtre regarde : un relevé tout de suite
        Assert.Equal(4, host.PrivateReads);
        rig.Time.AdvanceInSteps(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        rig.Community.Consumption(Service);
        Assert.Equal(6, host.PrivateReads); // puis un toutes les 5 s

        rig.Time.AdvanceInSteps(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)); // elle s'est tue : encore quelques relevés, puis la minute
        var settled = host.PrivateReads;
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(settled, host.PrivateReads);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(settled + 1, host.PrivateReads);
    }

    [WindowsHostFact]
    public async Task LastCaseClosed_TheRowAndTheMeasuresStop()
    {
        var rig = await InstalledAsync();
        var module = rig.Open("alex.demo");
        var host = await rig.Launcher.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        host.PrivateBytes = 40 * MiB;
        host.Usage = new JobUsage(TimeSpan.Zero, 0, false);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, rig.Community.Consumption(Service).Count);

        module.Dispose(); // la dernière case : l'hôte s'arrête
        await TestWait.UntilAsync(() => host.HasExited);
        var reads = host.PrivateReads;
        rig.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Single(rig.Community.Consumption(Service));
        Assert.Equal(reads, host.PrivateReads);
    }

    [WindowsHostFact]
    public async Task Disposed_NoMoreMeasures()
    {
        var rig = await InstalledAsync();
        var host = await StartHostAsync(rig);
        host.PrivateBytes = MiB;
        host.Usage = new JobUsage(TimeSpan.Zero, 0, false);
        await rig.Community.DisposeAsync();
        var reads = host.PrivateReads;

        rig.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(reads, host.PrivateReads);
    }
}
