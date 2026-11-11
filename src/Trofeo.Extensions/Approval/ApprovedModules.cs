using AshboardVision.Sdk;

namespace Trofeo.Extensions;

/// <summary>
/// Un module de la communauté que l'utilisateur a validé : sa version et l'empreinte de son contenu au moment de
/// l'installation, les permissions accordées et, s'il a été mis de côté après des pannes, la raison.
/// </summary>
/// <param name="Id">L'identifiant <c>auteur.module</c>.</param>
/// <param name="Version">La version validée.</param>
/// <param name="Directory">Le dossier du module (<c>&lt;racine&gt;\&lt;id&gt;\&lt;version&gt;</c>).</param>
/// <param name="ContentHash">L'empreinte du contenu, enregistrée à l'installation et revérifiée à chaque lancement.</param>
/// <param name="Granted">Les permissions accordées par l'utilisateur, une par une.</param>
/// <param name="SetAsideReason">Pourquoi le module est mis de côté ; <c>null</c> s'il ne l'est pas.</param>
/// <param name="Repository">Le dépôt GitHub <c>propriétaire/dépôt</c> d'où viennent les mises à jour ; <c>null</c> : aucun.</param>
public sealed record ApprovedModule(
    string Id,
    string Version,
    string Directory,
    string ContentHash,
    IReadOnlyList<ModulePermission> Granted,
    string? SetAsideReason,
    string? Repository);

/// <summary>Un dossier de développement (mode développeur) et les permissions que le créateur lui a accordées.</summary>
/// <param name="Path">Le dossier du module en cours de développement.</param>
/// <param name="Granted">Les permissions accordées à ce dossier.</param>
public sealed record DevFolder(string Path, IReadOnlyList<ModulePermission> Granted);

/// <summary>Le contenu de la liste scellée : tout ce que l'utilisateur a validé pour les modules de la communauté.</summary>
/// <param name="ModulesRoot">Le dossier des modules choisi par l'utilisateur ; <c>null</c> : le dossier par défaut.</param>
/// <param name="DeveloperMode">Le mode développeur est allumé.</param>
/// <param name="DevFolders">Les dossiers de développement.</param>
/// <param name="Modules">Les modules validés.</param>
public sealed record ApprovedModules(
    string? ModulesRoot,
    bool DeveloperMode,
    IReadOnlyList<DevFolder> DevFolders,
    IReadOnlyList<ApprovedModule> Modules)
{
    /// <summary>La liste vide : rien n'est validé, le mode développeur est éteint.</summary>
    public static ApprovedModules Empty { get; } = new(null, false, [], []);
}
