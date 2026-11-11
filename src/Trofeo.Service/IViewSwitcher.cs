namespace Trofeo.Display;

/// <summary>
/// Ce qui change la vue affichée sur l'écran : la boucle d'affichage, pour les raccourcis clavier. Un faux la remplace
/// dans les tests.
/// </summary>
public interface IViewSwitcher
{
    /// <summary>Affiche la vue d'à côté, en boucle (+1 : la suivante, −1 : la précédente). Faux s'il n'y a pas d'autre vue.</summary>
    bool ShowAdjacentView(int step);

    /// <summary>Affiche la vue de rang <paramref name="index"/> (à partir de 0). Faux s'il n'y a pas de vue de ce rang.</summary>
    bool ShowViewAt(int index);
}
