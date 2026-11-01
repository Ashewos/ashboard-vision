using System.Security.Cryptography;
using System.Text;

namespace Trofeo.Extensions;

/// <summary>
/// L'empreinte du contenu d'un module : un SHA-256 en hexadécimal minuscule qui couvre les noms et les octets de
/// tous ses fichiers. Une ligne par fichier, <c>chemin-relatif-en-minuscules-avec-/</c> + <c>\0</c> + SHA-256 du
/// fichier (hexadécimal minuscule), lignes triées dans l'ordre ordinal, chacune terminée par <c>\n</c> ; l'empreinte
/// est le SHA-256 de ce texte en UTF-8. L'ordre de lecture ne compte donc pas, un octet changé ou un fichier
/// renommé, ajouté ou retiré change l'empreinte.
/// </summary>
public static class ContentHash
{
    // Fichiers cachés et système compris : un fichier caché ne doit pas échapper à l'empreinte.
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>L'empreinte de tous les fichiers du dossier, sous-dossiers compris.</summary>
    public static string Compute(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = new List<(string RelativePath, Stream Content)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", AllFiles))
            {
                files.Add((Path.GetRelativePath(root, file), new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }

            return Compute(files);
        }
        finally
        {
            foreach (var (_, content) in files)
            {
                content.Dispose();
            }
        }
    }

    /// <summary>
    /// L'empreinte de fichiers déjà ouverts (le verrou d'exécution calcule l'empreinte sur les fichiers qu'il garde
    /// ouverts). Les flux sont lus depuis leur position actuelle jusqu'à la fin, et ne sont pas fermés ici. Deux
    /// chemins qui ne diffèrent que par la casse ou le séparateur sont refusés.
    /// </summary>
    public static string Compute(IReadOnlyList<(string RelativePath, Stream Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var lines = new List<string>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, content) in files)
        {
            var normalized = Normalize(relativePath);
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Deux fichiers portent le même chemin : « {normalized} ».", nameof(files));
            }

            lines.Add(normalized + "\0" + Convert.ToHexStringLower(SHA256.HashData(content)));
        }

        lines.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').ToLowerInvariant();
}
