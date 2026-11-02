using System.Runtime.Versioning;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions;

/// <summary>
/// Ce qui touche au disque quand on installe ou désinstalle un module : où va le module, l'extraction du paquet (par
/// <see cref="AvmodPackage.ExtractTo"/>, jamais autrement), la lecture accordée à l'AppContainer et les suppressions. Le
/// paquet vient de n'importe qui : l'inspection des chemins, de la taille et du nombre d'entrées est celle de
/// <see cref="AvmodPackage"/> ; ici on ajoute que l'identifiant et la version, qui servent de noms de dossiers, ne sortent
/// jamais de la racine des modules, et que ce qui est écrit sur le disque est bien ce qu'on a annoncé à l'utilisateur.
/// </summary>
internal sealed class ModuleInstaller
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Action<string, string> _grantRead;

    /// <param name="grantRead">Accorde au conteneur du module (identifiant, dossier) la lecture de son dossier ; par défaut l'AppContainer.</param>
    public ModuleInstaller(Action<string, string>? grantRead = null) => _grantRead = grantRead ?? GrantToContainer;

    /// <summary>
    /// Le dossier d'une version (<c>&lt;racine&gt;\&lt;id&gt;\&lt;version&gt;</c>), ou <c>null</c> si l'identifiant ou la version ne
    /// peut pas servir de nom de dossier sous cette racine (nom réservé par Windows, caractère interdit, chemin qui sortirait de la racine).
    /// </summary>
    public static string? VersionDirectory(string root, string id, string version)
    {
        if (!IsSafeName(id) || !IsSafeName(version))
        {
            return null;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directory = Path.GetFullPath(Path.Combine(fullRoot, id, version));
        return directory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? directory : null;
    }

    /// <summary>Le dossier de toutes les versions d'un module (<c>&lt;racine&gt;\&lt;id&gt;</c>), ou <c>null</c> si l'identifiant est inutilisable.</summary>
    public static string? ModuleDirectory(string root, string id)
    {
        if (!IsSafeName(id))
        {
            return null;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directory = Path.GetFullPath(Path.Combine(fullRoot, id));
        return directory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? directory : null;
    }

    /// <summary>
    /// Extrait le paquet dans <paramref name="directory"/> (qui ne doit pas exister), accorde la lecture au conteneur, relit le
    /// manifeste écrit sur le disque et calcule l'empreinte du contenu. Si quelque chose échoue, ou si le manifeste écrit n'est pas
    /// celui qu'on a annoncé (le fichier a pu être remplacé entre l'inspection et l'extraction), le dossier est supprimé.
    /// </summary>
    /// <param name="expected">Le manifeste montré à l'utilisateur.</param>
    /// <returns>Le manifeste tel qu'il est sur le disque et l'empreinte, ou la raison de l'échec en français.</returns>
    public (ModuleManifest? Manifest, string? ContentHash, string? Error) Extract(string avmodPath, string directory, ModuleManifest expected)
    {
        try
        {
            AvmodPackage.ExtractTo(avmodPath, directory);
        }
        catch (InvalidDataException ex)
        {
            return (null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveEmptyParent(directory);
            return (null, null, $"Le module n'a pas pu être copié dans le dossier des modules : {ex.Message}");
        }

        try
        {
            var read = CommunityCatalog.ReadManifest(directory);
            if (read.Manifest is not { } written)
            {
                return Fail(directory, read.Message ?? "Le manifeste écrit sur le disque est illisible.");
            }

            if (!string.Equals(written.Id, expected.Id, StringComparison.Ordinal)
                || !string.Equals(written.Version, expected.Version, StringComparison.Ordinal)
                || !written.Permissions.SequenceEqual(expected.Permissions)
                || !string.Equals(written.Repository, expected.Repository, StringComparison.Ordinal))
            {
                return Fail(directory, "Le paquet a changé pendant l'installation : recommence.");
            }

            _grantRead(written.Id, directory);
            return (written, ContentHash.Compute(directory), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return Fail(directory, $"Le module n'a pas pu être préparé : {ex.Message}");
        }
    }

    /// <summary>Supprime un dossier d'installation (extraction ratée, version remplacée). <c>null</c> : supprimé ou absent ; sinon, la raison.</summary>
    public static string? TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            RemoveEmptyParent(directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private (ModuleManifest? Manifest, string? ContentHash, string? Error) Fail(string directory, string error)
    {
        TryDelete(directory);
        return (null, null, error);
    }

    /// <summary>Retire <c>&lt;racine&gt;\&lt;id&gt;</c> s'il ne contient plus rien (une première installation qui échoue ne laisse pas de dossier vide).</summary>
    private static void RemoveEmptyParent(string versionDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(versionDirectory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un dossier vide qui reste ne gêne personne.
        }
    }

    private static bool IsSafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => c < ' ' || c == ':'))
        {
            return false;
        }

        // Windows réserve « con.txt » comme « con » : le nom est jugé sur ce qui précède le premier point.
        return !ReservedNames.Contains(name.Split('.')[0].TrimEnd(' '));
    }

    [SupportedOSPlatform("windows")]
    private static void GrantToContainer(string moduleId, string directory) => AppContainerProfile.Ensure(moduleId).GrantRead(directory);
}
