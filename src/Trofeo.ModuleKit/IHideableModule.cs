namespace Trofeo.ModuleKit;

/// <summary>
/// Un module qui veut savoir quand sa case est cachée (elle est dans une vue qui n'est pas affichée) ou visible, pour ne pas
/// travailler pour rien. Le service appelle <see cref="SetVisible"/> à la création de la case, puis à chaque changement de vue.
/// Réservé aux modules d'origine et à l'hôte des modules complémentaires.
/// </summary>
public interface IHideableModule
{
    /// <summary>
    /// La case devient visible ou cachée. <paramref name="place"/> donne sa place (bornes, taille sur le damier, taille du texte,
    /// thème), même quand elle est cachée. Appelé depuis le fil de la boucle d'affichage ou celui de l'interface : il ne doit
    /// jamais bloquer.
    /// </summary>
    void SetVisible(bool visible, ModuleRenderContext place);
}
