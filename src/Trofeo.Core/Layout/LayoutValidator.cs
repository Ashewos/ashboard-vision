using Trofeo.ModuleKit;

namespace Trofeo.Core.Layout;

/// <summary>
/// Résultat de la validation d'un emplacement. <see cref="NoRoom"/> : aucune place libre pour un ajout
/// (<see cref="LayoutEditor.Add"/>).
/// </summary>
public enum PlacementError { None, OutOfBounds, Overlap, BelowMinSize, UnknownType, NoRoom }

/// <summary>Règles de placement : dans le damier, pas de chevauchement, taille minimum respectée.</summary>
public static class LayoutValidator
{
    /// <summary>
    /// Vérifie un candidat contre les cases d'une vue ; l'instance de même <c>InstanceId</c> dans cette vue est ignorée.
    /// Les cases des autres vues ne comptent pas : chaque vue a son propre damier.
    /// </summary>
    public static PlacementError Check(
        IReadOnlyList<ModulePlacement> viewPlacements,
        ModulePlacement candidate,
        IReadOnlyDictionary<string, ModuleDescriptor> types)
    {
        if (!types.TryGetValue(candidate.TypeId, out var descriptor))
            return PlacementError.UnknownType;

        if (!IsInBounds(candidate))
            return PlacementError.OutOfBounds;

        if (candidate.Columns < descriptor.MinSize.Columns || candidate.Rows < descriptor.MinSize.Rows)
            return PlacementError.BelowMinSize;

        foreach (var other in viewPlacements)
        {
            if (other.InstanceId == candidate.InstanceId) continue;
            if (Intersects(candidate, other)) return PlacementError.Overlap;
        }

        return PlacementError.None;
    }

    /// <summary>Vrai si l'emplacement tient entièrement dans le damier.</summary>
    internal static bool IsInBounds(ModulePlacement p) =>
        p.Column >= 0 && p.Row >= 0 && p.Columns >= 1 && p.Rows >= 1 &&
        (long)p.Column + p.Columns <= GridGeometry.Columns && // long : pas de débordement d'entier
        (long)p.Row + p.Rows <= GridGeometry.Rows;

    internal static bool Intersects(ModulePlacement a, ModulePlacement b) =>
        a.Column < (long)b.Column + b.Columns && b.Column < (long)a.Column + a.Columns &&
        a.Row < (long)b.Row + b.Rows && b.Row < (long)a.Row + a.Rows;
}
