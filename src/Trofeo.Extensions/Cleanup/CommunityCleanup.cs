using System.Diagnostics;
using System.Security;

namespace Trofeo.Extensions;

/// <summary>
/// Le nettoyage des modules complémentaires à la désinstallation d'Ashboard Vision (lancé par <c>UserCleanup</c>, service arrêté) :
/// le conteneur AppContainer et la portée du coffre à secrets de chaque module connu, toujours ; avec « effacer mes réglages », aussi
/// la liste scellée, sa clé et le dossier des modules <b>s'il est le dossier par défaut</b> (<c>Documents\Ashboard Vision\Modules</c>,
/// puis <c>Documents\Ashboard Vision</c> s'il est resté vide). Un dossier choisi par l'utilisateur, même vide, et les dossiers de
/// développement ne sont jamais supprimés.
/// <para>
/// La liste scellée est dans le dossier des réglages, donc à la portée de quiconque peut y écrire : elle est lue <b>avant</b> toute
/// suppression, et on ne la croit que si son sceau tient. Sans sceau valable, seuls les identifiants conformes à la règle d'identifiant
/// servent (ils ne peuvent désigner qu'un conteneur <c>ashboard-vision.mod.&lt;id&gt;</c> et une portée de module de la communauté,
/// jamais un type d'origine ni un chemin), et le dossier des modules n'est pas supprimé puisque son emplacement n'est pas sûr. Liste
/// absente : le dossier des modules est celui par défaut. Les identifiants des dossiers de développement se lisent dans leur manifeste
/// (lu, jamais exécuté).
/// </para>
/// <para>Chaque étape est tentée même si la précédente a échoué : un désinstallateur ne s'arrête pas sur un conteneur déjà parti.</para>
/// </summary>
public sealed class CommunityCleanup
{
    private readonly ApprovedModulesStore _store;
    private readonly string _listPath;
    private readonly string _keyPath;
    private readonly Action<string> _deleteProfile;
    private readonly Action<string> _removeScope;
    private readonly string _defaultFolder;
    private readonly Action<string> _warn;

