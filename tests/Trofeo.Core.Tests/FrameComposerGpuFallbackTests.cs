using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Core.Tests;

/// <summary>
/// Le compositeur sur la carte se replie sur le processeur quand la carte est perdue, sans jamais perdre l'image. La carte est
/// ici un <see cref="FakeGpuDevice"/> (surfaces du processeur) : ces tests tournent aussi sous Linux.
/// </summary>
public class FrameComposerGpuFallbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static FrameComposer NewComposer(IGpuDevice? gpu) =>
        new(new RateLimitedErrorLog(TimeProvider.System, _ => { }), gpu: gpu);

    /// <summary>Un module qui dessine des formes et du texte, pour que la scène ne soit pas triviale.</summary>
    private static Dictionary<Guid, IModule> Modules(IReadOnlyList<ModulePlacement> placements) =>
        placements.ToDictionary(p => p.InstanceId, _ => (IModule)new FakeModule((canvas, ctx) =>
        {
            var b = ctx.Bounds;
            using var disc = new SKPaint { Color = ctx.Theme.Accent, IsAntialias = true };
            canvas.DrawCircle(b.MidX, b.MidY, Math.Min(b.Width, b.Height) / 4f, disc);
            using var font = ModuleDrawing.Font(ctx.Theme, 24f);
            using var text = new SKPaint { Color = ctx.Theme.Text, IsAntialias = true };
            canvas.DrawText("Ashboard", b.Left + 12, b.Top + 40, SKTextAlign.Left, font, text);
        }));

    private static SKBitmap Compose(FrameComposer composer, ThemeDefinition theme, SKColor background)
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        using var bg = new ColorBackground(background);
        return composer.Compose(placements, Modules(placements), theme, bg, Now);
    }

    private static void AssertSamePixels(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Info, actual.Info);
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "Les deux images ne sont pas identiques au pixel près.");
    }

    [Fact]
    public void LostMidFrame_StillReturnsTheCpuFrame()
    {
        using var reference = NewComposer(null);
        using var expected = Compose(reference, Themes.LiquidGlass, SKColors.DarkSlateBlue);

        var device = new FakeGpuDevice();
        device.LoseAfter(1); // la surface de l'image est créée, celle du flou du verre est refusée : la carte tombe en pleine image
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;
        Assert.True(composer.UsesGpu);

        using var actual = Compose(composer, Themes.LiquidGlass, SKColors.DarkSlateBlue);

        AssertSamePixels(expected, actual);
        Assert.Single(reasons);
        Assert.False(string.IsNullOrWhiteSpace(reasons[0]));
        Assert.False(composer.UsesGpu);
    }

    [Fact]
    public void LostOnSubmit_StillReturnsTheCpuFrame()
    {
        using var reference = NewComposer(null);
        using var expected = Compose(reference, Themes.ClassiqueDefinition, SKColors.DarkSlateBlue);

        var device = new FakeGpuDevice { LoseOnSubmit = true }; // le pilote plante en silence : seul IsLost le dit
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;

        using var actual = Compose(composer, Themes.ClassiqueDefinition, SKColors.DarkSlateBlue);

        AssertSamePixels(expected, actual);
        Assert.Single(reasons);
        Assert.False(composer.UsesGpu);
    }

    [Fact]
    public void CreateRefused_FallsBackToTheCpu()
    {
        using var reference = NewComposer(null);
        using var expected = Compose(reference, Themes.Pixel, SKColors.DarkSlateBlue);

        var device = new FakeGpuDevice { FailCreate = true };
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;

        using var actual = Compose(composer, Themes.Pixel, SKColors.DarkSlateBlue);

        AssertSamePixels(expected, actual);
        Assert.Single(reasons);
        Assert.False(composer.UsesGpu);
    }

    [Fact]
    public void AfterFailure_StaysOnCpu()
    {
        var device = new FakeGpuDevice();
        device.LoseAfter(1);
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;
        Compose(composer, Themes.LiquidGlass, SKColors.DarkSlateBlue).Dispose();
        int requests = device.SurfaceRequests, submits = device.Submits;

        for (int i = 0; i < 3; i++)
        {
            Compose(composer, Themes.LiquidGlass, SKColors.DarkSlateBlue).Dispose();
            Compose(composer, Themes.Pixel, SKColors.DarkSlateBlue).Dispose();
        }

        Assert.Equal(requests, device.SurfaceRequests);
        Assert.Equal(submits, device.Submits);
        Assert.Single(reasons); // GpuFailed n'est levé qu'une fois
    }

    [Fact]
    public void WorkingDevice_GivesTheSameFrameAsTheCpu()
    {
        // Les surfaces du faux appareil sont celles du processeur : l'image doit être identique au pixel près, pour chaque thème.
        foreach (var theme in new[] { Themes.ClassiqueDefinition, Themes.LiquidGlass, Themes.Pixel })
        {
            using var reference = NewComposer(null);
            using var expected = Compose(reference, theme, SKColors.DarkSlateBlue);

            var device = new FakeGpuDevice();
            using var composer = NewComposer(device);
            composer.GpuFailed += reason => Assert.Fail(reason);
            using var actual = Compose(composer, theme, SKColors.DarkSlateBlue);

            AssertSamePixels(expected, actual);
            Assert.True(composer.UsesGpu);
            Assert.True(device.SurfacesCreated >= 1, $"Le thème {theme.Theme.Id} n'a pas dessiné sur l'appareil.");
        }
    }

    [Fact]
    public void AdvancedGlass_StaysOnTheCpuUntilTask4()
    {
        using var reference = NewComposer(null);
        using var expected = Compose(reference, Themes.LiquidGlassAdvanced, SKColors.DarkSlateBlue);

        var device = new FakeGpuDevice { FailCreate = true }; // le moindre recours à l'appareil serait une panne
        using var composer = NewComposer(device);
        composer.GpuFailed += reason => Assert.Fail(reason);

        using var actual = Compose(composer, Themes.LiquidGlassAdvanced, SKColors.DarkSlateBlue);

        AssertSamePixels(expected, actual);
        Assert.True(composer.UsesGpu);
        Assert.Equal(0, device.SurfaceRequests);
    }

    [Fact]
    public void NoDevice_DoesNotUseTheGpu()
    {
        using var composer = NewComposer(null);

        Assert.False(composer.UsesGpu);
    }
}
