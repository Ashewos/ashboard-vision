namespace Trofeo.BrandAssets;

/// <summary>
/// Fabrique les images de la marque dans <c>assets/brand</c> : icônes, images de l'installateur, logo horizontal avec le nom en tracés.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage : Trofeo.BrandAssets --logo <dossier des SVG> --font <police .ttf> --out <dossier de sortie> [--control <dossier>]
          --logo     docs/superpowers/logo (les SVG « ashboard-symbole-… » du kit)
          --font     src/Trofeo.Core/Assets/Fonts/Manrope-Medium.ttf (le nom « Ashboard Vision », mis en tracés)
          --out      assets/brand (icônes .ico, logos .svg et .png, images .bmp de l'assistant d'installation)
          --control  facultatif, hors du dépôt : des PNG pour regarder le contenu des .ico et des .bmp produits
        """;

    private static int Main(string[] args)
    {
        try
        {
            var options = new Dictionary<string, string>();
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    return Fail($"Argument inattendu : {args[i]}");
                options[args[i]] = args[i + 1];
            }

            if (!options.TryGetValue("--logo", out var logo) || !options.TryGetValue("--font", out var font) || !options.TryGetValue("--out", out var output)
                || options.Keys.Any(key => key is not ("--logo" or "--font" or "--out" or "--control")))
                return Fail("Options attendues : --logo, --font, --out (et --control).");

            using var builder = new BrandBuilder(logo, font, output);
            foreach (var file in builder.BuildAll())
                Console.WriteLine($"écrit : {Path.Combine(output, file)}");

            if (options.TryGetValue("--control", out var control))
            {
                ControlImages.Write(output, control);
                Console.WriteLine($"images de contrôle : {control}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
