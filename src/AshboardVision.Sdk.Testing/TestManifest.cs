using System.Text.Json;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Testing;

/// <summary>
/// Ce que les aides de test lisent d'un <c>manifest.json</c> : les permissions déclarées et les champs <c>port</c> des réglages (les
/// seuls ports que <c>ConnectLocalAsync</c> peut joindre). Le reste du manifeste n'est pas vérifié ici : c'est le travail du banc d'essai
/// et du logiciel, qui lisent le manifeste en entier.
/// </summary>
internal sealed record TestManifest(IReadOnlyList<ModulePermission> Permissions, IReadOnlyList<PortField> PortFields)
{
    public static TestManifest Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Le manifeste « {path} » doit être un objet JSON.");
        }

        var permissions = new List<ModulePermission>();
        if (root.TryGetProperty("permissions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!ModulePermissions.TryParse(text, out var permission))
                {
                    throw new InvalidDataException(
                        $"Le manifeste « {path} » : permission inconnue « {text ?? item.GetRawText()} » (reseau-local, internet, ce-pc, capteurs, musique).");
                }

                if (!permissions.Contains(permission))
                {
                    permissions.Add(permission);
                }
            }
        }

        var ports = new List<PortField>();
        if (root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in settings.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object
                    && field.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "port"
                    && field.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                    && field.TryGetProperty("default", out var proposed) && proposed.TryGetInt32(out var value))
                {
                    var label = field.TryGetProperty("label", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : key.GetString()!;
                    ports.Add(new PortField(key.GetString()!, label, value));
                }
            }
        }

        return new TestManifest(permissions, ports);
    }
}
