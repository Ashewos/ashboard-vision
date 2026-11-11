using Trofeo.ModuleKit;

namespace Trofeo.Core.Shortcuts;

/// <summary>
/// Ce que fait un raccourci clavier. Les trois premières valeurs (onglets) gardent leurs noms d'avant les vues : un
/// <c>"Command": "PreviousTab"</c> déjà écrit dans <c>layout.json</c> se relit tel quel.
/// </summary>
public enum ShortcutCommand
{
    /// <summary>Onglet précédent du module qui accepte les commandes.</summary>
    PreviousTab,

    /// <summary>Onglet suivant du module qui accepte les commandes.</summary>
    NextTab,

    /// <summary>Onglet de rang donné (voir <see cref="ShortcutBinding.Index"/>).</summary>
    SelectTab,

    /// <summary>Vue précédente, en boucle.</summary>
    PreviousView,

    /// <summary>Vue suivante, en boucle.</summary>
    NextView,

    /// <summary>Vue de rang donné (voir <see cref="ShortcutBinding.Index"/>).</summary>
    SelectView,

    /// <summary>Un raccourci déclaré par un module complémentaire (voir <see cref="ShortcutBinding.ModuleId"/> et <see cref="ShortcutBinding.ShortcutId"/>).</summary>
    Community,
}

/// <summary>Passage d'une <see cref="ShortcutCommand"/> à la commande qu'un module d'origine comprend.</summary>
public static class ShortcutCommandExtensions
{
    /// <summary>La commande d'onglet correspondante ; <c>null</c> pour une commande de vue ou de module complémentaire.</summary>
    public static ModuleCommandKind? ToModuleCommand(this ShortcutCommand command) => command switch
    {
        ShortcutCommand.PreviousTab => ModuleCommandKind.PreviousTab,
        ShortcutCommand.NextTab => ModuleCommandKind.NextTab,
        ShortcutCommand.SelectTab => ModuleCommandKind.SelectTab,
        _ => null,
    };
}
