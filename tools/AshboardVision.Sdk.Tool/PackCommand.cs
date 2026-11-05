using System.Security.Cryptography;
using System.Text;
using Trofeo.Extensions;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// <c>ashboard-module pack [dossier] [--out dossier]</c> : vérifie le manifeste, fabrique <c>&lt;id&gt;-&lt;version&gt;.avmod</c> avec
/// <see cref="AvmodWriter"/> (le paquet est inspecté avant d'être mis en place) et son <c>.sha256</c> au format de <c>sha256sum</c>.
/// Ne fait aucun appel réseau.
/// </summary>
internal static class PackCommand
{
    /// <summary>Le dossier de sortie quand <c>--out</c> n'est pas donné, dans le dossier où on est.</summary>
    public const string DefaultOutputFolder = "pack-out";

    public static int Run(DirectoryInfo? directory, DirectoryInfo? output, TextWriter writer)
    {
        var folder = ModuleFolder.Resolve(directory);
        var (manifest, errors) = ModuleFolder.ReadManifest(folder);
        if (manifest is null)
        {
            foreach (var error in errors)
            {
                writer.WriteLine("✖ Manifeste : " + error);
            }

            writer.WriteLine();
            writer.WriteLine("Pas de paquet : corrige le manifeste d'abord.");
            return 1;
        }

        // Le refus « dans le dossier du module » passe avant la création du dossier : un refus ne laisse rien derrière lui.
        var requested = output?.FullName ?? DefaultOutputFolder;
        if (FullPathOrNull(requested) is { } full && IsInside(folder, full))
        {
            writer.WriteLine("✖ Le paquet serait rangé dans le dossier du module, et il y serait repris au prochain essai : choisis un autre dossier avec --out.");
            return 1;
        }

        var outputFolder = ModuleFolder.PrepareOutputFolder(requested, out var outputError);
        if (outputFolder is null)
        {
            writer.WriteLine("✖ Paquet : " + outputError);
            writer.WriteLine();
            writer.WriteLine("Pas de paquet : le dossier de sortie (--out) n'est pas utilisable.");
            return 1;
        }

        writer.WriteLine($"✔ Manifeste : {manifest.Id} {manifest.Version}");
        var fileName = $"{manifest.Id}-{manifest.Version}.avmod";
        var package = Path.Combine(outputFolder, fileName);
        try
        {
            AvmodWriter.Pack(folder, package);
            File.WriteAllText(package + ".sha256", $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package)))}  {fileName}\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            writer.WriteLine("✖ Paquet : " + ex.Message);
            writer.WriteLine();
            writer.WriteLine("Pas de paquet : " + ex.Message);
            return 1;
        }

        writer.WriteLine($"✔ Paquet : {package}");
        writer.WriteLine($"✔ Empreinte : {package}.sha256");
        writer.WriteLine();
        writer.WriteLine($"Joins ces deux fichiers à ta version GitHub (étiquette v{manifest.Version}).");
        return 0;
    }

    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // PrepareOutputFolder dira pourquoi
        }
    }

    private static bool IsInside(string folder, string candidate) =>
        string.Equals(folder, candidate, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
