using System.Text.Json;
using System.Text.Json.Serialization;
using AshboardVision.Sdk;
using Trofeo.ModuleKit;

namespace Trofeo.Control;

/// <summary>
/// Ce que la fenêtre sait d'un type de module qui vient de la communauté : qui l'a écrit, ce qu'il demande
/// (<paramref name="Declared"/>, dans son manifeste) et ce que l'utilisateur lui a accordé (<paramref name="Granted"/>).
/// </summary>
public sealed record CommunityTypeInfo(
    string Author, string Description, string Version, IReadOnlyList<ModulePermission> Declared, IReadOnlyList<ModulePermission> Granted);

/// <summary>
/// Un type de module tel que la fenêtre de réglage le connaît : de quoi construire son formulaire.
/// <paramref name="Community"/> n'est renseigné que pour un module complémentaire.
/// </summary>
public sealed record ModuleTypeInfo(string TypeId, string DisplayName, GridSize MinSize,
    IReadOnlyDictionary<SizePreset, GridSize> Presets, bool AcceptsCommands, SettingsSchema Schema, CommunityTypeInfo? Community = null);

/// <summary>Réponse à la poignée de main : version du service et types de modules disponibles.</summary>
public sealed record Welcome(string ServiceVersion, int ProtocolVersion, IReadOnlyList<ModuleTypeInfo> Types);

public enum LinkScreenState { Ready, Recovering, Disconnected }

public sealed record ScreenInfo(LinkScreenState State, string? Reason);

public sealed record ModuleState(Guid InstanceId, string TypeId, ModuleHealth Health, string? ActionMessage, IReadOnlyList<string> Notices);

public sealed record ProcessInfo(long PrivateBytes, long WorkingSetBytes, double CpuPercent);

public sealed record FrameInfo(int FramesSent, double RenderMs, double SendMs);

public sealed record SensorInfo(bool PawnIoInstalled, bool HelperInstalled, bool HelperConnected);

/// <summary>
/// L'état du service. <paramref name="CurrentViewId"/> est la vue affichée sur l'écran en ce moment ;
/// <paramref name="RefusedShortcutKeys"/> les touches que Windows a refusées au dernier enregistrement des raccourcis (écrites comme
/// dans la disposition, par exemple « Ctrl+Alt+Right ») ; vide si tout est enregistré.
/// </summary>
public sealed record ServiceStatus(ScreenInfo Screen, bool Emulated, IReadOnlyList<ModuleState> Modules,
    ProcessInfo Process, FrameInfo Frames, SensorInfo Sensors, Guid CurrentViewId, IReadOnlyList<string> RefusedShortcutKeys);

/// <summary>Ce que le service répond quand on lui demande si une combinaison de touches est libre (<see cref="IControlHandler.TestShortcutKeysAsync"/>).</summary>
public enum ShortcutKeyState
{
    /// <summary>Windows l'accepte (ou le service l'a déjà enregistrée lui-même : les conflits internes sont vérifiés par la fenêtre).</summary>
    Free,

    /// <summary>Les touches ne se lisent pas (voir <c>ShortcutKeys.TryParse</c>).</summary>
    Unreadable,

    /// <summary>Une autre application a déjà pris cette combinaison.</summary>
    TakenByAnotherProgram,
}

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

/// <summary>Où en est un module complémentaire, tel que la fenêtre de réglage le connaît (miroir de l'état du service).</summary>
public enum LinkCommunityState { Ready, NeedsApproval, Changed, NeedsNewerApp, TooOld, Invalid, Unavailable, SetAside }

/// <summary>
/// Un module complémentaire vu par la fenêtre. <paramref name="Message"/> est écrit pour être lu tel quel ; <paramref name="Declared"/>
/// est ce que le manifeste demande, <paramref name="Granted"/> ce que l'utilisateur a accordé. Les deux dernières valeurs décrivent une
/// mise à jour trouvée sur GitHub (<c>null</c> : aucune). Les permissions qu'elle apporte ne se connaissent qu'après sa préparation
/// (<see cref="ModuleUpdatePreparation"/>), qui télécharge le paquet. <paramref name="DevFolderPath"/> est le dossier de travail d'un module
/// du mode développeur (le même texte que dans <see cref="CommunityOverview.DevFolders"/>) ; <c>null</c> pour un module installé.
/// </summary>
public sealed record CommunityModuleInfo(
    string Id, string Name, string Author, string Description, string? Version, LinkCommunityState State, string? Message,
    IReadOnlyList<ModulePermission> Declared, IReadOnlyList<ModulePermission> Granted, bool FromDevFolder,
    string? UpdateVersion, string? UpdateNotes, string? DevFolderPath = null);

