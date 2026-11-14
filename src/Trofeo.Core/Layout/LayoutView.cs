namespace Trofeo.Core.Layout;

/// <summary>
/// Une vue de l'écran : une disposition de cases complète, avec un nom. Le tableau de bord en compte de 1 à
/// <see cref="MaxViews"/> ; l'écran n'en affiche qu'une à la fois.
/// </summary>
/// <param name="Id">Identifiant stable de la vue (il survit aux renommages et aux déplacements).</param>
/// <param name="Name">Nom de la vue, <see cref="MaxNameLength"/> caractères au plus.</param>
/// <param name="Placements">Les cases de cette vue, sur le damier.</param>
public sealed record LayoutView(Guid Id, string Name, IReadOnlyList<ModulePlacement> Placements)
{
    /// <summary>Nombre de vues au plus.</summary>
    public const int MaxViews = 9;

    /// <summary>Longueur au plus du nom d'une vue ou d'une case.</summary>
    public const int MaxNameLength = 30;

    /// <summary>Nom de la première vue, quand une ancienne disposition (sans vues) est migrée.</summary>
    public const string FirstViewName = "Vue 1";
}
