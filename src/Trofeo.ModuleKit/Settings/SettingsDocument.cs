using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trofeo.ModuleKit;

/// <summary>
/// Lecture et écriture de réglages JSON par chemin pointé (« unifi.host »). Ne lève jamais pour une forme
/// inattendue. Les éléments rendus sont des copies, jamais liées à un document libéré ; les entrées ne sont jamais modifiées.
/// </summary>
public static class SettingsDocument
{
    /// <summary>L'élément au chemin donné, ou <c>null</c> s'il est absent ou si la forme ne convient pas.</summary>
    public static JsonElement? Get(JsonElement settings, string key)
    {
        var current = settings;
        foreach (var segment in Split(key))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }
        if (current.ValueKind == JsonValueKind.Undefined)
            return null;
        return current.Clone();
    }

    /// <summary>value null (ou indéfini, <c>default(JsonElement)</c>) : supprime la clé. Crée les objets intermédiaires ; une racine qui n'est pas un objet devient {}.</summary>
    public static JsonElement Set(JsonElement settings, string key, JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Undefined }) value = null; // GetRawText lèverait sur un élément indéfini
        var root = settings.ValueKind == JsonValueKind.Object
            ? (JsonObject)JsonNode.Parse(settings.GetRawText())!
            : new JsonObject();

        var segments = Split(key);
        if (segments.Length > 0)
        {
            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (node[segments[i]] is JsonObject child)
                {
                    node = child;
                    continue;
                }
                if (value is null)
                    return ToElement(root); // rien à supprimer plus bas
                var created = new JsonObject();
                node[segments[i]] = created; // remplace un intermédiaire absent ou qui n'est pas un objet
                node = created;
            }

            var last = segments[^1];
            if (value is null)
                node.Remove(last);
            else
                node[last] = JsonNode.Parse(value.Value.GetRawText());
        }
        return ToElement(root);
    }

    public static string? GetString(JsonElement settings, string key) =>
        Get(settings, key) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static bool? GetBool(JsonElement settings, string key) =>
        Get(settings, key) is { } e && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : null;

    /// <summary>Les chaînes du tableau au chemin donné ; vide si le chemin est absent ou n'est pas un tableau.</summary>
    public static IReadOnlyList<string> GetStrings(JsonElement settings, string key)
    {
        if (Get(settings, key) is not { ValueKind: JsonValueKind.Array } array)
            return [];
        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    /// <summary>Condition absente : visible. Sinon, compare à la valeur du champ visé, ou à son Default s'il est absent.</summary>
    public static bool IsVisible(SettingField field, JsonElement settings, SettingsSchema schema)
    {
        if (field.VisibleWhen is not { } condition)
            return true;

        var actual = Get(settings, condition.Key) switch
        {
            { ValueKind: JsonValueKind.String } e => e.GetString(),
            { ValueKind: JsonValueKind.True } => "true",
            { ValueKind: JsonValueKind.False } => "false",
            { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
            _ => DefaultOf(schema, condition.Key),
        };
        return actual is not null && actual == condition.Value;
    }

    /// <summary>La valeur par défaut du champ, écrite comme une condition l'écrit ; <c>null</c> : pas de défaut comparable.</summary>
    private static string? DefaultOf(SettingsSchema schema, string key) =>
        schema.Fields.FirstOrDefault(f => f.Key == key) switch
        {
            ChoiceField c => c.Default,
            ToggleField t => t.Default ? "true" : "false",
            TextField => "",
            NumberField n => n.Default.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PortField p => p.Default.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

    private static string[] Split(string key) =>
        string.IsNullOrEmpty(key) ? [] : key.Split('.');

    private static JsonElement ToElement(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