    /// <param name="settingsDir">Le dossier des réglages (<c>%AppData%\ashboard-vision</c>), où sont <c>modules.json</c> et <c>modules.key</c>.</param>
    /// <param name="deleteProfile">Supprime l'AppContainer d'un module et ses données (<c>AppContainerProfile.Delete</c>).</param>
    /// <param name="removeScope">Efface la portée d'un module dans le coffre à secrets (<c>DpapiSecretVault.RemoveScope</c>).</param>
    /// <param name="defaultModulesFolder">Le dossier des modules par défaut ; par défaut <see cref="ModulesFolder.Default"/> (les tests en donnent un temporaire).</param>
    /// <param name="warn">Reçoit ce qui n'a pas pu être fait ; par défaut, le journal (<see cref="Trace"/>).</param>
    public CommunityCleanup(
        string settingsDir, Action<string> deleteProfile, Action<string> removeScope, string? defaultModulesFolder = null, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDir);
        _deleteProfile = deleteProfile ?? throw new ArgumentNullException(nameof(deleteProfile));
        _removeScope = removeScope ?? throw new ArgumentNullException(nameof(removeScope));
        _warn = warn ?? (message => Trace.TraceWarning(message));
        _listPath = Path.Combine(settingsDir, ApprovedModulesStore.FileName);
        _keyPath = Path.Combine(settingsDir, ApprovedModulesStore.KeyFileName);
        _store = new ApprovedModulesStore(_listPath, _keyPath, _warn);
        _defaultFolder = defaultModulesFolder ?? ModulesFolder.Default;
    }

    /// <summary>
    /// Vrai pour un chemin absolu sur un disque local : ni chemin relatif, ni partage réseau (<c>\\serveur\…</c>), ni chemin de
    /// périphérique (<c>\\?\</c>, <c>\\.\</c>). Lire un manifeste sur un partage donné par une liste altérée enverrait les identifiants de
    /// Windows de l'utilisateur à une machine choisie par un autre.
    /// </summary>
    public static bool IsLocalAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && !(path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]));

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <param name="deleteSettings">Vrai pour effacer aussi la liste, sa clé et le dossier des modules par défaut.</param>
    public void Run(bool deleteSettings)
    {
        // La liste d'abord : plus rien ne la lira après la première suppression.
        var inspection = _store.Inspect();
        var devFolders = DevFolderPaths(inspection);
        var ids = ModuleIds(inspection, devFolders);

        foreach (var id in ids)
        {
            Step(() => _deleteProfile(id), $"Le conteneur du module {id} n'a pas pu être supprimé");
            Step(() => _removeScope(id), $"Les secrets du module {id} n'ont pas pu être effacés");
        }

        if (!deleteSettings)
        {
            return;
        }

        // Sans liste ou avec une liste dont le sceau tient, on sait où sont les modules. Sinon (sceau faux, liste illisible), non.
        var folderIsKnown = inspection.Trust switch
        {
            ListTrust.Absent => true,
            ListTrust.Verified => string.IsNullOrWhiteSpace(inspection.ModulesRoot) || IsDefaultFolder(inspection.ModulesRoot),
            _ => false,
        };
        if (folderIsKnown)
        {
            RemoveDefaultFolder(devFolders);
        }

        foreach (var file in new[] { _listPath, _listPath + ".tmp", _keyPath, _keyPath + ".tmp" })
        {
            TryDelete(() => File.Delete(file), $"{file} n'a pas pu être supprimé");
        }
    }

    // --- Ce que la liste désigne ---------------------------------------------------------------------------------------------------

    /// <summary>Les dossiers de développement de la liste, avec un chemin utilisable (local si le sceau ne tient pas).</summary>
    private static List<string> DevFolderPaths(ListInspection inspection)
    {
        var paths = new List<string>();
        if (inspection.Trust is not (ListTrust.Verified or ListTrust.Unverified))
        {
            return paths;
        }

        foreach (var path in inspection.DevFolderPaths)
        {
            var usable = inspection.Trust == ListTrust.Verified ? Path.IsPathFullyQualified(path) : IsLocalAbsolutePath(path);
            if (usable)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>Les identifiants valides des modules installés et ceux des manifestes des dossiers de développement, sans doublon.</summary>
    private static List<string> ModuleIds(ListInspection inspection, List<string> devFolders)
    {
        var ids = new List<string>();
        foreach (var id in inspection.ModuleIds)
        {
            if (ManifestReader.IsValidId(id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        foreach (var folder in devFolders)
        {
            // Lu, jamais exécuté ; un dossier disparu, un manifeste illisible ou un identifiant de type d'origine : ignoré.
            if (CommunityCatalog.ReadManifest(folder).Manifest is { } manifest && ManifestReader.IsValidId(manifest.Id) && !ids.Contains(manifest.Id))
            {
                ids.Add(manifest.Id);
            }
        }

        return ids;
    }

    // --- Le dossier des modules par défaut -----------------------------------------------------------------------------------------

    private bool IsDefaultFolder(string root) =>
        Path.IsPathFullyQualified(root) && Normalize(root) is { } path
        && string.Equals(path, Normalize(_defaultFolder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Le chemin complet sans séparateur final ; <c>null</c> si Windows ne sait pas le normaliser.</summary>
    private static string? Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private void RemoveDefaultFolder(List<string> devFolders)
    {
        if (Normalize(_defaultFolder) is not { } folder)
        {
            return;
        }

        var parent = Path.GetDirectoryName(folder);
        if (parent is not null && Directory.Exists(parent) && IsLink(new DirectoryInfo(parent)))
        {
            // Un lien sur Documents\Ashboard Vision : les modules sont ailleurs, on ne va pas y supprimer à travers lui.
            _warn($"{parent} est un lien : le dossier des modules n'a pas été supprimé.");
            return;
        }

        // Les dossiers de développement ne sont jamais supprimés, même s'ils sont rangés dans ce dossier.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in devFolders)
        {
            if (Normalize(path) is { } normalized)
            {
                keep.Add(normalized);
            }
        }

        var info = new DirectoryInfo(folder);
        if (info.Exists)
        {
            DeleteTree(info, keep);
        }

        // Documents\Ashboard Vision ne part que s'il est vide : on n'efface jamais ce que l'utilisateur y a mis.
        if (parent is not null)
        {
            TryDelete(() =>
            {
                var product = new DirectoryInfo(parent);
                if (product.Exists && !product.EnumerateFileSystemInfos().Any())
                {
                    product.Delete();
                }
            }, $"{parent} n'a pas pu être supprimé");
        }
    }

    /// <summary>
    /// Supprime un dossier sans jamais suivre un lien (jonction ou lien symbolique : seul le lien est retiré) et sans toucher à ce que
    /// <paramref name="keep"/> protège : un dossier protégé reste entier, ses parents restent avec lui. Rend vrai si le dossier a disparu.
    /// </summary>
    private bool DeleteTree(DirectoryInfo folder, HashSet<string> keep)
    {
        var path = Normalize(folder.FullName) ?? folder.FullName;
        if (keep.Contains(path))
        {
            return false;
        }

        var gone = true;
        if (!IsLink(folder))
        {
            foreach (var entry in folder.EnumerateFileSystemInfos().ToList())
            {
                if (entry is DirectoryInfo child)
                {
                    gone &= DeleteTree(child, keep);
                }
                else
                {
                    gone &= TryDelete(() =>
                    {
                        entry.Attributes = FileAttributes.Normal;
                        entry.Delete();
                    }, $"{entry.FullName} n'a pas pu être supprimé");
                }
            }

            if (keep.Any(k => IsInside(k, path)))
            {
                return false;
            }
        }

        // Pour un lien, Delete ne retire que le lien ; sinon le dossier est vide ici (ou il reste un fichier qui résiste, et Delete échoue).
        return gone && TryDelete(folder.Delete, $"{folder.FullName} n'a pas pu être supprimé");
    }

    // --- Étapes tentées l'une après l'autre ----------------------------------------------------------------------------------------

    /// <summary>
    /// Tente une suppression de profil ou de portée. Ce qu'elle peut lever est trop varié pour une liste de types (une erreur
    /// d'API Windows devient une <c>COMException</c> ou l'une des exceptions que <c>Marshal.ThrowExceptionForHR</c> choisit selon le code,
    /// le coffre peut dépasser son délai) : comme <c>CommunityModules.Try</c>, tout est rattrapé sauf le manque de mémoire, car un
    /// désinstallateur doit tenter la suite quoi qu'il arrive. L'échec est journalisé.
    /// </summary>
    private void Step(Action action, string failure)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warn($"{failure} ({ex.Message}).");
        }
    }

    /// <summary>Tente une suppression de fichier ou de dossier ; rend faux (et journalise) si elle échoue.</summary>
    private bool TryDelete(Action delete, string failure)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Un fichier encore ouvert : il reste, l'utilisateur pourra le supprimer à la main.
            _warn($"{failure} ({ex.Message}).");
            return false;
        }
    }
}
