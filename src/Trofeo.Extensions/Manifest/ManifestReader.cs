using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AshboardVision.Sdk;
using Trofeo.Distribution;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions;

/// <summary>Le résultat de la lecture d'un manifeste : le manifeste, ou la liste de ce qui ne va pas (jamais les deux).</summary>
public sealed record ManifestReadResult(ModuleManifest? Manifest, IReadOnlyList<string> Errors);

/// <summary>
/// Lit et vérifie un <c>manifest.json</c>. Un contenu douteux (JSON cassé, mauvais types, valeurs hors limites) ne
/// lève jamais d'exception : il revient en erreurs en français, que la fenêtre de réglage peut montrer telles quelles.
/// </summary>
public static partial class ManifestReader
{
    /// <summary>Taille maximale d'un identifiant, qui sert aussi de nom de dossier.</summary>
    public const int MaxIdLength = 40;

    private const int MaxNameLength = 40;
    private const int MaxAuthorLength = 40;
    private const int MaxDescriptionLength = 300;
    private const int GridColumns = 16;
    private const int GridRows = 4;

    private static readonly JsonSerializerOptions SettingsOptions = new(JsonSerializerDefaults.Web)
    {
        // Les genres de champ s'écrivent en minuscules ; les énumérations voyagent en texte.
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Le « kind » d'un champ de schéma n'est pas forcément la première propriété.
        AllowOutOfOrderMetadataProperties = true,
        // Un champ sans clé, sans libellé ou sans les nombres qu'il exige est refusé, pas rempli de valeurs vides.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\.[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    /// <summary>Lit un manifeste ; en cas de problème, <see cref="ManifestReadResult.Manifest"/> est <c>null</c> et les erreurs disent tout ce qui ne va pas.</summary>
    public static ManifestReadResult Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return Refused($"Le manifeste n'est pas un JSON valide : {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refused("Le manifeste doit être un objet JSON.");
            }

            var errors = new List<string>();

            var id = ReadText(root, "id", errors, MaxIdLength * 4, blankAllowed: true);
            if (id is not null)
            {
                if (id.Length > MaxIdLength)
                {
                    errors.Add($"\"id\" : l'identifiant fait plus de {MaxIdLength} caractères.");
                }
                else if (!IdPattern().IsMatch(id))
                {
                    errors.Add("\"id\" : L'identifiant doit avoir la forme auteur.module (minuscules, chiffres et tirets, avec un point au milieu).");
                }
            }

            var name = ReadText(root, "name", errors, MaxNameLength);
            var description = ReadText(root, "description", errors, MaxDescriptionLength);
            var author = ReadText(root, "author", errors, MaxAuthorLength);

            var version = ReadText(root, "version", errors, 64);
            if (version is not null && (version.StartsWith('v') || !IsPlainVersionText(version) || !AppVersion.TryParse(version, out _)))
            {
                errors.Add("\"version\" : doit être une version SemVer comme 1.2.3 ou 1.2.3-beta.1.");
            }

            var sdkText = ReadText(root, "sdk", errors, 16);
            var sdk = default(SdkVersion);
            if (sdkText is not null && !SdkVersion.TryParse(sdkText, out sdk))
            {
                errors.Add("\"sdk\" : doit avoir la forme 1.0 (majeur.mineur).");
            }

            var entry = ReadText(root, "entry", errors, 120);
            if (entry is not null && !IsRootFileName(entry, ".dll"))
            {
                errors.Add("\"entry\" : doit être le nom d'une DLL à la racine du module, sans dossier.");
            }

            var permissions = ReadPermissions(root, errors);

            string? repository = null;
            if (ReadOptionalText(root, "repository", errors, 140) is { } repo)
            {
                if (!RepositoryPattern().IsMatch(repo) || repo.EndsWith("/.", StringComparison.Ordinal) || repo.EndsWith("/..", StringComparison.Ordinal))
                {
                    errors.Add("\"repository\" : doit avoir la forme propriétaire/dépôt.");
                }
                else
                {
                    repository = repo;
                }
            }

            string? icon = null;
            if (ReadOptionalText(root, "icon", errors, 120) is { } iconName)
            {
                if (!IsRootFileName(iconName, ".png"))
                {
                    errors.Add("\"icon\" : doit être le nom d'un fichier PNG à la racine du module, sans dossier.");
                }
                else
                {
                    icon = iconName;
                }
            }

            GridSize? minSize = null;
            if (!root.TryGetProperty("minSize", out var minSizeElement) || minSizeElement.ValueKind == JsonValueKind.Null)
            {
                errors.Add("\"minSize\" : champ obligatoire.");
            }
            else
            {
                minSize = ReadSize(minSizeElement, "minSize", errors);
            }

            var presets = ReadPresets(root, errors);
            var settings = ReadSettings(root, errors);

            if (permissions.Contains(ModulePermission.ThisComputer) && !settings.Fields.Any(field => field is PortField))
            {
                errors.Add("Un module qui demande \"Cet ordinateur\" doit proposer un champ port dans ses réglages.");
            }

            if (errors.Count > 0)
            {
                return new ManifestReadResult(null, errors);
            }

            var manifest = new ModuleManifest(
                id!, name!, description!, author!, version!, sdk, entry!, permissions, repository,
                minSize!.Value, presets, settings, icon);
            return new ManifestReadResult(manifest, []);
        }
    }

    /// <summary>
    /// Seuls les caractères d'une version SemVer (chiffres, lettres, point, tiret, plus) : la version devient un nom de
    /// dossier et une étiquette, et le motif d'<see cref="AppVersion"/> accepte un saut de ligne final.
    /// </summary>
    private static bool IsPlainVersionText(string version) =>
        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    private static ManifestReadResult Refused(string error) => new(null, [error]);

    /// <summary>Un texte obligatoire ; <c>null</c> (avec une erreur) s'il manque, n'est pas un texte, est vide ou trop long.</summary>
    private static string? ReadText(JsonElement root, string property, List<string> errors, int maxLength, bool blankAllowed = false)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"\"{property}\" : champ obligatoire.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"\"{property}\" : doit être un texte.");
            return null;
        }

        var text = element.GetString()!;
        if (!blankAllowed && string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"\"{property}\" : ne peut pas être vide.");
            return null;
        }

        if (text.Length > maxLength)
        {
            errors.Add($"\"{property}\" : {maxLength} caractères au plus.");
            return null;
        }

        return text;
    }

    /// <summary>Un texte facultatif ; absent ou <c>null</c> : <c>null</c> sans erreur.</summary>
    private static string? ReadOptionalText(JsonElement root, string property, List<string> errors, int maxLength)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadText(root, property, errors, maxLength);
    }

    /// <summary>Un nom de fichier seul (ni dossier, ni lecteur) qui finit par <paramref name="extension"/>.</summary>
    private static bool IsRootFileName(string name, string extension) =>
        name.Length > extension.Length
        && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name == name.Trim()
        && !name.EndsWith('.');

    private static IReadOnlyList<ModulePermission> ReadPermissions(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("permissions", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add("\"permissions\" : doit être un tableau de noms.");
            return [];
        }

        var result = new List<ModulePermission>();
        foreach (var item in element.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!ModulePermissions.TryParse(text, out var permission))
            {
                var shown = item.ValueKind == JsonValueKind.String ? text : item.GetRawText();
                errors.Add($"\"permissions\" : permission inconnue « {shown} » (permissions connues : reseau-local, internet, ce-pc, capteurs, musique).");
                continue;
            }

            if (!result.Contains(permission))
            {
                result.Add(permission);
            }
        }

        return result;
    }

    private static GridSize? ReadSize(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("columns", out var columnsElement) && columnsElement.TryGetInt32(out var columns)
            && element.TryGetProperty("rows", out var rowsElement) && rowsElement.TryGetInt32(out var rows)
            && columns is >= 1 and <= GridColumns
            && rows is >= 1 and <= GridRows)
        {
            return new GridSize(columns, rows);
        }

        errors.Add($"\"{path}\" : doit être {{ \"columns\": 1 à {GridColumns}, \"rows\": 1 à {GridRows} }}, dans le damier de l'écran.");
        return null;
    }

    private static IReadOnlyDictionary<SizePreset, GridSize> ReadPresets(JsonElement root, List<string> errors)
    {
        var result = new Dictionary<SizePreset, GridSize>();
        if (!root.TryGetProperty("presets", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("\"presets\" : doit être un objet avec compact, ideal et large (chacun facultatif).");
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            var preset = property.Name switch
            {
                "compact" => SizePreset.Compact,
                "ideal" => SizePreset.Ideal,
                "large" => SizePreset.Large,
                _ => (SizePreset?)null,
            };
            if (preset is null)
            {
                errors.Add($"\"presets\" : préréglage inconnu « {property.Name} » (compact, ideal ou large).");
            }
            else if (ReadSize(property.Value, $"presets.{property.Name}", errors) is { } size)
            {
                result[preset.Value] = size;
            }
        }

        return result;
    }

    private static SettingsSchema ReadSettings(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("settings", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return SettingsSchema.Empty;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add("\"settings\" : doit être un tableau de champs.");
            return SettingsSchema.Empty;
        }

        List<SettingField?>? fields;
        try
        {
            fields = element.Deserialize<List<SettingField?>>(SettingsOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            errors.Add($"\"settings\" : réglages illisibles ({ex.Message})");
            return SettingsSchema.Empty;
        }

        var valid = new List<SettingField>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields ?? [])
        {
            if (field is null)
            {
                errors.Add("\"settings\" : un champ est vide.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Key) || string.IsNullOrWhiteSpace(field.Label))
            {
                errors.Add("\"settings\" : chaque champ doit avoir une clé (key) et un libellé (label).");
                continue;
            }

            if (!keys.Add(field.Key))
            {
                errors.Add($"Réglage \"{field.Key}\" : cette clé est utilisée par plusieurs champs.");
            }

            switch (field)
            {
                case NumberField number when number.Min > number.Max || number.Default < number.Min || number.Default > number.Max:
                    errors.Add($"Réglage \"{number.Key}\" : le minimum ne doit pas dépasser le maximum, et la valeur proposée doit être comprise entre les deux.");
                    break;
                case PortField port when port.Default is < 1 or > 65535:
                    errors.Add($"Réglage \"{port.Key}\" : la valeur proposée d'un champ port doit être comprise entre 1 et 65535.");
                    break;
            }

            valid.Add(field);
        }

        return new SettingsSchema(valid);
    }
}
