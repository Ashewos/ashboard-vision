using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Core.Tests;

public class FrameComposerTests
{
    private static ModulePlacement Place(Guid id, int column, int row, int columns, int rows) =>
        new(id, "fake", column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private static void AssertClose(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.InRange(actual.Red, expected.Red - tolerance, expected.Red + tolerance);
        Assert.InRange(actual.Green, expected.Green - tolerance, expected.Green + tolerance);
        Assert.InRange(actual.Blue, expected.Blue - tolerance, expected.Blue + tolerance);
    }

    [Fact]
    public void Compose_Size()
    {
        using var frame = new FrameComposer().Compose(
            [], new Dictionary<Guid, IModule>(), Themes.Classique, DateTimeOffset.UnixEpoch);

        Assert.Equal(1920, frame.Width);
        Assert.Equal(462, frame.Height);
    }

    [Fact]
    public void Compose_NullPlacements_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new FrameComposer().Compose(
            null!, new Dictionary<Guid, IModule>(), Themes.Classique, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Compose_PaintsPanelInModuleRect()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 12, 0, 4, 2)]);
        var modules = new Dictionary<Guid, IModule> { [id] = new FakeModule() };
        var theme = Themes.Classique;

        using var frame = new FrameComposer().Compose(layout.Views[0].Placements, modules, theme, DateTimeOffset.UnixEpoch);

        Assert.Equal(theme.Background, frame.GetPixel(5, 5));

        var inside = frame.GetPixel(1500, 100);
        Assert.NotEqual(theme.Background, inside);
        Assert.Equal(255, inside.Alpha);
        float o = theme.PanelOpacity;
        var expected = new SKColor(
            (byte)Math.Round(theme.Panel.Red * o + theme.Background.Red * (1 - o)),
            (byte)Math.Round(theme.Panel.Green * o + theme.Background.Green * (1 - o)),
            (byte)Math.Round(theme.Panel.Blue * o + theme.Background.Blue * (1 - o)));
        AssertClose(expected, inside);
    }

    [Fact]
    public void Compose_PassesContextToRender()
    {
        var id = Guid.NewGuid();
        var module = new FakeModule();
        var layout = DashboardLayout.SingleView(0, [Place(id, 12, 0, 4, 2) with { TextSize = TextSize.Large }]);
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule> { [id] = module }, Themes.Classique, now);

        var call = Assert.Single(module.RenderCalls);
        var rect = GridGeometry.ModuleRect(12, 0, 4, 2);
        Assert.Equal(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height), call.Bounds);
        Assert.Equal(new GridSize(4, 2), call.Size);
        Assert.Equal(1.2f, call.TextScale);
        Assert.Equal(now, call.Now);
        Assert.Same(Themes.Classique, call.Theme);
    }

    [Fact]
    public void Compose_PlacementWithoutInstance_PaintsPanelOnly()
    {
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 12, 0, 4, 2)]);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.Classique, DateTimeOffset.UnixEpoch);

        Assert.NotEqual(Themes.Classique.Background, frame.GetPixel(1500, 100));
    }

    [Fact]
    public void Compose_ModuleThrows_DrawsErrorPanelOnly()
    {
        var badId = Guid.NewGuid();
        var goodId = Guid.NewGuid();
        var green = new SKColor(0, 255, 0);
        var layout = DashboardLayout.SingleView(0, [Place(badId, 0, 0, 4, 2), Place(goodId, 12, 0, 4, 2)]);
        var modules = new Dictionary<Guid, IModule>
        {
            [badId] = new FakeModule(throwOnRender: true),
            [goodId] = new FakeModule((canvas, ctx) => canvas.DrawRect(ctx.Bounds, new SKPaint { Color = green })),
        };

        using var frame = new FrameComposer().Compose(layout.Views[0].Placements, modules, Themes.Classique, DateTimeOffset.UnixEpoch);

        Assert.Equal(green, frame.GetPixel(1500, 100));

        // Le panneau d'erreur contient du texte Warning : au moins un pixel de la zone en est proche.
        var bad = GridGeometry.ModuleRect(0, 0, 4, 2);
        var warning = Themes.Classique.Warning;
        bool foundWarning = false;
        for (int y = bad.Top; y < bad.Bottom && !foundWarning; y++)
        {
            for (int x = bad.Left; x < bad.Right; x++)
            {
                var p = frame.GetPixel(x, y);
                if (Math.Abs(p.Red - warning.Red) < 40 && Math.Abs(p.Green - warning.Green) < 40 && Math.Abs(p.Blue - warning.Blue) < 40)
                {
                    foundWarning = true;
                    break;
                }
            }
        }

        Assert.True(foundWarning);
    }

    [Fact]
    public void Compose_ModuleDrawsOutsideBounds_IsClipped()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 12, 0, 4, 2)]);
        var modules = new Dictionary<Guid, IModule>
        {
            [id] = new FakeModule((canvas, _) => canvas.DrawRect(0, 0, 1920, 462, new SKPaint { Color = SKColors.Red })),
        };

        using var frame = new FrameComposer().Compose(layout.Views[0].Placements, modules, Themes.Classique, DateTimeOffset.UnixEpoch);

        Assert.Equal(Themes.Classique.Background, frame.GetPixel(5, 5));
        Assert.Equal(SKColors.Red, frame.GetPixel(1500, 100));
    }

    [Fact]
    public void Compose_ModuleFillsBounds_IsClippedToRoundedCorners()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 12, 0, 4, 2)]);
        var modules = new Dictionary<Guid, IModule>
        {
            [id] = new FakeModule((canvas, ctx) => canvas.DrawRect(ctx.Bounds, new SKPaint { Color = SKColors.Red })),
        };
        var rect = GridGeometry.ModuleRect(12, 0, 4, 2);

        using var frame = new FrameComposer().Compose(layout.Views[0].Placements, modules, Themes.Classique, DateTimeOffset.UnixEpoch);

        Assert.Equal(Themes.Classique.Background, frame.GetPixel(rect.Left, rect.Top)); // le coin arrondi n'est pas rouge
        Assert.Equal(SKColors.Red, frame.GetPixel(rect.Left + 40, rect.Top + 40));
    }

    [Fact]
    public void Compose_ModuleKeepsThrowing_LogsOnlyOnce()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 12, 0, 4, 2)]);
        var modules = new Dictionary<Guid, IModule> { [id] = new FakeModule(throwOnRender: true) };
        var lines = new List<string>();
        var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, lines.Add));

        for (var i = 0; i < 5; i++)
            composer.Compose(layout.Views[0].Placements, modules, Themes.Classique, DateTimeOffset.UnixEpoch).Dispose();

        Assert.Single(lines);
        Assert.Contains("fake", lines[0]);
    }

    // ---- Composition par thème (plan 4, tâche 6) ----

    private static readonly SKColor ClassiqueBackground = SKColor.Parse("#0E1116");

    // Somme de tous les octets de l'image : indépendante de l'ordre des canaux (BGRA ou RGBA).
    private static long ByteSum(SKBitmap bitmap)
    {
        long sum = 0;
        foreach (var b in bitmap.Bytes) sum += b;
        return sum;
    }

    private static bool SameBytes(SKBitmap a, SKBitmap b) => a.Bytes.AsSpan().SequenceEqual(b.Bytes);

    private static IModule FillModule(SKColor color) =>
        new FakeModule((canvas, ctx) => canvas.DrawRect(ctx.Bounds, new SKPaint { Color = color }));

    private static double Luminance(SKColor c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
    }

    [Fact]
    public void Compose_OldOverload_IsPixelIdentical()
    {
        var composer = new FrameComposer();
        var empty = DashboardLayout.SingleView(0, []);

        // Empreinte relevée avant de toucher au compositeur : fond seul, 1920 × 462 × (14 + 17 + 22 + 255).
        using var oldEmpty = composer.Compose(empty.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.Classique, DateTimeOffset.UnixEpoch);
        Assert.Equal(273_208_320L, ByteSum(oldEmpty));

        // Avec des panneaux : la surcharge ancienne et la neuve (Classique, fond #0E1116) donnent la même image.
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 0, 0, 4, 2), Place(Guid.NewGuid(), 12, 1, 4, 3)]);
        var modules = new Dictionary<Guid, IModule> { [id] = FillModule(SKColors.Red) };
        using var background = new ColorBackground(ClassiqueBackground);

        using var oldFrame = composer.Compose(layout.Views[0].Placements, modules, Themes.Classique, DateTimeOffset.UnixEpoch);
        using var newFrame = composer.Compose(layout.Views[0].Placements, modules, Themes.ClassiqueDefinition, background, DateTimeOffset.UnixEpoch);

        Assert.True(SameBytes(oldFrame, newFrame));
    }

    [Fact]
    public void Compose_MatteWithoutOutline_MatchesTheLegacyPanelPixelForPixel()
    {
        // Le peintre d'avant le plan 4, recopié : Clear, puis le panneau arrondi, puis le module rogné à l'arrondi.
        var theme = Themes.Classique;
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 0, 0, 4, 2), Place(Guid.NewGuid(), 12, 1, 4, 3)]);
        var modules = new Dictionary<Guid, IModule> { [id] = FillModule(SKColors.Red) };

        using var reference = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        using (var canvas = new SKCanvas(reference))
        {
            canvas.Clear(theme.Background);
            foreach (var placement in layout.Views[0].Placements)
            {
                var r = GridGeometry.ModuleRect(placement.Column, placement.Row, placement.Columns, placement.Rows);
                var rect = SKRect.Create(r.Left, r.Top, r.Width, r.Height);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = theme.Panel.WithAlpha((byte)Math.Round(theme.PanelOpacity * 255f)),
                };
                canvas.DrawRoundRect(rect, theme.CornerRadius, theme.CornerRadius, paint);
                if (!modules.ContainsKey(placement.InstanceId)) continue;
                canvas.Save();
                using var clip = new SKRoundRect(rect, theme.CornerRadius);
                canvas.ClipRoundRect(clip, antialias: true);
                canvas.DrawRect(rect, new SKPaint { Color = SKColors.Red });
                canvas.Restore();
            }
        }

        var noOutline = Themes.ClassiqueDefinition with { Panel = new PanelLook(PanelKind.Matte) };
        using var background = new ColorBackground(ClassiqueBackground);
        using var frame = new FrameComposer().Compose(layout.Views[0].Placements, modules, noOutline, background, DateTimeOffset.UnixEpoch);

        Assert.True(SameBytes(reference, frame));
    }

    [Fact]
    public void Compose_Classique_DrawsTheOutlineInsideTheModuleRect()
    {
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 12, 0, 4, 2)]);
        var rect = GridGeometry.ModuleRect(12, 0, 4, 2);
        using var background = new ColorBackground(ClassiqueBackground);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.ClassiqueDefinition, background, DateTimeOffset.UnixEpoch);

        int y = (rect.Top + rect.Bottom) / 2;
        Assert.True(frame.GetPixel(rect.Left, y).Red > frame.GetPixel(rect.Left + 4, y).Red + 5);
        Assert.Equal(ClassiqueBackground, frame.GetPixel(rect.Left - 1, y));
    }

    [Fact]
    public void Compose_UsesThemeGrid()
    {
        var id = Guid.NewGuid();
        var module = new FakeModule();
        using var background = new ColorBackground(Themes.Pixel.Theme.Background);

        using var frame = new FrameComposer().Compose(
            [Place(id, 0, 0, 1, 1)],
            new Dictionary<Guid, IModule> { [id] = module }, Themes.Pixel, background, DateTimeOffset.UnixEpoch);

        var call = Assert.Single(module.RenderCalls);
        Assert.Equal(18, call.Bounds.Left);
        Assert.Equal(18, call.Bounds.Top);
        Assert.Same(Themes.Pixel.Theme, call.Theme);
    }

    [Fact]
    public void Compose_Glass_ReusesBackdropForSameFrame()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 4, 0, 4, 2), Place(Guid.NewGuid(), 10, 1, 4, 3)]);
        var modules = new Dictionary<Guid, IModule> { [id] = new FakeModule() };
        using var background = new StubBackground(SKColor.Parse("#336699"));
        var composer = new FrameComposer();

        composer.Compose(layout.Views[0].Placements, modules, Themes.LiquidGlass, background, DateTimeOffset.UnixEpoch).Dispose();
        composer.Compose(layout.Views[0].Placements, modules, Themes.LiquidGlass, background, DateTimeOffset.UnixEpoch).Dispose();
        Assert.Equal(1, composer.BackdropComputations);

        background.Replace(StubBackground.Make(c => c.Clear(SKColor.Parse("#993366"))));
        composer.Compose(layout.Views[0].Placements, modules, Themes.LiquidGlass, background, DateTimeOffset.UnixEpoch).Dispose();
        Assert.Equal(2, composer.BackdropComputations);
    }

    [Fact]
    public void Compose_NotGlass_NeverBlurs()
    {
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 4, 0, 4, 2)]);
        using var background = new StubBackground(SKColor.Parse("#336699"));
        var composer = new FrameComposer();

        composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.ClassiqueDefinition, background, DateTimeOffset.UnixEpoch).Dispose();
        composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.Pixel, background, DateTimeOffset.UnixEpoch).Dispose();

        Assert.Equal(0, composer.BackdropComputations);
    }

    [Fact]
    public void Glass_ShowsBlurredBackground()
    {
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 6, 0, 4, 4)]);
        var rect = GridGeometry.ModuleRect(6, 0, 4, 4);
        Assert.True(rect.Left < 960 && rect.Right > 960);
        using var background = new StubBackground(new SKColor(255, 0, 0), new SKColor(0, 0, 255));

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.LiquidGlass, background, DateTimeOffset.UnixEpoch);

        var center = frame.GetPixel(960, (rect.Top + rect.Bottom) / 2);
        Assert.True(center.Red > 40 && center.Blue > 40, $"mélange attendu, obtenu {center}");
        // Loin de la limite, le fond reste proche de sa couleur (rouge à gauche), teinte et reflet compris.
        var leftOfModule = frame.GetPixel(5, 200);
        Assert.Equal(new SKColor(255, 0, 0), leftOfModule);
    }

    [Fact]
    public void Pixel_DrawsBorderAndOffsetShadow()
    {
        var bg = new SKColor(112, 112, 112);
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2, Themes.Pixel.Grid);
        using var background = new ColorBackground(bg);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Themes.Pixel, background, DateTimeOffset.UnixEpoch);

        int midY = (rect.Top + rect.Bottom) / 2;
        Assert.Equal(SKColor.Parse("#2E2850"), frame.GetPixel(rect.Left - 2, midY));
        Assert.Equal(Themes.Pixel.Theme.Panel, frame.GetPixel(rect.Left + 10, midY));
        var shadow = frame.GetPixel(rect.Right + 10, rect.Bottom + 10);
        Assert.True(shadow.Red < bg.Red - 20, $"ombre attendue, obtenu {shadow}");
        Assert.Equal(bg, frame.GetPixel(rect.Left - 8, rect.Top - 8)); // l'ombre est décalée vers le bas à droite
    }

    [Fact]
    public void Pixel_ModuleClipIsAPlainRect()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2, Themes.Pixel.Grid);
        using var background = new ColorBackground(Themes.Pixel.Theme.Background);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule> { [id] = FillModule(SKColors.Red) }, Themes.Pixel, background, DateTimeOffset.UnixEpoch);

        Assert.Equal(SKColors.Red, frame.GetPixel(rect.Left, rect.Top));
        Assert.Equal(SKColors.Red, frame.GetPixel(rect.Right - 1, rect.Bottom - 1));
        Assert.NotEqual(SKColors.Red, frame.GetPixel(rect.Left - 1, rect.Top));
    }

    [Fact]
    public void Glass_ModuleClipFollowsTheRoundedCorners()
    {
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        using var background = new ColorBackground(SKColor.Parse("#241A3D"));

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule> { [id] = FillModule(SKColors.Red) }, Themes.LiquidGlass, background, DateTimeOffset.UnixEpoch);

        Assert.NotEqual(SKColors.Red, frame.GetPixel(rect.Left, rect.Top));
        Assert.Equal(SKColors.Red, frame.GetPixel(rect.Left + 60, rect.Top + 60));
    }

    [Theory]
    [InlineData("classique")]
    [InlineData("liquid-glass")]
    [InlineData("pixel")]
    public void Compose_ModuleThrows_ErrorPanelInTheThemeStyle(string themeId)
    {
        var def = Themes.Find(themeId)!;
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 2, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(2, 1, 4, 2, def.Grid);
        var junk = new FakeModule((canvas, ctx) =>
        {
            canvas.DrawRect(ctx.Bounds, new SKPaint { Color = SKColors.Lime });
            throw new InvalidOperationException("raté");
        });
        using var background = new ColorBackground(def.DefaultBackground.Color);
        var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }));

        using var broken = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule> { [id] = junk }, def, background, DateTimeOffset.UnixEpoch);
        using var clean = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);

        // Ce que le module a peint avant d'échouer a disparu : hors texte, le panneau est celui d'un module absent.
        Assert.NotEqual(SKColors.Lime, broken.GetPixel(rect.Left + 20, rect.Top + 20));
        Assert.Equal(clean.GetPixel(rect.Left + 20, rect.Top + 20), broken.GetPixel(rect.Left + 20, rect.Top + 20));
        Assert.Equal(clean.GetPixel(rect.Left - 3, rect.MidY), broken.GetPixel(rect.Left - 3, (rect.Top + rect.Bottom) / 2));

        bool foundWarning = false;
        for (int y = rect.Top; y < rect.Bottom && !foundWarning; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var p = broken.GetPixel(x, y);
                if (Math.Abs(p.Red - def.Theme.Warning.Red) < 30 && Math.Abs(p.Green - def.Theme.Warning.Green) < 30 && Math.Abs(p.Blue - def.Theme.Warning.Blue) < 30)
                {
                    foundWarning = true;
                    break;
                }
            }
        Assert.True(foundWarning);
    }

    [Fact]
    public void Panel_BrightBackdrop_IsDarkenedForLegibility()
    {
        var def = Themes.ClassiqueDefinition with { Theme = Themes.Classique with { PanelOpacity = 0f } };
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        using var background = new ColorBackground(SKColors.White);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);

        double sum = 0;
        int count = 0;
        for (int y = rect.Top + 5; y < rect.Bottom - 5; y += 3)
            for (int x = rect.Left + 5; x < rect.Right - 5; x += 3, count++)
                sum += Luminance(frame.GetPixel(x, y));
        Assert.InRange(sum / count, 0.0, 0.36);
        Assert.NotEqual(SKColors.White, frame.GetPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2));
    }

    [Fact]
    public void Panel_MostlyDarkBackdropWithOneBrightCell_IsDarkenedUnderTheBrightCell()
    {
        // La moyenne du panneau est basse (un carré blanc sur du noir) : l'ancienne garde ne faisait rien.
        var def = Themes.ClassiqueDefinition with { Theme = Themes.Classique with { PanelOpacity = 0f } };
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        int cx = (rect.Left + rect.Right) / 2, cy = (rect.Top + rect.Bottom) / 2;
        using var bright = new StubBackground(StubBackground.Make(canvas =>
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(SKRect.Create(cx - 120, cy - 100, 240, 200), paint); // plus large que deux cellules de mesure
        }));

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, bright, DateTimeOffset.UnixEpoch);

        Assert.InRange(Luminance(frame.GetPixel(cx, cy)), 0.0, 0.36);
    }

    [Fact]
    public void Panel_DarkBackdrop_KeepsUserOpacity()
    {
        var def = Themes.ClassiqueDefinition with { Theme = Themes.Classique with { PanelOpacity = 0.3f } };
        var layout = DashboardLayout.SingleView(0, [Place(Guid.NewGuid(), 4, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        using var background = new ColorBackground(ClassiqueBackground);

        using var frame = new FrameComposer().Compose(
            layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);

        var panel = def.Theme.Panel;
        var expected = new SKColor(
            (byte)Math.Round(panel.Red * 0.3 + ClassiqueBackground.Red * 0.7),
            (byte)Math.Round(panel.Green * 0.3 + ClassiqueBackground.Green * 0.7),
            (byte)Math.Round(panel.Blue * 0.3 + ClassiqueBackground.Blue * 0.7));
        AssertClose(expected, frame.GetPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2));
    }

    // ---- Liquid Glass avancé (plan 9, tâche 3) ----

    [Fact]
    public void ErrorPanel_InAdvancedTheme_DrawsGlassAndMessage()
    {
        // Point de vigilance 5 : un module qui lève dans Render.
        var def = GlassTesting.Advanced();
        var id = Guid.NewGuid();
        var layout = DashboardLayout.SingleView(0, [Place(id, 2, 1, 4, 2)]);
        var rect = GridGeometry.ModuleRect(2, 1, 4, 2, def.Grid);
        var junk = new FakeModule((canvas, ctx) =>
        {
            canvas.DrawRect(ctx.Bounds, new SKPaint { Color = SKColors.Lime });
            throw new InvalidOperationException("raté");
        });
        using var background = new AuroraBackground();
        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }));

        using var broken = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule> { [id] = junk }, def, background, DateTimeOffset.UnixEpoch);
        using var clean = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);
        using var bare = composer.Compose([], new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);

        // Hors texte, la case est celle d'un module absent : le verre et sa teinte, sans ce que le module avait peint.
        Assert.Equal(clean.GetPixel(rect.Left + 20, rect.Top + 20), broken.GetPixel(rect.Left + 20, rect.Top + 20));
        Assert.Equal(clean.GetPixel(rect.Left + 3, rect.Top + 60), broken.GetPixel(rect.Left + 3, rect.Top + 60));
        Assert.NotEqual(bare.GetPixel(rect.Left + 20, rect.Top + 20), broken.GetPixel(rect.Left + 20, rect.Top + 20));
        // L'ombre, hors de la case, est restée.
        Assert.Equal(clean.GetPixel(rect.Left - 3, (rect.Top + rect.Bottom) / 2), broken.GetPixel(rect.Left - 3, (rect.Top + rect.Bottom) / 2));
        Assert.NotEqual(bare.GetPixel(rect.Left - 3, (rect.Top + rect.Bottom) / 2), broken.GetPixel(rect.Left - 3, (rect.Top + rect.Bottom) / 2));

        bool foundWarning = false;
        for (int y = rect.Top; y < rect.Bottom && !foundWarning; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var p = broken.GetPixel(x, y);
                if (Math.Abs(p.Red - def.Theme.Warning.Red) < 30 && Math.Abs(p.Green - def.Theme.Warning.Green) < 30 && Math.Abs(p.Blue - def.Theme.Warning.Blue) < 30)
                {
                    foundWarning = true;
                    break;
                }
            }
        Assert.True(foundWarning);
        Assert.Equal(1, composer.GlassComputations);
    }

    /// <summary>Attend que le fil à part ait annoncé <paramref name="count"/> couches.</summary>
    private static void WaitForGlass(Func<int> ready, int count) =>
        Assert.True(SpinWait.SpinUntil(() => ready() >= count, TimeSpan.FromSeconds(30)), $"couches prêtes : {ready()} sur {count}");

    [Fact]
    public void BackgroundMode_ShowsFallbackThenLayer()
    {
        var placements = GlassTesting.TwoCases();
        var clair = GlassTesting.Advanced();
        var prisme = GlassTesting.Advanced(GlassPresets.Prisme);
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var sync = new FrameComposer();
        using var composer = new FrameComposer(backgroundGlass: true);
        int ready = 0;
        composer.GlassReady += () => Interlocked.Increment(ref ready);

        // 1. Rien n'est prêt : le verre simple de Liquid Glass, sans attendre.
        using var first = composer.Compose(placements, GlassTesting.NoModules, clair, background, GlassTesting.T0);
        using var simple = sync.Compose(placements, GlassTesting.NoModules, clair with { Panel = Themes.LiquidGlass.Panel }, background, GlassTesting.T0);
        Assert.True(SameBytes(simple, first));

        // 2. Les couches arrivent : la même image que sans fil à part.
        WaitForGlass(() => Volatile.Read(ref ready), 2);
        using var second = composer.Compose(placements, GlassTesting.NoModules, clair, background, GlassTesting.T0);
        using var expected = sync.Compose(placements, GlassTesting.NoModules, clair, background, GlassTesting.T0);
        Assert.True(SameBytes(expected, second));
        Assert.Equal(2, composer.GlassComputations);

        // 3. Un réglage change : la couche précédente de chaque case reste à l'écran pendant le calcul.
        using var third = composer.Compose(placements, GlassTesting.NoModules, prisme, background, GlassTesting.T0);
        Assert.True(SameBytes(second, third));

        // 4. Puis la nouvelle.
        WaitForGlass(() => Volatile.Read(ref ready), 4);
        using var fourth = composer.Compose(placements, GlassTesting.NoModules, prisme, background, GlassTesting.T0);
        using var expectedPrisme = sync.Compose(placements, GlassTesting.NoModules, prisme, background, GlassTesting.T0);
        Assert.True(SameBytes(expectedPrisme, fourth));
        Assert.Equal(2, composer.GlassLayers);
    }

    [Fact]
    public void BackgroundMode_RapidSettingChanges_SettleOnTheLastOnes()
    {
        // Un curseur qu'on bouge : chaque image demande d'autres réglages avant que les précédents soient prêts.
        var placements = GlassTesting.TwoCases();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var sync = new FrameComposer();
        using var composer = new FrameComposer(backgroundGlass: true);
        ThemeDefinition last = GlassTesting.Advanced();
        for (int i = 0; i <= 20; i++)
        {
            float refraction = 0.3f + i * 0.02f;
            last = GlassTesting.Advanced(change: s => s with { Refraction = refraction });
            composer.Compose(placements, GlassTesting.NoModules, last, background, GlassTesting.T0).Dispose();
            Thread.Sleep(5);
        }

        using var expected = sync.Compose(placements, GlassTesting.NoModules, last, background, GlassTesting.T0);
        bool settled = SpinWait.SpinUntil(
            () =>
            {
                using var frame = composer.Compose(placements, GlassTesting.NoModules, last, background, GlassTesting.T0);
                return SameBytes(expected, frame);
            },
            TimeSpan.FromSeconds(30));

        Assert.True(settled, "le verre des derniers réglages n'est jamais arrivé");
        Assert.Equal(2, composer.GlassLayers);
        Assert.InRange(composer.GlassComputations, 2, 2 * 21); // les demandes dépassées avant d'avoir commencé sont sautées
    }

    [Fact]
    public void BackgroundMode_AnimatedBackground_ComputesAtOnce()
    {
        var placements = GlassTesting.TwoCases();
        var theme = GlassTesting.Advanced();
        using var background = new StubBackground(GlassTesting.Detailed());
        using var sync = new FrameComposer();
        using var composer = new FrameComposer(backgroundGlass: true);

        using var frame = composer.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0);
        using var expected = sync.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0);

        Assert.True(SameBytes(expected, frame));
        Assert.Equal(2, composer.GlassComputations);
    }

    /// <summary>Attend l'arrêt du fil à part, puis vérifie qu'il n'a rien laissé : aucune image en vie, aucun calcul en échec.</summary>
    private static void AssertNothingLeft(LiquidGlassLayers layers)
    {
        Assert.True(SpinWait.SpinUntil(() => !layers.IsWorking, TimeSpan.FromSeconds(30)), "le fil à part ne s'arrête pas");
        Assert.Equal(0, layers.WorkerFailures);
        Assert.Equal(0, layers.LiveImages);
        Assert.Equal(0, layers.LiveLayers);
    }

    [Fact]
    public void BackgroundMode_DisposedWhileComputing_LeavesNothingAndDoesNotFail()
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        for (int i = 0; i < 5; i++)
        {
            var background = new FixedImageBackground(GlassTesting.Detailed());
            var composer = new FrameComposer(backgroundGlass: true);
            composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(GlassPresets.Givre), background, GlassTesting.T0).Dispose();
            var layers = composer.Glass!;
            Assert.True(layers.IsWorking); // cinq cases viennent de partir : le fil à part est en plein calcul
            Assert.True(layers.LiveImages > 0); // au moins la copie du fond

            // Comme le service : le compositeur d'abord, puis le fond, sans attendre le fil à part.
            composer.Dispose();
            background.Dispose();

            AssertNothingLeft(layers);
        }
    }

    [Fact]
    public void BackgroundMode_ThemeLeftWhileComputing_DropsTheLateLayers()
    {
        var placements = DashboardLayout.Default.Views[0].Placements;
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer(backgroundGlass: true);

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(GlassPresets.Givre), background, GlassTesting.T0).Dispose();
        var layers = composer.Glass!;
        Assert.True(layers.IsWorking);

        composer.Compose(placements, GlassTesting.NoModules, Themes.LiquidGlass, background, GlassTesting.T0).Dispose();

        Assert.Null(composer.Glass);
        Assert.Equal(0, composer.GlassLayers);
        AssertNothingLeft(layers); // la couche finie après le départ du thème a bien été libérée
    }

    [Fact]
    public void BackgroundMode_AfterTheLayersArrive_OnlyTheLayersStayAlive()
    {
        var placements = GlassTesting.TwoCases();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer(backgroundGlass: true);

        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(GlassPresets.Givre), background, GlassTesting.T0).Dispose();
        var layers = composer.Glass!;
        Assert.True(SpinWait.SpinUntil(() => !layers.IsWorking, TimeSpan.FromSeconds(30)));
        composer.Compose(placements, GlassTesting.NoModules, GlassTesting.Advanced(GlassPresets.Givre), background, GlassTesting.T0).Dispose();

        // Deux verres et deux ombres : la copie du fond et son flou sont partis avec le dernier calcul.
        Assert.Equal(4, layers.LiveImages);
        Assert.Equal(0, layers.WorkerFailures);
    }

    /// <summary>Distance signée au rectangle arrondi (négative dedans), au centre du pixel.</summary>
    private static double RoundRectDistance(SKRectI rect, double radius, int x, int y)
    {
        double px = Math.Abs(x + 0.5 - (rect.Left + rect.Right) / 2.0) - rect.Width / 2.0 + radius;
        double py = Math.Abs(y + 0.5 - (rect.Top + rect.Bottom) / 2.0) - rect.Height / 2.0 + radius;
        double outside = Math.Sqrt(Math.Max(px, 0) * Math.Max(px, 0) + Math.Max(py, 0) * Math.Max(py, 0));
        return outside + Math.Min(Math.Max(px, py), 0) - radius;
    }

    [Fact]
    public void AdvancedTheme_GlassRadius_ClipsTheModuleAndTheErrorPanel()
    {
        // Un thème qui n'est pas passé par AppearanceResolver : le rayon des cases est resté à 30, celui du verre est à 49.
        var def = GlassTesting.Advanced(change: s => s with { CornerRadius = 49f, ShadowOpacity = 1f, ShadowSpread = 20f });
        Assert.Equal(30f, def.Theme.CornerRadius);
        var id = Guid.NewGuid();
        var placements = new[] { Place(id, 2, 1, 4, 2) };
        var rect = GridGeometry.ModuleRect(2, 1, 4, 2, def.Grid);
        var filling = FillModule(SKColors.Lime);
        var junk = new FakeModule((canvas, ctx) =>
        {
            canvas.DrawRect(ctx.Bounds, new SKPaint { Color = SKColors.Lime });
            throw new InvalidOperationException("raté");
        });
        using var background = new ColorBackground(new SKColor(200, 200, 200));
        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }));

        using var filled = composer.Compose(placements, new Dictionary<Guid, IModule> { [id] = filling }, def, background, DateTimeOffset.UnixEpoch);
        using var broken = composer.Compose(placements, new Dictionary<Guid, IModule> { [id] = junk }, def, background, DateTimeOffset.UnixEpoch);
        using var clean = composer.Compose(placements, new Dictionary<Guid, IModule>(), def, background, DateTimeOffset.UnixEpoch);

        // Hors du verre arrondi à 49 (à plus d'un pixel du bord), rien ne vient du module ni du panneau d'erreur : on y voit
        // le fond et l'ombre de la case, comme sans module.
        int slivers = 0, shadowed = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (RoundRectDistance(rect, 49, x, y) <= 1.0) continue;

                Assert.Equal(clean.GetPixel(x, y), filled.GetPixel(x, y));
                Assert.Equal(clean.GetPixel(x, y), broken.GetPixel(x, y));
                if (RoundRectDistance(rect, 30, x, y) < -1.0)
                {
                    slivers++; // dans l'ancien rognage à 30, hors du verre à 49
                    if (clean.GetPixel(x, y) != new SKColor(200, 200, 200)) shadowed++;
                }
            }

        Assert.True(slivers > 100, $"pixels entre les deux rayons : {slivers}");
        Assert.Equal(slivers, shadowed); // l'ombre de la case y est, et le panneau d'erreur ne l'a pas effacée

        // Dedans, le module remplit bien la case.
        Assert.Equal(SKColors.Lime, filled.GetPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2));
        Assert.Equal(SKColors.Lime, filled.GetPixel(rect.Left + 20, rect.Top + 20)); // dans le coin, à l'intérieur du rayon de 49
    }
}
