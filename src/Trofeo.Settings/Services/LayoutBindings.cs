using Trofeo.Core.Layout;

namespace Trofeo.Settings.Services;

/// <summary>Les raccourcis d'une disposition qui visent des cases précises.</summary>
public static class LayoutBindings
{
    /// <summary>
    /// La disposition sans les raccourcis qui visent l'une des cases <paramref name="removed"/> : à appeler quand une case disparaît
    /// pour de bon (oubliée, module désinstallé), dans la même modification, pour qu'Annuler remette la case et ses raccourcis
    /// ensemble. Les raccourcis sans case visée (« le premier module qui accepte ») ne sont pas touchés. Rien à retirer : la même
    /// disposition, avec la même liste de raccourcis.
    /// </summary>
    public static DashboardLayout WithoutTargets(DashboardLayout layout, IReadOnlyCollection<Guid> removed)
    {
        if (layout.Shortcuts is not { } shortcuts || removed.Count == 0)
            return layout;

        var kept = shortcuts.Where(b => b.Target is not { } target || !removed.Contains(target)).ToList();
        return kept.Count == shortcuts.Count ? layout : layout with { Shortcuts = kept };
    }
}
