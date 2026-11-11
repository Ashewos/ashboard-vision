namespace Trofeo.Settings.Services;

/// <summary>
/// Ce que la ligne de commande dit d'un module à installer : le chemin à ouvrir (<see cref="Path"/>), ou la raison pour laquelle il ne
/// s'ouvre pas (<see cref="Problem"/>, une phrase à montrer à l'utilisateur). Les deux sont vides quand aucun argument n'est un <c>.avmod</c>.
/// </summary>
public readonly record struct PackageResolution(string? Path, string? Problem);

/// <summary>
/// <c>Trofeo.Settings.exe &lt;chemin.avmod&gt;</c> : le chemin d'un module à installer, tel que la ligne de commande le donne. Ce n'est
/// qu'un chemin : le service lit le fichier (sans rien écrire), la boîte d'installation montre ce qu'il contient, et rien ne s'installe
/// sans le clic sur « Installer ». Seul un fichier d'un disque local est accepté : un chemin réseau (<c>\hôte\partage</c>,
/// <c>\?\UNC\…</c>) ou un lecteur réseau ferait connecter la fenêtre à un hôte choisi par celui qui a écrit la ligne de commande. Le
/// sélecteur de fichiers, lui, reste libre : c'est un choix de l'utilisateur.
/// <para>
/// La même règle sert à la ligne de commande du premier lancement et aux chemins qu'un second lancement lui passe
/// (<see cref="SingleInstance.ArgumentsReceived"/>) : ce qui arrive par le tube n'est pas plus digne de confiance.
/// </para>
/// </summary>
public static class PackageArgument
{
    /// <summary>
    /// Le premier argument qui est un fichier <c>.avmod</c> existant d'un disque local, en chemin complet ; <c>null</c> sinon. Un chemin
    /// réseau est écarté avant tout accès au disque.
    /// </summary>
    public static string? Find(IEnumerable<string>? args, Func<string, bool>? fileExists = null, Func<string, DriveType>? driveType = null) =>
        Resolve(args, fileExists, driveType).Path;

    /// <summary>
    /// Comme <see cref="Find"/>, mais dit aussi pourquoi un <c>.avmod</c> ne s'ouvre pas : fichier d'un partage réseau (« Copie ce fichier sur
    /// ce PC pour l'installer. ») ou introuvable (disparu, chemin invalide). C'est le premier argument qui se termine par <c>.avmod</c> qui décide.
    /// </summary>
    public static PackageResolution Resolve(IEnumerable<string>? args, Func<string, bool>? fileExists = null, Func<string, DriveType>? driveType = null)
    {
        fileExists ??= File.Exists;
        driveType ??= root => new DriveInfo(root).DriveType;

        var network = new PackageResolution(null, Texts.PackageNetworkPath);
        var missing = new PackageResolution(null, Texts.PackageMissing);
        var unopenable = new PackageResolution(null, Texts.PackageUnopenable);

        foreach (var raw in args ?? [])
        {
            var arg = raw?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(arg) || !arg.EndsWith(".avmod", StringComparison.OrdinalIgnoreCase))
                continue;

            // Un chemin de disque local écrit avec le préfixe long ou de périphérique (« \\?\C:\x.avmod », « \\.\C:\x.avmod ») reste un
            // fichier local : le préfixe est retiré, la suite le traite comme « C:\x.avmod ».
            if ((arg.StartsWith(@"\\?\", StringComparison.Ordinal) || arg.StartsWith(@"\\.\", StringComparison.Ordinal)) && StartsWithDriveRoot(arg.AsSpan(4)))
                arg = arg[4..];

            // Un nom de réseau (« \hôte\partage », « //hôte/partage », « \\?\UNC\… », « \\.\UNC\… ») est refusé sans rien toucher.
            // Les autres noms de périphérique (« \\?\Volume{…}\ ») ne disent pas où est le fichier : message neutre.
            if (arg.StartsWith(@"\\", StringComparison.Ordinal) || arg.StartsWith("//", StringComparison.Ordinal))
            {
                var device = arg.StartsWith(@"\\?\", StringComparison.Ordinal) || arg.StartsWith(@"\\.\", StringComparison.Ordinal);
                return device && !arg.AsSpan(4).StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? unopenable : network;
            }

            try
            {
                var full = Path.GetFullPath(arg);
                if (full.StartsWith(@"\\", StringComparison.Ordinal))
                    return network;
                if (Path.GetPathRoot(full) is not { Length: > 0 } root)
                    return missing;
                var drive = driveType(root);
                if (drive == DriveType.Network)
                    return network;
                if (drive == DriveType.Unknown)
                    return unopenable; // Windows ne sait pas dire de quel genre de lecteur il s'agit : ni « réseau » ni « introuvable » ne serait juste
                if (drive == DriveType.NoRootDirectory)
                    return missing;
                return fileExists(full) ? new PackageResolution(full, null) : missing;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return missing;
            }
        }

        return default;
    }

    // « C:\… » ou « C:/… ».
    private static bool StartsWithDriveRoot(ReadOnlySpan<char> path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
}
