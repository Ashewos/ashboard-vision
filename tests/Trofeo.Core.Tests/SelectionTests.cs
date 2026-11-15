using System.Diagnostics;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using Xunit.Abstractions;

namespace Trofeo.Core.Tests;

/// <summary>
/// La pastille de l'élément choisi (plan 9, tâche 4) : <see cref="ModuleDrawing.DrawSelection"/> dessine l'onglet d'avant dans
/// les trois autres thèmes ; dans « Liquid Glass avancé », elle note un rectangle, et le service dessine la pastille de verre
/// sous tout ce que le module dessine.
/// </summary>
public class SelectionTests(ITestOutputHelper output)
{
    private sealed class Sink : ISelectionSink
    {
        public List<SKRect> Rects { get; } = [];
        public void Add(SKRect caseLocalRect, float opacity = 1f) => Rects.Add(caseLocalRect);
    }

    private static readonly SKRect Bounds = SKRect.Create(40, 30, 300, 200);

    private static ModuleRenderContext Context(Theme theme, float scale = 1f, ISelectionSink? sink = null) =>
        new(Bounds, new GridSize(4, 2), theme, scale, GlassTesting.T0) { Selections = sink };

    private static SKBitmap Draw(Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(400, 260);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        return bitmap;
    }

    // ---- L'aide ----

