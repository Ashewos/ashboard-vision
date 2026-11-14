using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Rendering;

/// <summary>
/// La place d'une case à l'écran : son rectangle en pixels, sa taille sur le damier et la taille de son texte, avec le thème.
/// Un seul calcul, partagé par le compositeur (qui dessine) et par le service (qui l'annonce aux modules cachés).
/// </summary>
public static class ModulePlace
{
    /// <summary>Le rectangle en pixels de la case, avec les écarts du thème.</summary>
    public static SKRect Rect(ModulePlacement placement, ThemeDefinition theme)
    {
        var r = GridGeometry.ModuleRect(placement.Column, placement.Row, placement.Columns, placement.Rows, theme.Grid);
        return SKRect.Create(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Le contexte de dessin de la case : ses bornes, sa taille sur le damier, le thème, l'échelle du texte et l'heure.</summary>
    public static ModuleRenderContext Context(ModulePlacement placement, ThemeDefinition theme, DateTimeOffset now) =>
        new(Rect(placement, theme), new GridSize(placement.Columns, placement.Rows), theme.Theme, placement.TextSize.Factor(), now);
}
