using System.Globalization;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.ThemePreview;

namespace Trofeo.ReadmeHero;

/// <summary>
/// Fabrique l'image d'accueil animée du README : les trois thèmes sur le vrai rendu de l'écran (Trofeo.ThemePreview), inclinés
/// dans une scène sombre. Écrit des PNG numérotés (cycle de 12 s à 12 images par seconde) et <c>encode.txt</c> (les commandes ffmpeg).
/// </summary>
internal static class Program
{
    private const int FramesPerSecond = 12;

    private const string Usage = """
        Usage : Trofeo.ReadmeHero --name <nom> --tagline <phrase> --out <dossier> [--logo <logo.svg>] [--at <s,s,…>]
          --name     le nom du projet (écrit en haut quand il n'y a pas de logo, ou à côté d'un logo en symbole seul)
          --tagline  la phrase d'accroche, sous le nom
          --logo     facultatif : un logo SVG. Un logo horizontal (largeur au moins double de la hauteur) contient déjà le
                     nom ; il est alors dessiné seul. Sur ce fond sombre, prendre la version « sombre » du logo
          --out      le dossier qui reçoit image-000.png … image-143.png et encode.txt
          --at       facultatif : n'écrit que ces instants (en secondes, par exemple 0,2.5,4.1), en instant-<s>.png, pour regarder
        """;

    // Les noms affichés des trois thèmes, dans l'ordre de l'image (le même que Themes.All).
    private static readonly Dictionary<string, string> ThemeNames = new(StringComparer.Ordinal)
    {
        ["classique"] = "Classique",
        ["liquid-glass"] = "Liquid Glass",
        ["pixel"] = "Pixel",
    };

    private static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                Console.Error.WriteLine(Usage);
                return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 2;
            }

            options[args[i]] = args[i + 1];
        }

        if (!options.TryGetValue("--name", out var name) || !options.TryGetValue("--tagline", out var tagline)
            || !options.TryGetValue("--out", out var output)
            || options.Keys.Any(key => key is not ("--name" or "--tagline" or "--out" or "--logo" or "--at")))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        double[]? instants = null;
        if (options.TryGetValue("--at", out var rawInstants))
        {
            var parts = rawInstants.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            instants = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out instants[i]))
                {
                    Console.Error.WriteLine("Instant invalide : " + parts[i]);
                    return 2;
                }
            }
        }

        string? logoSvg = null;
        if (options.TryGetValue("--logo", out var logoPath))
        {
            if (!File.Exists(logoPath))
            {
                Console.Error.WriteLine("Logo introuvable : " + logoPath);
                return 2;
            }

            logoSvg = await File.ReadAllTextAsync(logoPath).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(output);
            var themes = await RenderThemesAsync().ConfigureAwait(false);
            var typeface = EmbeddedFonts.Find(EmbeddedFonts.Manrope) ?? SKTypeface.Default;
            using var renderer = new HeroRenderer(name, tagline, logoSvg, themes, typeface);

            if (instants is not null)
            {
                foreach (var seconds in instants)
                {
                    WriteFrame(renderer, seconds, Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"instant-{seconds}.png")));
                }
            }
            else
            {
                var frames = (int)(HeroTimeline.CycleSeconds * FramesPerSecond);
                for (var i = 0; i < frames; i++)
                {
                    WriteFrame(renderer, (double)i / FramesPerSecond, Path.Combine(output, $"image-{i:000}.png"));
                }

                File.WriteAllText(Path.Combine(output, "encode.txt"), EncodeCommands(output));
                Console.WriteLine($"{frames} images écrites dans {output} ; les commandes ffmpeg sont dans encode.txt.");
            }

            foreach (var theme in themes)
            {
                theme.Screen.Dispose();
            }

            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Erreur d'écriture : " + e.Message);
            return 1;
        }
    }

    /// <summary>Le rendu complet de l'écran dans chaque thème, comme dans <c>Trofeo.ThemePreview</c> (même scène, mêmes données).</summary>
    private static async Task<List<HeroTheme>> RenderThemesAsync()
    {
        using var scene = await PreviewScene.CreateAsync(longTexts: false).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var themes = new List<HeroTheme>();
        foreach (var definition in Themes.All)
        {
            var id = definition.Theme.Id;
            var theme = Trofeo.ThemePreview.Program.Resolve(new AppearanceSettings(id));
            using var background = BackgroundFactory.Create(null, theme, null, Trofeo.ThemePreview.Program.Warn);
            var frame = composer.Compose(PreviewScene.Layout(TextSize.Normal).Views[0].Placements, scene.ByPlacement, theme, background, PreviewScene.Now);
            themes.Add(new HeroTheme(ThemeNames.GetValueOrDefault(id, id), frame));
        }

        return themes;
    }

    private static void WriteFrame(HeroRenderer renderer, double seconds, string file)
    {
        using var frame = renderer.Render(seconds);
        using var image = SKImage.FromBitmap(frame);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(file);
        data.SaveTo(stream);
    }

    /// <summary>Les commandes ffmpeg : le WebP animé en boucle infinie, et le GIF par palette si GitHub n'affichait pas le WebP.</summary>
    private static string EncodeCommands(string folder)
    {
        var frames = folder.TrimEnd('/', '\\').Replace('\\', '/') + "/image-%03d.png";
        return $"""
            # WebP animé, en couleurs complètes, boucle infinie. Mesuré : environ 4 Mo à 90 et 2,7 Mo à 80.
            # Le fichier doit rester sous 5 Mo : baisser -quality si besoin.
            ffmpeg -y -framerate {FramesPerSecond} -i {frames} -c:v libwebp_anim -lossless 0 -quality 90 -compression_level 6 -loop 0 -an docs/images/accueil.webp

            # Repli GIF (256 couleurs par palette), seulement si le WebP animé ne s'affiche pas sur GitHub.
            ffmpeg -y -framerate {FramesPerSecond} -i {frames} -vf "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" -loop 0 docs/images/accueil.gif
            """ + Environment.NewLine;
    }
}
