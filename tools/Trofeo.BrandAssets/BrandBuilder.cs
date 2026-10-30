using SkiaSharp;

namespace Trofeo.BrandAssets;

/// <summary>
/// Fabrique tous les fichiers de <c>assets/brand</c> à partir des SVG du kit (<c>docs/superpowers/logo</c>) et de la police du nom.
/// Les fichiers produits sont commités : le logiciel et l'installateur n'ont besoin ni de cet outil ni des SVG.
/// </summary>
internal sealed class BrandBuilder : IDisposable
{
    // Les couleurs de la palette (spécification 4.7).
    private const string AshText = "#ECECEE";   // Cendre : le nom sur fond sombre
    private const string CoalText = "#17181C";   // Charbon : le nom sur fond clair

    // Le fond des pages de l'assistant d'installation (Inno Setup), clair et sombre.
    private static readonly SKColor WizardLight = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor WizardDark = new(0x20, 0x20, 0x20);

    private const int IconLargeFrom = 32; // jusqu'à 24 px : la coupe « petit » ; au-dessus : la grande icône

    private readonly string _logoDir;
    private readonly string _fontPath;
    private readonly string _outDir;
    private readonly Dictionary<string, SvgRaster> _rasters = new();
    private readonly Dictionary<string, string> _texts = new();

    public BrandBuilder(string logoDir, string fontPath, string outDir)
    {
        _logoDir = logoDir;
        _fontPath = fontPath;
        _outDir = outDir;
    }

    /// <summary>Écrit tout, et rend les fichiers produits (relatifs au dossier de sortie).</summary>
    public IReadOnlyList<string> BuildAll()
    {
        Directory.CreateDirectory(_outDir);
        var written = new List<string>();

        // Icône de l'exécutable, des raccourcis et de la fenêtre : la coupe « petit sombre » jusqu'à 24 px, la grande icône au-dessus.
        written.Add(WriteIco("ashboard-vision.ico", [16, 20, 24, 32, 40, 48, 64, 256],
            size => size < IconLargeFrom ? "ashboard-symbole-petit-sombre" : "ashboard-symbole-icone"));

        // Zone de notification : l'icône qui suit le thème de la barre des tâches.
        written.Add(WriteIco("tray-clair.ico", [16, 20, 24, 32], _ => "ashboard-symbole-petit-clair"));
        written.Add(WriteIco("tray-sombre.ico", [16, 20, 24, 32], _ => "ashboard-symbole-petit-sombre"));

        // Logo avec le nom, pour fond sombre (symbole sombre, nom Cendre) et fond clair (symbole clair, nom Charbon).
        written.Add(WriteText("ashboard-vision-horizontal-sombre.svg",
            HorizontalLogo.Build(Text("ashboard-symbole-icone"), _fontPath, AshText)));
        written.Add(WriteText("ashboard-vision-horizontal-clair.svg",
            HorizontalLogo.Build(Text("ashboard-symbole-icone-clair"), _fontPath, CoalText)));

        written.Add(WriteBytes("logo-256.png", Raster("ashboard-symbole-icone").RenderPng(256)));
        written.Add(WriteBytes("logo-512.png", Raster("ashboard-symbole-icone").RenderPng(512)));

        // Assistant d'installation : une image d'accueil et un petit logo par thème, à 100 % et à 200 %.
        foreach (var (theme, symbol, background, ink) in new[]
                 {
                     ("clair", "ashboard-symbole-icone-clair", WizardLight, CoalText),
                     ("sombre", "ashboard-symbole-icone", WizardDark, AshText),
                 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                written.Add(WriteBmp($"assistant-{theme}.bmp", 164 * scale, 314 * scale, background, scale, symbol, ink, nameBelow: true, suffixed: scale == 2));
                written.Add(WriteBmp($"petit-logo-{theme}.bmp", 55 * scale, 55 * scale, background, scale, symbol, ink, nameBelow: false, suffixed: scale == 2));
            }
        }

        return written;
    }

    // Le fichier en 200 % porte le même nom avec « -2x » : assistant-clair.bmp (164 x 314) et assistant-clair-2x.bmp (328 x 628).
    private string WriteBmp(string name, int width, int height, SKColor background, int scale, string symbol, string ink, bool nameBelow, bool suffixed)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            if (nameBelow)
                DrawWelcome(canvas, width, height, scale, symbol, ink);
            else
                Raster(symbol).Draw(canvas, 0, 0, width); // le petit logo : le symbole remplit le carré (sa grille porte déjà sa marge)
        }

        var file = suffixed ? Path.ChangeExtension(name, null) + "-2x.bmp" : name;
        return WriteBytes(file, BmpWriter.Write(bitmap, background));
    }

    /// <summary>L'image d'accueil : le symbole centré, le nom en tracés dessous, le tout à une hauteur de regard. Dessinée pour 164 x 314, mise à l'échelle.</summary>
    private void DrawWelcome(SKCanvas canvas, int width, int height, int scale, string symbol, string ink)
    {
        canvas.Scale(scale);
        const float W = 164f;
        const float symbolSize = 112f;            // la grille du symbole, marge comprise : le bloc en occupe les 3/4
        const float blockTop = 72f;               // haut du bloc : l'ensemble symbole et nom tient un peu au-dessus du milieu
        const float blockBottom = blockTop + symbolSize * 0.75f;
        var top = blockTop - symbolSize * 0.125f; // la marge de la grille est de 32 sur 256
        Raster(symbol).Draw(canvas, (W - symbolSize) / 2, top, symbolSize);

        var color = SKColor.Parse(ink);
        const float nameSize = 23f;
        using var name = WordmarkBuilder.Build("Ashboard", _fontPath, nameSize, nameSize * -0.01f);
        const float visionSize = nameSize * 0.42f;
        using var vision = WordmarkBuilder.Build("VISION", _fontPath, visionSize, visionSize * 0.28f);

        // Les bornes sont celles de l'encre : « VISION » est centré sans compter l'espacement qui suivrait sa dernière lettre.
        name.Offset((W - name.Bounds.Width) / 2 - name.Bounds.Left, blockBottom + 16f - name.Bounds.Top);
        vision.Offset((W - vision.Bounds.Width) / 2 - vision.Bounds.Left, name.Bounds.Bottom + 11f - vision.Bounds.Top);

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawPath(name, paint);
        paint.Color = color.WithAlpha((byte)Math.Round(255 * 0.62));
        canvas.DrawPath(vision, paint);
    }

    private string WriteIco(string name, int[] sizes, Func<int, string> symbolFor)
    {
        var images = sizes.Select(size => (size, Raster(symbolFor(size)).RenderPng(size))).ToList();
        return WriteBytes(name, IcoWriter.Write(images));
    }

    private SvgRaster Raster(string symbol)
    {
        if (!_rasters.TryGetValue(symbol, out var raster))
            _rasters[symbol] = raster = new SvgRaster(Text(symbol));
        return raster;
    }

    private string Text(string symbol)
    {
        if (!_texts.TryGetValue(symbol, out var text))
        {
            var path = Path.Combine(_logoDir, symbol + ".svg");
            if (!File.Exists(path))
                throw new FileNotFoundException("SVG du kit introuvable.", path);
            _texts[symbol] = text = File.ReadAllText(path);
        }

        return text;
    }

    private string WriteText(string name, string text)
    {
        File.WriteAllText(Path.Combine(_outDir, name), text, new System.Text.UTF8Encoding(false));
        return name;
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_outDir, name), bytes);
        return name;
    }

    public void Dispose()
    {
        foreach (var raster in _rasters.Values)
            raster.Dispose();
    }
}
