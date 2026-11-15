using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using Xunit.Abstractions;

namespace Trofeo.Core.Tests;

/// <summary>Un fond fixe fait d'une image donnée.</summary>
internal sealed class FixedImageBackground(SKImage image) : IBackground
{
    public bool IsAnimated => false;
    public SKImage Current => image;
    public int Version => 1;
    public TimeSpan? UntilNextFrame => null;
    public bool Advance(TimeSpan elapsed) => false;
    public void Dispose() => image.Dispose();
}

/// <summary>Aides partagées par les essais du thème « Liquid Glass avancé ».</summary>
internal static class GlassTesting
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public static ThemeDefinition Advanced(string preset = GlassPresets.Clair, Func<GlassSettings, GlassSettings>? change = null)
    {
        var glass = GlassPresets.Find(preset)!;
        var theme = Themes.LiquidGlassAdvanced;
        return theme with { Panel = theme.Panel with { Glass = change is null ? glass : change(glass) } };
    }

    public static ModulePlacement Place(int column, int row, int columns, int rows) =>
        new(Guid.NewGuid(), "fake", column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>Deux cases voisines, séparées par l'écart du damier.</summary>
    public static ModulePlacement[] TwoCases() => [Place(0, 0, 4, 2), Place(4, 0, 4, 2)];

    public static readonly IReadOnlyDictionary<Guid, IModule> NoModules = new Dictionary<Guid, IModule>();

    public static bool SameBytes(SKBitmap a, SKBitmap b) => a.Bytes.AsSpan().SequenceEqual(b.Bytes);

    public static double Luminance(SKColor c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
    }

    /// <summary>Une image pleine de détails : rayures de 8 px, damier et disques, sur un dégradé.</summary>
    public static SKImage Detailed() => StubBackground.Make(canvas =>
    {
        using var gradient = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(GridGeometry.Width, GridGeometry.Height),
            [new SKColor(20, 40, 90), new SKColor(230, 120, 40)], SKShaderTileMode.Clamp);
        using var back = new SKPaint { Shader = gradient };
        canvas.DrawPaint(back);
        using var white = new SKPaint { Color = SKColors.White.WithAlpha(200) };
        for (int x = 4; x < GridGeometry.Width / 2; x += 16) canvas.DrawRect(x, 0, 8, GridGeometry.Height, white);
        using var dark = new SKPaint { Color = new SKColor(10, 10, 10) };
        for (int y = 0; y < GridGeometry.Height; y += 24)
            for (int x = GridGeometry.Width / 2 + (y / 24 % 2) * 24; x < GridGeometry.Width; x += 48)
                canvas.DrawRect(x, y, 24, 24, dark);
        using var disc = new SKPaint { Color = new SKColor(80, 220, 160), IsAntialias = true };
        for (int i = 0; i < 12; i++) canvas.DrawCircle(80 + i * 160, 120 + (i % 3) * 110, 30, disc);
    });
}

/// <summary>
/// Les couches de verre gardées en mémoire (plan 9, tâche 3) : calculées une fois par case, refaites quand le fond, le
/// rectangle ou les réglages changent, libérées quand elles ne servent plus ; les ombres sous les verres ; la garde de
/// lisibilité mesurée sur le verre.
/// </summary>
public class LiquidGlassLayersTests
{
    private static readonly DateTimeOffset T0 = GlassTesting.T0;

    private static SKRect RectOf(ModulePlacement placement, ThemeDefinition theme) => ModulePlace.Rect(placement, theme);

    [Fact]
    public void FixedBackground_ComputesOnce()
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        var theme = GlassTesting.Advanced();
        using var background = new AuroraBackground();
        using var composer = new FrameComposer();

        for (int i = 0; i < 10; i++)
        {
            composer.Compose(placements, GlassTesting.NoModules, theme, background, T0 + TimeSpan.FromSeconds(i)).Dispose();
        }

