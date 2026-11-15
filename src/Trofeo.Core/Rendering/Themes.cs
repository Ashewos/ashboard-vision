using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Rendering;

/// <summary>Thèmes fournis avec l'application (valeurs de la maquette <c>themes-v3.html</c>).</summary>
public static class Themes
{
    // Opacité (0 à 1) en octet, arrondie loin de zéro.
    private static SKColor Alpha(string hex, double opacity) =>
        SKColor.Parse(hex).WithAlpha((byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero));

    private static readonly Lazy<ThemeDefinition> ClassiqueLazy = new(() => new ThemeDefinition(
        new Theme(
            Id: "classique",
            Background: SKColor.Parse("#0E1116"),
            Panel: SKColor.Parse("#111317"),
            PanelOpacity: 0.86f,
            CornerRadius: 14f,
            Text: SKColor.Parse("#ECEEF0"),
            TextMuted: SKColor.Parse("#8C929C"),
            Accent: SKColor.Parse("#7FD1C7"),
            Warning: SKColor.Parse("#F2B84B"),
            Typeface: EmbeddedFonts.Find(EmbeddedFonts.Barlow)!),
        GridSpacing.Default,
        new PanelLook(PanelKind.Matte) { Outline = Alpha("#FFFFFF", 0.06) },
        new ThemeBackground(SKColor.Parse("#0E1116"), false)));

    private static readonly Lazy<ThemeDefinition> LiquidGlassLazy = new(() =>
    {
        var manrope = EmbeddedFonts.Find(EmbeddedFonts.Manrope)!;
        return new ThemeDefinition(
            new Theme(
                Id: "liquid-glass",
                Background: SKColor.Parse("#241A3D"),
                Panel: SKColor.Parse("#101220"),
                PanelOpacity: 0.22f,
                CornerRadius: 30f,
                Text: SKColor.Parse("#FFFFFF"),
                TextMuted: Alpha("#FFFFFF", 0.72),
                Accent: SKColor.Parse("#FFFFFF"),
                Warning: SKColor.Parse("#FFD56B"),
                Typeface: manrope)
            {
                Shape = ThemeShape.Rounded,
                Highlight = SKColor.Parse("#FFFFFF"),
                Surface = Alpha("#FFFFFF", 0.22),
                Track = Alpha("#FFFFFF", 0.22),
                FeedTypeface = manrope,
                FeedScale = 1f,
            },
            GridSpacing.Default,
            new PanelLook(PanelKind.Glass)
            {
                Outline = Alpha("#FFFFFF", 0.22),
                BlurSigma = 26f,
                Saturation = 1.7f,
                SheenTop = Alpha("#FFFFFF", 0.18),
                SheenMid = Alpha("#FFFFFF", 0.04),
                SheenBottom = Alpha("#FFFFFF", 0.08),
                TopLight = Alpha("#FFFFFF", 0.55),
                DropShadow = Alpha("#000000", 0.18),
            },
            new ThemeBackground(SKColor.Parse("#241A3D"), true));
    });

    // Même palette, même police, mêmes écarts et même fond Aurore que Liquid Glass ; seul le verre (PanelKind.LiquidGlass) change,
    // et le service y dessine lui-même la pastille de l'élément choisi.
    private static readonly Lazy<ThemeDefinition> LiquidGlassAdvancedLazy = new(() =>
    {
        var glass = LiquidGlassLazy.Value;
        return glass with
        {
            Theme = glass.Theme with { Id = "liquid-glass-avance", ServiceDrawsSelection = true },
            Panel = new PanelLook(PanelKind.LiquidGlass) { Glass = GlassPresets.Find(GlassPresets.Clair) },
        };
    });

    private static readonly Lazy<ThemeDefinition> PixelLazy = new(() => new ThemeDefinition(
        new Theme(
            Id: "pixel",
            Background: SKColor.Parse("#17142A"),
            Panel: SKColor.Parse("#17142A"),
            PanelOpacity: 1f,
            CornerRadius: 0f,
            Text: SKColor.Parse("#F4EEDE"),
            TextMuted: SKColor.Parse("#8F86B8"),
            Accent: SKColor.Parse("#7CE0A3"),
            Warning: SKColor.Parse("#FFD27A"),
            Typeface: EmbeddedFonts.Find(EmbeddedFonts.Vt323)!)
        {
            Shape = ThemeShape.Pixel,
            Highlight = SKColor.Parse("#FFD27A"),
            Surface = SKColor.Parse("#2E2850"),
            Track = SKColor.Parse("#2E2850"),
            // VT323 dessine petit dans son carré : on agrandit les caractères (pas la mise en page), au même facteur partout.
            TypefaceScale = EmbeddedFonts.ScaleOf(EmbeddedFonts.Vt323),
            FeedTypeface = EmbeddedFonts.Find(EmbeddedFonts.Vt323)!,
            FeedScale = EmbeddedFonts.ScaleOf(EmbeddedFonts.Vt323),
        },
        new GridSpacing(18, 20),
        new PanelLook(PanelKind.Pixel)
        {
            Border = SKColor.Parse("#2E2850"),
            BorderWidth = 4,
            PixelShadow = Alpha("#0A0814", 0.6),
        },
        new ThemeBackground(SKColor.Parse("#17142A"), true)));

    private static readonly Lazy<IReadOnlyList<ThemeDefinition>> AllLazy = new(() =>
        new[] { ClassiqueLazy.Value, LiquidGlassLazy.Value, PixelLazy.Value, LiquidGlassAdvancedLazy.Value });

    /// <summary>Thème par défaut : fond sombre, accent turquoise, Barlow Semi Condensed. Même instance que <c>ClassiqueDefinition.Theme</c>.</summary>
    public static Theme Classique => ClassiqueLazy.Value.Theme;

    public static ThemeDefinition ClassiqueDefinition => ClassiqueLazy.Value;

    public static ThemeDefinition LiquidGlass => LiquidGlassLazy.Value;

    public static ThemeDefinition Pixel => PixelLazy.Value;

    /// <summary>« Liquid Glass avancé » : le verre adapté de liquidglass, avec ses réglages et la pastille de l'élément choisi.</summary>
    public static ThemeDefinition LiquidGlassAdvanced => LiquidGlassAdvancedLazy.Value;

    /// <summary>Les quatre thèmes : Classique, Liquid Glass, Pixel, Liquid Glass avancé.</summary>
    public static IReadOnlyList<ThemeDefinition> All => AllLazy.Value;

    /// <summary>Thème par identifiant (casse ignorée) ; null si inconnu.</summary>
    public static ThemeDefinition? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(t => string.Equals(t.Theme.Id, id, StringComparison.OrdinalIgnoreCase));
}
