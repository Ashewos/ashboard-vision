using AshboardVision.Sdk;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions;

/// <summary>
/// Le manifeste d'un module de la communauté (<c>manifest.json</c>, à la racine de son paquet). Il porte tout ce que la
/// fenêtre de réglage montre avant que le module soit validé : identité, permissions, tailles et schéma des réglages.
/// Un manifeste ne s'obtient que par <see cref="ManifestReader.Read"/>, qui en a vérifié chaque champ.
/// </summary>
/// <param name="Id">Identifiant <c>auteur.module</c> ; sert de <c>TypeId</c> dans la disposition et de portée du coffre à secrets.</param>
/// <param name="Version">La version du module (SemVer).</param>
/// <param name="Sdk">La version minimale du SDK dont le module a besoin.</param>
/// <param name="Entry">Le nom de la DLL du module, à la racine de son dossier.</param>
/// <param name="Repository">Le dépôt GitHub <c>propriétaire/dépôt</c> d'où viennent les mises à jour ; absent : pas de mise à jour.</param>
/// <param name="Presets">Les préréglages de taille que le module propose (certains peuvent manquer).</param>
/// <param name="Icon">Le nom d'un PNG à la racine ; absent : pas d'icône.</param>
public sealed record ModuleManifest(
    string Id,
    string Name,
    string Description,
    string Author,
    string Version,
    SdkVersion Sdk,
    string Entry,
    IReadOnlyList<ModulePermission> Permissions,
    string? Repository,
    GridSize MinSize,
    IReadOnlyDictionary<SizePreset, GridSize> Presets,
    SettingsSchema Settings,
    string? Icon)
{
    /// <summary>
    /// Les raccourcis que le module déclare (vide si le manifeste n'en a pas). Une propriété et non un paramètre :
    /// les manifestes des modules écrits pour le SDK 1.0 n'en ont pas, et ceux qui en déclarent demandent le SDK 1.1.
    /// </summary>
    public IReadOnlyList<ModuleShortcut> Shortcuts { get; init; } = [];

    /// <summary>La description du type de module, comme celle des modules d'origine (sans commandes au clavier).</summary>
    public ModuleDescriptor ToDescriptor() => new(Id, Name, MinSize, Presets);
}

/// <summary>
/// Un raccourci déclaré par un module de la communauté : une action que l'utilisateur peut lier à une touche. Le module
/// ne voit jamais le clavier : c'est le service qui écoute la touche et qui lui transmet l'identifiant.
/// </summary>
/// <param name="Id">L'identifiant de l'action, que le module reçoit quand elle est demandée.</param>
/// <param name="Name">Le nom lisible, nettoyé (sans caractère de contrôle ni de sens d'écriture) et coupé à 40 caractères.</param>
/// <param name="Keys">La touche proposée par défaut (« Ctrl+Alt+Shift+R ») ; absente : pas de touche par défaut.</param>
public sealed record ModuleShortcut(string Id, string Name, string? Keys);
