using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Core.Tests;

/// <summary>
/// Les couches du verre avancé calculées « sur la carte » (plan 10, tâche 4). L'appareil est un <see cref="FakeGpuDevice"/> : ses
/// surfaces sont celles du processeur, si bien que ces tests tournent aussi sous Linux et que l'image doit être identique au pixel
/// près à celle du processeur. Les écarts réels de la carte (WARP) sont mesurés dans <c>GpuGlassTests</c> de Display.Tests.
/// </summary>
public class LiquidGlassLayersGpuTests
{
    private static FrameComposer NewComposer(IGpuDevice? gpu, bool backgroundGlass = false) =>
        new(new RateLimitedErrorLog(TimeProvider.System, _ => { }), backgroundGlass, gpu);

    private static IModule Tabs(int chosen) => new FakeModule((canvas, ctx) =>
    {
        var b = ctx.Bounds;
        using var font = ModuleDrawing.Font(ctx.Theme, 22f);
        using var text = new SKPaint { Color = ctx.Theme.Text, IsAntialias = true };
        for (int i = 0; i < 3; i++)
        {
            var tab = SKRect.Create(b.Left + 16, b.Top + 14 + i * 56, 240, 46);
            if (i == chosen) ModuleDrawing.DrawSelection(canvas, tab, ctx);
            canvas.DrawText($"projet-{i}", tab.Left + 20, tab.MidY + 7, SKTextAlign.Left, font, text);
        }
    });

    private static IReadOnlyDictionary<Guid, IModule> For(ModulePlacement placement, IModule module) =>
        new Dictionary<Guid, IModule> { [placement.InstanceId] = module };

    [Fact]
    public void Pills_OnTheGpu_GiveTheCpuFrame_AndAreRecomputedEveryFrame()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var reference = NewComposer(null);
        var device = new FakeGpuDevice();
        using var composer = NewComposer(device);
        composer.GpuFailed += reason => Assert.Fail(reason);

        for (int frame = 0; frame < 3; frame++)
        {
            var modules = For(placement, Tabs(frame == 2 ? 1 : 0));
            using var expected = reference.Compose([placement], modules, theme, background, GlassTesting.T0);
            using var actual = composer.Compose([placement], modules, theme, background, GlassTesting.T0);

            Assert.True(GlassTesting.SameBytes(expected, actual), $"image {frame}");
        }

        Assert.True(composer.GlassOnGpu);
        Assert.Equal(2, reference.GlassPillComputations); // le processeur garde sa pastille tant que la case ne change pas
        Assert.Equal(3, composer.GlassPillComputations); // la carte la refait à chaque image
        Assert.Equal(1, composer.GlassComputations); // mais le verre de la case, lui, est gardé
    }

    [Fact]
    public void OnTheGpu_NoBackgroundWorker_EvenWhenAskedFor()
    {
        var placements = GlassTesting.TwoCases();
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        var device = new FakeGpuDevice();
        using var composer = NewComposer(device, backgroundGlass: true);
        int ready = 0;
        composer.GlassReady += () => Interlocked.Increment(ref ready);

        using var frame = composer.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0);

        Assert.True(composer.GlassOnGpu);
        Assert.False(composer.Glass!.IsWorking);
        Assert.Equal(2, composer.GlassComputations); // calculé pendant Compose, pas en attendant un fil
        Assert.Equal(2, composer.GlassLayers);
        Assert.Equal(0, ready);
        Assert.Equal(0, composer.Glass.WorkerFailures);

        // Le verre est déjà là : l'image est celle du verre avancé, pas celle du verre simple d'attente.
        using var reference = NewComposer(null);
        using var expected = reference.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0);
        Assert.True(GlassTesting.SameBytes(expected, frame));
    }

    [Fact]
    public void Layers_AreReleasedWithTheComposerAndOnThemeChange()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        var device = new FakeGpuDevice();
        var composer = NewComposer(device);

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, GlassTesting.T0).Dispose();
        var layers = composer.Glass!;
        Assert.True(layers.LiveImages > 0);

        composer.Compose(placements, GlassTesting.NoModules, Themes.ClassiqueDefinition, background, GlassTesting.T0).Dispose();
        Assert.Null(composer.Glass);
        Assert.Equal(0, layers.LiveImages); // les images de la carte du thème quitté sont libérées

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, GlassTesting.T0).Dispose();
        var again = composer.Glass!;
        Assert.True(again.LiveImages > 0);
        composer.Dispose();
        Assert.Equal(0, again.LiveImages);
    }

    [Fact]
    public void PillOverloads_RefuseTheOtherSide()
    {
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        var theme = GlassTesting.Advanced();
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var pixels = bitmap.PeekPixels();
        using var canvas = new SKCanvas(bitmap);
        var caseRect = SKRect.Create(20, 20, 400, 200);
        var pill = SKRect.Create(40, 60, 200, 46);

        using var cpu = new LiquidGlassLayers();
        cpu.BeginFrame(background, theme);
        using var cpuSurface = SKSurface.Create(new SKImageInfo(GridGeometry.Width, GridGeometry.Height));
        Assert.Throws<InvalidOperationException>(() => cpu.PaintPill(cpuSurface.Canvas, cpuSurface, caseRect, pill));

        var device = new FakeGpuDevice();
        using var gpu = new LiquidGlassLayers(gpu: device);
        gpu.BeginFrame(background, theme);
        Assert.Throws<InvalidOperationException>(() => gpu.PaintPill(canvas, pixels, caseRect, pill));
    }
}
