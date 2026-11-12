using Trofeo.Control;
using Trofeo.Core.Shortcuts;

namespace Trofeo.Settings.Services;

/// <summary>Les mots qui désignent ce qu'un raccourci déclenche, partagés par l'onglet Raccourcis et la boîte d'installation d'un module.</summary>
public static class ShortcutLabels
{
    /// <summary>
    /// L'action d'un raccourci en mots (« Onglet suivant », « Vue n° 2 »). Celle d'un module complémentaire est le nom que son manifeste lui
    /// donne, une donnée non fiable qui est nettoyée (<see cref="UntrustedText"/>) ; si le module ou le raccourci n'est pas (ou plus)
    /// connu, c'est l'identifiant du raccourci.
    /// </summary>
    public static string ActionText(ShortcutBinding binding, IReadOnlyList<CommunityModuleInfo> community)
    {
        switch (binding.Command)
        {
            case ShortcutCommand.PreviousTab: return Texts.ShortcutPrevious;
            case ShortcutCommand.NextTab: return Texts.ShortcutNext;
            case ShortcutCommand.SelectTab: return string.Format(Texts.ShortcutSelect, binding.Index + 1);
            case ShortcutCommand.PreviousView: return Texts.ShortcutPreviousView;
            case ShortcutCommand.NextView: return Texts.ShortcutNextView;
            case ShortcutCommand.SelectView: return string.Format(Texts.ShortcutSelectView, binding.Index + 1);
            case ShortcutCommand.Community:
                var declared = Declared(community, binding.ModuleId)?.FirstOrDefault(s => string.Equals(s.Id, binding.ShortcutId, StringComparison.Ordinal));
                var name = UntrustedText.Line(declared?.Name);
                return name.Length > 0 ? name : UntrustedText.Line(binding.ShortcutId);
            default: return UntrustedText.Line(binding.Command.ToString());
        }
    }

    /// <summary>Le nom du module complémentaire tel que la fenêtre le montre (nettoyé), ou <c>null</c> s'il n'est pas connu.</summary>
    public static string? ModuleName(IReadOnlyList<CommunityModuleInfo> community, string? moduleId)
    {
        if (community.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.Ordinal)) is not { } module)
            return null;
        var name = UntrustedText.Line(module.Name);
        return name.Length > 0 ? name : UntrustedText.Line(module.Id);
    }

    /// <summary>Les raccourcis que le module déclare ; <c>null</c> : module inconnu ou service qui ne les a pas dits.</summary>
    public static IReadOnlyList<ModuleShortcutInfo>? Declared(IReadOnlyList<CommunityModuleInfo> community, string? moduleId) =>
        community.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.Ordinal))?.Shortcuts;
}
