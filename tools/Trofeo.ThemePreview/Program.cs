using System.Globalization;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.ThemePreview;

/// <summary>
/// Aperçus PNG de l'écran complet (1920 × 462) : les cinq vrais modules, nourris de fausses données, dans chaque thème.
/// <c>--out &lt;dossier&gt;</c> écrit la série complète, <c>--theme …  --png &lt;fichier&gt;</c> une image à la demande,
/// <c>--check</c> vérifie qu'aucun module ne déborde.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage : Trofeo.ThemePreview [option]
          (rien) | --out <dossier>    écrit la série d'aperçus (par défaut : apercus/) :
                                      <thème>.png, <thème>-opacite-0.png, -opacite-1.png, -texte-petit.png, -texte-grand.png,
                                      -fond-clair.png, et liquid-glass-gif-1..4.png (4 images d'un GIF fabriqué, à 83 ms)
          --theme <id> [--opacity x] [--font <famille>] [--background color:#hex|image:<chemin>|gif:<chemin>]
                       [--text-size small|normal|large] --png <fichier>
                                      une image à la demande (thèmes : classique, liquid-glass, pixel)
          --thumbnails <dossier> [--width n]
                                      une vignette par thème (<thème>.png, 480 px de large par défaut) : celles de
                                      src/Trofeo.Settings/Assets/Themes, pour les cartes de l'onglet Personnalisation
          --modules <dossier> [--theme <id>]
                                      une capture par module, découpée dans l'écran complet (Classique par défaut) :
                                      claude-code.png, codex.png, monitoring.png, en-ecoute.png, heure-meteo.png, reseau.png
                                      (celles du README, dans docs/images/modules)
          --check                     rend chaque thème à chaque taille de texte avec des textes longs et vérifie que rien
                                      n'est peint hors des panneaux ; affiche OK, ou une ligne par faute (code de sortie 1)
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--check")
            {
                return await OverflowCheck.RunAsync(Console.Out).ConfigureAwait(false);
            }

            if (args.Length == 0)
            {
                return await WriteSeriesAsync("apercus").ConfigureAwait(false);
            }

            if (args.Length == 2 && args[0] == "--out")
            {
                return await WriteSeriesAsync(args[1]).ConfigureAwait(false);
            }

            if (args.Length is 2 or 4 && args[0] == "--thumbnails")
            {
                return await WriteThumbnailsAsync(args).ConfigureAwait(false);
            }

            if (args.Length is 2 or 4 && args[0] == "--modules")
            {
                return await WriteModuleShotsAsync(args).ConfigureAwait(false);
            }

            if (args[0] == "--theme")
            {
                return await OneImageAsync(args).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Erreur d'écriture : " + e.Message);
            return 1;
        }

        Console.Error.WriteLine(Usage);
        return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 2;
    }

    // --- Série complète ---

    private static async Task<int> WriteSeriesAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var white = Path.Combine(Path.GetTempPath(), $"trofeo-fond-clair-{Guid.NewGuid():N}.png");
        var gif = Path.Combine(Path.GetTempPath(), $"trofeo-fond-anime-{Guid.NewGuid():N}.gif");
        try
        {
            WriteWhitePng(white);
            SampleGif.Write(gif);

            foreach (var theme in Themes.All)
            {
                var id = theme.Theme.Id;
                Write(folder, $"{id}.png", scene, composer, new AppearanceSettings(id), TextSize.Normal);
                Write(folder, $"{id}-opacite-0.png", scene, composer, new AppearanceSettings(id, PanelOpacity: 0f), TextSize.Normal);
                Write(folder, $"{id}-opacite-1.png", scene, composer, new AppearanceSettings(id, PanelOpacity: 1f), TextSize.Normal);
                Write(folder, $"{id}-texte-petit.png", scene, composer, new AppearanceSettings(id), TextSize.Small);
                Write(folder, $"{id}-texte-grand.png", scene, composer, new AppearanceSettings(id), TextSize.Large);
                Write(folder, $"{id}-fond-clair.png", scene, composer,
                    new AppearanceSettings(id, Background: new BackgroundSettings(BackgroundKind.Image, Path: white)), TextSize.Normal);
            }

            WriteGifFrames(folder, scene, composer, gif);
            return 0;
        }
        finally
        {
            File.Delete(white);
            File.Delete(gif);
        }
    }

    private static void WriteGifFrames(string folder, PreviewScene scene, FrameComposer composer, string gif)
    {
        var settings = new AppearanceSettings("liquid-glass", Background: new BackgroundSettings(BackgroundKind.Animated, Path: gif));
        var theme = Resolve(settings);
        using var background = BackgroundFactory.Create(settings.Background, theme, null, Warn);
        var instants = new[] { 0, 83, 166, 249 };
        for (var i = 0; i < instants.Length; i++)
        {
            background.Advance(TimeSpan.FromMilliseconds(instants[i]));
            using var frame = composer.Compose(
                PreviewScene.Layout(TextSize.Normal), scene.ByPlacement, theme, background, PreviewScene.Now);
            var file = Path.Combine(folder, $"liquid-glass-gif-{i + 1}.png");
            SavePng(frame, file);
            Console.WriteLine("Aperçu écrit : " + file);
        }
    }

    private static void Write(
        string folder, string name, PreviewScene scene, FrameComposer composer, AppearanceSettings settings, TextSize size)
    {
        var file = Path.Combine(folder, name);
        RenderTo(file, scene, composer, settings, size);
        Console.WriteLine("Aperçu écrit : " + file);
    }

    // --- Les vignettes des thèmes ---

    private const int DefaultThumbnailWidth = 480;

    /// <summary>
    /// Une vignette par thème : l'écran complet de la série, ramené à <c>--width</c> pixels (480 par défaut) en gardant les
    /// proportions de la dalle. Ce sont les images embarquées par le logiciel de réglage.
    /// </summary>
    private static async Task<int> WriteThumbnailsAsync(string[] args)
    {
        var width = DefaultThumbnailWidth;
        if (args.Length == 4 && (args[2] != "--width" || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out width) || width is < 16 or > GridGeometry.Width))
        {
            Console.Error.WriteLine("Largeur invalide (16 à 1920) : " + string.Join(' ', args.Skip(2)));
            return 2;
        }

        var folder = args[1];
        Directory.CreateDirectory(folder);
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var height = (int)Math.Round(width * (double)GridGeometry.Height / GridGeometry.Width);
        foreach (var definition in Themes.All)
        {
            var theme = Resolve(new AppearanceSettings(definition.Theme.Id));
            using var background = BackgroundFactory.Create(null, theme, null, Warn);
            using var frame = composer.Compose(PreviewScene.Layout(TextSize.Normal), scene.ByPlacement, theme, background, PreviewScene.Now);
            using var thumbnail = frame.Resize(new SKImageInfo(width, height, frame.ColorType, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
            var file = Path.Combine(folder, definition.Theme.Id + ".png");
            SavePng(thumbnail, file);
            Console.WriteLine("Vignette écrite : " + file);
        }

        return 0;
    }

    // --- Une capture par module ---

    // Le nom de fichier de chaque module, dans l'ordre où le README les présente.
    private static readonly (string TypeId, string File)[] ModuleFiles =
    [
        ("claudecode", "claude-code.png"),
        ("codex", "codex.png"),
        ("monitoring", "monitoring.png"),
        ("nowplaying", "en-ecoute.png"),
        ("clock", "heure-meteo.png"),
        ("network", "reseau.png"),
    ];

    /// <summary>
    /// Une capture par module : l'écran complet est rendu comme le service le fait, puis on découpe le rectangle du
    /// module. Codex, qui n'est pas dans la disposition par défaut, est rendu à la place de Claude Code.
    /// </summary>
    private static async Task<int> WriteModuleShotsAsync(string[] args)
    {
        var themeId = "classique";
        if (args.Length == 4)
        {
            if (args[2] != "--theme")
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            themeId = args[3];
        }

        var folder = args[1];
        Directory.CreateDirectory(folder);
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var theme = Resolve(new AppearanceSettings(themeId));
        using var background = BackgroundFactory.Create(null, theme, null, Warn);

        var standard = PreviewScene.Layout(TextSize.Normal);
        var withCodex = standard with
        {
            Placements = standard.Placements.Select(p => p.TypeId == "claudecode" ? PreviewScene.CodexPlacement : p).ToList(),
        };
        foreach (var layout in new[] { standard, withCodex })
        {
            using var frame = composer.Compose(layout, scene.ByPlacement, theme, background, PreviewScene.Now);
            foreach (var placement in layout.Placements)
            {
                // La disposition avec Codex ne sert qu'à Codex : les autres modules y sont identiques.
                if ((placement.TypeId == "codex") != ReferenceEquals(layout, withCodex))
                {
                    continue;
                }

                var entry = ModuleFiles.First(m => m.TypeId == placement.TypeId);

                var rect = GridGeometry.ModuleRect(placement.Column, placement.Row, placement.Columns, placement.Rows, theme.Grid);
                var file = Path.Combine(folder, entry.File);
                using var crop = new SKBitmap(rect.Width, rect.Height, frame.ColorType, frame.AlphaType);
                if (!frame.ExtractSubset(crop, rect))
                {
                    Console.Error.WriteLine("Découpe impossible : " + entry.File);
                    return 1;
                }

                SavePng(crop, file);
                Console.WriteLine("Capture écrite : " + file);
            }
        }

        return 0;
    }

    // --- Une image à la demande ---

    private static async Task<int> OneImageAsync(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Option incomplète ou inconnue : " + args[i]);
                return 2;
            }

            options[args[i]] = args[i + 1];
        }

        var known = new[] { "--theme", "--opacity", "--font", "--background", "--text-size", "--png" };
        if (options.Keys.Any(k => !known.Contains(k)) || !options.TryGetValue("--png", out var png))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        float? opacity = null;
        if (options.TryGetValue("--opacity", out var rawOpacity))
        {
            if (!float.TryParse(rawOpacity, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                Console.Error.WriteLine("Opacité invalide : " + rawOpacity);
                return 2;
            }

            opacity = parsed;
        }

        BackgroundSettings? background = null;
        if (options.TryGetValue("--background", out var rawBackground) && !TryParseBackground(rawBackground, out background))
        {
            Console.Error.WriteLine("Fond invalide (color:#hex, image:<chemin> ou gif:<chemin>) : " + rawBackground);
            return 2;
        }

        var size = options.GetValueOrDefault("--text-size", "normal") switch
        {
            "small" => TextSize.Small,
            "normal" => TextSize.Normal,
            "large" => TextSize.Large,
            _ => (TextSize?)null,
        };
        if (size is null)
        {
            Console.Error.WriteLine("Taille de texte invalide (small, normal ou large) : " + options["--text-size"]);
            return 2;
        }

        var settings = new AppearanceSettings(options["--theme"], opacity, options.GetValueOrDefault("--font"), background);
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        RenderTo(png, scene, composer, settings, size.Value);
        Console.WriteLine("Aperçu écrit : " + png);
        return 0;
    }

    private static bool TryParseBackground(string text, out BackgroundSettings? settings)
    {
        settings = null;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var value = text[(colon + 1)..];
        settings = text[..colon] switch
        {
            "color" => new BackgroundSettings(BackgroundKind.Color, Color: value),
            "image" => new BackgroundSettings(BackgroundKind.Image, Path: value),
            "gif" => new BackgroundSettings(BackgroundKind.Animated, Path: value),
            _ => null,
        };
        return settings is not null;
    }

    // --- Rendu ---

    internal static void Warn(string message) => Console.Error.WriteLine("Avertissement : " + message);

    internal static ThemeDefinition Resolve(AppearanceSettings settings) =>
        AppearanceResolver.Resolve(settings, new SkiaSystemFonts(), Warn);

    /// <summary>Compose comme le service : réglages résolus, fond du thème ou choisi, puis <see cref="FrameComposer"/>.</summary>
    private static void RenderTo(string file, PreviewScene scene, FrameComposer composer, AppearanceSettings settings, TextSize size)
    {
        var theme = Resolve(settings);
        using var background = BackgroundFactory.Create(settings.Background, theme, null, Warn);
        using var frame = composer.Compose(PreviewScene.Layout(size), scene.ByPlacement, theme, background, PreviewScene.Now);
        SavePng(frame, file);
    }

    internal static void SavePng(SKBitmap bitmap, string file)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(file);
        data.SaveTo(output);
    }

    /// <summary>Une image blanche de l'écran : montre la garde de lisibilité (texte clair sur fond clair).</summary>
    private static void WriteWhitePng(string file)
    {
        using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        bitmap.Erase(SKColors.White);
        SavePng(bitmap, file);
    }
}
