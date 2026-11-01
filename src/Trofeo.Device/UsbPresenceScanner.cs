using System.Runtime.InteropServices;
using System.Text;

namespace Trofeo.Device;

/// <summary>
/// Liste les périphériques USB présents (VID et PID), pilote installé ou non, par SetupAPI. Windows seulement ;
/// n'ouvre aucun périphérique.
/// </summary>
public static class UsbPresenceScanner
{
    private const int InitialBufferSize = 512;

    /// <summary>
    /// Les VID:PID des périphériques USB présents, sans doublon. Une erreur (ou un autre système que Windows)
    /// donne une liste vide : la recherche d'un écran non pris en charge ne doit jamais gêner le service.
    /// </summary>
    public static IReadOnlyList<(ushort Vid, ushort Pid)> Scan()
    {
        var found = new List<(ushort Vid, ushort Pid)>();
        try
        {
            // Le handle détruit la liste (SetupDiDestroyDeviceInfoList) à la sortie, erreur ou non.
            using var set = WinUsbNative.SetupDiGetClassDevs(
                0, "USB", 0, WinUsbNative.DigcfAllClasses | WinUsbNative.DigcfPresent);
            if (set.IsInvalid)
                return found;

            var info = new WinUsbNative.SpDevinfoData { CbSize = (uint)Marshal.SizeOf<WinUsbNative.SpDevinfoData>() };
            for (uint index = 0; WinUsbNative.SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                foreach (var id in ReadHardwareIds(set, ref info))
                {
                    if (KnownThermalrightDevices.TryParseHardwareId(id, out var vid, out var pid) && !found.Contains((vid, pid)))
                        found.Add((vid, pid));
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ExternalException)
        {
            // Pas de SetupAPI (autre système) ou échec du système : on rend ce qu'on a lu, sans lever.
        }

        return found;
    }

    /// <summary>Les identifiants matériels (valeur REG_MULTI_SZ) d'un périphérique ; liste vide s'ils sont illisibles.</summary>
    private static string[] ReadHardwareIds(SafeDeviceInfoSetHandle set, ref WinUsbNative.SpDevinfoData info)
    {
        var buffer = new byte[InitialBufferSize];
        if (!WinUsbNative.SetupDiGetDeviceRegistryProperty(
                set, ref info, WinUsbNative.SpdrpHardwareId, out _, ref buffer[0], (uint)buffer.Length, out var required))
        {
            if (Marshal.GetLastWin32Error() != WinUsbNative.ErrorInsufficientBuffer || required == 0)
                return [];

            buffer = new byte[required];
            if (!WinUsbNative.SetupDiGetDeviceRegistryProperty(
                    set, ref info, WinUsbNative.SpdrpHardwareId, out _, ref buffer[0], (uint)buffer.Length, out required))
                return [];
        }

        var length = (int)Math.Min(required, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
