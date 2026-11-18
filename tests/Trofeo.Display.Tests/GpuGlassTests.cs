using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using Xunit.Abstractions;

namespace Trofeo.Display.Tests;

/// <summary>
/// Le verre de « Liquid Glass avancé » et ses pastilles, calculés sur la carte (WARP), donnent la même image que sur le processeur :
/// 99,9 % des pixels à 4/255 au plus par couleur, écart moyen sous 0,5/255 (face au compositeur sans appareil, synchrone).
/// </summary>
[Collection(GpuTestSupport.CollectionName)]
public class GpuGlassTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static FrameComposer NewComposer(IGpuDevice? gpu) =>
        new(new RateLimitedErrorLog(TimeProvider.System, _ => { }), backgroundGlass: false, gpu: gpu);

    private static ThemeDefinition Advanced(string preset)
    {
        var theme = Themes.LiquidGlassAdvanced;
        return theme with { Panel = theme.Panel with { Glass = GlassPresets.Find(preset)! } };
    }

    private static ModulePlacement Place(int column, int row, int columns, int rows) =>
        new(Guid.NewGuid(), "fake", column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>Les modules de la disposition par défaut : des formes, des traits et du texte, comme dans les tests du compositeur.</summary>
    private static (IReadOnlyList<ModulePlacement> Placements, Dictionary<Guid, IModule> Modules) DefaultScene()
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        var colors = new[] { SKColors.Red, SKColors.Orange, SKColors.DodgerBlue, SKColors.Green, SKColors.Purple };
        var modules = placements.Select((p, i) => (p.InstanceId, Module: (IModule)new FakeModule((canvas, ctx) =>
        {
            var b = ctx.Bounds;
            using var fill = new SKPaint { Color = colors[i % colors.Length].WithAlpha(120), IsAntialias = true };
            canvas.DrawRect(SKRect.Create(b.Left + 12, b.Top + 12, b.Width / 2, b.Height / 3), fill);
            using var bar = new SKPaint { Color = ctx.Theme.Accent, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(b.Left + 16, b.Bottom - 40, b.Width / 2, 10), 5, 5, bar);
            canvas.DrawCircle(b.Right - 24, b.Top + 24, 8, bar);
            using var font = ModuleDrawing.Font(ctx.Theme, 28f);
            using var text = new SKPaint { Color = ctx.Theme.Text, IsAntialias = true };
            canvas.DrawText($"Ashboard {i} 12:34", b.Left + 16, b.Top + 44, SKTextAlign.Left, font, text);
        }))).ToDictionary(x => x.InstanceId, x => x.Module);
        return (placements, modules);
    }

    /// <summary>
    /// Un module qui ressemble à Claude Code : une colonne d'onglets, dont l'onglet choisi est marqué par
    /// <see cref="ModuleDrawing.DrawSelection"/> (le service en fait la pastille de verre), et du texte à côté.
    /// </summary>
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
        canvas.DrawText("Écrire les tests", b.Left + 290, b.Top + 60, SKTextAlign.Left, font, text);
    });

    /// <summary>Ce que <see cref="SharpShare"/> exige : 99,9 % des pixels à 4/255 au plus (écart moyen sous 0,5/255 dans tous les cas).</summary>
    private const double SharpShare = 0.999;

    private void AssertClose(SKBitmap cpu, SKBitmap gpu, string what, double minShare = SharpShare)
    {
        var (mean, share, max) = ImageDiff.Compare(cpu, gpu);
        output.WriteLine($"{what} : écart moyen {mean:F3}/255, {share:P3} des pixels à 4/255 au plus, écart maximal {max}/255.");
        Assert.True(
            mean < 0.5 && share >= minShare,
            $"{what} : écart moyen {mean:F3}/255, {share:P3} des pixels à 4/255 au plus (au moins {minShare:P1} attendus), écart maximal {max}/255.");
    }

    private void AssertMatches(
        FrameComposer cpuComposer, FrameComposer gpuComposer, IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules, ThemeDefinition theme, IBackground background, string what, int frame = 0,
        double minShare = SharpShare)
    {
        var at = Now + TimeSpan.FromMilliseconds(frame * 100);
        using var cpu = cpuComposer.Compose(placements, modules, theme, background, at);
        using var gpu = gpuComposer.Compose(placements, modules, theme, background, at);

        Assert.True(gpuComposer.UsesGpu, $"{what} : le compositeur a abandonné la carte.");
        Assert.True(gpuComposer.GlassOnGpu, $"{what} : le verre n'est pas calculé sur la carte.");
        Assert.Equal(cpu.Info, gpu.Info);
        AssertClose(cpu, gpu, what, minShare);
    }

    [WarpFact]
    public void Advanced_EachPreset_MatchesCpu()
    {
        var (placements, modules) = DefaultScene();
        using var aurora = new AuroraBackground();

        foreach (var preset in GlassPresets.Ids)
        {
            using var device = GpuTestSupport.Warp();
            using var cpuComposer = NewComposer(null);
            using var gpuComposer = NewComposer(device);
            gpuComposer.GpuFailed += reason => Assert.Fail(reason);

            AssertMatches(cpuComposer, gpuComposer, placements, modules, Advanced(preset), aurora, $"préréglage {preset}");

            Assert.Equal(cpuComposer.GlassComputations, gpuComposer.GlassComputations);
            Assert.Equal(cpuComposer.GlassShadowComputations, gpuComposer.GlassShadowComputations);
            Assert.False(device.IsLost);
        }
    }

    [WarpFact]
    public void Advanced_AnimatedBackground_EveryFrameMatchesCpu()
    {
        // Cinq images d'un fond animé : chaque image est neuve, le verre est recalculé à chaque fois (même flou, même luminance,
        // sans rien garder d'une image à l'autre). Un dégradé qui glisse, plutôt que les aplats d'un GIF de test : sur un aplat,
        // l'arrondi de la teinte translucide de la carte et celui du processeur (un niveau d'écart) tombent pareil sur tous les
        // pixels d'une case, et l'écart moyen frôle 0,5/255 (0,56 sur un aplat magenta) sans que rien ne soit faux.
        var (placements, modules) = DefaultScene();
        using var background = new SlidingGradient();

        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var gpuComposer = NewComposer(device);
        gpuComposer.GpuFailed += reason => Assert.Fail(reason);

        for (int frame = 0; frame < 5; frame++)
        {
            background.Next();
            AssertMatches(
                cpuComposer, gpuComposer, placements, modules, Advanced(GlassPresets.Clair), background, $"image {frame} du fond animé", frame);
        }

        Assert.Equal(cpuComposer.GlassComputations, gpuComposer.GlassComputations);
        Assert.True(gpuComposer.GlassComputations >= 5 * placements.Count, "le verre n'a pas été recalculé à chaque image");
    }

    [WarpFact]
    public void Pills_MatchCpu()
    {
        var placement = Place(0, 0, 4, 2);
        IReadOnlyList<ModulePlacement> placements = [placement];
        using var aurora = new AuroraBackground();
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var gpuComposer = NewComposer(device);
        gpuComposer.GpuFailed += reason => Assert.Fail(reason);

        // Trois images, l'onglet choisi change à la deuxième : les pastilles sont recalculées à chaque image, sans rien garder.
        for (int frame = 0; frame < 3; frame++)
        {
            var modules = new Dictionary<Guid, IModule> { [placement.InstanceId] = Tabs(frame == 0 ? 0 : 1) };
            AssertMatches(cpuComposer, gpuComposer, placements, modules, Advanced(GlassPresets.Clair), aurora, $"image {frame}", frame);
        }

        Assert.Equal(3, gpuComposer.GlassPillComputations); // une pastille, recalculée à chaque image
    }

    [WarpFact]
    public void CommunityModule_PillsAndOfflineVeil_MatchCpu()
    {
        // Ce que fait RemoteModule.Render : l'image du module, ses éléments choisis notés avec l'opacité du voile quand il est
        // hors ligne (90/255), l'image elle-même voilée de la même valeur.
        var placement = Place(0, 0, 4, 2);
        IReadOnlyList<ModulePlacement> placements = [placement];
        using var image = new SKBitmap(new SKImageInfo(300, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true };
            canvas.DrawCircle(70, 70, 50, paint);
            paint.Color = SKColors.White;
            canvas.DrawRoundRect(SKRect.Create(150, 30, 120, 30), 6, 6, paint);
        }
        SKRect[] selections = [SKRect.Create(10, 20, 220, 44), SKRect.Create(10, 80, 220, 44)];

        IModule Community(bool offline) => new FakeModule((canvas, ctx) =>
        {
            if (ctx.Selections is { } sink)
            {
                foreach (var selection in selections) sink.Add(selection, offline ? 90 / 255f : 1f);
            }
            using var veil = offline ? new SKPaint { Color = SKColors.White.WithAlpha(90) } : null;
            canvas.DrawBitmap(image, ctx.Bounds.Left, ctx.Bounds.Top, veil);
        });

        using var aurora = new AuroraBackground();
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var gpuComposer = NewComposer(device);
        gpuComposer.GpuFailed += reason => Assert.Fail(reason);

        foreach (var offline in new[] { false, true })
        {
            var modules = new Dictionary<Guid, IModule> { [placement.InstanceId] = Community(offline) };
            AssertMatches(
                cpuComposer, gpuComposer, placements, modules, Advanced(GlassPresets.Clair), aurora,
                offline ? "module hors ligne" : "module en ligne");
        }

        // Deux pastilles : le processeur garde les siennes d'une image à l'autre (la case ne change pas), la carte les recalcule.
        Assert.Equal(2, cpuComposer.GlassPillComputations);
        Assert.Equal(4, gpuComposer.GlassPillComputations);
    }

    [WarpFact]
    public void PillBudget_StillApplies()
    {
        // 20 pastilles de 3 000 px : 16 sont notées au plus, et seules 13 tiennent dans les 40 000 px de la case. Les autres sont
        // dessinées en pilules claires, sans calcul de verre, comme sur le processeur.
        var placement = Place(0, 0, 8, 2);
        IReadOnlyList<ModulePlacement> placements = [placement];
        var modules = new Dictionary<Guid, IModule>
        {
            [placement.InstanceId] = new FakeModule((canvas, ctx) =>
            {
                var b = ctx.Bounds;
                for (int i = 0; i < 20; i++)
                {
                    ModuleDrawing.DrawSelection(canvas, SKRect.Create(b.Left + 12 + i % 10 * 80, b.Top + 12 + i / 10 * 60, 60, 50), ctx);
                }
            }),
        };
        using var aurora = new AuroraBackground();
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var gpuComposer = NewComposer(device);
        gpuComposer.GpuFailed += reason => Assert.Fail(reason);

        AssertMatches(cpuComposer, gpuComposer, placements, modules, Advanced(GlassPresets.Clair), aurora, "budget de pastilles");

        Assert.Equal(FrameComposer.MaxPillAreaPerCase / 3_000, cpuComposer.GlassPillComputations);
        Assert.Equal(cpuComposer.GlassPillComputations, gpuComposer.GlassPillComputations);
    }

    [WarpFact]
    public void Advanced_RemovedDevice_FallsBackToTheCpuAndDropsItsLayers()
    {
        // Le pilote plante en cours de route (point de vigilance 1) : l'image sort quand même, par le processeur, et les couches de
        // verre de la carte (images de l'appareil perdu) sont jetées avec elle.
        var placement = Place(0, 0, 4, 2);
        IReadOnlyList<ModulePlacement> placements = [placement];
        var modules = new Dictionary<Guid, IModule> { [placement.InstanceId] = Tabs(0) };
        var theme = Advanced(GlassPresets.Clair);
        using var aurora = new AuroraBackground();
        using var device = GpuTestSupport.Warp();
        using var cpuComposer = NewComposer(null);
        using var composer = NewComposer(device);
        var reasons = new List<string>();
        composer.GpuFailed += reasons.Add;

        composer.Compose(placements, modules, theme, aurora, Now).Dispose(); // une image sur la carte, qui marche
        Assert.True(composer.GlassOnGpu);
        var gpuLayers = composer.Glass!;
        Assert.True(gpuLayers.LiveImages > 0);
        using (var device5 = device.Device.QueryInterface<Vortice.Direct3D12.ID3D12Device5>())
        {
            device5.RemoveDevice();
        }

        using var expected = cpuComposer.Compose(placements, modules, theme, aurora, Now);
        using var actual = composer.Compose(placements, modules, theme, aurora, Now);

        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "L'image de repli n'est pas celle du processeur.");
        Assert.False(composer.UsesGpu);
        Assert.False(composer.GlassOnGpu);
        Assert.Single(reasons);
        Assert.Contains("retirée", reasons[0]);
        Assert.Equal(0, gpuLayers.LiveImages);
    }

    [WarpFact]
    public void Layers_NoTextureLeftAfterThemeChange()
    {
        var (placements, modules) = DefaultScene();
        using var aurora = new AuroraBackground();

        using var device = GpuTestSupport.Warp();
        using var composer = NewComposer(device);
        composer.Compose(placements, modules, Advanced(GlassPresets.Prisme), aurora, Now).Dispose();
        Assert.True(composer.GlassOnGpu);
        Assert.True(composer.GlassLayers > 0);
        composer.Compose(placements, modules, Themes.ClassiqueDefinition, aurora, Now).Dispose();
        Assert.Null(composer.Glass);
        device.PurgeUnused();

        using var freshDevice = GpuTestSupport.Warp();
        using var fresh = NewComposer(freshDevice);
        fresh.Compose(placements, modules, Themes.ClassiqueDefinition, aurora, Now).Dispose();
        freshDevice.PurgeUnused();

        Assert.True(composer.UsesGpu);
        Assert.Equal(freshDevice.CacheResources, device.CacheResources);
        Assert.Equal(freshDevice.CacheBytes, device.CacheBytes);
    }

    /// <summary>Un fond animé dont chaque image est neuve, comme une vidéo ou un GIF : un dégradé qui glisse.</summary>
    private sealed class SlidingGradient : IBackground
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
                    new SKPoint(step * 151 % GridGeometry.Width, 0), new SKPoint(GridGeometry.Width, GridGeometry.Height),
                    [SKColors.DarkBlue, SKColors.Orange, SKColors.Teal, SKColors.Magenta], SKShaderTileMode.Mirror),
            })
            {
                canvas.DrawPaint(paint);
            }

            bitmap.SetImmutable();
            return SKImage.FromBitmap(bitmap);
        }
    }
}
