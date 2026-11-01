using System.Text.Json;
using System.Text.Json.Serialization;
using AshboardVision.Sdk;

namespace Trofeo.Extensions.Protocol;

/// <summary>Les réglages JSON du protocole : camelCase, énumérations en texte (jamais en nombre), propriétés absentes quand elles sont nulles, champs obligatoires exigés.</summary>
internal static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // La permission d'abord : elle prend le pas sur le convertisseur d'énumérations générique.
        Converters = { new PermissionConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Un autre langage n'écrira pas forcément « type » en premier.
        AllowOutOfOrderMetadataProperties = true,

        // Ce que dit un hôte n'est pas fiable : une chaîne ou une liste non nullable ne peut être ni absente ni nulle, et un
        // paramètre de constructeur sans valeur par défaut est obligatoire (les champs facultatifs ont « = null »).
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
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
