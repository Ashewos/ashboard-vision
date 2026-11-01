using System.Globalization;
using System.Xml.Linq;

namespace Trofeo.BrandAssets;

/// <summary>
/// Le logo horizontal : le symbole, puis « Ashboard » en Manrope Medium et « VISION » en capitales espacées, le tout en tracés
/// (aucun élément <c>&lt;text&gt;</c>). Les proportions sont celles de la maquette du kit : symbole de 1,5 em, nom à
/// -0,01 em d'espacement, « VISION » à 42 % de la taille, 0,28 em d'espacement et 62 % d'opacité, centré sur le nom.
/// </summary>
internal static class HorizontalLogo
{
    private const string Svg = "http://www.w3.org/2000/svg";

    /// <summary>La taille du symbole sur sa grille, et donc de la ligne qui porte le nom.</summary>
    private const float SymbolBox = 256f;

    /// <summary>Taille de « Ashboard » : le symbole fait 1,5 em, comme sur la maquette.</summary>
    private const float NameSize = SymbolBox / 1.5f;

    private const float VisionRatio = 0.42f;
    private const float VisionSpacingEm = 0.28f;
    private const float VisionOpacity = 0.62f;
    private const float NameSpacingEm = -0.01f;

    /// <summary>Espace libre autour du symbole : sa grille de 256 porte déjà 32 de marge, le total dépasse le quart de la largeur du bloc.</summary>
    private const float Margin = 32f;

    /// <summary>
    /// Le SVG du logo horizontal. <paramref name="symbolSvg"/> est le texte d'un des SVG du kit (il apporte son dégradé et ses formes),
    /// <paramref name="textColor"/> la couleur du nom (Cendre sur fond sombre, Charbon sur fond clair).
    /// </summary>
    public static string Build(string symbolSvg, string fontPath, string textColor)
    {
        var symbol = XDocument.Parse(symbolSvg).Root ?? throw new InvalidDataException("SVG du symbole vide.");

        // Le nom : « Ashboard », puis « VISION » à sa droite, les deux centrés sur le milieu du symbole.
        using var name = WordmarkBuilder.Build("Ashboard", fontPath, NameSize, NameSize * NameSpacingEm);
        var visionSize = NameSize * VisionRatio;
        using var vision = WordmarkBuilder.Build("VISION", fontPath, visionSize, visionSize * VisionSpacingEm);

        var centerY = Margin + SymbolBox / 2;
        var nameLeft = Margin + SymbolBox + NameSize * 0.42f; // l'écart de la maquette entre le symbole et le nom
        name.Offset(nameLeft - name.Bounds.Left, centerY - name.Bounds.MidY);

        var visionLeft = name.Bounds.Right + NameSize * 0.42f + visionSize * 0.15f;
        vision.Offset(visionLeft - vision.Bounds.Left, centerY - vision.Bounds.MidY);

        // À droite, la même marge visible qu'à gauche (celle du bloc du symbole : 32 de sa grille + la marge).
        var width = vision.Bounds.Right + Margin + 32f;
        var height = SymbolBox + 2 * Margin;

        var root = new XElement(XName.Get("svg", Svg),
            new XAttribute("viewBox", $"0 0 {Num(width)} {Num(height)}"),
            new XAttribute("width", Num(width)),
            new XAttribute("height", Num(height)),
            new XAttribute("role", "img"),
            new XElement(XName.Get("title", Svg), "Ashboard Vision"),
            new XComment(" Logo horizontal fabriqué par tools/Trofeo.BrandAssets : le symbole du kit et le nom en tracés (Manrope Medium). Ne pas modifier à la main. "));

        // Les dégradés du symbole, puis ses formes, posées à la marge.
        foreach (var defs in symbol.Elements(XName.Get("defs", Svg)))
            root.Add(new XElement(defs));
        var group = new XElement(XName.Get("g", Svg), new XAttribute("transform", $"translate({Num(Margin)} {Num(Margin)})"));
        foreach (var shape in symbol.Elements().Where(e => e.Name.LocalName is not ("title" or "defs" or "desc")))
            group.Add(new XElement(shape));
        root.Add(group);

        root.Add(new XElement(XName.Get("path", Svg),
            new XAttribute("d", name.ToSvgPathData()),
            new XAttribute("fill", textColor)));
        root.Add(new XElement(XName.Get("path", Svg),
            new XAttribute("d", vision.ToSvgPathData()),
            new XAttribute("fill", textColor),
            new XAttribute("fill-opacity", Num(VisionOpacity))));

        return new XDocument(root).ToString() + "\n";
    }

    private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
