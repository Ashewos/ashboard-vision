namespace Trofeo.Settings.Services;

/// <summary>
/// <c>Trofeo.Settings.exe &lt;chemin.avmod&gt;</c> : le chemin d'un module à installer, tel que la ligne de commande le donne. Ce n'est
/// qu'un chemin : le service lit le fichier (sans rien écrire), la boîte d'installation montre ce qu'il contient, et rien ne s'installe
/// sans le clic sur « Installer ». Seul un fichier d'un disque local est accepté : un chemin réseau (<c>\hôte\partage</c>,
/// <c>\?\UNC\…</c>) ou un lecteur réseau ferait connecter la fenêtre à un hôte choisi par celui qui a écrit la ligne de commande. Le
/// sélecteur de fichiers, lui, reste libre : c'est un choix de l'utilisateur.
/// </summary>
public static class PackageArgument
{
    /// <summary>
    /// Le premier argument qui est un fichier <c>.avmod</c> existant d'un disque local, en chemin complet ; <c>null</c> sinon. Un chemin
    /// réseau est écarté avant tout accès au disque.
    /// </summary>
    public static string? Find(IEnumerable<string>? args, Func<string, bool>? fileExists = null, Func<string, DriveType>? driveType = null)
    {
        fileExists ??= File.Exists;
        driveType ??= root => new DriveInfo(root).DriveType;

        foreach (var raw in args ?? [])
        {
            var arg = raw?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(arg) || !arg.EndsWith(".avmod", StringComparison.OrdinalIgnoreCase))
                continue;

            // Un nom de réseau (« \hôte\partage », « //hôte/partage », « \?\UNC\… », « \.\UNC\… ») est refusé sans rien toucher.
            if (arg.StartsWith(@"\\", StringComparison.Ordinal) || arg.StartsWith("//", StringComparison.Ordinal))
                return null;

            try
            {
                var full = Path.GetFullPath(arg);
                if (full.StartsWith(@"\\", StringComparison.Ordinal))
                    return null;
                if (Path.GetPathRoot(full) is not { Length: > 0 } root || driveType(root) is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                    return null;
                return fileExists(full) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }
}
