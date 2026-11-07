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
    private readonly Action<string> _deleteProfile;

    /// <param name="grantRead">Accorde au conteneur du module (identifiant, dossier) la lecture de son dossier ; par défaut l'AppContainer.</param>
    /// <param name="deleteProfile">Supprime le conteneur d'un module (et ses données) ; par défaut l'AppContainer.</param>
    public ModuleInstaller(Action<string, string>? grantRead = null, Action<string>? deleteProfile = null)
    {
        _grantRead = grantRead ?? GrantToContainer;
        _deleteProfile = deleteProfile ?? DeleteContainer;
    }

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
    /// <param name="newModule">Vrai pour une première installation de cet identifiant : si elle échoue après la création du conteneur
    /// (l'accord de la lecture le crée), le conteneur est supprimé aussi, car aucune liste ne le retrouverait jamais. Faux pour une mise à
    /// jour ou une réinstallation : le conteneur existait déjà, avec les données du module installé.</param>
    /// <returns>Le manifeste tel qu'il est sur le disque et l'empreinte, ou la raison de l'échec en français.</returns>
    public (ModuleManifest? Manifest, string? ContentHash, string? Error) Extract(
        string avmodPath, string directory, ModuleManifest expected, bool newModule = false)
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
            if (newModule)
            {
                DiscardContainer(expected.Id);
            }

            return Fail(directory, $"Le module n'a pas pu être préparé : {ex.Message}");
        }
    }

    /// <summary>Supprime le conteneur qu'une première installation a créé puis abandonné (sans erreur : c'est un nettoyage).</summary>
    public void DiscardContainer(string moduleId)
    {
        try
        {
            _deleteProfile(moduleId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"Le conteneur de « {moduleId} » n'a pas pu être supprimé après l'échec de l'installation : {ex.Message}");
        }
    }

    /// <summary>
    /// Supprime un dossier d'installation (extraction ratée, version remplacée, module désinstallé). <c>null</c> : supprimé ou absent ;
    /// sinon, la raison. Le dossier qui le contient est retiré lui aussi s'il est vide (<c>&lt;racine&gt;\&lt;id&gt;</c> quand on supprime une
    /// version), sauf si <paramref name="removeEmptyParent"/> est faux (on supprime alors <c>&lt;id&gt;</c> : sa racine n'est pas à nous).
    /// </summary>
    public static string? TryDelete(string directory, bool removeEmptyParent = true)
    {
        // OneDrive, l'antivirus ou l'indexation gardent souvent un fichier ou le dossier ouvert un court instant juste après l'arrêt de
        // l'hôte (« accès refusé » sur le dossier vidé, vu dans un dossier des modules synchronisé par OneDrive) : on réessaie avant de renoncer.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                if (removeEmptyParent)
                {
                    RemoveEmptyParent(directory);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    return ex.Message;
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    /// <summary>Essais de <see cref="TryDelete"/> avant de renoncer, espacés de <see cref="DeleteRetryDelay"/> (3 secondes en tout).</summary>
    private const int DeleteAttempts = 16;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

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
    private static void DeleteContainer(string moduleId) => AppContainerProfile.Delete(moduleId);

    [SupportedOSPlatform("windows")]
    private static void GrantToContainer(string moduleId, string directory) => AppContainerProfile.Ensure(moduleId).GrantRead(directory);
}
