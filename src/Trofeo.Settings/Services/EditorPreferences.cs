using System.Text.Json;

namespace Trofeo.Settings.Services;

/// <summary>
/// Préférences de la fenêtre elle-même (pas de la disposition) : <c>%AppData%\ashboard-vision\settings-app.json</c>.
/// </summary>
/// <param name="FirstRunDone">L'assistant de première ouverture a été terminé. Fermé en route, il se rouvre au lancement suivant ; « Relancer l'assistant » (Paramètres) le remet à faux.</param>
/// <param name="RealTimePreview">Aperçu à 12 images par seconde au lieu de 4.</param>
/// <param name="FollowView">Choisir une vue dans l'onglet Disposition l'affiche aussi sur l'écran ; décoché, seul l'aperçu change.</param>
public sealed record EditorPreferences(bool FirstRunDone = false, bool RealTimePreview = false, bool FollowView = true)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Lit le fichier ; absent, illisible ou de mauvais type : les valeurs par défaut. Ne lève jamais.</summary>
    public static EditorPreferences Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(path), Options) ?? new EditorPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new EditorPreferences();
        }
    }

    /// <summary>Écrit le fichier (écriture atomique). Une préférence perdue ne vaut pas une fenêtre qui plante : l'échec est avalé.</summary>
    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
