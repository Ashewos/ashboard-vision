namespace Trofeo.ModuleKit;

/// <summary>Ce que demande un raccourci à un module.</summary>
public enum ModuleCommandKind
{
    /// <summary>Onglet précédent.</summary>
    PreviousTab,

    /// <summary>Onglet suivant.</summary>
    NextTab,

    /// <summary>Onglet de rang donné (voir <see cref="ModuleCommand.Index"/>).</summary>
    SelectTab,
}

/// <summary>Une commande envoyée à un module par un raccourci.</summary>
/// <param name="Kind">Ce qui est demandé.</param>
/// <param name="Index">Pour <see cref="ModuleCommandKind.SelectTab"/> : rang de l'onglet, à partir de 0.</param>
public readonly record struct ModuleCommand(ModuleCommandKind Kind, int Index = 0);

/// <summary>
/// Un module qui réagit aux raccourcis. Appelé depuis le thread de l'interface ; rend vrai si la commande a servi.
/// </summary>
public interface ICommandableModule
{
    /// <summary>Réagit à une commande de raccourci ; rend vrai si elle a servi. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    bool HandleCommand(ModuleCommand command);
}
