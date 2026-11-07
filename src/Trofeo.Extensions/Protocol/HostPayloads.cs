using System.Text.Json;
using System.Text.Json.Serialization;
using AshboardVision.Sdk;
using Trofeo.Core.Rendering;

namespace Trofeo.Extensions.Protocol;

/// <summary>
/// Les charges utiles JSON que le protocole transporte sans les typer dans <see cref="HostMessage"/> : l'apparence
/// (<see cref="Hello.Appearance"/>, <see cref="AppearanceChanged.Appearance"/>) et les données du service
/// (<see cref="DataValue.Value"/>). Le service les écrit et l'hôte les lit avec ces seules méthodes, pour que le format ne dérive jamais.
/// <para>
/// Apparence : les <see cref="AppearanceSettings"/> telles que <c>layout.json</c> les écrit (noms en majuscule initiale,
/// énumérations en texte), lues sans tenir compte de la casse. Données : noms en camelCase ; une durée s'écrit
/// <c>hh:mm:ss</c> et la pochette en base 64.
/// </para>
/// </summary>
public static class HostPayloads
{
    /// <summary><see cref="DataGet.Kind"/> pour les capteurs.</summary>
    public const string SensorsKind = "sensors";

    /// <summary><see cref="DataGet.Kind"/> pour la musique en cours.</summary>
    public const string NowPlayingKind = "nowPlaying";

    private static readonly JsonSerializerOptions AppearanceOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>L'apparence en JSON, pour <see cref="Hello"/> et <see cref="AppearanceChanged"/>.</summary>
    public static JsonElement AppearanceToJson(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return JsonSerializer.SerializeToElement(appearance, AppearanceOptions);
    }

    /// <summary>Lit l'apparence ; un JSON nul donne l'apparence par défaut. Lève <see cref="JsonException"/> si elle est illisible.</summary>
    public static AppearanceSettings ReadAppearance(JsonElement appearance) =>
        appearance.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? AppearanceSettings.Default
            : appearance.Deserialize<AppearanceSettings>(AppearanceOptions) ?? AppearanceSettings.Default;

    /// <summary>Les capteurs en JSON, pour <see cref="DataValue"/>.</summary>
    public static JsonElement SensorsToJson(SensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return JsonSerializer.SerializeToElement(reading, DataOptions);
    }

    /// <summary>Lit les capteurs ; <c>null</c> si le JSON est nul. Lève <see cref="JsonException"/> s'il est illisible.</summary>
    public static SensorReading? ReadSensors(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.Deserialize<SensorReading>(DataOptions);

    /// <summary>La musique en cours en JSON, pour <see cref="DataValue"/>.</summary>
    public static JsonElement NowPlayingToJson(NowPlayingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return JsonSerializer.SerializeToElement(info, DataOptions);
    }

    /// <summary>Lit la musique en cours ; <c>null</c> si le JSON est nul. Lève <see cref="JsonException"/> s'il est illisible.</summary>
    public static NowPlayingInfo? ReadNowPlaying(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.Deserialize<NowPlayingInfo>(DataOptions);
}
