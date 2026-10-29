using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trofeo.Distribution;

/// <summary>Lecture et écriture des choix de mise à jour (<c>%AppData%\ashboard-vision\mises-a-jour.json</c>).</summary>
public sealed class UpdatePreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Ne lève jamais d'exception : fichier absent, verrouillé ou illisible, c'est le canal stable.</summary>
    public UpdatePreferences Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return UpdatePreferences.Default;
            }

            return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(path), Options) ?? UpdatePreferences.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdatePreferences.Default;
        }
    }

    /// <summary>Écriture atomique : fichier temporaire puis remplacement.</summary>
    public void Save(UpdatePreferences preferences)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(preferences, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
