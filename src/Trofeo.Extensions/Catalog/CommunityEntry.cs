namespace Trofeo.Extensions;

/// <summary>L'état d'un module de la communauté trouvé dans le dossier des modules (ou dans un dossier de développement).</summary>
public enum CommunityState
{
    /// <summary>Validé, inchangé, compatible : il peut tourner.</summary>
    Ready,

    /// <summary>Pas encore validé (ou nouvelle version à valider) : aucun code ne tourne avant la validation.</summary>
    NeedsApproval,

    /// <summary>Le contenu a changé depuis la validation.</summary>
    Changed,

    /// <summary>Il demande une version du SDK plus récente que celle du logiciel.</summary>
    NeedsNewerApp,

    /// <summary>Il vise une version du SDK que le logiciel ne prend plus en charge.</summary>
    TooOld,

    /// <summary>Le manifeste ou le dossier est invalide.</summary>
    Invalid,

    /// <summary>Ses fichiers (ou la liste des modules validés) ne se lisent pas pour le moment.</summary>
    Unavailable,

    /// <summary>Mis de côté après des pannes répétées.</summary>
    SetAside,
}

/// <summary>Un module trouvé par <see cref="CommunityCatalog"/>.</summary>
/// <param name="Id">L'identifiant du module (le nom de son dossier ; pour un dossier de développement, celui de son manifeste si lisible).</param>
/// <param name="Directory">Le dossier du module.</param>
/// <param name="Manifest">Son manifeste ; <c>null</c> s'il est absent ou invalide.</param>
/// <param name="State">Ce que le logiciel peut en faire.</param>
/// <param name="Message">Une phrase française pour l'utilisateur ; <c>null</c> pour <see cref="CommunityState.Ready"/>.</param>
/// <param name="Approval">La validation enregistrée pour cet identifiant, s'il y en a une (elle peut porter une autre version que celle du dossier) ; toujours <c>null</c> pour un dossier de développement.</param>
/// <param name="FromDevFolder">Vrai pour une entrée du mode développeur.</param>
public sealed record CommunityEntry(
    string Id,
    string Directory,
    ModuleManifest? Manifest,
    CommunityState State,
    string? Message,
    ApprovedModule? Approval,
    bool FromDevFolder);
