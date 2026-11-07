namespace Trofeo.Extensions;

/// <summary>Jusqu'où on peut croire ce que dit la liste scellée (voir <see cref="ApprovedModulesStore.Inspect"/>).</summary>
public enum ListTrust
{
    /// <summary>Le fichier n'existe pas : aucun module n'a jamais été validé (ou la liste a déjà été effacée).</summary>
    Absent,

    /// <summary>Le sceau tient : le contenu est celui que le logiciel a écrit.</summary>
    Verified,

    /// <summary>Le fichier est là mais son sceau ne tient pas (modifié, clé absente ou indéchiffrable, contenu invalide) : ce qu'il dit
    /// est lu au mieux, sans être cru.</summary>
    Unverified,

    /// <summary>Le fichier ou sa clé n'a pas pu être lu (verrou d'un antivirus, de OneDrive…) : on ne sait rien de son contenu.</summary>
    Unreadable,
}

/// <summary>
/// Ce que la désinstallation tire de la liste scellée, avec le degré de confiance. Les textes sont rendus tels que le fichier les
/// porte : à l'appelant de ne s'en servir qu'après les avoir vérifiés (un identifiant qui ne répond pas à la règle ne sert à rien).
/// </summary>
/// <param name="Trust">Jusqu'où croire le reste.</param>
/// <param name="ModulesRoot">Le dossier des modules choisi ; <c>null</c> s'il n'y en a pas, et toujours <c>null</c> sauf si <see cref="ListTrust.Verified"/>.</param>
/// <param name="ModuleIds">Les identifiants des modules validés.</param>
/// <param name="DevFolderPaths">Les chemins des dossiers de développement.</param>
public sealed record ListInspection(
    ListTrust Trust, string? ModulesRoot, IReadOnlyList<string> ModuleIds, IReadOnlyList<string> DevFolderPaths);
