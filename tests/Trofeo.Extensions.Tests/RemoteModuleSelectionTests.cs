using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Les pastilles de l'élément choisi d'un module de la communauté (plan 9, tâche 5) : l'hôte note les rectangles de
/// <c>DrawSelection</c> et les envoie avec l'image ; le service les valide (entrée hostile), les garde avec l'image, et les
/// transmet au compositeur avant de poser l'image, comme un module natif qui appelle <c>DrawSelection</c>.
/// </summary>
[Collection("Hosts")]
public sealed class RemoteModuleSelectionTests : IAsyncLifetime
{
    private sealed class Sink : ISelectionSink
    {
        public List<SKRect> Rects { get; } = [];

        public List<float> Opacities { get; } = [];

        public void Add(SKRect caseLocalRect, float opacity = 1f)
        {
            Rects.Add(caseLocalRect);
            Opacities.Add(opacity);
        }
    }

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

    /// <summary>Une case de 96 × 48 ouverte dans un faux hôte.</summary>
    private async Task<(SupervisorRig Rig, RemoteModule Module, FakeHostProcess Host, Open Open)> OpenedAsync()
    {
        var factory = new FakeHostFactory();
        var rig = FakeRig(factory);
        var module = rig.Open();
        using (rig.Draw(module))
        {
        }

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        await TestWait.UntilAsync(() => module.Slot is not null);
        return (rig, module, host, open);
    }

    private static List<SKRect> SelectionsOf(SupervisorRig rig, RemoteModule module)
    {
        var sink = new Sink();
        using var bitmap = rig.Draw(module, sink: sink);
        return sink.Rects;
    }

    // ---- La validation (entrée hostile) ----

