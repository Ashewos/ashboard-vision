using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Core.Tests;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

/// <summary>
/// La composition de l'image sur la carte (WARP) donne la même image que sur le processeur, à la tolérance près :
/// 99,9 % des pixels à 4/255 au plus par couleur, écart moyen sous 0,5/255.
/// </summary>
public class GpuCompositionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("trofeo-gpu-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FrameComposer NewComposer(IGpuDevice? gpu) =>
        new(new RateLimitedErrorLog(TimeProvider.System, _ => { }), gpu: gpu);

    /// <summary>Des modules de la disposition par défaut : des formes, des traits et du texte (la partie la plus sensible à la carte).</summary>
    private static (IReadOnlyList<ModulePlacement> Placements, Dictionary<Guid, IModule> Modules) Scene()
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        var colors = new[] { SKColors.Red, SKColors.Orange, SKColors.DodgerBlue, SKColors.Green, SKColors.Purple };
        var modules = placements.Select((p, i) => (p.InstanceId, Module: (IModule)new FakeModule((canvas, ctx) =>
        {
            var b = ctx.Bounds;
            using var fill = new SKPaint { Color = colors[i % colors.Length].WithAlpha(120), IsAntialias = true };
            canvas.DrawRect(SKRect.Create(b.Left + 12, b.Top + 12, b.Width / 2, b.Height / 3), fill);

            // Une jauge à bouts ronds et un point d'état : ce que les modules dessinent, bords anticrénelés compris.
            using var bar = new SKPaint { Color = ctx.Theme.Accent, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(b.Left + 16, b.Bottom - 40, b.Width / 2, 10), 5, 5, bar);
            canvas.DrawCircle(b.Right - 24, b.Top + 24, 8, bar);

            using var font = ModuleDrawing.Font(ctx.Theme, 28f);
            using var text = new SKPaint { Color = ctx.Theme.Text, IsAntialias = true };
            canvas.DrawText($"Ashboard {i} 12:34", b.Left + 16, b.Top + 44, SKTextAlign.Left, font, text);
            using var small = ModuleDrawing.Font(ctx.Theme, 14f);
            canvas.DrawText("CPU 42 %  RAM 8,1 Go", b.Left + 16, b.Bottom - 16, SKTextAlign.Left, small, text);
        }))).ToDictionary(x => x.InstanceId, x => x.Module);
        return (placements, modules);
    }

    /// <summary>La part des pixels à 4/255 au plus qu'on exige (écart moyen sous 0,5/255 dans tous les cas).</summary>
    private const double SharpShare = 0.999;

    /// <summary>
    /// Pour le verre et le glissement : la carte et le processeur ne bordent pas les angles arrondis (rognage anticrénelé du verre)
    /// ni les flous exactement de la même façon. Mesuré sur WARP : Liquid Glass 99,82 %, glissement de 99,50 % (à 240 ms) à
    /// 99,79 % (à 150 ms), tous à 22/255 au plus sauf les angles du verre (44/255), sur quelques centaines de pixels de bord.
    /// L'écart moyen, lui, reste sous 0,5/255.
    /// </summary>
    private const double BlurredShare = 0.99;

    private static void AssertClose(SKBitmap cpu, SKBitmap gpu, string what, double minShare = SharpShare)
    {
        var (mean, share, max) = ImageDiff.Compare(cpu, gpu);
        Assert.True(
            mean < 0.5 && share >= minShare,
            $"{what} : écart moyen {mean:F3}/255, {share:P3} des pixels à 4/255 au plus (au moins {minShare:P1} attendus), écart maximal {max}/255.");
    }

    private static void AssertMatchesCpu(ThemeDefinition theme, IBackground background)
    {
        var (placements, modules) = Scene();
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var gpuComposer = NewComposer(device);
        gpuComposer.GpuFailed += reason => Assert.Fail(reason);

        using var cpu = cpuComposer.Compose(placements, modules, theme, background, Now);
        using var gpu = gpuComposer.Compose(placements, modules, theme, background, Now);

        Assert.True(gpuComposer.UsesGpu);
        Assert.Equal(cpu.Info, gpu.Info);
        AssertClose(cpu, gpu, theme.Theme.Id, theme.Panel.Kind == PanelKind.Glass ? BlurredShare : SharpShare);
    }

    [WarpFact]
    public void Classique_MatchesCpu()
    {
        using var aurora = new AuroraBackground();
        AssertMatchesCpu(Themes.ClassiqueDefinition, aurora);
    }

    [WarpFact]
    public void LiquidGlass_MatchesCpu()
    {
        using var aurora = new AuroraBackground();
        AssertMatchesCpu(Themes.LiquidGlass, aurora);
    }

    [WarpFact]
    public void Pixel_MatchesCpu()
    {
        using var aurora = new AuroraBackground();
        AssertMatchesCpu(Themes.Pixel, aurora);
    }

    [WarpFact]
    public void AnimatedBackground_MatchesCpu()
    {
        // Aurore, puis un GIF à son image 3 (les trois images : rouge, vert, bleu).
        using var aurora = new AuroraBackground();
        var path = Path.Combine(_dir, "rgb.gif");
        TinyGif.Write(path, [SKColors.Red, SKColors.Green, SKColors.Blue], [20, 20, 20]);
        using var gif = AnimatedBackground.TryLoad(path, null)!;
        Assert.NotNull(gif);
        Assert.True(gif.Advance(TimeSpan.FromMilliseconds(450)));
        Assert.Equal(SKColors.Blue, Pixel(gif.Current, 960, 231));

        foreach (var theme in new[] { Themes.ClassiqueDefinition, Themes.LiquidGlass, Themes.Pixel })
        {
            AssertMatchesCpu(theme, aurora);
            AssertMatchesCpu(theme, gif);
        }
    }

    [WarpFact]
    public void Glide_MatchesCpu()
    {
        var (placements, modules) = Scene();
        using var aurora = new AuroraBackground();
        using var composer = NewComposer(null);
        using var from = composer.Compose(placements, modules, Themes.ClassiqueDefinition, aurora, Now);
        using var to = composer.Compose(placements, modules, Themes.Pixel, aurora, Now);
        var glide = new ViewTransition(from, 1, ViewTransition.Duration);
        using var device = GpuTestSupport.Warp();

        foreach (var elapsed in new[] { 0, 60, 150, 240, 300 })
        {
            using var cpu = glide.Compose(to, TimeSpan.FromMilliseconds(elapsed));
            using var gpu = glide.Compose(to, TimeSpan.FromMilliseconds(elapsed), device);
            Assert.Equal(cpu.Info, gpu.Info);
            AssertClose(cpu, gpu, $"glissement à {elapsed} ms", BlurredShare);
        }

        Assert.False(device.IsLost);
    }

    [Fact]
    public void Glide_OnALostDevice_StillGivesTheCpuFrame()
    {
        // Sans Direct3D 12 non plus : le faux appareil perdu suffit, ses surfaces sont celles du processeur.
        var (placements, modules) = Scene();
        using var background = new ColorBackground(SKColors.DarkSlateBlue);
        using var composer = NewComposer(null);
        using var from = composer.Compose(placements, modules, Themes.ClassiqueDefinition, background, Now);
        using var to = composer.Compose(placements, modules, Themes.Pixel, background, Now);
        var glide = new ViewTransition(from, -1, ViewTransition.Duration);
        var lost = new FakeGpuDevice();
        lost.LoseAfter(0);

        using var expected = glide.Compose(to, TimeSpan.FromMilliseconds(150));
        using var actual = glide.Compose(to, TimeSpan.FromMilliseconds(150), lost);

        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [WarpFact]
    public void RemovedDevice_FallsBackToTheCpuWithoutLosingTheFrame()
    {
        // Le pilote plante ou est mis à jour en cours de route : la vraie panne, sur WARP (RemoveDevice).
        var (placements, modules) = Scene();
        using var background = new ColorBackground(SKColors.DarkSlateBlue);
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;

        composer.Compose(placements, modules, Themes.Pixel, background, Now).Dispose(); // une image sur la carte, qui marche
        Assert.True(composer.UsesGpu);
        using (var device5 = device.Device.QueryInterface<Vortice.Direct3D12.ID3D12Device5>())
        {
            device5.RemoveDevice();
        }

        using var expected = cpuComposer.Compose(placements, modules, Themes.Pixel, background, Now);
        using var actual = composer.Compose(placements, modules, Themes.Pixel, background, Now);

        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "L'image de repli n'est pas celle du processeur.");
        Assert.False(composer.UsesGpu);
        Assert.Single(reasons);
        Assert.Contains("retirée", reasons[0]);

        // Le glissement sort aussi, par le processeur, sur la même carte perdue.
        var glide = new ViewTransition(expected, 1, ViewTransition.Duration);
        using var glideCpu = glide.Compose(actual, TimeSpan.FromMilliseconds(150));
        using var glideLost = glide.Compose(actual, TimeSpan.FromMilliseconds(150), device);
        Assert.True(glideCpu.GetPixelSpan().SequenceEqual(glideLost.GetPixelSpan()));
    }

    [WarpFact]
    public void Cache_ReturnsToItsLevel()
    {
        // Un fond qui change à chaque image envoie une nouvelle texture à la carte : le cache de Skia doit se plafonner.
        var (placements, modules) = Scene();
        using var device = GpuTestSupport.Warp();
        using var composer = NewComposer(device);
        using var background = new ChangingBackground();

        void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                background.Next();
                composer.Compose(placements, modules, Themes.LiquidGlass, background, Now + TimeSpan.FromMilliseconds(i * 100)).Dispose();
            }
        }

        Frames(50);
        int afterFirst = device.CacheResources;
        long bytesFirst = device.CacheBytes;
        Frames(50);

        Assert.True(composer.UsesGpu);
        Assert.True(device.CacheResources <= afterFirst, $"Le cache a grandi : {afterFirst} ressources, puis {device.CacheResources}.");
        Assert.True(device.CacheBytes <= bytesFirst * 11 / 10, $"Le cache a grandi : {bytesFirst} octets, puis {device.CacheBytes}.");
    }

    private static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>Un fond animé dont chaque image est neuve (comme une vidéo ou un GIF) : un dégradé qui glisse.</summary>
    private sealed class ChangingBackground : IBackground
    {
        private SKImage _image = Make(0);
        private int _step;

        public bool IsAnimated => true;
        public SKImage Current => _image;
        public int Version { get; private set; } = 1;
        public TimeSpan? UntilNextFrame => TimeSpan.FromMilliseconds(40);
        public bool Advance(TimeSpan elapsed) => false;

        public void Next()
        {
            var old = _image;
            _image = Make(++_step);
            old.Dispose();
            Version++;
        }

        public void Dispose() => _image.Dispose();

        private static SKImage Make(int step)
        {
            using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(step * 17 % GridGeometry.Width, 0), new SKPoint(GridGeometry.Width, GridGeometry.Height),
                    [SKColors.DarkBlue, SKColors.Orange, SKColors.Teal], SKShaderTileMode.Mirror),
            })
            {
                canvas.DrawPaint(paint);
            }

            bitmap.SetImmutable();
            return SKImage.FromBitmap(bitmap);
        }
    }
}