/// <summary>
/// Ce que le service a lu dans le paquet d'une mise à jour qu'il vient de télécharger et de vérifier (clic sur « Mettre à jour »). Si
/// <paramref name="Ok"/> est faux, <paramref name="Message"/> dit pourquoi (en français, à montrer tel quel) et le reste est vide. Sinon
/// le fichier est gardé par le service : l'installation (<see cref="IControlHandler.InstallModuleUpdateAsync"/>) installe celui-là, sans
/// rien retélécharger, et <see cref="IControlHandler.CancelModuleUpdate"/> l'abandonne. <paramref name="Declared"/> est tout ce que la
/// nouvelle version déclare, <paramref name="NewPermissions"/> ce qu'elle déclare en plus de la version installée.
/// </summary>
public sealed record ModuleUpdatePreparation(
    bool Ok, string? Message, string? Version, string? Notes,
    IReadOnlyList<ModulePermission> Declared, IReadOnlyList<ModulePermission> NewPermissions);

/// <summary>
/// Les modules complémentaires et leurs réglages généraux : le dossier des modules, le mode développeur et ses dossiers, et
/// <paramref name="StoreTampered"/> quand la liste des modules validés a été trouvée altérée (rien n'est alors validé).
/// </summary>
public sealed record CommunityOverview(
    IReadOnlyList<CommunityModuleInfo> Modules, string ModulesRoot, bool DeveloperMode, IReadOnlyList<string> DevFolders, bool StoreTampered);

/// <summary>
/// Ce que le service a vu dans un paquet <c>.avmod</c> avant d'y écrire quoi que ce soit. <paramref name="Errors"/> dit, en français,
/// ce qui empêche l'installation (vide : elle est possible) ; <paramref name="Conflict"/> signale un identifiant déjà pris par un autre
/// dépôt ; <paramref name="InstalledVersion"/> est la version déjà installée de ce module (une mise à jour, donc).
/// </summary>
public sealed record PackageInfo(
    string? Id, string? Name, string? Author, string? Version, string? Description, IReadOnlyList<ModulePermission> Declared,
    IReadOnlyList<string> Errors, string? Conflict, string? InstalledVersion);

