using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Trofeo.Extensions.Sandbox;

/// <summary>
/// Si le disque d'un dossier garde des droits d'accès (ACL). L'enfermement d'un module repose sur une règle posée sur son dossier
/// (<see cref="AppContainerProfile.GrantRead"/>) : exFAT et FAT32 (clés USB, cartes) n'en portent pas, l'accord échouerait à
/// chaque installation et à chaque lancement.
/// </summary>
internal static partial class VolumeAcls
{
    private const uint FilePersistentAcls = 0x00000008;

    /// <summary>Vrai si les capacités du volume (<c>GetVolumeInformation</c>) comprennent <c>FILE_PERSISTENT_ACLS</c>.</summary>
    public static bool SupportsPersistentAcls(uint fileSystemFlags) => (fileSystemFlags & FilePersistentAcls) != 0;

    /// <summary>
    /// Vrai si le disque du chemin garde les droits d'accès, faux s'il n'en garde pas, <c>null</c> si on ne peut pas le savoir (disque
    /// absent, chemin réseau qui ne répond pas, système autre que Windows) : l'incertitude ne bloque rien, l'installation le dira.
    /// </summary>
    public static bool? Check(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return CheckWindows(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool? CheckWindows(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        if (!root.EndsWith('\\'))
        {
            root += '\\';
        }

        return GetVolumeInformation(root, null, 0, IntPtr.Zero, IntPtr.Zero, out var flags, null, 0)
            ? SupportsPersistentAcls(flags)
            : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName, char[]? volumeNameBuffer, uint volumeNameSize, IntPtr volumeSerialNumber, IntPtr maximumComponentLength,
        out uint fileSystemFlags, char[]? fileSystemNameBuffer, uint fileSystemNameSize);
}
