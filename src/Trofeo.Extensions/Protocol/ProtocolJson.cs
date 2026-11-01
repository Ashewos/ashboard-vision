using System.Text.Json;
using System.Text.Json.Serialization;
using AshboardVision.Sdk;

namespace Trofeo.Extensions.Protocol;

/// <summary>Les réglages JSON du protocole : camelCase, énumérations en texte, propriétés absentes quand elles sont nulles.</summary>
internal static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // La permission d'abord : elle prend le pas sur le convertisseur d'énumérations générique.
        Converters = { new PermissionConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Un autre langage n'écrira pas forcément « type » en premier.
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>Une permission voyage avec son nom de manifeste, comme dans <c>manifest.json</c>.</summary>
    private sealed class PermissionConverter : JsonConverter<ModulePermission>
    {
        public override ModulePermission Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && ModulePermissions.TryParse(reader.GetString(), out var permission))
            {
                return permission;
            }

            throw new JsonException("Permission inconnue.");
        }

        public override void Write(Utf8JsonWriter writer, ModulePermission value, JsonSerializerOptions options)
            => writer.WriteStringValue(ModulePermissions.ToManifest(value));
    }
}
