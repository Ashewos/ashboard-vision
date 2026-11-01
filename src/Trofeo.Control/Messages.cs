using System.Text.Json;
using System.Text.Json.Serialization;
using Trofeo.ModuleKit;

namespace Trofeo.Control;

/// <summary>Un type de module tel que la fenêtre de réglage le connaît : de quoi construire son formulaire.</summary>
public sealed record ModuleTypeInfo(string TypeId, string DisplayName, GridSize MinSize,
    IReadOnlyDictionary<SizePreset, GridSize> Presets, bool AcceptsCommands, SettingsSchema Schema);

/// <summary>Réponse à la poignée de main : version du service et types de modules disponibles.</summary>
public sealed record Welcome(string ServiceVersion, int ProtocolVersion, IReadOnlyList<ModuleTypeInfo> Types);

public enum LinkScreenState { Ready, Recovering, Disconnected }

public sealed record ScreenInfo(LinkScreenState State, string? Reason);

public sealed record ModuleState(Guid InstanceId, string TypeId, ModuleHealth Health, string? ActionMessage, IReadOnlyList<string> Notices);

public sealed record ProcessInfo(long PrivateBytes, long WorkingSetBytes, double CpuPercent);

public sealed record FrameInfo(int FramesSent, double RenderMs, double SendMs);

public sealed record SensorInfo(bool PawnIoInstalled, bool HelperInstalled, bool HelperConnected);

public sealed record ServiceStatus(ScreenInfo Screen, bool Emulated, IReadOnlyList<ModuleState> Modules,
    ProcessInfo Process, FrameInfo Frames, SensorInfo Sensors);

public sealed record Reloaded(bool Applied, string? Error, IReadOnlyList<string> Warnings);

/// <summary>Une image de l'écran (JPEG) et la rotation que l'écran lui applique, en degrés.</summary>
public sealed record PreviewFrame(byte[] Jpeg, int Rotation);

/// <summary>Où en sont les mises à jour, tel que la fenêtre de réglage le connaît (miroir de l'état du service).</summary>
public enum LinkUpdateState { Unknown, Checking, UpToDate, NoStableYet, Available, Downloading, Installing, Failed }

/// <summary>Les versions suivies : seulement les stables, ou aussi les bêtas d'essai.</summary>
public enum LinkUpdateChannel { Stable, Beta }

/// <summary>
/// Les mises à jour vues par la fenêtre. <paramref name="Version"/> est la version proposée, <paramref name="IsDowngrade"/> dit
/// qu'elle est plus ancienne que l'installée, <paramref name="Percent"/> n'a de sens que pendant le téléchargement et
/// <paramref name="Message"/> est écrit pour être lu tel quel. <paramref name="Channel"/> et <paramref name="WaitForNextStable"/>
/// sont les choix en vigueur.
/// </summary>
public sealed record UpdateInfo(
    LinkUpdateState State, string? Version, bool IsDowngrade, int? Percent, string? Message, LinkUpdateChannel Channel, bool WaitForNextStable);

/// <summary>Ce que le service sait faire pour la fenêtre de réglage. Implémenté côté service.</summary>
public interface IControlHandler
{
    Welcome Hello();
    ServiceStatus GetStatus();
    Task<Reloaded> ReloadAsync(CancellationToken ct);

    /// <summary>Vrai si l'instance existait et a été recréée (un module gardé au rechargement ne relit pas ses secrets).</summary>
    Task<bool> RestartModuleAsync(Guid instanceId, CancellationToken ct);

    Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query, CancellationToken ct);
    Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey, CancellationToken ct);

    /// <summary>Où en sont les mises à jour (sans rien lancer).</summary>
    UpdateInfo GetUpdate();

    /// <summary>Vérifie les mises à jour tout de suite ; le résultat se lit ensuite avec <see cref="GetUpdate"/>.</summary>
    Task CheckUpdatesAsync(CancellationToken ct);

    /// <summary>Enregistre le canal suivi et le choix « attendre la prochaine stable », puis vérifie de nouveau.</summary>
    Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken ct);

    /// <summary>Télécharge et lance l'installateur de la version proposée ; un retour en arrière demande <paramref name="confirmDowngrade"/>.</summary>
    Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken ct);

    /// <summary>
    /// <paramref name="onFrame"/> est appelé depuis le fil d'affichage : il ne doit jamais bloquer.
    /// Le tableau d'octets d'une image n'est plus modifié après l'appel (il est écrit plus tard, sur un autre fil).
    /// </summary>
    IDisposable SubscribePreview(int maxFps, Action<PreviewFrame> onFrame);
}

/// <summary>Erreur renvoyée par le service, ou connexion perdue ; le message est affichable.</summary>
public class ControlException(string message) : Exception(message);

/// <summary>Le service parle une autre version du protocole que la fenêtre : les deux ne se comprendront pas avant d'être mis à jour.</summary>
public sealed class ProtocolMismatchException(string message) : ControlException(message);

/// <summary>Types de requête du protocole.</summary>
internal static class MessageTypes
{
    public const string Hello = "hello";
    public const string Status = "status";
    public const string Reload = "reload";
    public const string Restart = "restart";
    public const string Lookup = "lookup";
    public const string Action = "action";
    public const string Preview = "preview";
    public const string GetUpdate = "getUpdate";
    public const string CheckUpdates = "checkUpdates";
    public const string SetUpdatePreferences = "setUpdatePreferences";
    public const string InstallUpdate = "installUpdate";
    public const string Error = "error";
}

/// <summary>Requête JSON : seuls les champs utiles au type de requête sont renseignés.</summary>
internal sealed record Request(int Id, string Type)
{
    public string? TypeId { get; init; }
    public string? FieldKey { get; init; }
    public string? Query { get; init; }
    public Guid? InstanceId { get; init; }
    public string? ActionKey { get; init; }
    public int? MaxFps { get; init; }
    public LinkUpdateChannel? Channel { get; init; }
    public bool? WaitForNextStable { get; init; }
    public bool? ConfirmDowngrade { get; init; }
}

/// <summary>Réponse JSON : <see cref="ReplyTo"/> reprend l'identifiant de la requête ; type « error » pour un échec.</summary>
internal sealed record Response(int ReplyTo, string Type)
{
    public string? Message { get; init; }
    public Welcome? Welcome { get; init; }
    public ServiceStatus? Status { get; init; }
    public Reloaded? Reloaded { get; init; }
    public bool? Restarted { get; init; }
    public IReadOnlyList<LookupResult>? Results { get; init; }
    public ActionResult? Action { get; init; }
    public UpdateInfo? Update { get; init; }
}

internal static class ControlJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // Les énumérations voyagent en texte.
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Le « kind » d'un champ de schéma n'est pas forcément la première propriété.
        AllowOutOfOrderMetadataProperties = true,
    };
}
