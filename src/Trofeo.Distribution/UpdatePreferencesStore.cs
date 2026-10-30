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

    /// <summary>
    /// Ne lève jamais d'exception : fichier absent, verrouillé ou illisible, ce sont les choix par défaut de la version installée
    /// (<see cref="UpdatePreferences.DefaultFor"/>). Un choix enregistré l'emporte toujours.
    /// </summary>
    public UpdatePreferences Load(AppVersion installed)
    {
        try
        {
            if (!File.Exists(path))
            {
                return UpdatePreferences.DefaultFor(installed);
            }

            return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(path), Options) ?? UpdatePreferences.DefaultFor(installed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdatePreferences.DefaultFor(installed);
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
