using System.Globalization;
using System.Text.RegularExpressions;

namespace Trofeo.Device;

/// <summary>
/// Écrans Thermalright reconnus par leur identifiant USB mais pas encore pris en charge (note du
/// 29/10/2026 sur les écrans). Sert à dire à la personne que son écran est vu, et pourquoi il ne s'affiche pas.
/// L'écran pris en charge (<c>0416:5408</c>) n'y figure pas.
/// </summary>
public static partial class KnownThermalrightDevices
{
    private readonly record struct Family(string Name, string Examples);

    private static readonly Dictionary<(ushort Vid, ushort Pid), Family> Table = new()
    {
        [(0x87CD, 0x70DB)] = new("SCSI", "Frozen Horizon Pro, Frozen Magic Pro, Frozen Vision V2"),
        [(0x87AD, 0x70DB)] = new("Bulk", "GrandVision 360, Mjolnir Vision 360, Hyper Vision 360, Peerless Vision 360, Levita Vision"),
        [(0x0402, 0x3922)] = new("SCSI", "Frozen Warframe, Frozen Warframe 240"),
        [(0x0416, 0x5406)] = new("HID type 3", "LC1, LC2, LC3, LC5"),
        [(0x0416, 0x5302)] = new("HID type 2", "Frozen Warframe SE, Assassin Spirit 120 Vision, Trofeo Vision LCD 1280 × 480"),
        [(0x0418, 0x5303)] = new("HID type 3", "TARAN ARMS"),
        [(0x0418, 0x5304)] = new("HID type 3", "TARAN ARMS"),
        [(0x0416, 0x5409)] = new("LY1", "Trofeo Vision de révision LY1, au protocole différent"),
        [(0x0416, 0x8001)] = new("HID, série Digital", "série Digital (afficheur à segments et LED, sans écran)"),
    };

    /// <summary>La famille d'un écran Thermalright connu (« HID type 2 », « SCSI »…), ou <c>null</c> si l'identifiant n'est pas dans la table.</summary>
    public static string? Describe(ushort vid, ushort pid) =>
        Table.TryGetValue((vid, pid), out var family) ? family.Name : null;

    /// <summary>Quelques modèles de cette famille, pour le motif affiché ; <c>null</c> si l'identifiant n'est pas dans la table.</summary>
    public static string? Examples(ushort vid, ushort pid) =>
        Table.TryGetValue((vid, pid), out var family) ? family.Examples : null;

    /// <summary>
    /// Lit le VID et le PID d'un identifiant matériel, par exemple <c>USB\VID_0416&amp;PID_5302&amp;REV_0100</c>.
    /// </summary>
    public static bool TryParseHardwareId(string id, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        var match = HardwareIdPattern().Match(id);
        return match.Success
            && ushort.TryParse(match.Groups["vid"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vid)
            && ushort.TryParse(match.Groups["pid"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out pid);
    }

    [GeneratedRegex("VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})")]
    private static partial Regex HardwareIdPattern();
}
