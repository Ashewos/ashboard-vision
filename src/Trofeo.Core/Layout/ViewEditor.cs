namespace Trofeo.Core.Layout;

/// <summary>Pourquoi une modification des vues est refusée.</summary>
public enum ViewError { None, TooManyViews, LastView, UnknownView, EmptyName }

/// <summary>
/// Résultat d'une modification des vues. <c>Error</c> différent de <see cref="ViewError.None"/> : <c>Layout</c> est
/// la disposition d'entrée, inchangée.
/// </summary>
public sealed record ViewEditResult(DashboardLayout Layout, ViewError Error)
{
    public bool Ok => Error == ViewError.None;
}

/// <summary>
/// Règles d'édition des vues : ajouter, renommer, dupliquer, supprimer, réordonner. Sans état ; aucune opération ne
/// modifie la disposition reçue. Les cases elles-mêmes se modifient avec <see cref="LayoutEditor"/>.
/// </summary>
public static class ViewEditor
{
    /// <summary>Ajoute une vue vide à la fin. Refusée au-delà de <see cref="LayoutView.MaxViews"/> vues ou sans nom.</summary>
    public static ViewEditResult AddView(DashboardLayout layout, string name, Guid newViewId)
    {
        if (layout.Views.Count >= LayoutView.MaxViews) return Refuse(layout, ViewError.TooManyViews);
        if (CleanName(name) is not { } cleaned) return Refuse(layout, ViewError.EmptyName);

        return Accept(layout with { Views = [.. layout.Views, new LayoutView(newViewId, cleaned, [])] });
    }

    /// <summary>Renomme une vue (nom coupé à <see cref="LayoutView.MaxNameLength"/> caractères) ; un nom vide est refusé.</summary>
    public static ViewEditResult RenameView(DashboardLayout layout, Guid viewId, string name)
    {
        if (!Contains(layout, viewId)) return Refuse(layout, ViewError.UnknownView);
        if (CleanName(name) is not { } cleaned) return Refuse(layout, ViewError.EmptyName);

        return Accept(layout with { Views = [.. layout.Views.Select(v => v.Id == viewId ? v with { Name = cleaned } : v)] });
    }

    /// <summary>
    /// Copie une vue juste après l'original, sous le nom « … (copie) » : chaque case garde ses réglages et reçoit un
    /// identifiant neuf (<paramref name="newInstanceId"/>). Refusée au-delà de <see cref="LayoutView.MaxViews"/> vues.
    /// </summary>
    public static ViewEditResult DuplicateView(DashboardLayout layout, Guid viewId, Guid newViewId, Func<Guid> newInstanceId)
    {
        var index = IndexOf(layout, viewId);
        if (index < 0) return Refuse(layout, ViewError.UnknownView);
        if (layout.Views.Count >= LayoutView.MaxViews) return Refuse(layout, ViewError.TooManyViews);

        var original = layout.Views[index];
        const string suffix = " (copie)";
        var name = original.Name.Length + suffix.Length <= LayoutView.MaxNameLength
            ? original.Name + suffix
            : original.Name[..(LayoutView.MaxNameLength - suffix.Length)].TrimEnd() + suffix;
        var copy = new LayoutView(
            newViewId, name, [.. original.Placements.Select(p => p with { InstanceId = newInstanceId() })]);

        var views = layout.Views.ToList();
        views.Insert(index + 1, copy);
        return Accept(layout with { Views = views });
    }

    /// <summary>
    /// Supprime une vue : ses cases vont à la fin de l'étagère, avec leurs réglages et leur identifiant. La dernière
    /// vue ne se supprime pas.
    /// </summary>
    public static ViewEditResult DeleteView(DashboardLayout layout, Guid viewId)
    {
        var index = IndexOf(layout, viewId);
        if (index < 0) return Refuse(layout, ViewError.UnknownView);
        if (layout.Views.Count <= 1) return Refuse(layout, ViewError.LastView);

        return Accept(layout with
        {
            Views = [.. layout.Views.Where((_, i) => i != index)],
            Shelf = [.. layout.Shelf ?? [], .. layout.Views[index].Placements],
        });
    }

    /// <summary>Met la vue au rang <paramref name="newIndex"/> (ramené dans les bornes) ; les autres gardent leur ordre.</summary>
    public static ViewEditResult MoveView(DashboardLayout layout, Guid viewId, int newIndex)
    {
        var index = IndexOf(layout, viewId);
        if (index < 0) return Refuse(layout, ViewError.UnknownView);

        var views = layout.Views.ToList();
        var moved = views[index];
        views.RemoveAt(index);
        views.Insert(Math.Clamp(newIndex, 0, views.Count), moved);
        return Accept(layout with { Views = views });
    }

    /// <summary>
    /// Nettoie un nom de vue ou de case : espaces de bord retirés, coupé à <see cref="LayoutView.MaxNameLength"/>
    /// caractères ; vide ou fait d'espaces : <c>null</c>.
    /// </summary>
    internal static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var trimmed = name.Trim();
        if (trimmed.Length <= LayoutView.MaxNameLength) return trimmed;

        // Ne pas couper une paire de substitution (emoji...) en deux.
        var length = char.IsHighSurrogate(trimmed[LayoutView.MaxNameLength - 1]) ? LayoutView.MaxNameLength - 1 : LayoutView.MaxNameLength;
        return trimmed[..length].TrimEnd();
    }

    private static int IndexOf(DashboardLayout layout, Guid viewId)
    {
        for (var i = 0; i < layout.Views.Count; i++)
            if (layout.Views[i].Id == viewId) return i;
        return -1;
    }

    private static bool Contains(DashboardLayout layout, Guid viewId) => IndexOf(layout, viewId) >= 0;

    private static ViewEditResult Accept(DashboardLayout layout) => new(layout, ViewError.None);

    private static ViewEditResult Refuse(DashboardLayout layout, ViewError error) => new(layout, error);
}
