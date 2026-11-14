using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Une case de module de la communauté dans une vue cachée : elle est ouverte dans l'hôte à sa vraie taille et tourne, mais l'hôte ne
/// la dessine pas ; ses images, si l'hôte en envoie quand même, sont gardées sans réveiller l'affichage.
/// </summary>
[Collection("Hosts")]
public sealed class RemoteModuleVisibilityTests : IAsyncLifetime
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

    private SupervisorRig FakeRig(FakeHostFactory factory)
    {
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SmallModule(), ModuleSandbox.Manifest(entry: "module.dll")), factory);
        _rigs.Add(rig);
        return rig;
    }

    private static ModuleRenderContext Place(SupervisorRig rig, int width = 96, int height = 48, int columns = 2, int rows = 1) =>
        new(SKRect.Create(10, 10, width, height), new GridSize(columns, rows), Themes.ClassiqueDefinition.Theme, 1f, rig.Time.GetUtcNow());

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.Pixels.All(pixel => pixel.Alpha == 0);

    [WindowsHostFact]
    public async Task HiddenCase_IsOpenedAtItsSizeWithoutBeingDrawn()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();

        // Aucun Render : la case est créée cachée, sa place vient de SetVisible.
        module.SetVisible(false, Place(rig, 120, 60, columns: 3));

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        var visibility = await host.WaitAsync<Visibility>();
        Assert.Equal((120, 60, 3, 1), (open.Width, open.Height, open.Columns, open.Rows));
        Assert.Equal((open.InstanceId, false), (visibility.InstanceId, visibility.Visible));
        Assert.Single(host.Of<Open>());
        Assert.False(module.IsAnimating);
        Assert.Equal(ModuleHealth.Online, module.Status.Health);
    }

    [WindowsHostFact]
    public async Task HiddenCase_ThenRenderedAtTheSameSize_DoesNotOpenTwice()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        module.SetVisible(false, Place(rig));
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        using (rig.Draw(module))
        {
        }

        await Task.Delay(100);
        Assert.Single(host.Of<Open>());
    }

    [WindowsHostFact]
    public async Task ShownCase_SendsVisibilityTrue_AndKeepsItsLastImageMeanwhile()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        await host.SendFrameAsync(open, 0, SKColors.Blue);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);

        module.SetVisible(false, Place(rig));
        module.SetVisible(false, Place(rig)); // sans changement : rien de plus n'est envoyé
        module.SetVisible(true, Place(rig));

        await TestWait.UntilAsync(() => host.Of<Visibility>().Count() == 2);
        await Task.Delay(100);
        Assert.Equal([false, true], host.Of<Visibility>().Select(v => v.Visible));
        Assert.All(host.Of<Visibility>(), v => Assert.Equal(open.InstanceId, v.InstanceId));
        Assert.Single(host.Of<Open>());

        // Entre-temps comme après, la dernière image reste celle de la case.
        using var bitmap = rig.Draw(module);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(10 + 5, 10 + 5));
    }

    [WindowsHostFact]
    public async Task HiddenCase_FramesAreKeptButDoNotInvalidate()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        var invalidated = 0;
        module.Invalidated += (_, _) => Interlocked.Increment(ref invalidated);

        module.SetVisible(false, Place(rig));
        await host.SendFrameAsync(open, 0, SKColors.Red, animating: true);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);
        await host.WaitAsync<FrameDone>();
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref invalidated));
        Assert.False(module.IsAnimating); // cachée, elle ne tient pas l'écran éveillé même si l'hôte la disait animée

        // Quand la case revient, l'image gardée est là tout de suite ; les images suivantes réveillent de nouveau l'affichage.
        module.SetVisible(true, Place(rig));
        using (var shown = rig.Draw(module))
        {
            Assert.Equal(SKColors.Red, shown.GetPixel(10 + 5, 10 + 5));
        }

        Assert.True(module.IsAnimating);
        await host.SendFrameAsync(open, 1, SKColors.Green);
        await TestWait.UntilAsync(() => Volatile.Read(ref invalidated) == 1);
        using var next = rig.Draw(module);
        Assert.Equal(SKColors.Green, next.GetPixel(10 + 5, 10 + 5));
    }

    [WindowsHostFact]
    public async Task HiddenCase_ReopenedAtAnotherSize_StaysHiddenInTheHost()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        module.SetVisible(false, Place(rig));
        var host = await factory.WaitForStartAsync();
        var first = await host.WaitAsync<Open>();

        module.SetVisible(false, Place(rig, 120, 60, columns: 3));

        var second = await host.WaitAsync<Open>(open => open.InstanceId != first.InstanceId);
        await host.WaitAsync<Visibility>(v => v.InstanceId == second.InstanceId && !v.Visible);
    }

    [WindowsHostFact]
    public async Task HiddenCase_WhenTheHostRestarts_IsReopenedHidden()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        module.SetVisible(false, Place(rig));
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Visibility>();

        host.Exit(1);
        await TestWait.UntilAsync(() => module.Status.Health == ModuleHealth.Offline); // l'arrêt est compté, la relance programmée
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        var second = await factory.WaitForStartAsync(2);

        var open = await second.WaitAsync<Open>();
        var visibility = await second.WaitAsync<Visibility>();
        Assert.Equal((open.InstanceId, false), (visibility.InstanceId, visibility.Visible));
    }

    [WindowsHostFact]
    public async Task Dispose_WhileHidden_ThenSetVisible_DoesNothing()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        module.SetVisible(false, Place(rig));
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        module.Dispose();
        module.SetVisible(true, Place(rig));

        await Task.Delay(100);
        Assert.Single(host.Of<Open>());
        Assert.Single(host.Of<Visibility>());
        using var bitmap = rig.Draw(module);
        Assert.True(IsEmpty(bitmap));
    }

    [WindowsHostFact]
    public async Task HiddenCase_IsNeverReportedAsNotResponding_UntilItIsShownWithoutAFrame()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        var statusChanged = 0;
        module.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanged);
        module.SetVisible(false, Place(rig));
        var host = await factory.WaitForStartAsync();
        await host.WaitAsync<Open>();

        // L'hôte ne dessine pas une case cachée : aucune image en 10 s n'est normal, y compris après un changement d'apparence.
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        rig.Supervisor.SetAppearance(ModuleSandbox.Json("""{"theme":"light"}"""));
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ModuleHealth.Online, module.Status.Health);
        Assert.Equal(0, Volatile.Read(ref statusChanged));

        // Elle revient sans qu'aucune image soit arrivée : l'attente reprend, et le délai court de nouveau.
        module.SetVisible(true, Place(rig));
        rig.Time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(ModuleHealth.Online, module.Status.Health);
        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(new ModuleStatus(ModuleHealth.Offline, RemoteModule.NotRespondingText), module.Status);
        Assert.Equal(1, Volatile.Read(ref statusChanged));
    }

    [WindowsHostFact]
    public async Task ShownCase_HiddenBeforeTheTimeout_StopsWaiting_AndAFrameWhileHiddenEndsTheWait()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        module.SetVisible(false, Place(rig));
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ModuleHealth.Online, module.Status.Health);

        // Une image arrivée pendant que la case est cachée finit l'attente : revenue, elle ne se dit jamais en panne.
        await host.SendFrameAsync(open, 0, SKColors.Red);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);
        module.SetVisible(true, Place(rig));
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ModuleHealth.Online, module.Status.Health);
    }
}