    [WindowsHostFact]
    public async Task RemoteModule_ForwardsValidatedSelections()
    {
        var (rig, module, _, _) = await OpenedAsync();
        var valid = new SelectionRect(10, 10, 20, 8);
        var spilling = new SelectionRect(-10, -10, 200, 100); // déborde de tous les côtés
        var clippedOnTheRight = new SelectionRect(80, 40, 50, 50);
        var manyMore = Enumerable.Range(0, 20).Select(i => new SelectionRect(i, 2, 4, 4)).ToArray();

        IReadOnlyList<SelectionRect> sent =
        [
            new(float.NaN, 5, 10, 10),
            new(5, float.NaN, 10, 10),
            new(5, 5, float.NaN, 10),
            new(5, 5, 10, float.NaN),
            new(float.PositiveInfinity, 5, 10, 10),
            new(5, 5, float.PositiveInfinity, 10),
            new(5, 5, 10, float.NegativeInfinity),
            new(5, 5, -10, 10), // largeur négative
            new(5, 5, 10, 0), // hauteur nulle
            new(200, 0, 10, 10), // tout entier à droite de l'image
            new(0, -30, 10, 20), // tout entier au-dessus
            new(float.MaxValue, 0, float.MaxValue, 10), // une somme qui déborde
            null!,
            valid,
            spilling,
            clippedOnTheRight,
            .. manyMore,
        ];

        using var image = new SKBitmap(new SKImageInfo(96, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.True(module.Publish(module.Slot!, image.Copy(), false, sent));

        var rects = SelectionsOf(rig, module);

        // Seuls les rectangles valides, coupés à l'image ; les 16 premiers au plus.
        Assert.Equal(Frame.MaxSelections, rects.Count);
        Assert.Equal(new SKRect(10, 10, 30, 18), rects[0]);
        Assert.Equal(new SKRect(0, 0, 96, 48), rects[1]);
        Assert.Equal(new SKRect(80, 40, 96, 48), rects[2]);
        for (var i = 0; i < 13; i++)
        {
            Assert.Equal(new SKRect(i, 2, i + 4, 6), rects[3 + i]);
        }
    }

    [WindowsHostFact]
    public async Task RemoteModule_NewFrameWithoutSelections_ForgetsTheOldOnes()
    {
        var (rig, module, host, open) = await OpenedAsync();
        await host.SendFrameAsync(open, 0, SKColors.Red, selections: [new SelectionRect(4, 4, 30, 10)]);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);
        Assert.Equal([new SKRect(4, 4, 34, 14)], SelectionsOf(rig, module));

        await host.SendFrameAsync(open, 1, SKColors.Red); // un hôte plus ancien, ou un module sans élément choisi
        await TestWait.UntilAsync(() => module.FramesReceived == 2);
        Assert.Empty(SelectionsOf(rig, module));
    }

    [WindowsHostFact]
    public async Task RemoteModule_SelectionsArriveBeforeTheImageIsDrawn_AndOnlyWithASink()
    {
        var (rig, module, host, open) = await OpenedAsync();
        await host.SendFrameAsync(open, 0, SKColors.Red, selections: [new SelectionRect(4, 4, 30, 10)]);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);

        // Sans puits (tous les autres thèmes) : l'image est posée comme avant, et rien d'autre.
        using var without = rig.Draw(module);
        Assert.Equal(SKColors.Red, without.GetPixel(10 + 5, 10 + 5));

        // Avec un puits : les rectangles sont notés avant que l'image soit posée (rien n'est encore peint sur la case à ce moment-là).
        using var bitmap = new SKBitmap(116, 68);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var paintedWhenNoted = new List<byte>();
        var sink = new CallbackSink(() => paintedWhenNoted.Add(bitmap.GetPixel(10 + 5, 10 + 5).Alpha));
        module.Render(canvas, new ModuleRenderContext(
            SKRect.Create(10, 10, 96, 48), new GridSize(2, 1), Themes.LiquidGlassAdvanced.Theme, 1f, rig.Time.GetUtcNow()) { Selections = sink });
        Assert.Equal([(byte)0], paintedWhenNoted);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(10 + 5, 10 + 5)); // l'image est posée ensuite
    }

    private sealed class CallbackSink(Action onAdd) : ISelectionSink
    {
        public void Add(SKRect caseLocalRect, float opacity = 1f) => onAdd();
    }

    // ---- La chaîne complète ----

    [WindowsHostFact]
    public async Task RemoteModule_ForwardsSelectionsSentByTheHost()
    {
        var (rig, module, host, open) = await OpenedAsync();

        await host.SendFrameAsync(open, 0, SKColors.Green, selections: [new SelectionRect(4, 6, 40, 12), new SelectionRect(50, 20, 100, 100)]);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);

        var online = new Sink();
        using (rig.Draw(module, sink: online))
        {
        }

        Assert.Equal([new SKRect(4, 6, 44, 18), new SKRect(50, 20, 96, 48)], online.Rects);
        Assert.Equal([1f, 1f], online.Opacities); // en ligne : des pastilles pleines
    }

    [WindowsHostFact]
    public async Task RemoteModule_Offline_StillForwardsLastSelections()
    {
        var (rig, module, host, open) = await OpenedAsync();
        await host.SendFrameAsync(open, 0, SKColors.Red, selections: [new SelectionRect(4, 6, 40, 12)]);
        await TestWait.UntilAsync(() => module.FramesReceived == 1);

        host.Exit(1);
        await TestWait.UntilAsync(() => module.Status == new ModuleStatus(ModuleHealth.Offline, ModuleHostSupervisor.HostStoppedText));

        // La dernière image reste (voilée) avec ses pastilles, voilées du même alpha ; la case ne peut plus en changer.
        var sink = new Sink();
        using (rig.Draw(module, sink: sink))
        {
        }

        Assert.Equal([new SKRect(4, 6, 44, 18)], sink.Rects);
        Assert.Equal([RemoteModule.OfflineVeilAlpha / 255f], sink.Opacities);
        Assert.Equal([new SKRect(4, 6, 44, 18)], SelectionsOf(rig, module));
        using var bitmap = rig.Draw(module);
        var veiled = bitmap.GetPixel(10 + 5, 10 + 5);
        Assert.True(veiled.Alpha is > 0 and < 255, $"L'image devrait rester, voilée : {veiled}");
    }

    [WindowsHostFact]
    public async Task RemoteModule_ImageNotYetReceived_ForwardsNothing()
    {
        var (rig, module, _, _) = await OpenedAsync();

        Assert.Empty(SelectionsOf(rig, module));
    }
}
