using System.Security.Cryptography;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Core.Tests;

/// <summary>
/// Classique, Liquid Glass et Pixel restent identiques au pixel près (plan 9) : les empreintes ci-dessous ont été relevées sur le
/// code d'avant la tâche 3 du plan 9 (commit <c>0d23098</c>), avant toute modification du compositeur.
/// L'empreinte est le SHA-256 des pixels en RGBA, quel que soit l'ordre des canaux de la plateforme.
/// </summary>
public class OtherThemesUnchangedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Les empreintes de référence, par thème et par scène.</summary>
    private static readonly Dictionary<string, string> Reference = new()
    {
        ["classique/anime"] = "7D6C830AF9CE24D5F4DFFC0338B30542E0020A34B4C2AA30EF2F7C0DE7217724",
        ["classique/aurore"] = "7C876E7B464E7817AD8CF37076BC603417D0D5359EC626323CBFC88723573F0D",
        ["classique/aurore-sans-garde"] = "7C876E7B464E7817AD8CF37076BC603417D0D5359EC626323CBFC88723573F0D",
        ["classique/blanc"] = "7D6C830AF9CE24D5F4DFFC0338B30542E0020A34B4C2AA30EF2F7C0DE7217724",
        ["liquid-glass/anime"] = "3F4D194ED0DA465AEBDF58B84B83E3B7675D0C3D64CEEF7B410E97F35880841D",
        ["liquid-glass/aurore"] = "3EF975C6D0C29D1316C1296A6988478C5EE35302DC89605A8B83D5725348E66B",
        ["liquid-glass/aurore-sans-garde"] = "5E32DF352D7CBF27F4ECA67BD8E0BBA16E5148980345B3FB348C6B031AB3B9FE",
        ["liquid-glass/blanc"] = "F2843D3673E4BCC26162D0F592E6823AB0E8D8D04BBBE7343E01F2890713B74A",
        ["pixel/anime"] = "D072947F7D11BB02DC89224DCBEBA3D2883233B860D762AB3256B54D0376F91E",
        ["pixel/aurore"] = "17C3F8F43D0A9D69E70FEBF8F74EF8472078302A1FFFB8DEAFB3767E40A674D3",
        ["pixel/aurore-sans-garde"] = "17C3F8F43D0A9D69E70FEBF8F74EF8472078302A1FFFB8DEAFB3767E40A674D3",
        ["pixel/blanc"] = "D072947F7D11BB02DC89224DCBEBA3D2883233B860D762AB3256B54D0376F91E",
    };

    /// <summary>Un module sans texte (le dessin des lettres dépend de la plateforme) : un aplat translucide, un disque et un trait.</summary>
    private static IModule Shapes(SKColor color) => new FakeModule((canvas, ctx) =>
    {
        var b = ctx.Bounds;
        using var fill = new SKPaint { Color = color.WithAlpha(120), IsAntialias = true };
        canvas.DrawRect(SKRect.Create(b.Left + 12, b.Top + 12, b.Width / 2, b.Height / 3), fill);
        using var disc = new SKPaint { Color = ctx.Theme.Accent, IsAntialias = true };
        canvas.DrawCircle(b.MidX, b.MidY, Math.Min(b.Width, b.Height) / 4f, disc);
        using var line = new SKPaint { Color = ctx.Theme.Text, IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke };
        canvas.DrawLine(b.Left - 20, b.Bottom - 10, b.Right + 20, b.Top + 10, line); // dépasse : le rognage du panneau compte
    });

    private static string Hash(SKBitmap frame)
    {
        using var rgba = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(rgba))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(frame, 0, 0, paint);
        }
        return Convert.ToHexString(SHA256.HashData(rgba.GetPixelSpan()));
    }

    /// <summary>La vue par défaut sur Aurore, puis une case sur du blanc (la garde de lisibilité joue), puis un fond qui change.</summary>
    private static Dictionary<string, string> Fingerprints(ThemeDefinition theme)
    {
        var result = new Dictionary<string, string>();
        var id = theme.Theme.Id;
        var placements = DashboardLayout.Default.Views[0].Placements;
        var colors = new[] { SKColors.Red, SKColors.Orange, SKColors.Blue, SKColors.Green, SKColors.Purple };
        var modules = placements.Select((p, i) => (p.InstanceId, Module: Shapes(colors[i % colors.Length])))
            .ToDictionary(x => x.InstanceId, x => x.Module);

        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }));
        using (var aurora = new AuroraBackground())
        {
            using var frame = composer.Compose(placements, modules, theme, aurora, Now);
            result[$"{id}/aurore"] = Hash(frame);
            using var unguarded = composer.Compose(placements, modules, theme with { LegibilityGuard = false }, aurora, Now);
            result[$"{id}/aurore-sans-garde"] = Hash(unguarded);
        }

        using (var white = new ColorBackground(SKColors.White))
        {
            using var frame = composer.Compose(placements, modules, theme, white, Now);
            result[$"{id}/blanc"] = Hash(frame);
        }

        // Un fond animé : l'opacité est lissée d'une image à l'autre.
        using (var animated = new StubBackground(SKColors.Black))
        {
            composer.Compose(placements, modules, theme, animated, Now).Dispose();
            animated.Replace(StubBackground.Make(canvas => canvas.Clear(SKColors.White)));
            using var frame = composer.Compose(placements, modules, theme, animated, Now + TimeSpan.FromMilliseconds(200));
            result[$"{id}/anime"] = Hash(frame);
        }

        return result;
    }

    [Fact]
    public void OtherThemes_AreUnchanged()
    {
        var actual = new SortedDictionary<string, string>();
        foreach (var theme in new[] { Themes.ClassiqueDefinition, Themes.LiquidGlass, Themes.Pixel })
            foreach (var (name, hash) in Fingerprints(theme))
                actual[name] = hash;

        var lines = string.Join("\n", actual.Select(p => $"        [\"{p.Key}\"] = \"{p.Value}\","));
        Assert.True(
            actual.Count == Reference.Count && actual.All(p => Reference.TryGetValue(p.Key, out var expected) && expected == p.Value),
            "Les images des autres thèmes ont changé. Empreintes obtenues :\n" + lines);
    }
}