        Assert.Equal(placements.Count, composer.GlassComputations);
        Assert.Equal(placements.Count, composer.GlassShadowComputations);
        Assert.Equal(placements.Count, composer.GlassLayers);
    }

    [Fact]
    public void NewBackgroundVersion_Recomputes()
    {
        var shown = GlassTesting.TwoCases();
        var theme = GlassTesting.Advanced();
        using var background = new StubBackground(SKColors.DarkBlue);
        using var composer = new FrameComposer();

        composer.Compose(shown, GlassTesting.NoModules, theme, background, T0).Dispose();
        Assert.Equal(2, composer.GlassComputations); // seulement les cases de la vue composée

        composer.Compose(shown, GlassTesting.NoModules, theme, background, T0).Dispose();
        Assert.Equal(2, composer.GlassComputations); // même version : rien n'est refait

        background.Replace(StubBackground.Make(canvas => canvas.Clear(SKColors.DarkRed)));
        using var frame = composer.Compose(shown, GlassTesting.NoModules, theme, background, T0);
        Assert.Equal(4, composer.GlassComputations);
        Assert.Equal(2, composer.GlassShadowComputations); // l'ombre ne dépend pas du fond

        // Le verre montre bien la nouvelle image du fond.
        var rect = RectOf(shown[0], theme);
        var inside = frame.GetPixel((int)rect.MidX, (int)rect.MidY);
        Assert.True(inside.Red > inside.Blue + 40, $"le verre devrait être rouge : {inside}");
    }

    [Fact]
    public void SettingsChange_Recomputes()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        using var fresh = new FrameComposer();
        var prisme = GlassTesting.Advanced(GlassPresets.Prisme);

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, T0).Dispose();
        using var changed = composer.Compose(placements, GlassTesting.NoModules, prisme, background, T0);
        using var expected = fresh.Compose(placements, GlassTesting.NoModules, prisme, background, T0);

        Assert.Equal(4, composer.GlassComputations);
        Assert.Equal(4, composer.GlassShadowComputations); // Clair (0,25) et Prisme (0,3) n'ont pas la même ombre
        Assert.Equal(2, composer.GlassLayers);
        Assert.True(GlassTesting.SameBytes(expected, changed));
    }

    [Fact]
    public void ShadowSettingChange_RecomputesTheShadowOnly()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new ColorBackground(SKColors.Gray);
        using var composer = new FrameComposer();

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, T0).Dispose();
        composer.Compose(
            placements, GlassTesting.NoModules, GlassTesting.Advanced(change: s => s with { ShadowSpread = 25f }), background, T0).Dispose();

        Assert.Equal(4, composer.GlassShadowComputations);
        Assert.Equal(4, composer.GlassComputations); // les réglages font partie de la clé du verre
    }

    [Fact]
    public void ThemeSwitchAndBack_LeavesNoStaleLayer()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        using var fresh = new FrameComposer();
        var givre = GlassTesting.Advanced(GlassPresets.Givre);

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, T0).Dispose();
        Assert.Equal(2, composer.GlassLayers);

        composer.Compose(placements, GlassTesting.NoModules, Themes.LiquidGlass, background, T0).Dispose();
        Assert.Equal(0, composer.GlassLayers); // rien ne reste en mémoire dans un autre thème

        using var back = composer.Compose(placements, GlassTesting.NoModules, givre, background, T0);
        using var expected = fresh.Compose(placements, GlassTesting.NoModules, givre, background, T0);
        Assert.True(GlassTesting.SameBytes(expected, back));
        Assert.Equal(2, composer.GlassLayers);
    }

    [Fact]
    public void RemovedCase_FreesItsLayers()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new ColorBackground(SKColors.Gray);
        using var composer = new FrameComposer();

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(), background, T0).Dispose();
        composer.Compose([placements[0]], GlassTesting.NoModules, GlassTesting.Advanced(), background, T0).Dispose();

        Assert.Equal(1, composer.GlassLayers);
        Assert.Equal(2, composer.GlassComputations); // la case gardée n'est pas refaite
    }

    [Fact]
    public void Layers_EndFrame_FreesUnusedRects()
    {
        var theme = GlassTesting.Advanced();
        using var background = new ColorBackground(SKColors.Gray);
        using var layers = new LiquidGlassLayers();
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var canvas = new SKCanvas(bitmap);
        var a = SKRect.Create(20, 20, 200, 120);
        var b = SKRect.Create(300, 20, 200, 120);

        layers.BeginFrame(background, theme);
        layers.PaintShadows(canvas, [a, b]);
        layers.PaintGlass(canvas, a);
        layers.PaintGlass(canvas, b);
        layers.EndFrame();
        Assert.Equal(2, layers.LiveLayers);

        layers.BeginFrame(background, theme);
        layers.PaintGlass(canvas, b);
        layers.EndFrame();
        Assert.Equal(1, layers.LiveLayers);

        layers.BeginFrame(background, theme);
        layers.EndFrame();
        Assert.Equal(0, layers.LiveLayers);
        Assert.Equal(2, layers.Computations);
    }

    [Fact]
    public void Layers_FractionalRect_IsSnappedToPixels()
    {
        var theme = GlassTesting.Advanced();
        using var background = new ColorBackground(SKColors.Gray);
        using var layers = new LiquidGlassLayers();
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var canvas = new SKCanvas(bitmap);

        layers.BeginFrame(background, theme);
        layers.PaintGlass(canvas, SKRect.Create(20.4f, 20.3f, 200f, 120f));
        layers.PaintGlass(canvas, SKRect.Create(20f, 20f, 200f, 120f)); // la même case, une fois calée
        Assert.True(layers.Luminance(SKRect.Create(19.6f, 20.2f, 200.3f, 120.1f)) > 0f);
        layers.EndFrame();

        Assert.Equal(1, layers.Computations);
    }

    [Fact]
    public void Layers_ExtremeValues_DoNotThrowNorSpill()
    {
        // Point de vigilance 3 : une case 1 × 1 du damier, rayon 49, biseau 100, Dôme, flou 1.
        var theme = GlassTesting.Advanced(change: s => s with { CornerRadius = 49f, ZRadius = 100f, Bevel = GlassBevel.Dome, Blur = 1f, ShadowOpacity = 0f });
        var placement = GlassTesting.Place(3, 1, 1, 1);
        var rect = ModulePlace.Rect(placement, theme);
        using var background = new ColorBackground(new SKColor(40, 40, 40));
        using var composer = new FrameComposer();

        using var frame = composer.Compose([placement], GlassTesting.NoModules, theme, background, T0);

        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) continue;
                Assert.Equal(new SKColor(40, 40, 40), frame.GetPixel(x, y));
            }
        Assert.NotEqual(new SKColor(40, 40, 40), frame.GetPixel((int)rect.MidX, (int)rect.MidY));
    }

    [Fact]
    public void Layers_EmptyRect_PaintsNothing()
    {
        using var background = new ColorBackground(SKColors.Gray);
        using var layers = new LiquidGlassLayers();
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var canvas = new SKCanvas(bitmap);
        var empty = SKRect.Create(50, 50, 0.2f, 100);

        layers.BeginFrame(background, GlassTesting.Advanced());
        layers.PaintShadows(canvas, [empty]);
        layers.PaintGlass(canvas, empty);
        Assert.True(layers.IsReady(empty));
        Assert.Equal(0f, layers.Luminance(empty));
        layers.EndFrame();

        Assert.Equal(0, layers.Computations);
        Assert.Equal(0, layers.LiveLayers);
    }

    [Fact]
    public void Shadows_AreUnderNeighbourGlass()
    {
        var placements = GlassTesting.TwoCases();
        var withShadow = GlassTesting.Advanced(change: s => s with { ShadowSpread = 40f, ShadowOpacity = 1f, Opacity = 1f });
        var without = GlassTesting.Advanced(change: s => s with { ShadowSpread = 40f, ShadowOpacity = 0f, Opacity = 1f });
        var left = RectOf(placements[0], withShadow);
        var right = RectOf(placements[1], withShadow);
        using var background = new ColorBackground(new SKColor(200, 200, 200));
        using var composer = new FrameComposer();

        using var shadowed = composer.Compose(placements, GlassTesting.NoModules, withShadow, background, T0);
        using var plain = composer.Compose(placements, GlassTesting.NoModules, without, background, T0);

        // Dans l'écart entre les deux cases, l'ombre se voit.
        int gapX = (int)((left.Right + right.Left) / 2), y = (int)left.MidY;
        Assert.Equal(new SKColor(200, 200, 200), plain.GetPixel(gapX, y));
        Assert.True(
            GlassTesting.Luminance(shadowed.GetPixel(gapX, y)) < GlassTesting.Luminance(plain.GetPixel(gapX, y)) * 0.5,
            $"l'ombre devrait assombrir l'écart : {shadowed.GetPixel(gapX, y)}");

        // Dans le verre de la voisine, l'ombre de l'autre case est dessous : le pixel est le même que sans ombre.
        foreach (int inset in new[] { 4, 12, 30 })
        {
            Assert.Equal(plain.GetPixel((int)right.Left + inset, y), shadowed.GetPixel((int)right.Left + inset, y));
            Assert.Equal(plain.GetPixel((int)left.Right - 1 - inset, y), shadowed.GetPixel((int)left.Right - 1 - inset, y));
        }
    }

    private static double MeanPanelLuminance(SKBitmap frame, SKRect rect)
    {
        double sum = 0;
        int count = 0;
        for (int y = (int)rect.Top + 20; y < rect.Bottom - 20; y += 3)
            for (int x = (int)rect.Left + 20; x < rect.Right - 20; x += 3, count++)
                sum += GlassTesting.Luminance(frame.GetPixel(x, y));
        return sum / count;
    }

    [Fact]
    public void Legibility_PrismeOnWhite_KeepsTextReadable()
    {
        var placement = GlassTesting.Place(4, 1, 4, 2);
        var prisme = GlassTesting.Advanced(GlassPresets.Prisme);
        var rect = RectOf(placement, prisme);
        using var white = new ColorBackground(SKColors.White);
        using var composer = new FrameComposer();

        using var guarded = composer.Compose([placement], GlassTesting.NoModules, prisme, white, T0);
        using var raw = composer.Compose([placement], GlassTesting.NoModules, prisme with { LegibilityGuard = false }, white, T0);

        Assert.InRange(MeanPanelLuminance(guarded, rect), 0.0, PanelPainter.MaxLuminance);
        Assert.True(MeanPanelLuminance(raw, rect) > PanelPainter.MaxLuminance, "sans la garde, le verre sur du blanc reste clair");
        Assert.Equal(1, composer.GlassComputations); // la garde ne fait pas partie de la clé : la teinte se pose par-dessus
    }

    [Fact]
    public void Legibility_IsMeasuredOnTheGlass_NotOnTheBackground()
    {
        // Un fond sombre, mais un verre très éclairci (luminosité au maximum) : seule une mesure sur le verre le voit.
        var placement = GlassTesting.Place(4, 1, 4, 2);
        var bright = GlassTesting.Advanced(change: s => s with { Brightness = 0.5f, Tint = 0f, Saturation = 0f });
        bright = bright with { Theme = bright.Theme with { PanelOpacity = 0f } };
        var rect = RectOf(placement, bright);
        using var background = new ColorBackground(new SKColor(190, 190, 190)); // luminance 0,51 ; × 1,5 dans le verre : blanc
        using var layers = new LiquidGlassLayers();
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using var canvas = new SKCanvas(bitmap);

        layers.BeginFrame(background, bright);
        float measured = layers.Luminance(rect);
        layers.EndFrame();

        Assert.True(measured > 0.9f, $"luminance mesurée sur le verre : {measured}");
    }

    [Fact]
    public void Legibility_TranslucentGlass_IsMeasuredOverTheBackground()
    {
        // Opacité du verre à 0,5 : la couche seule est sombre (couleur × 0,125, alpha 0,25, soit une luminance de 0,01), mais
        // posée sur du blanc elle reste claire (0,125 + 0,75 de blanc : une luminance de 0,74).
        var placement = GlassTesting.Place(4, 1, 4, 2);
        var faint = GlassTesting.Advanced(change: s => s with { Opacity = 0.5f });
        using var white = new ColorBackground(SKColors.White);
        using var layers = new LiquidGlassLayers();

        layers.BeginFrame(white, faint);
        float measured = layers.Luminance(RectOf(placement, faint));
        layers.EndFrame();

        Assert.InRange(measured, 0.65f, 0.85f);
    }

    /// <summary>
    /// L'écart moyen et le plus grand écart, en niveaux sur 255, entre les couleurs de deux verres rendus avec deux copies
    /// floutées différentes (l'alpha, identique des deux côtés, ne compte pas).
    /// </summary>
    private static (double Mean, int Max) Difference(SKImage a, SKImage b)
    {
        using var first = SKBitmap.FromImage(a);
        using var second = SKBitmap.FromImage(b);
        var x = first.GetPixelSpan();
        var y = second.GetPixelSpan();
        long sum = 0;
        int max = 0, count = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 4 == 3) continue; // l'alpha
            int d = Math.Abs(x[i] - y[i]);
            sum += d;
            max = Math.Max(max, d);
            count++;
        }
        return (sum / (double)count, max);
    }

    /// <summary>La copie floutée au quart de la taille (comme <see cref="Backdrop"/>, σ / 4), ramenée à la taille de l'écran.</summary>
    private static SKImage QuarterBlur(SKImage sharp, float sigma)
    {
        using var small = new SKBitmap(new SKImageInfo(Backdrop.Width, Backdrop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Backdrop.Shrunk(sharp, small);
        using var blurredSmall = new SKBitmap(small.Info);
        using (var canvas = new SKCanvas(blurredSmall))
        {
            using var filter = SKImageFilter.CreateBlur(sigma / 4f, sigma / 4f, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(small, 0, 0, paint);
        }

        using var full = new SKBitmap(new SKImageInfo(sharp.Width, sharp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(full))
        using (var image = SKImage.FromBitmap(blurredSmall))
        {
            canvas.DrawImage(image, new SKRect(0, 0, sharp.Width, sharp.Height), new SKSamplingOptions(SKFilterMode.Linear));
        }
        return SKImage.FromBitmap(full);
    }

    /// <summary>
    /// Décide de la copie floutée. Mesure faite au début de la tâche 3 : sur un fond détaillé, la copie au quart s'écarte du
    /// flou plein de bien plus que 2 niveaux pour un petit flou (0,1 : le centre du verre montre la scène floutée, et le quart
    /// efface des détails que le flou plein garde). Le flou se fait donc à pleine taille : ce test garde la mesure qui l'a
    /// décidé, et vérifie que le flou retenu (<see cref="LiquidGlassLayers.Blur"/>) est bien un flou plein.
    /// </summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.6f)]
    public void QuarterBlur_MatchesFullBlur(float blur)
    {
        using var effect = new LiquidGlassEffect();
        using var sharp = GlassTesting.Detailed();
        var settings = GlassPresets.Find(GlassPresets.Clair)! with { Blur = blur };
        var shape = SKRect.Create(480, 20, 470, 210);
        float sigma = LiquidGlassEffect.BlurSigma(blur);

        using var fullBlur = LiquidGlassLayers.Blur(sharp, sigma);
        using var quarterBlur = QuarterBlur(sharp, sigma);
        using var reference = effect.RenderGlass(sharp, fullBlur, SKPoint.Empty, shape, settings);
        using var quarter = effect.RenderGlass(sharp, quarterBlur, SKPoint.Empty, shape, settings);

        // Le flou plein de référence : celui de Skia, sans passer par les couches.
        using var direct = DirectBlur(sharp, sigma);
        using var expected = effect.RenderGlass(sharp, direct, SKPoint.Empty, shape, settings);
        Assert.Equal(0, Difference(reference, expected).Max);

        var (error, worst) = Difference(reference, quarter);
        _output.WriteLine(
            $"Flou {blur} (σ {sigma:0.00} px) : écart du quart avec le flou plein = {error:0.00} niveaux sur 255 en moyenne, {worst} au plus.");
        if (blur <= 0.1f)
        {
            Assert.True(error > 2.0, $"si le quart suffit désormais ({error:0.00}), la décision du flou plein est à revoir");
        }
    }

    [Fact]
    public void RenderGlass_BlurredSceneOfAnotherSize_IsRefused()
    {
        using var effect = new LiquidGlassEffect();
        using var sharp = GlassTesting.Detailed();
        using var small = new SKBitmap(new SKImageInfo(Backdrop.Width, Backdrop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Backdrop.Shrunk(sharp, small);
        using var quarter = SKImage.FromBitmap(small);

        Assert.Throws<ArgumentException>(() =>
            effect.RenderGlass(sharp, quarter, SKPoint.Empty, SKRect.Create(100, 100, 200, 120), new GlassSettings()));
    }

    private static SKImage DirectBlur(SKImage sharp, float sigma)
    {
        using var surface = SKSurface.Create(new SKImageInfo(sharp.Width, sharp.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawImage(sharp, 0, 0, paint);
        return surface.Snapshot();
    }

    private readonly ITestOutputHelper _output;

    public LiquidGlassLayersTests(ITestOutputHelper output) => _output = output;

    /// <summary>Un fond animé, gris, sauf un bloc à droite de la case qui alterne blanc et noir à chaque image.</summary>
    private static SKImage Blinking(SKRect panel, bool white) => StubBackground.Make(canvas =>
    {
        canvas.Clear(new SKColor(128, 128, 128));
        using var paint = new SKPaint { Color = white ? SKColors.White : SKColors.Black };
        canvas.DrawRect(SKRect.Create(panel.Right - 150, panel.Top, 150, panel.Height), paint);
    });

    [Fact]
    public void AnimatedBackground_TintIsSmoothed_NotCached()
    {
        // Point de vigilance 1. Opacité choisie 0 : seule la garde teinte la case, et elle voudrait 0 puis 0,65 une image sur deux.
        var placement = GlassTesting.Place(4, 1, 4, 2);
        var theme = GlassTesting.Advanced();
        theme = theme with { Theme = theme.Theme with { PanelOpacity = 0f } };
        var rect = RectOf(placement, theme);
        var frameTime = TimeSpan.FromMilliseconds(1000.0 / 12);
        using var background = new StubBackground(Blinking(rect, white: false));
        using var composer = new FrameComposer();

        // Un pixel où le fond reste gris, loin du biseau et du bloc : seule la teinte le fait bouger.
        int x = (int)rect.Left + 120, y = (int)rect.MidY;
        var series = new List<double>();
        for (int i = 0; i < 48; i++)
        {
            background.Replace(Blinking(rect, white: i % 2 == 0));
            using var frame = composer.Compose([placement], GlassTesting.NoModules, theme, background, T0 + i * frameTime);
            series.Add(GlassTesting.Luminance(frame.GetPixel(x, y)));
            Assert.Equal(i + 1, composer.GlassComputations); // une fois par version du fond…

            composer.Compose([placement], GlassTesting.NoModules, theme, background, T0 + i * frameTime).Dispose();
            Assert.Equal(i + 1, composer.GlassComputations); // … et pas plus
        }

        double biggestStep = Enumerable.Range(1, series.Count - 1).Max(i => Math.Abs(series[i] - series[i - 1]));
        Assert.True(biggestStep < 0.04, $"plus grand saut : {biggestStep}");
        Assert.True(series.Max() < 0.12, $"la teinte de la garde reste posée entre deux images claires : {series.Max()}");

        // Contrôle : la cible de la garde, elle, saute bien d'une image à l'autre (sur un fond fixe, rien n'est lissé).
        using var fixedComposer = new FrameComposer();
        using var whiteBlock = new FixedImageBackground(Blinking(rect, white: true));
        using var blackBlock = new FixedImageBackground(Blinking(rect, white: false));
        using var onWhite = fixedComposer.Compose([placement], GlassTesting.NoModules, theme, whiteBlock, T0);
        using var onBlack = fixedComposer.Compose([placement], GlassTesting.NoModules, theme, blackBlock, T0);
        Assert.True(
            Math.Abs(GlassTesting.Luminance(onWhite.GetPixel(x, y)) - GlassTesting.Luminance(onBlack.GetPixel(x, y))) > 0.1,
            "sans lissage, la teinte devrait sauter");
    }
}

/// <summary>
/// Mesure du calcul des couches (plan 9, tâche 3, étape 4) : imprime les temps, sans rien exiger.
/// <c>dotnet test tests/Trofeo.Core.Tests --filter "Category=Bench" --logger "console;verbosity=detailed"</c>.
/// </summary>
public class LiquidGlassLayersBench(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Bench")]
    [InlineData(GlassPresets.Clair)]
    [InlineData(GlassPresets.Givre)]
    public void DefaultView_OnAurora(string preset)
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        var theme = GlassTesting.Advanced(preset);
        using var background = new AuroraBackground();

        // Un premier compositeur chauffe Skia et compile l'effet : son temps ne compte pas.
        using (var warm = new FrameComposer())
        {
            warm.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose();
        }

        var first = new List<double>();
        var cached = new List<double>();
        for (int run = 0; run < 3; run++)
        {
            using var composer = new FrameComposer();
            composer.Compose([], GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose(); // l'effet est compilé ici
            var watch = Stopwatch.StartNew();
            composer.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose();
            first.Add(watch.Elapsed.TotalMilliseconds);

            watch.Restart();
            for (int i = 0; i < 20; i++)
            {
                composer.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose();
            }
            cached.Add(watch.Elapsed.TotalMilliseconds / 20);
        }

        using var reference = new FrameComposer();
        reference.Compose(placements, GlassTesting.NoModules, Themes.LiquidGlass, background, GlassTesting.T0).Dispose();
        var simple = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            reference.Compose(placements, GlassTesting.NoModules, Themes.LiquidGlass, background, GlassTesting.T0).Dispose();
        }
        double simpleMs = simple.Elapsed.TotalMilliseconds / 20;

        first.Sort();
        cached.Sort();
        output.WriteLine(
            $"{preset} : calcul des couches de la vue par défaut ({placements.Count} cases) = {first[first.Count / 2]:0.0} ms (médiane de 3, " +
            $"de {first[0]:0.0} à {first[^1]:0.0}) ; composition avec les couches en mémoire = {cached[cached.Count / 2]:0.00} ms ; " +
            $"Liquid Glass simple = {simpleMs:0.00} ms.");
    }

    [Fact]
    [Trait("Category", "Bench")]
    public void FullSizeBlur_OnAurora()
    {
        using var background = new AuroraBackground();
        foreach (float blur in new[] { 0.1f, 0.6f, 1f })
        {
            float sigma = LiquidGlassEffect.BlurSigma(blur);
            LiquidGlassLayers.Blur(background.Current, sigma).Dispose();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) LiquidGlassLayers.Blur(background.Current, sigma).Dispose();
            output.WriteLine($"Flou {blur} (σ {sigma:0.00} px), 1920 × 462 à pleine taille : {watch.Elapsed.TotalMilliseconds / 10:0.0} ms.");
        }
    }
}