/// <summary>
/// Ce que consomme un module : le service lui-même, un module d'origine ou un module complémentaire (<paramref name="IsCommunity"/>).
/// <paramref name="High"/> signale une consommation durablement élevée.
/// </summary>
public sealed record ModuleConsumption(string Id, string DisplayName, bool IsCommunity, double CpuPercent, long MemoryBytes, bool High);

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
    /// <paramref name="viewId"/> : la vue à montrer, affichée ou non (<c>null</c> : celle qui est affichée) ; une vue qui n'existe
    /// pas lève <see cref="InvalidOperationException"/>.
    /// </summary>
    IDisposable SubscribePreview(int maxFps, Guid? viewId, Action<PreviewFrame> onFrame);

    // ---- Vues ----

    /// <summary>
    /// Levé après chaque changement de la vue affichée sur l'écran (raccourci clavier, fenêtre de réglage ou rechargement), avec la
    /// nouvelle vue. Peut l'être depuis n'importe quel fil : un abonné ne doit pas bloquer.
    /// </summary>
    event Action<Guid>? ViewChanged;

    /// <summary>
    /// Affiche la vue <paramref name="viewId"/> sur l'écran (avec le glissement, dont le sens se déduit de l'ordre des vues). Faux
    /// si cette vue n'existe pas ; vrai, sans rien faire, si elle est déjà affichée.
    /// </summary>
    Task<bool> ShowViewAsync(Guid viewId, CancellationToken ct);

    // ---- Raccourcis ----

    /// <summary>
    /// Essaie d'enregistrer <paramref name="keys"/> auprès de Windows puis la relâche aussitôt : la combinaison est-elle libre ? Une
    /// combinaison que le service a lui-même enregistrée rend <see cref="ShortcutKeyState.Free"/>. Lève une exception si le service
    /// n'a pas de raccourcis (la fenêtre des raccourcis est indisponible).
    /// </summary>
    Task<ShortcutKeyState> TestShortcutKeysAsync(string keys, CancellationToken ct);

    // ---- Modules complémentaires ----

    /// <summary>Les types de modules connus maintenant (la liste de la poignée de main change quand un module s'installe ou se valide).</summary>
    IReadOnlyList<ModuleTypeInfo> GetTypes();

    /// <summary>Les modules complémentaires trouvés, le dossier des modules et le mode développeur.</summary>
    CommunityOverview GetCommunity();

    /// <summary>Ce qu'il y a dans un <c>.avmod</c>, sans rien écrire sur le disque.</summary>
    PackageInfo InspectPackage(string path);

    /// <summary>Installe un <c>.avmod</c> avec les permissions cochées par l'utilisateur.</summary>
    Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct);

    /// <summary>Valide un module trouvé dans le dossier des modules, avec les permissions cochées.</summary>
    ActionResult ApproveModule(string id, IReadOnlyList<ModulePermission> granted);

    /// <summary>Change les permissions d'un module ; son hôte est relancé avec les nouvelles.</summary>
    Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct);

    /// <summary>Retire un module : dossier, secrets, validation.</summary>
    Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct);

    /// <summary>Remet en route un module mis de côté.</summary>
    Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct);

    /// <summary>Choisit le dossier des modules.</summary>
    ActionResult SetModulesRoot(string path);

    /// <summary>Allume ou éteint le mode développeur.</summary>
    ActionResult SetDeveloperMode(bool on);

    /// <summary>Ajoute un dossier de développement avec les permissions accordées.</summary>
    ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted);

    /// <summary>
    /// Ce que contient un dossier de travail, avant de l'ajouter : le service lit son manifeste et n'exécute rien, n'écrit rien et ne change
    /// aucun état. Tout refus (chemin, manifeste illisible, SDK incompatible, identifiant déjà pris) est dans <see cref="PackageInfo.Errors"/>.
    /// </summary>
    PackageInfo InspectDevFolder(string path);

    /// <summary>Retire un dossier de développement (le dossier lui-même n'est pas touché).</summary>
    ActionResult RemoveDevFolder(string path);

    /// <summary>Recharge un module du mode développeur avec les fichiers de son dossier.</summary>
    Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct);

    /// <summary>Cherche les mises à jour des modules complémentaires ; le résultat se lit ensuite avec <see cref="GetCommunity"/>.</summary>
    Task CheckModuleUpdatesAsync(CancellationToken ct);

    /// <summary>
    /// Prépare la mise à jour proposée pour un module : télécharge le paquet, vérifie son empreinte et lit son manifeste. Le fichier est
    /// gardé jusqu'à l'installation, l'abandon ou son expiration ; une nouvelle préparation (de n'importe quel module) remplace la précédente.
    /// </summary>
    Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id, CancellationToken ct);

    /// <summary>Abandonne la préparation en cours ou prête pour ce module (supprime le fichier) ; sans effet s'il n'y en a pas.</summary>
    void CancelModuleUpdate(string id);

    /// <summary>
    /// Installe le fichier préparé pour ce module, avec les permissions cochées (les nouvelles comprises), sans rien retélécharger. Sans
    /// préparation, refuse.
    /// </summary>
    Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct);

    /// <summary>Ce que consomment le service et chaque module.</summary>
    IReadOnlyList<ModuleConsumption> GetConsumption();
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
    public const string ShowView = "showView";
    public const string ViewShown = "viewShown";
    public const string TestShortcutKeys = "testShortcutKeys";
    public const string GetUpdate = "getUpdate";
    public const string CheckUpdates = "checkUpdates";
    public const string SetUpdatePreferences = "setUpdatePreferences";
    public const string InstallUpdate = "installUpdate";
    public const string Types = "types";
    public const string Community = "community";
    public const string InspectPackage = "inspectPackage";
    public const string InstallPackage = "installPackage";
    public const string ApproveModule = "approveModule";
    public const string SetModulePermissions = "setModulePermissions";
    public const string UninstallModule = "uninstallModule";
    public const string RelaunchModule = "relaunchModule";
    public const string SetModulesRoot = "setModulesRoot";
    public const string SetDeveloperMode = "setDeveloperMode";
    public const string AddDevFolder = "addDevFolder";
    public const string InspectDevFolder = "inspectDevFolder";
    public const string RemoveDevFolder = "removeDevFolder";
    public const string ReloadDevModule = "reloadDevModule";
    public const string CheckModuleUpdates = "checkModuleUpdates";
    public const string PrepareModuleUpdate = "prepareModuleUpdate";
    public const string CancelModuleUpdate = "cancelModuleUpdate";
    public const string InstallModuleUpdate = "installModuleUpdate";
    public const string Consumption = "consumption";
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
    public Guid? ViewId { get; init; }
    public LinkUpdateChannel? Channel { get; init; }
    public bool? WaitForNextStable { get; init; }
    public bool? ConfirmDowngrade { get; init; }
    public string? ModuleId { get; init; }
    public string? Path { get; init; }
    public IReadOnlyList<ModulePermission>? Granted { get; init; }
    public bool? On { get; init; }
    public string? Keys { get; init; }
}

/// <summary>Réponse JSON : <see cref="ReplyTo"/> reprend l'identifiant de la requête ; type « error » pour un échec.</summary>
internal sealed record Response(int ReplyTo, string Type)
{
    public string? Message { get; init; }
    public Welcome? Welcome { get; init; }
    public ServiceStatus? Status { get; init; }
    public Reloaded? Reloaded { get; init; }
    public bool? Restarted { get; init; }
    public bool? Shown { get; init; }
    public Guid? ViewId { get; init; }
    public ShortcutKeyState? KeyState { get; init; }
    public IReadOnlyList<LookupResult>? Results { get; init; }
    public ActionResult? Action { get; init; }
    public UpdateInfo? Update { get; init; }
    public IReadOnlyList<ModuleTypeInfo>? Types { get; init; }
    public CommunityOverview? Community { get; init; }
    public PackageInfo? Package { get; init; }
    public IReadOnlyList<ModuleConsumption>? Consumption { get; init; }
    public ModuleUpdatePreparation? Preparation { get; init; }
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
