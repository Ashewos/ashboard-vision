using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trofeo.Distribution;

/// <summary>
/// Les choix de l'installateur, qu'il écrit dans <c>{app}\options-installation.json</c> (clé <c>demarrerAvecWindows</c>) pour que
/// la préparation propre à l'utilisateur, au premier lancement, les applique.
/// </summary>
public sealed record InstallOptions(bool StartWithWindows)
{
    /// <summary>Les choix de l'installateur, ou <c>null</c> si le fichier est absent, illisible ou sans choix clair.</summary>
    public static InstallOptions? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject { } root
                && root["demarrerAvecWindows"] is JsonValue value
                && value.TryGetValue<bool>(out var startWithWindows)
                ? new InstallOptions(startWithWindows)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
