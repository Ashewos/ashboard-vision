using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions;

/// <summary>Ce que <see cref="ICommunityModules.Inspect"/> sait d'un <c>.avmod</c> avant d'y écrire quoi que ce soit.</summary>
/// <param name="Manifest">Le manifeste ; <c>null</c> si le paquet est refusé.</param>
/// <param name="Errors">Ce qui empêche l'installation, en français, à montrer tel quel ; vide si le paquet peut être installé.</param>
/// <param name="Conflict">« Un autre module porte déjà cet identifiant » quand l'identifiant existe avec un autre dépôt ; sinon <c>null</c>.</param>
/// <param name="Installed">La validation déjà enregistrée pour cet identifiant (une mise à jour, donc), ou <c>null</c>.</param>
public sealed record PackageInspection(
    ModuleManifest? Manifest, IReadOnlyList<string> Errors, string? Conflict, ApprovedModule? Installed);

/// <summary>Ce que la liste scellée dit, pour la fenêtre de réglage.</summary>
/// <param name="ModulesRoot">Le dossier des modules en vigueur (celui de l'utilisateur, ou le dossier par défaut).</param>
/// <param name="DeveloperMode">Le mode développeur est allumé.</param>
/// <param name="DevFolders">Les dossiers de développement.</param>
/// <param name="Tampered">La liste a été trouvée altérée : rien n'est validé.</param>
/// <param name="Unreadable">La liste ne se lit pas pour le moment (verrou d'un antivirus…) : les validations sont inconnues.</param>
public sealed record CommunityStoreInfo(
    string ModulesRoot, bool DeveloperMode, IReadOnlyList<string> DevFolders, bool Tampered, bool Unreadable);

/// <summary>
/// Le point d'entrée du service pour les modules de la communauté : les voir, installer, valider, régler et désinstaller.
/// <see cref="CommunityModules"/> l'implémente ; le canal de contrôle ne dépend que de cette interface.
/// </summary>
public interface ICommunityModules
{
    /// <summary>Les modules trouvés au dernier examen du dossier des modules (et des dossiers de développement).</summary>
    IReadOnlyList<CommunityEntry> Entries { get; }

    /// <summary>La liste scellée, vue au dernier examen.</summary>
    CommunityStoreInfo StoreInfo { get; }

    /// <summary>
    /// Les permissions que l'utilisateur a accordées à ce module : celles de sa validation, ou celles de son dossier de développement
    /// (jamais celles de son manifeste). Vide si rien n'est accordé, si la liste scellée est altérée ou si le module n'est pas validé.
    /// </summary>
    IReadOnlyList<ModulePermission> GrantedTo(CommunityEntry entry);

    /// <summary>Levé après chaque changement (installation, validation, panne, mise de côté…), depuis un fil du pool.</summary>
    event EventHandler? Changed;

    /// <summary>Examine le dossier des modules, met à jour les fabriques du registre et lève <see cref="Changed"/>.</summary>
    void Refresh();

    /// <summary>Ce qu'on sait d'un <c>.avmod</c> sans rien écrire sur le disque.</summary>
    PackageInspection Inspect(string avmodPath);

    /// <summary>Installe un <c>.avmod</c> avec les permissions que l'utilisateur a cochées.</summary>
    Task<ActionResult> InstallAsync(string avmodPath, IReadOnlyList<ModulePermission> granted, CancellationToken ct);

    /// <summary>Valide un module trouvé dans le dossier des modules (nouveau, ou dont le contenu a changé).</summary>
    ActionResult Approve(string id, IReadOnlyList<ModulePermission> granted);

    /// <summary>Change les permissions accordées ; l'hôte en cours est relancé avec les nouvelles.</summary>
    Task<ActionResult> SetPermissionsAsync(string id, IReadOnlyList<ModulePermission> granted);

    /// <summary>Retire le module : dossier, AppContainer, secrets, validation.</summary>
    Task<ActionResult> UninstallAsync(string id);

    /// <summary>Remet en route un module mis de côté.</summary>
    Task<ActionResult> RelaunchAsync(string id);

    /// <summary>Choisit le dossier des modules.</summary>
    ActionResult SetModulesRoot(string path);

    /// <summary>Allume ou éteint le mode développeur.</summary>
    ActionResult SetDeveloperMode(bool on);

    /// <summary>Ajoute un dossier de développement avec les permissions accordées.</summary>
    ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted);

    /// <summary>Retire un dossier de développement (le dossier lui-même n'est pas touché).</summary>
    ActionResult RemoveDevFolder(string path);

    /// <summary>Recharge un module du mode développeur : son hôte repart avec les fichiers du dossier.</summary>
    Task<ActionResult> ReloadDevModuleAsync(string id);

    /// <summary>Le plafond d'images par seconde et l'apparence, transmis à chaque hôte (et à ceux qui seront lancés plus tard).</summary>
    void SetDisplayOptions(int maxFps, AppearanceSettings appearance);

    /// <summary>La mise à jour que GitHub propose pour ce module, ou <c>null</c> (aucune, ou module sans dépôt, ou vérification jamais faite).</summary>
    ModuleUpdateOffer? UpdateFor(string id);

    /// <summary>
    /// Cherche les mises à jour des modules maintenant. Sans module portant un dépôt, ne fait aucune requête ; un échec réseau ne lève rien.
    /// </summary>
    Task CheckUpdatesAsync(CancellationToken ct);

    /// <summary>
    /// Prépare la mise à jour proposée pour ce module (clic sur « Mettre à jour ») : le paquet est téléchargé, son empreinte vérifiée, son
    /// manifeste lu (identifiant, dépôt, version), et le fichier gardé jusqu'à <see cref="InstallUpdateAsync"/>, <see cref="CancelUpdate"/> ou
    /// son expiration. Une seule préparation pour tout le service : une nouvelle remplace la précédente. Un échec se dit dans la réponse.
    /// </summary>
    Task<ModuleUpdatePreparation> PrepareUpdateAsync(string id, CancellationToken ct);

    /// <summary>Abandonne la préparation de ce module (téléchargement annulé, fichier supprimé) ; sans effet s'il n'y en a pas.</summary>
    void CancelUpdate(string id);

    /// <summary>
    /// Installe le fichier préparé pour ce module, avec les seules permissions cochées, comme n'importe quel <c>.avmod</c> et sans rien
    /// retélécharger. Sans préparation, refuse.
    /// </summary>
    Task<ActionResult> InstallUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct);

    /// <summary>
    /// Ce que consomment les modules : la ligne des modules d'origine (le service, d'après <paramref name="service"/>) puis un module de
    /// la communauté par hôte qui tourne. Lire la consommation fait passer les relevés à un toutes les 5 s pendant que la fenêtre
    /// continue de la lire ; sans hôte, ne mesure rien.
    /// </summary>
    IReadOnlyList<ModuleConsumption> Consumption(ProcessInfo service);
}
