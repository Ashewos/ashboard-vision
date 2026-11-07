using System.IO.Compression;

namespace Trofeo.Extensions;

/// <summary>
/// Fabrique un <c>.avmod</c> à partir du dossier d'un module (c'est ce que fait la commande <c>pack</c> du banc
/// d'essai). Le paquet obtenu est inspecté avant d'être mis en place : un dossier qui ne donnerait pas un paquet
/// acceptable ne laisse aucun fichier.
/// </summary>
public static class AvmodWriter
{
    // Une date fixe : le même dossier donne le même paquet, octet pour octet.
    private static readonly DateTimeOffset FixedTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Écrit <paramref name="outputPath"/> (remplacé s'il existe) avec tous les fichiers du dossier. Si le paquet
    /// est dans le dossier du module, il n'est pas rangé dans lui-même.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">Le dossier du module n'existe pas.</exception>
    /// <exception cref="InvalidDataException">Le dossier ne donne pas un paquet acceptable (le message dit pourquoi).</exception>
    public static void Pack(string moduleDirectory, string outputPath)
    {
        var root = Path.GetFullPath(moduleDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Le dossier du module « {root} » n'existe pas.");
        }

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new IOException($"« {output} » n'a pas de dossier parent."));
        var temp = $"{output}.tmp-{Guid.NewGuid():N}";

        try
        {
            var files = Directory.EnumerateFiles(root, "*", AllFiles)
                .Where(file => !string.Equals(file, output, StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(file, temp, StringComparison.OrdinalIgnoreCase))
                .Select(file => (Full: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
                .OrderBy(file => file.Relative, StringComparer.Ordinal)
                .ToList();

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (full, relative) in files)
                {
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = FixedTime;
                    using var entryStream = entry.Open();
                    using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    source.CopyTo(entryStream);
                }
            }

            var inspection = AvmodPackage.Inspect(temp);
            if (inspection.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join(" ", inspection.Errors));
            }

            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Le nettoyage est un effort : l'erreur d'origine est celle qui compte.
            }

            throw;
        }
    }
}
