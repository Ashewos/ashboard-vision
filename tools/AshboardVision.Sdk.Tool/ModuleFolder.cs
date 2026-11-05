using System.Text.Json;
using Trofeo.Extensions;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Tool;

/// <summary>Ce que les deux commandes lisent dans le dossier d'un module : son manifeste, ses tailles, ses réglages par défaut.</summary>
internal static class ModuleFolder
{
    /// <summary>La taille au-delà de laquelle <c>manifest.json</c> n'est pas pris pour un manifeste (celle du logiciel).</summary>
    private const long MaxManifestBytes = 256 * 1024;

    /// <summary>Le dossier visé : celui qu'on a donné, sinon le dossier où on est. Chemin complet, sans séparateur final.</summary>
    public static string Resolve(DirectoryInfo? directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory?.FullName ?? Directory.GetCurrentDirectory()));

    /// <summary>
    /// Prépare un dossier de sortie : chemin complet, dossier créé s'il manque, et vérifié inscriptible (un fichier d'essai écrit puis supprimé).
    /// Rend <c>null</c> et la raison en français si le dossier est inutilisable (un fichier de ce nom, un dossier protégé, un chemin invalide).
    /// </summary>
    public static string? PrepareOutputFolder(string path, out string? error)
    {
        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".ashboard-module-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            error = null;
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"le dossier de sortie « {path} » ne peut pas servir ({ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// Lit et valide <c>manifest.json</c> avec les règles du logiciel (<see cref="ManifestReader"/>). Si le manifeste est refusé,
    /// <c>Manifest</c> est <c>null</c> et <c>Errors</c> dit pourquoi, en français.
    /// </summary>
    public static (ModuleManifest? Manifest, IReadOnlyList<string> Errors) ReadManifest(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (null, [$"Le dossier « {folder} » n'existe pas."]);
        }

        var path = Path.Combine(folder, "manifest.json");
        try
        {
            if (!File.Exists(path))
            {
                return (null, [$"Le fichier manifest.json est introuvable dans « {folder} » : lance la commande dans le dossier de ton module, ou donne-le en argument."]);
            }

            if (new FileInfo(path).Length > MaxManifestBytes)
            {
                return (null, ["Le fichier manifest.json est trop gros pour être un manifeste."]);
            }

            var result = ManifestReader.Read(File.ReadAllText(path));
            return (result.Manifest, result.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, [$"Le fichier manifest.json ne se lit pas : {ex.Message}"]);
        }
    }

    /// <summary>Les tailles à dessiner : la taille minimale puis chaque préréglage (compact, idéal, grand), sans doublon.</summary>
    public static IReadOnlyList<GridSize> Sizes(ModuleManifest manifest)
    {
        var sizes = new List<GridSize> { manifest.MinSize };
        foreach (var preset in new[] { SizePreset.Compact, SizePreset.Ideal, SizePreset.Large })
        {
            if (manifest.Presets.TryGetValue(preset, out var size) && !sizes.Contains(size))
            {
                sizes.Add(size);
            }
        }

        return sizes;
    }

    /// <summary>
    /// Les réglages d'une case neuve, tels que la fenêtre de réglage les proposerait : la valeur par défaut de chaque champ qui en a une
    /// (les secrets, les recherches et les actions n'en ont pas).
    /// </summary>
    public static JsonElement DefaultSettings(SettingsSchema schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in schema.Fields)
            {
                switch (field)
                {
                    case ChoiceField choice:
                        writer.WriteString(choice.Key, choice.Default);
                        break;
                    case ToggleField toggle:
                        writer.WriteBoolean(toggle.Key, toggle.Default);
                        break;
                    case NumberField number:
                        writer.WriteNumber(number.Key, number.Default);
                        break;
                    case PortField port:
                        writer.WriteNumber(port.Key, port.Default);
                        break;
                    case TextField text:
                        writer.WriteString(text.Key, "");
                        break;
                    case ListField list:
                        writer.WriteStartArray(list.Key);
                        foreach (var value in list.Default)
                        {
                            writer.WriteStringValue(value);
                        }

                        writer.WriteEndArray();
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
