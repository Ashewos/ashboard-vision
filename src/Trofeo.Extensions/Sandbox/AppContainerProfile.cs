using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Trofeo.Extensions.Sandbox;

/// <summary>
/// Le profil AppContainer d'un module de la communauté (le mécanisme des applications du Store) : une identité Windows à
/// lui, qui ne voit pas les fichiers de l'utilisateur et que seules des règles explicites (<see cref="GrantRead"/>) laissent
/// lire un dossier. Nom : <c>ashboard-vision.mod.&lt;id&gt;</c>. Marche sans droits d'administrateur.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AppContainerProfile
{
    /// <summary>Début du nom de tous les profils de modules.</summary>
    public const string NamePrefix = "ashboard-vision.mod.";

    /// <summary>
    /// Début du nom des profils du banc d'essai (<c>ashboard-module check</c>) : un profil à part, pour que l'essai d'un module que le
    /// créateur a aussi installé ne touche jamais au profil de l'installation (ni à son nettoyage, qui ne connaît que <see cref="NamePrefix"/>).
    /// </summary>
    public const string CheckNamePrefix = "ashboard-vision.check.";

    // Windows tronque ou refuse au-delà de 64 caractères et n'accepte que lettres, chiffres, point, tiret et soulignement.
    private const int MaxNameLength = 64;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidName();

    private AppContainerProfile(string name, SecurityIdentifier sid, string dataDirectory)
    {
        Name = name;
        Sid = sid;
        DataDirectory = dataDirectory;
    }

    /// <summary>Le nom du profil (<c>ashboard-vision.mod.&lt;id&gt;</c>, ou <c>ashboard-vision.check.&lt;id&gt;</c> pour le banc d'essai).</summary>
    public string Name { get; }

    /// <summary>L'identité du conteneur.</summary>
    public SecurityIdentifier Sid { get; }

    /// <summary>
    /// Le dossier privé du conteneur (sous <c>%LocalAppData%\Packages</c>), qu'il peut lire et écrire. Supprimé avec le profil.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>Le nom du profil d'un module.</summary>
    public static string NameFor(string moduleId) => NameFor(NamePrefix, moduleId);

    /// <summary>Le nom du profil du banc d'essai pour un module.</summary>
    public static string CheckNameFor(string moduleId) => NameFor(CheckNamePrefix, moduleId);

    private static string NameFor(string prefix, string moduleId)
    {
        var name = prefix + moduleId;
        if (moduleId.Length == 0 || name.Length > MaxNameLength || !ValidName().IsMatch(name))
        {
            throw new ArgumentException($"« {moduleId} » ne peut pas servir de nom de conteneur.", nameof(moduleId));
        }

        return name;
    }

    /// <summary>Crée le profil du module, ou retrouve celui qui existe déjà.</summary>
    public static AppContainerProfile Ensure(string moduleId) =>
        EnsureNamed(NameFor(moduleId), "Ashboard Vision - " + moduleId, "Module complémentaire d'Ashboard Vision : " + moduleId);

    /// <summary>Crée le profil du banc d'essai pour ce module (<see cref="CheckNamePrefix"/>), ou retrouve celui qui existe déjà.</summary>
    public static AppContainerProfile EnsureForCheck(string moduleId) =>
        EnsureNamed(CheckNameFor(moduleId), "Ashboard Vision (essai) - " + moduleId, "Essai d'un module avec le banc d'essai d'Ashboard Vision : " + moduleId);

    private static AppContainerProfile EnsureNamed(string name, string displayName, string description)
    {
        var hr = Native.CreateAppContainerProfile(name, displayName, description, IntPtr.Zero, 0, out var sidPointer);
        if (hr == Native.ErrorAlreadyExistsHResult)
        {
            hr = Native.DeriveAppContainerSidFromAppContainerName(name, out sidPointer);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var sid = new SecurityIdentifier(sidPointer);
            return new AppContainerProfile(name, sid, FolderOf(sid));
        }
        finally
        {
            Native.FreeSid(sidPointer);
        }
    }

    /// <summary>Supprime le profil du module et son dossier de données. Sans erreur si le profil n'existe pas.</summary>
    public static void Delete(string moduleId) => DeleteNamed(NameFor(moduleId));

    /// <summary>Supprime le profil du banc d'essai pour ce module et son dossier de données. Sans erreur si le profil n'existe pas.</summary>
    public static void DeleteForCheck(string moduleId) => DeleteNamed(CheckNameFor(moduleId));

    private static void DeleteNamed(string name)
    {
        var hr = Native.DeleteAppContainerProfile(name);
        if (hr != Native.ErrorNotFoundHResult)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>
    /// Autorise ce conteneur, et lui seul, à lire et à exécuter ce dossier et tout ce qu'il contient (règle héritée par les
    /// fichiers présents et futurs). Sans effet si la règle y est déjà.
    /// </summary>
    public void GrantRead(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException($"Dossier introuvable : {directory}");
        }

        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(Sid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string FolderOf(SecurityIdentifier sid)
    {
        Marshal.ThrowExceptionForHR(Native.GetAppContainerFolderPath(sid.Value, out var path));
        try
        {
            return Marshal.PtrToStringUni(path) ?? throw new Win32Exception("Dossier du conteneur introuvable.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
