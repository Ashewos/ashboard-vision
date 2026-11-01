using SkiaSharp;

namespace Trofeo.BrandAssets;

/// <summary>
/// Le nom de la marque en tracés : les contours des lettres de la police du dépôt, sans élément <c>&lt;text&gt;</c>, pour qu'il
/// s'affiche pareil partout (GitHub, un navigateur, un autre logiciel) sans que la police y soit installée.
/// </summary>
internal static class WordmarkBuilder
{
    /// <summary>
    /// Les contours de <paramref name="text"/> en données de tracé SVG (<c>d="…"</c>), ligne de base à y = 0 et début à x = 0.
    /// <paramref name="size"/> est la taille de la police, en unités du dessin.
    /// </summary>
    public static string PathData(string text, string fontPath, float size, float letterSpacing = 0f)
    {
        using var path = Build(text, fontPath, size, letterSpacing);
        return path.ToSvgPathData();
    }

    /// <summary>
    /// Les contours de <paramref name="text"/>, ligne de base à y = 0 et début à x = 0. <paramref name="letterSpacing"/> s'ajoute
    /// après chaque lettre. Pas de crénage : chaque lettre avance de sa largeur propre.
    /// </summary>
    public static SKPath Build(string text, string fontPath, float size, float letterSpacing = 0f)
    {
        if (!File.Exists(fontPath))
            throw new FileNotFoundException("Police introuvable.", fontPath);

        using var typeface = SKTypeface.FromFile(fontPath)
            ?? throw new InvalidDataException($"Police illisible : {fontPath}");
        // Pas de hinting : on veut les vrais contours de la police, pas leur déformation pour une grille de pixels.
        using var font = new SKFont(typeface, size) { Hinting = SKFontHinting.None, Subpixel = true };

        var glyphs = font.GetGlyphs(text);
        var widths = font.GetGlyphWidths(glyphs);
        var positions = new SKPoint[glyphs.Length];
        var x = 0f;
        for (var i = 0; i < glyphs.Length; i++)
        {
            positions[i] = new SKPoint(x, 0);
            x += widths[i] + letterSpacing;
        }

        return font.GetTextPath(text, positions);
    }
}
