using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.Services;

/// <summary>Où en est la liaison avec le service d'affichage.</summary>
public enum LinkState
{
    Connecting,
    Connected,

    /// <summary>Aucun service ne répond (trois tentatives de suite).</summary>
    ServiceStopped,

    /// <summary>Un service répond, mais il parle une autre version du protocole que la fenêtre : relancer le service n'y changerait rien.</summary>
    VersionMismatch,
}

/// <summary>
/// La liaison de la fenêtre avec le service d'affichage : tout ce que les réglages lui demandent passe par là.
/// Les événements arrivent sur le fil de l'interface.
/// </summary>
public interface IServiceLink : IAsyncDisposable
{
    LinkState State { get; }

    /// <summary>Ce que le service a annoncé à la connexion ; <c>null</c> tant qu'on n'est pas connecté.</summary>
    Welcome? Welcome { get; }

    /// <summary>L'état du service, relu chaque seconde quand on est connecté.</summary>
    ServiceStatus? Status { get; }

    /// <summary>Où en sont les mises à jour, relu avec l'état du service ; <c>null</c> tant qu'on n'est pas connecté.</summary>
    UpdateInfo? Updates { get; }

    /// <summary><see cref="State"/>, <see cref="Welcome"/>, <see cref="Status"/>, <see cref="Updates"/> ou <see cref="Types"/> a changé. Sur le fil de l'interface.</summary>
    event Action? Changed;

    /// <summary>Une image de l'écran est arrivée. Sur le fil de l'interface.</summary>
    event Action<PreviewFrame>? FrameReceived;

    /// <summary>Aperçu à 12 images par seconde (<c>true</c>) ou à 4 (<c>false</c>).</summary>
    void SetRealTimePreview(bool realTime);

    /// <summary>
    /// Met l'aperçu en pause (<c>true</c> : plus aucune image demandée, par exemple fenêtre réduite) ou le reprend à 4 ou
    /// 12 images par seconde.
    /// </summary>
    void SetPreviewPaused(bool paused);

    /// <summary>
    /// Les demandes au service. Liaison absente ou fermée (fermeture de la fenêtre comprise) : <see cref="ControlException"/>
    /// avec un message en français, jamais <see cref="ObjectDisposedException"/> ni <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Reloaded> ReloadAsync();
    Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query);
    Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey);

    /// <summary>Vrai si l'instance existait et a été recréée (à appeler après un changement de secret).</summary>
    Task<bool> RestartModuleAsync(Guid instanceId);

    /// <summary>Cherche une mise à jour tout de suite ; au retour, <see cref="Updates"/> dit ce qui a été trouvé.</summary>
    Task CheckUpdatesAsync();

    /// <summary>Enregistre le canal suivi et « attendre la prochaine version stable », puis cherche de nouveau (au retour, <see cref="Updates"/> est à jour).</summary>
    Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable);

    /// <summary>
    /// Télécharge et lance l'installateur de la version proposée. Un résultat non Ok dit pourquoi (autorisation de Windows
    /// refusée, installation déjà lancée…) ; un retour en arrière demande <paramref name="confirmDowngrade"/>.
    /// </summary>
    Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade);

    // ---- Modules complémentaires ----

    /// <summary>
    /// Les types de modules connus du service : ceux de la poignée de main, relus après chaque changement des modules complémentaires
    /// (installation, validation, retrait…). <c>null</c> tant qu'on n'est pas connecté.
    /// </summary>
    IReadOnlyList<ModuleTypeInfo>? Types { get; }

    /// <summary>Les modules complémentaires, le dossier des modules et le mode développeur.</summary>
    Task<CommunityOverview> GetCommunityAsync();

    /// <summary>Ce qu'il y a dans un <c>.avmod</c>, sans rien installer.</summary>
    Task<PackageInfo> InspectPackageAsync(string path);

    /// <summary>
    /// Les demandes qui changent les modules complémentaires. Un résultat non Ok dit pourquoi (en français, à montrer tel quel) ;
    /// au retour, <see cref="Types"/> est à jour.
    /// </summary>
    Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted);
    Task<ActionResult> ApproveModuleAsync(string id, IReadOnlyList<ModulePermission> granted);
    Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted);
    Task<ActionResult> UninstallModuleAsync(string id);
    Task<ActionResult> RelaunchModuleAsync(string id);
    Task<ActionResult> SetModulesRootAsync(string path);
    Task<ActionResult> SetDeveloperModeAsync(bool on);
    Task<ActionResult> AddDevFolderAsync(string path, IReadOnlyList<ModulePermission> granted);
    Task<ActionResult> RemoveDevFolderAsync(string path);

    /// <summary>
    /// Ce que le service lit dans le manifeste d'un dossier de travail avant qu'on l'ajoute : rien n'est lancé ni ajouté. Les refus
    /// (chemin, manifeste illisible, identifiant déjà pris…) sont dans <see cref="PackageInfo.Errors"/>.
    /// </summary>
    Task<PackageInfo> InspectDevFolderAsync(string path);
    Task<ActionResult> ReloadDevModuleAsync(string id);

    /// <summary>Cherche les mises à jour des modules ; le résultat se lit ensuite avec <see cref="GetCommunityAsync"/>.</summary>
    Task CheckModuleUpdatesAsync();

    /// <summary>
    /// Clic sur « Mettre à jour » : le service télécharge le paquet de la mise à jour proposée, vérifie son empreinte et lit son manifeste. La
    /// réponse dit ce que la nouvelle version déclare et ce qu'elle ajoute (les cases à présenter) ; ne change encore aucun module.
    /// </summary>
    Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id);

    /// <summary>Abandonne la mise à jour préparée (le service supprime le fichier).</summary>
    Task CancelModuleUpdateAsync(string id);

    /// <summary>Installe la mise à jour préparée pour un module, avec les permissions cochées (les nouvelles comprises) et sans rien retélécharger.</summary>
    Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted);

    /// <summary>Ce que consomment le service et chaque module.</summary>
    Task<IReadOnlyList<ModuleConsumption>> GetConsumptionAsync();

    /// <summary>Lance Trofeo.Display.exe ; faux s'il est introuvable ou ne démarre pas.</summary>
    Task<bool> StartServiceAsync();
}
