using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trofeo.Core.Layout;

/// <summary>Export et import d'une disposition, pour la partager (un fichier à envoyer à un ami).</summary>
public static class LayoutTransfer
{
    /// <summary>Marque du fichier partagé ; la fin donne la version du format.</summary>
    public const string Format = "trofeo-HB-dashboard/disposition/1";

    private const string NotALayout = "Ce fichier n'est pas une disposition " + ProductIdentity.DisplayName + ".";

    /// <summary>
    /// <c>{"Format": Format, "Layout": {...}}</c>. La disposition ne contient que des réglages : les secrets (clés,
    /// jetons) vivent dans le coffre, pas dans <c>layout.json</c>, et ne sont donc jamais exportés.
    /// </summary>
    public static string Export(DashboardLayout layout) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["Format"] = Format, ["Layout"] = layout }, LayoutJson.Options);

    /// <summary>
    /// Même validation que <see cref="LayoutStore.TryLoad"/>. Un fichier qui n'est pas du JSON, n'a pas la bonne
    /// marque <see cref="Format"/> ou n'a pas de <c>Layout</c> : « Ce fichier n'est pas une disposition Trofeo. ».
    /// </summary>
    public static LayoutLoadResult Import(string json)
    {
        JsonNode? layout;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root ||
                root["Format"] is not JsonValue format || !format.TryGetValue<string>(out var name) || name != Format ||
                root["Layout"] is not JsonObject)
                return Refused();
            layout = root["Layout"];
        }
        catch (Exception)
        {
            return Refused();
        }

        return LayoutJson.Parse(layout!.ToJsonString(), warn: null);
    }

    private static LayoutLoadResult Refused() => new(null, NotALayout, []);
}
