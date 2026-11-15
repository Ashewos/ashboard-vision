using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Rendering;

/// <summary>Façon de dessiner le panneau d'un module.</summary>
public enum PanelKind { Matte, Glass, Pixel, LiquidGlass }

/// <summary>Dessin du panneau. Champs inutiles pour un style : laissés à leur défaut.</summary>
public sealed record PanelLook(PanelKind Kind)
{
    /// <summary>Contour intérieur de 1 px (Matte, Glass).</summary>
    public SKColor Outline { get; init; }

    /// <summary>Flou du fond derrière le panneau (Glass).</summary>
    public float BlurSigma { get; init; }

    /// <summary>Saturation du fond flouté (Glass).</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>Glass : dégradé à 155°, de SheenTop (0 %) à SheenMid (55 %) puis SheenBottom (100 %).</summary>
    public SKColor SheenTop { get; init; }
    public SKColor SheenMid { get; init; }
    public SKColor SheenBottom { get; init; }

    /// <summary>Glass : trait de 1,5 px le long du bord haut.</summary>
    public SKColor TopLight { get; init; }

    /// <summary>Glass : ombre portée, décalage (0, 10), flou 30. Pixel : voir <see cref="PixelShadow"/>.</summary>
    public SKColor DropShadow { get; init; }

    /// <summary>Pixel : bordure extérieure.</summary>
    public SKColor Border { get; init; }

    /// <summary>Pixel : épaisseur de la bordure (4).</summary>
    public int BorderWidth { get; init; }

    /// <summary>Pixel : ombre nette décalée de (8, 8), étendue de 4.</summary>
    public SKColor PixelShadow { get; init; }

    /// <summary>LiquidGlass : les réglages du verre (posés par <see cref="AppearanceResolver"/> d'après le choix de l'utilisateur).</summary>
    public GlassSettings? Glass { get; init; }
}

/// <summary>Nature du fond de l'écran choisi par l'utilisateur.</summary>
public enum BackgroundKind { Theme, Color, Image, Animated, Video }

/// <summary>Color : « #RRGGBB » ; Path : fichier image, GIF/WebP animé ou vidéo.</summary>
public sealed record BackgroundSettings(BackgroundKind Kind, string? Color = null, string? Path = null);

/// <summary>Le fond par défaut d'un thème : une couleur, ou « Aurore » (<paramref name="Aurora"/> = true, la couleur sert alors de secours).</summary>
public sealed record ThemeBackground(SKColor Color, bool Aurora);

/// <summary>
/// Un thème complet : palette, écarts du damier, dessin du panneau et fond par défaut.
/// <paramref name="LegibilityGuard"/> : la garde de lisibilité assombrit le panneau sur un fond clair ; faux, le panneau garde
/// exactement l'opacité choisie, quel que soit le fond (réglage <c>Legibility</c>, désactivable par l'utilisateur).
/// </summary>
public sealed record ThemeDefinition(
    Theme Theme, GridSpacing Grid, PanelLook Panel, ThemeBackground DefaultBackground, bool LegibilityGuard = true);
