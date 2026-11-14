using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Layout;

/// <summary>Bord (ou coin) d'un module que l'on tire pour le redimensionner.</summary>
public enum ResizeEdge { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Résultat d'une modification. <c>Error</c> différent de <see cref="PlacementError.None"/> : <c>Layout</c> est la
/// disposition d'entrée, inchangée ; <c>Blocker</c> est alors le module qui gêne (chevauchement).
/// </summary>
public sealed record EditResult(DashboardLayout Layout, PlacementError Error, Guid? Blocker = null)
{
    public bool Ok => Error == PlacementError.None;
}

/// <summary>
/// Règles d'édition de la disposition sur le damier : déplacer, redimensionner, ajouter, retirer. Chaque opération
/// vise une vue (son identifiant est le deuxième paramètre), construit la disposition candidate puis la fait valider
/// par <see cref="LayoutValidator"/> ; aucune ne modifie la disposition reçue. Un identifiant inconnu (case ou vue) est
/// refusé avec <see cref="PlacementError.UnknownType"/>.
/// </summary>
public sealed class LayoutEditor(IReadOnlyDictionary<string, ModuleDescriptor> types)
{
    public EditResult Move(DashboardLayout layout, Guid viewId, Guid id, int column, int row)
    {
        if (Locate(layout, viewId, id) is not var (view, index)) return Refuse(layout, PlacementError.UnknownType);

        return Replace(layout, view, index, view.Placements[index] with { Column = column, Row = row });
    }