    [Fact]
    public void OtherThemes_DrawSelection_EqualsOldTab()
    {
        // Un rectangle à des positions fractionnaires, comme un onglet à l'échelle 1,25.
        var rect = new SKRect(56.25f, 91.5f, 271.75f, 149f);
        foreach (var theme in new[] { Themes.Classique, Themes.LiquidGlass.Theme, Themes.Pixel.Theme })
        {
            foreach (float scale in new[] { 1f, 1.25f })
            {
                // Les deux Fill d'AgentPainter.DrawTab, tels qu'ils étaient avant la tâche 4.
                using var expected = Draw(canvas =>
                {
                    var pixel = theme.Shape == ThemeShape.Pixel;
                    using (var paint = new SKPaint { Color = theme.Surface, Style = SKPaintStyle.Fill })
                    {
                        canvas.DrawRect(rect, paint);
                    }
                    var marker = pixel ? 4f : 3f;
                    using (var paint = new SKPaint { Color = pixel ? theme.Highlight : theme.Accent, Style = SKPaintStyle.Fill })
                    {
                        canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + marker * scale, rect.Bottom), paint);
                    }
                });
                var sink = new Sink();
                using var actual = Draw(canvas => ModuleDrawing.DrawSelection(canvas, rect, Context(theme, scale, sink)));

                Assert.True(GlassTesting.SameBytes(expected, actual), $"{theme.Id} à l'échelle {scale}");
                Assert.Empty(sink.Rects); // hors de Liquid Glass avancé, rien n'est noté
                Assert.True(actual.GetPixel(200, 120).Alpha > 0, "l'aplat est dessiné");
            }
        }
    }

    [Fact]
    public void AdvancedTheme_RecordsCaseLocalRect_DrawsNothing()
    {
        var rect = new SKRect(60, 90, 260, 136);
        var sink = new Sink();

        using var actual = Draw(canvas => ModuleDrawing.DrawSelection(canvas, rect, Context(Themes.LiquidGlassAdvanced.Theme, 1f, sink)));

        Assert.All(actual.Pixels, p => Assert.Equal(0, p.Alpha));
        Assert.Equal([new SKRect(20, 60, 220, 106)], sink.Rects);
    }

    [Fact]
    public void AdvancedTheme_NoSink_DrawsLocalPill()
    {
        var rect = new SKRect(60, 90, 260, 136);

        using var actual = Draw(canvas => ModuleDrawing.DrawSelection(canvas, rect, Context(Themes.LiquidGlassAdvanced.Theme)));

        int inside = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                if (actual.GetPixel(x, y).Alpha == 0) continue;

                Assert.True(x >= 60 && x < 260 && y >= 90 && y < 136, $"pixel peint hors de la pastille : ({x}, {y})");
                inside++;
            }
        }
        Assert.True(inside > 200 * 46 / 2, $"pixels peints : {inside}");
        Assert.Equal(0, actual.GetPixel(60, 90).Alpha); // une pilule : ses coins sont vides
        Assert.True(actual.GetPixel(160, 91).Alpha > actual.GetPixel(160, 113).Alpha, "le trait intérieur est plus marqué que le fond");
        Assert.True(actual.GetPixel(160, 91).Alpha > actual.GetPixel(160, 134).Alpha, "le trait est plus clair en haut qu'en bas");
    }

    // ---- Le compositeur ----

    private static SKRect RectOf(ModulePlacement placement, ThemeDefinition theme) => ModulePlace.Rect(placement, theme);

    /// <summary>Les rectangles d'une pastille, relatifs à la case, donnés par le test.</summary>
    private static IModule Selecting(Func<SKRect, SKRect[]> pills, Action<SKCanvas, ModuleRenderContext>? before = null,
        Action<SKCanvas, ModuleRenderContext>? after = null) => new FakeModule((canvas, ctx) =>
    {
        before?.Invoke(canvas, ctx);
        foreach (var pill in pills(ctx.Bounds)) ModuleDrawing.DrawSelection(canvas, pill, ctx);
        after?.Invoke(canvas, ctx);
    });

    private static IReadOnlyDictionary<Guid, IModule> For(ModulePlacement placement, IModule module) =>
        new Dictionary<Guid, IModule> { [placement.InstanceId] = module };

    private static SKRect Tab(SKRect b) => SKRect.Create(b.Left + 20, b.Top + 60, 220, 46);

    private static int Differences(SKBitmap a, SKBitmap b, Func<int, int, bool> where)
    {
        int count = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (where(x, y) && a.GetPixel(x, y) != b.GetPixel(x, y)) count++;
        return count;
    }

    [Fact]
    public void Composer_PillIsUnderEverythingTheModuleDraws()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        var red = new SKColor(255, 0, 0);
        SKRect Red(SKRect b) => SKRect.Create(b.Left + 40, b.Top + 70, 30, 20);

        void Before(SKCanvas canvas, ModuleRenderContext ctx)
        {
            using var paint = new SKPaint { Color = red };
            canvas.DrawRect(Red(ctx.Bounds), paint); // dessiné AVANT l'appel : la pastille passe quand même dessous
        }
        void After(SKCanvas canvas, ModuleRenderContext ctx)
        {
            using var font = ModuleDrawing.Font(ctx.Theme, 22f);
            font.Edging = SKFontEdging.Alias; // des pixels pleinement verts, ou pas de pixel
            using var paint = new SKPaint { Color = new SKColor(0, 255, 0), IsAntialias = false };
            canvas.DrawText("projet", ctx.Bounds.Left + 90, ctx.Bounds.Top + 92, SKTextAlign.Left, font, paint);
        }

        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        using var with = composer.Compose([placement], For(placement, Selecting(b => [Tab(b)], Before, After)), theme, background, GlassTesting.T0);
        using var without = composer.Compose([placement], For(placement, Selecting(_ => [], Before, After)), theme, background, GlassTesting.T0);

        var bounds = RectOf(placement, theme);
        var redRect = Red(bounds);
        var tab = Tab(bounds);
        for (int y = (int)redRect.Top; y < redRect.Bottom; y++)
            for (int x = (int)redRect.Left; x < redRect.Right; x++)
                Assert.Equal(red, with.GetPixel(x, y));

        // Le texte : ses pixels verts sont les mêmes, avec ou sans pastille.
        int green = 0;
        for (int y = (int)tab.Top; y < tab.Bottom; y++)
        {
            for (int x = (int)tab.Left; x < tab.Right; x++)
            {
                if (without.GetPixel(x, y) != new SKColor(0, 255, 0)) continue;

                green++;
                Assert.Equal(new SKColor(0, 255, 0), with.GetPixel(x, y));
            }
        }
        Assert.True(green > 50, $"pixels du texte : {green}");

        // La pastille est bien là, dans son rectangle et nulle part ailleurs.
        bool InTab(int x, int y) => x >= tab.Left && x < tab.Right && y >= tab.Top && y < tab.Bottom;
        Assert.True(Differences(with, without, InTab) > 500, "la pastille ne se voit pas");
        Assert.Equal(0, Differences(with, without, (x, y) => !InTab(x, y)));
        Assert.Equal(1, composer.GlassPillComputations);
    }

    [Fact]
    public void Composer_PillIsClippedToCase_AndTinyPillIgnored()
    {
        // Point de vigilance 3.
        var placements = GlassTesting.TwoCases();
        var theme = GlassTesting.Advanced();
        var bounds = RectOf(placements[0], theme);
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        using var none = composer.Compose(placements, For(placements[0], Selecting(_ => [])), theme, background, GlassTesting.T0);

        // Une pastille qui déborde sur la case voisine et sur le coin arrondi : rien ne change hors de la case.
        SKRect[] Spilling(SKRect b) => [new SKRect(b.Right - 80, b.Top - 30, b.Right + 150, b.Top + 40)];
        using var spilled = composer.Compose(placements, For(placements[0], Selecting(Spilling)), theme, background, GlassTesting.T0);
        bool InCase(int x, int y) => x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
        Assert.True(Differences(spilled, none, InCase) > 500, "la partie de la pastille dans la case ne se voit pas");
        Assert.Equal(0, Differences(spilled, none, (x, y) => !InCase(x, y)));
        Assert.Equal(none.GetPixel((int)bounds.Right - 1, (int)bounds.Top), spilled.GetPixel((int)bounds.Right - 1, (int)bounds.Top)); // le coin arrondi
        int computed = composer.GlassPillComputations;
        Assert.Equal(1, computed);

        // Moins de 4 px de haut ou de large, vide, à l'envers, hors de la case ou pas un nombre : ignorée, sans calcul.
        SKRect[] Tiny(SKRect b) =>
        [
            SKRect.Create(b.Left + 20, b.Top + 20, 200, 3),
            SKRect.Create(b.Left + 20, b.Top + 40, 3, 40),
            SKRect.Create(b.Left + 20, b.Top + 60, 0, 0),
            new SKRect(b.Left + 200, b.Top + 100, b.Left + 100, b.Top + 60),
            SKRect.Create(b.Right + 300, b.Top + 20, 100, 40),
            new SKRect(float.NaN, b.Top, b.Left + 100, b.Top + 40),
            new SKRect(b.Left, b.Top, float.PositiveInfinity, b.Top + 40),
            new SKRect(b.Left - 100, b.Top + 20, b.Left + 3, b.Top + 60), // 3 px seulement dans la case
        ];
        using var tiny = composer.Compose(placements, For(placements[0], Selecting(Tiny)), theme, background, GlassTesting.T0);
        Assert.True(GlassTesting.SameBytes(none, tiny));
        Assert.Equal(computed, composer.GlassPillComputations);
    }

    [Fact]
    public void Composer_ExtremePill_DoesNotThrowNorSpill()
    {
        // Une case 1 × 1 aux réglages extrêmes, et une pastille bien plus grande qu'elle.
        var placement = GlassTesting.Place(3, 1, 1, 1);
        var theme = GlassTesting.Advanced(change: s => s with { CornerRadius = 49f, ZRadius = 100f, Bevel = GlassBevel.Dome, Blur = 1f });
        var bounds = RectOf(placement, theme);
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        var errors = new List<string>();
        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, errors.Add));
        using var none = composer.Compose([placement], For(placement, Selecting(_ => [])), theme, background, GlassTesting.T0);

        using var frame = composer.Compose(
            [placement], For(placement, Selecting(b => [new SKRect(-1e9f, -1e9f, 1e9f, 1e9f), SKRect.Create(b.Left + 10, b.Top + 10, 4, 4)])),
            theme, background, GlassTesting.T0);

        Assert.Empty(errors);
        Assert.Equal(2, composer.GlassPillComputations);
        Assert.Equal(0, Differences(frame, none, (x, y) => !(x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)));
        Assert.All(frame.Pixels, p => Assert.Equal(255, p.Alpha));
    }

    [Fact]
    public void Composer_PillsAreCached()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        var modules = For(placement, Selecting(b => [Tab(b)]));
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();

        using var first = composer.Compose([placement], modules, theme, background, GlassTesting.T0);
        for (int i = 1; i < 5; i++)
        {
            using var frame = composer.Compose([placement], modules, theme, background, GlassTesting.T0 + TimeSpan.FromSeconds(i));
            Assert.True(GlassTesting.SameBytes(first, frame));
        }

        Assert.Equal(1, composer.GlassPillComputations);
        Assert.Equal(1, composer.GlassComputations);
    }

    /// <summary><paramref name="count"/> pastilles de 40 × 20, sur quatre colonnes.</summary>
    private static SKRect[] Grid(SKRect b, int count) =>
        [.. Enumerable.Range(0, count).Select(i => SKRect.Create(b.Left + 20 + i % 4 * 50, b.Top + 20 + i / 4 * 30, 40, 20))];

    [Fact]
    public void Composer_ManyPillsInOneCase_BlurTheCaseOnce()
    {
        // Givre floute : toutes les pastilles d'une case partagent la même image floutée de la case.
        var placements = GlassTesting.TwoCases();
        var theme = GlassTesting.Advanced(GlassPresets.Givre);
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        var modules = new Dictionary<Guid, IModule>
        {
            [placements[0].InstanceId] = Selecting(b => Grid(b, 8)),
            [placements[1].InstanceId] = Selecting(b => Grid(b, 3)),
        };

        composer.Compose(placements, modules, theme, background, GlassTesting.T0).Dispose();
        var layers = composer.Glass!;
        Assert.Equal(11, layers.PillComputations);
        Assert.Equal(2, layers.PillBlurComputations); // une fois par case, pas une fois par pastille

        // Après l'image, il ne reste que les couches des cases et les pastilles : ni image de la case, ni flou.
        using var bare = new FrameComposer();
        bare.Compose(placements, GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose();
        Assert.Equal(bare.Glass!.LiveImages + 11, layers.LiveImages);

        // Image suivante, rien n'a changé : ni pastille ni flou refaits.
        composer.Compose(placements, modules, theme, background, GlassTesting.T0).Dispose();
        Assert.Equal(11, layers.PillComputations);
        Assert.Equal(2, layers.PillBlurComputations);

        // Une pastille de plus dans la première case : un seul flou de plus, pour elle seule.
        modules[placements[0].InstanceId] = Selecting(b => Grid(b, 9));
        composer.Compose(placements, modules, theme, background, GlassTesting.T0).Dispose();
        Assert.Equal(12, layers.PillComputations);
        Assert.Equal(3, layers.PillBlurComputations);
        Assert.Equal(bare.Glass!.LiveImages + 12, layers.LiveImages);

        composer.Dispose();
        Assert.Equal(0, layers.LiveImages);
    }

    [Fact]
    public void Composer_PillsBeyondTheBudgetOfACase_AreIgnored()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        Assert.Equal(16, FrameComposer.MaxPillsPerCase);

        using var twenty = composer.Compose([placement], For(placement, Selecting(b => Grid(b, 20))), theme, background, GlassTesting.T0);
        Assert.Equal(16, composer.GlassPillComputations);

        using var sixteen = composer.Compose([placement], For(placement, Selecting(b => Grid(b, 16))), theme, background, GlassTesting.T0);
        Assert.True(GlassTesting.SameBytes(sixteen, twenty)); // la 17e et les suivantes n'ont rien peint
        Assert.Equal(16, composer.GlassPillComputations);

        // Un module qui change ses vingt rectangles à chaque image : toujours 16 calculs et 16 images au plus.
        using var bare = new FrameComposer();
        bare.Compose([placement], GlassTesting.NoModules, theme, background, GlassTesting.T0).Dispose();
        int shift = 0;
        var moving = For(placement, Selecting(b => [.. Grid(b, 20).Select(r => { r.Offset(shift, 0); return r; })]));
        for (shift = 1; shift <= 3; shift++)
        {
            composer.Compose([placement], moving, theme, background, GlassTesting.T0).Dispose();
            Assert.Equal(16 + 16 * shift, composer.GlassPillComputations);
            Assert.Equal(bare.Glass!.LiveImages + 16, composer.Glass!.LiveImages);
        }

        var layers = composer.Glass!;
        composer.Dispose();
        Assert.Equal(0, layers.LiveImages);
    }

    [Fact]
    public void Composer_PillFollowsTheSelection_AndFreesTheOldOne()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        int selected = 0;
        var modules = For(placement, Selecting(b => [SKRect.Create(b.Left + 20, b.Top + 20 + selected * 52, 220, 46)]));

        composer.Compose([placement], modules, theme, background, GlassTesting.T0).Dispose();
        int oneImage = composer.Glass!.LiveImages;
        selected = 1;
        composer.Compose([placement], modules, theme, background, GlassTesting.T0).Dispose();

        Assert.Equal(2, composer.GlassPillComputations);
        Assert.Equal(oneImage, composer.Glass!.LiveImages); // l'ancienne pastille est libérée

        // Plus de pastille du tout : plus rien en mémoire pour elle.
        composer.Compose([placement], For(placement, Selecting(_ => [])), theme, background, GlassTesting.T0).Dispose();
        Assert.Equal(oneImage - 1, composer.Glass!.LiveImages);

        var layers = composer.Glass!;
        composer.Dispose();
        Assert.Equal(0, layers.LiveImages);
    }

    [Fact]
    public void Composer_PillIsRecomputed_WhenTheTintOrTheBackgroundOrTheSettingsChange()
    {
        // La pastille déforme la case déjà peinte : elle ne doit jamais rester celle d'une autre teinte, d'un autre fond ou
        // d'autres réglages. Chaque image est comparée à celle d'un compositeur tout neuf.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var modules = For(placement, Selecting(b => [Tab(b)]));
        var clair = GlassTesting.Advanced() with { LegibilityGuard = false };
        ThemeDefinition Opacity(float o) => clair with { Theme = clair.Theme with { PanelOpacity = o } };
        using var detailed = new FixedImageBackground(GlassTesting.Detailed());
        using var white = new FixedImageBackground(StubBackground.Make(canvas => canvas.Clear(SKColors.White)));
        using var composer = new FrameComposer();

        var steps = new (ThemeDefinition Theme, IBackground Background)[]
        {
            (Opacity(0.2f), detailed),
            (Opacity(0.7f), detailed), // la teinte
            (Opacity(0.7f), white), // le fond
            (Opacity(0.7f) with { Theme = Opacity(0.7f).Theme with { Panel = new SKColor(90, 20, 20) } }, white), // la couleur du panneau
            (GlassTesting.Advanced(GlassPresets.Prisme) with { LegibilityGuard = false }, white), // les réglages
            (Opacity(0.2f), detailed), // retour au début
        };
        for (int i = 0; i < steps.Length; i++)
        {
            using var frame = composer.Compose([placement], modules, steps[i].Theme, steps[i].Background, GlassTesting.T0);
            using var fresh = new FrameComposer();
            using var expected = fresh.Compose([placement], modules, steps[i].Theme, steps[i].Background, GlassTesting.T0);
            Assert.True(GlassTesting.SameBytes(expected, frame), $"étape {i}");
            Assert.Equal(i + 1, composer.GlassPillComputations);
        }
    }

    [Fact]
    public void Composer_AnimatedBackground_PillFollowsEachFrame()
    {
        // Sans garde, l'image ne dépend pas du lissage : sur un fond animé, elle est celle du même fond, fixe.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced() with { LegibilityGuard = false };
        var modules = For(placement, Selecting(b => [Tab(b)]));
        static SKImage Red() => StubBackground.Make(canvas => canvas.Clear(new SKColor(200, 60, 60)));
        using var background = new StubBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        composer.Compose([placement], modules, theme, background, GlassTesting.T0).Dispose();

        background.Replace(Red());
        using var actual = composer.Compose([placement], modules, theme, background, GlassTesting.T0);

        using var still = new FixedImageBackground(Red());
        using var fresh = new FrameComposer();
        using var expected = fresh.Compose([placement], modules, theme, still, GlassTesting.T0);
        Assert.True(GlassTesting.SameBytes(expected, actual));
        Assert.Equal(2, composer.GlassPillComputations);
    }

    [Fact]
    public void Composer_TwoPills_DoNotDeformEachOther()
    {
        // Deux pastilles qui se chevauchent : chacune déforme la case, pas l'autre pastille. L'ordre ne compte donc que pour
        // la pose, et une pastille seule a les mêmes pixels là où l'autre ne passe pas.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        SKRect A(SKRect b) => SKRect.Create(b.Left + 20, b.Top + 40, 200, 46);
        SKRect B(SKRect b) => SKRect.Create(b.Left + 120, b.Top + 60, 200, 46);

        using var both = composer.Compose([placement], For(placement, Selecting(b => [A(b), B(b)])), theme, background, GlassTesting.T0);
        using var onlyB = composer.Compose([placement], For(placement, Selecting(b => [B(b)])), theme, background, GlassTesting.T0);

        var bRect = B(RectOf(placement, theme));
        // Loin des bouts arrondis de B (rayon 23), où B est opaque.
        Assert.Equal(0, Differences(both, onlyB, (x, y) => x >= bRect.Left + 24 && x < bRect.Right - 24 && y >= bRect.Top + 4 && y < bRect.Bottom - 4));
        using var none = composer.Compose([placement], For(placement, Selecting(_ => [])), theme, background, GlassTesting.T0);
        Assert.True(Differences(both, none, (x, y) => x >= bRect.Left + 24 && x < bRect.Right - 24 && y >= bRect.Top + 4 && y < bRect.Bottom - 4) > 500);
    }

    [Fact]
    public void Composer_ModuleThatThrows_GetsTheErrorPanel_WithoutPill()
    {
        // Point de vigilance 5 : le module note une pastille puis échoue. Le panneau d'erreur est celui d'un module qui échoue
        // tout de suite.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }));
        var failing = Selecting(b => [Tab(b)], after: (_, _) => throw new InvalidOperationException("boum"));

        using var frame = composer.Compose([placement], For(placement, failing), theme, background, GlassTesting.T0);
        using var expected = composer.Compose([placement], For(placement, new FakeModule(throwOnRender: true)), theme, background, GlassTesting.T0);

        Assert.True(GlassTesting.SameBytes(expected, frame));
        Assert.Equal(0, composer.GlassPillComputations);
    }

    [Fact]
    public void Composer_OtherThemes_DoNotRecord_AndDrawTheOldTab()
    {
        // Sans le drapeau, le module dessine directement sur la toile de l'écran : pas d'enregistrement, pas de puits.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        foreach (var theme in new[] { Themes.ClassiqueDefinition, Themes.LiquidGlass, Themes.Pixel })
        {
            ModuleRenderContext? seen = null;
            using var frame = composer.Compose(
                [placement], For(placement, Selecting(b => [Tab(b)], before: (_, ctx) => seen = ctx)), theme, background, GlassTesting.T0);

            Assert.Null(seen!.Selections);
            var tab = Tab(RectOf(placement, theme));
            var marker = theme.Theme.Shape == ThemeShape.Pixel ? theme.Theme.Highlight : theme.Theme.Accent;
            Assert.Equal(marker, frame.GetPixel((int)tab.Left + 1, (int)tab.MidY)); // le trait de l'onglet d'avant
            Assert.Equal(0, composer.GlassPillComputations);
        }
    }

    [Fact]
    public void Composer_FlagWithoutAdvancedGlass_DrawsTheLocalPill()
    {
        // Le drapeau sur un thème dont le panneau n'est pas le verre avancé : la pastille locale, toujours sous le module.
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = Themes.ClassiqueDefinition with { Theme = Themes.Classique with { ServiceDrawsSelection = true } };
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var composer = new FrameComposer();
        var red = new SKColor(255, 0, 0);
        void Before(SKCanvas canvas, ModuleRenderContext ctx)
        {
            using var paint = new SKPaint { Color = red };
            canvas.DrawRect(SKRect.Create(ctx.Bounds.Left + 40, ctx.Bounds.Top + 70, 30, 20), paint);
        }

        using var with = composer.Compose([placement], For(placement, Selecting(b => [Tab(b)], Before)), theme, background, GlassTesting.T0);
        using var without = composer.Compose([placement], For(placement, Selecting(_ => [], Before)), theme, background, GlassTesting.T0);

        var tab = Tab(RectOf(placement, theme));
        Assert.Equal(red, with.GetPixel((int)tab.Left + 30, (int)tab.Top + 20));
        Assert.NotEqual(without.GetPixel((int)tab.MidX, (int)tab.MidY), with.GetPixel((int)tab.MidX, (int)tab.MidY));
        Assert.Equal(0, Differences(with, without, (x, y) => !(x >= tab.Left && x < tab.Right && y >= tab.Top && y < tab.Bottom)));
    }

    [Fact]
    public void BackgroundMode_PillNeverWaits_AndFollowsTheLayer()
    {
        var placement = GlassTesting.Place(0, 0, 4, 2);
        var theme = GlassTesting.Advanced();
        var modules = For(placement, Selecting(b => [Tab(b)]));
        using var background = new FixedImageBackground(GlassTesting.Detailed());
        using var sync = new FrameComposer();
        using var composer = new FrameComposer(backgroundGlass: true);
        int ready = 0;
        composer.GlassReady += () => Interlocked.Increment(ref ready);

        // 1. Le verre de la case n'est pas prêt : la pastille déforme le verre simple qui la remplace, tout de suite.
        using var first = composer.Compose([placement], modules, theme, background, GlassTesting.T0);
        using var simple = sync.Compose([placement], For(placement, Selecting(_ => [])), theme with { Panel = Themes.LiquidGlass.Panel }, background, GlassTesting.T0);
        var tab = Tab(RectOf(placement, theme));
        Assert.Equal(1, composer.GlassPillComputations);
        Assert.True(Differences(first, simple, (x, y) => x >= tab.Left && x < tab.Right && y >= tab.Top && y < tab.Bottom) > 500);
        Assert.Equal(0, Differences(first, simple, (x, y) => !(x >= tab.Left && x < tab.Right && y >= tab.Top && y < tab.Bottom)));

        // 2. Le verre arrive : la pastille est refaite sur lui, comme sans fil à part.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ready) >= 1, TimeSpan.FromSeconds(30)));
        using var second = composer.Compose([placement], modules, theme, background, GlassTesting.T0);
        using var expected = sync.Compose([placement], modules, theme, background, GlassTesting.T0);
        Assert.True(GlassTesting.SameBytes(expected, second));
        Assert.Equal(2, composer.GlassPillComputations);
    }

    [Fact]
    [Trait("Category", "Bench")]
    public void PillCost()
    {
        // Mesure, sans rien exiger : un onglet de 220 × 46 dans une case 4 × 2, puis dans une case de tout l'écran.
        foreach (var (name, placement, preset) in new[]
        {
            ("case 4 × 2, Clair", GlassTesting.Place(0, 0, 4, 2), GlassPresets.Clair),
            ("case 4 × 2, Givre", GlassTesting.Place(0, 0, 4, 2), GlassPresets.Givre),
            ("case 16 × 4, Clair", GlassTesting.Place(0, 0, 16, 4), GlassPresets.Clair),
            ("case 16 × 4, Givre", GlassTesting.Place(0, 0, 16, 4), GlassPresets.Givre),
        })
        {
            var theme = GlassTesting.Advanced(preset);
            var rect = RectOf(placement, theme);
            using var background = new FixedImageBackground(GlassTesting.Detailed());
            using var layers = new LiquidGlassLayers();
            using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
            using var canvas = new SKCanvas(bitmap);
            using var pixels = bitmap.PeekPixels();

            double Time(bool moving)
            {
                const int Runs = 30;
                var watch = new Stopwatch();
                for (int i = -1; i < Runs; i++) // le premier tour n'est pas compté
                {
                    canvas.DrawImage(background.Current, 0, 0);
                    layers.BeginFrame(background, theme);
                    layers.PaintGlass(canvas, rect);
                    var tab = Tab(rect);
                    if (moving) tab.Offset(0, i % 2); // une autre pastille à chaque image : elle est refaite
                    if (i >= 0) watch.Start();
                    layers.PaintPill(canvas, pixels, rect, tab);
                    watch.Stop();
                    layers.EndFrame();
                }
                return watch.Elapsed.TotalMilliseconds / Runs;
            }

            double kept = Time(moving: false);
            int before = layers.PillComputations;
            double redone = Time(moving: true);
            Assert.True(layers.PillComputations - before >= 30);
            output.WriteLine($"{name} ({rect.Width} × {rect.Height}) : pastille gardée {kept:F3} ms par image (comparaison de la case), refaite {redone:F2} ms");
        }
    }
}
