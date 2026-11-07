using AshboardVision.Sdk;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// Le relevé de capteurs que le banc d'essai prête aux modules : toujours le même, l'essai ne lit pas le matériel de ce PC. Les unités sont
/// celles du logiciel (<c>SensorKeys</c>) : charges et <c>disk.used</c> en %, températures en °C, fréquences en MHz, puissances en W, mémoire
/// vidéo en Mo, ventilateur en tr/min, mémoire vive (<c>ram.*</c>) en Go.
/// Ce fichier est aussi inclus dans les tests du SDK, qui en vérifient les unités.
/// </summary>
internal static class SampleSensors
{
    public static SensorReading Reading() => new("CPU d'essai", "GPU d'essai", new Dictionary<string, double>
    {
        ["cpu.load"] = 23, ["cpu.temp"] = 54, ["cpu.clock"] = 4200, ["cpu.power"] = 45,
        ["gpu.load"] = 31, ["gpu.temp"] = 61, ["gpu.clock"] = 1800, ["gpu.power"] = 120, ["gpu.fan"] = 1100,
        ["gpu.vram.used"] = 4096, ["gpu.vram.total"] = 12288,
        ["ram.used"] = 14, ["ram.total"] = 32, ["disk.temp"] = 38, ["disk.used"] = 54,
    });
}