    /// <summary>
    /// Déplace le ou les bords nommés de <c>delta</c> cases : à gauche et en haut, un delta négatif agrandit (l'index
    /// de colonne ou de rangée recule) ; à droite et en bas, un delta positif agrandit. Les coins combinent les deux axes.
    /// </summary>
    public EditResult Resize(DashboardLayout layout, Guid viewId, Guid id, ResizeEdge edge, int deltaColumns, int deltaRows)
    {
        if (Locate(layout, viewId, id) is not var (view, index)) return Refuse(layout, PlacementError.UnknownType);

        var p = view.Placements[index];
        long column = p.Column, row = p.Row, columns = p.Columns, rows = p.Rows;

        if (edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            column += deltaColumns;
            columns -= deltaColumns;
        }

        if (edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            columns += deltaColumns;

        if (edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            row += deltaRows;
            rows -= deltaRows;
        }

        if (edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            rows += deltaRows;

        // Une taille nulle ou négative est « sous le minimum », pas « hors du damier ».
        if (columns < 1 || rows < 1) return Refuse(layout, PlacementError.BelowMinSize);
        if (Math.Max(Math.Max(column, row), Math.Max(columns, rows)) > int.MaxValue || Math.Min(column, row) < int.MinValue)
            return Refuse(layout, PlacementError.OutOfBounds);

        return Replace(layout, view, index, p with
        {
            Column = (int)column, Row = (int)row, Columns = (int)columns, Rows = (int)rows,
        });
    }

    /// <summary>
    /// Applique un préréglage en gardant le coin haut-gauche ; si la taille sort du damier, le module est poussé vers
    /// la gauche ou vers le haut, juste assez pour tenir. Un préréglage que le type n'offre pas est refusé
    /// (<see cref="PlacementError.UnknownType"/>).
    /// </summary>
    public EditResult ApplyPreset(DashboardLayout layout, Guid viewId, Guid id, SizePreset preset)
    {
        if (Locate(layout, viewId, id) is not var (view, index)) return Refuse(layout, PlacementError.UnknownType);

        var p = view.Placements[index];
        if (!types.TryGetValue(p.TypeId, out var descriptor) || !descriptor.Presets.TryGetValue(preset, out var size))
            return Refuse(layout, PlacementError.UnknownType);

        var column = Math.Max(0, Math.Min(p.Column, GridGeometry.Columns - size.Columns));
        var row = Math.Max(0, Math.Min(p.Row, GridGeometry.Rows - size.Rows));
        return Replace(layout, view, index, p with { Column = column, Row = row, Columns = size.Columns, Rows = size.Rows });
    }

    /// <summary>
    /// Ajoute à la vue <c>viewId</c>. Reprend le dernier module retiré de ce type (identifiant, réglages, taille de texte ; <c>newId</c> est alors
    /// ignoré), sinon en crée un avec <c>newId</c> et des réglages <c>{}</c>. Essaie la taille Idéal, puis Compact, puis
    /// la taille minimum ; pour chacune, la première place libre, rangée par rangée puis colonne par colonne.
    /// </summary>
    public EditResult Add(DashboardLayout layout, Guid viewId, string typeId, Guid newId)
    {
        if (!types.TryGetValue(typeId, out var descriptor)) return Refuse(layout, PlacementError.UnknownType);
        if (FindView(layout, viewId) is not { } view) return Refuse(layout, PlacementError.UnknownType);

        var shelf = layout.Shelf ?? [];
        var shelvedIndex = -1;
        for (var i = shelf.Count - 1; i >= 0 && shelvedIndex < 0; i--)
            if (shelf[i].TypeId == typeId) shelvedIndex = i;

        var template = shelvedIndex >= 0
            ? shelf[shelvedIndex]
            : new ModulePlacement(newId, typeId, 0, 0, 1, 1, TextSize.Normal, EmptySettings());

        foreach (var size in TrySizes(descriptor))
        {
            for (var row = 0; row <= GridGeometry.Rows - size.Rows; row++)
            {
                for (var column = 0; column <= GridGeometry.Columns - size.Columns; column++)
                {
                    var candidate = template with { Column = column, Row = row, Columns = size.Columns, Rows = size.Rows };
                    if (LayoutValidator.Check(view.Placements, candidate, types) != PlacementError.None) continue;

                    return new EditResult(layout.WithPlacements(viewId, [.. view.Placements, candidate]) with
                    {
                        Shelf = shelvedIndex >= 0 ? shelf.Where((_, i) => i != shelvedIndex).ToList() : layout.Shelf,
                    }, PlacementError.None);
                }
            }
        }

        return Refuse(layout, PlacementError.NoRoom);
    }

    /// <summary>Retire le module de la vue <c>viewId</c> ; il va à la fin de <see cref="DashboardLayout.Shelf"/> avec ses réglages.</summary>
    public EditResult Remove(DashboardLayout layout, Guid viewId, Guid id)
    {
        if (Locate(layout, viewId, id) is not var (view, index)) return Refuse(layout, PlacementError.UnknownType);

        return new EditResult(layout.WithPlacements(viewId, view.Placements.Where((_, i) => i != index).ToList()) with
        {
            Shelf = [.. layout.Shelf ?? [], view.Placements[index]],
        }, PlacementError.None);
    }

    /// <summary>Retire un module de <see cref="DashboardLayout.Shelf"/> pour de bon.</summary>
    public EditResult Forget(DashboardLayout layout, Guid shelvedId)
    {
        var shelf = layout.Shelf ?? [];
        var index = IndexOf(shelf, shelvedId);
        if (index < 0) return Refuse(layout, PlacementError.UnknownType);

        return new EditResult(layout with { Shelf = shelf.Where((_, i) => i != index).ToList() }, PlacementError.None);
    }

    /// <summary>Change la taille de texte d'un module placé ou retiré.</summary>
    public EditResult SetTextSize(DashboardLayout layout, Guid id, TextSize size) =>
        Update(layout, id, p => p with { TextSize = size });

    /// <summary>
    /// Donne un nom à une case placée (dans n'importe quelle vue) ou retirée. Un nom vide ou fait d'espaces vaut
    /// <c>null</c> (pas de nom) ; au-delà de <see cref="LayoutView.MaxNameLength"/> caractères, il est coupé.
    /// </summary>
    public EditResult SetName(DashboardLayout layout, Guid id, string? name) =>
        Update(layout, id, p => p with { Name = ViewEditor.CleanName(name) });

    /// <summary>Change les réglages d'un module placé ou retiré.</summary>
    public EditResult SetSettings(DashboardLayout layout, Guid id, JsonElement settings)
    {
        var copy = settings.Clone(); // indépendant du document JSON de l'appelant
        return Update(layout, id, p => p with { Settings = copy });
    }

    /// <summary>Change une case placée dans n'importe quelle vue, sinon une case de l'étagère (les identifiants sont uniques).</summary>
    private static EditResult Update(DashboardLayout layout, Guid id, Func<ModulePlacement, ModulePlacement> change)
    {
        foreach (var view in layout.Views)
        {
            var index = IndexOf(view.Placements, id);
            if (index < 0) continue;

            var placements = view.Placements.ToList();
            placements[index] = change(placements[index]);
            return new EditResult(layout.WithPlacements(view.Id, placements), PlacementError.None);
        }

        var shelf = layout.Shelf ?? [];
        var shelved = IndexOf(shelf, id);
        if (shelved < 0) return Refuse(layout, PlacementError.UnknownType);

        var updated = shelf.ToList();
        updated[shelved] = change(updated[shelved]);
        return new EditResult(layout with { Shelf = updated }, PlacementError.None);
    }

    /// <summary>Valide le candidat à la place de l'instance <paramref name="index"/> de la vue ; refuse en nommant le premier module gênant.</summary>
    private EditResult Replace(DashboardLayout layout, LayoutView view, int index, ModulePlacement candidate)
    {
        var error = LayoutValidator.Check(view.Placements, candidate, types);
        if (error == PlacementError.Overlap) return Refuse(layout, error, FirstBlocker(view.Placements, candidate));
        if (error != PlacementError.None) return Refuse(layout, error);

        var placements = view.Placements.ToList();
        placements[index] = candidate;
        return new EditResult(layout.WithPlacements(view.Id, placements), PlacementError.None);
    }

    private static Guid? FirstBlocker(IReadOnlyList<ModulePlacement> viewPlacements, ModulePlacement candidate)
    {
        foreach (var other in viewPlacements)
            if (other.InstanceId != candidate.InstanceId && LayoutValidator.Intersects(candidate, other))
                return other.InstanceId;
        return null;
    }

    private static IEnumerable<GridSize> TrySizes(ModuleDescriptor descriptor)
    {
        var tried = new List<GridSize>();
        foreach (var size in new[]
                 {
                     descriptor.Presets.GetValueOrDefault(SizePreset.Ideal),
                     descriptor.Presets.GetValueOrDefault(SizePreset.Compact),
                     descriptor.MinSize,
                 })
        {
            // Un préréglage absent donne (0, 0) ; une taille déjà essayée ne l'est pas deux fois.
            if (size.Columns < 1 || size.Rows < 1 || tried.Contains(size)) continue;
            tried.Add(size);
            yield return size;
        }
    }

    private static LayoutView? FindView(DashboardLayout layout, Guid viewId) =>
        layout.Views.FirstOrDefault(v => v.Id == viewId);

    /// <summary>La vue et la position de la case dans cette vue ; <c>null</c> si la vue ou la case n'existe pas.</summary>
    private static (LayoutView View, int Index)? Locate(DashboardLayout layout, Guid viewId, Guid id)
    {
        if (FindView(layout, viewId) is not { } view) return null;
        var index = IndexOf(view.Placements, id);
        return index < 0 ? null : (view, index);
    }

    private static int IndexOf(IReadOnlyList<ModulePlacement> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].InstanceId == id) return i;
        return -1;
    }

    private static EditResult Refuse(DashboardLayout layout, PlacementError error, Guid? blocker = null) =>
        new(layout, error, blocker);

    private static JsonElement EmptySettings() => JsonDocument.Parse("{}").RootElement.Clone();
}
