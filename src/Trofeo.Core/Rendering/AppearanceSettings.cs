namespace Trofeo.Core.Rendering;

/// <summary>
/// Réglages d'apparence choisis par l'utilisateur, écrits dans <c>layout.json</c> (clé <c>Appearance</c>).
/// </summary>
/// <param name="Theme">Identifiant du thème ; défaut « classique ».</param>
/// <param name="PanelOpacity">Opacité du fond des panneaux (0 à 1) ; <c>null</c> : celle du thème.</param>
/// <param name="Font">Nom de famille de la police ; <c>null</c> : celle du thème.</param>
/// <param name="Background">Fond de l'écran ; <c>null</c> : celui du thème.</param>
/// <param name="Legibility">
/// Garde de lisibilité (le panneau s'assombrit sur un fond clair pour que le texte reste lisible) : <c>null</c> ou
/// <c>true</c>, activée (défaut) ; <c>false</c>, coupée : le panneau garde l'opacité choisie, quel que soit le fond.
/// </param>
/// <param name="AnimateViewSwitch">
/// Le passage d'une vue à l'autre glisse (environ 300 ms, avec un flou de mouvement) : vrai par défaut ; faux, la nouvelle
/// vue s'affiche d'un coup.
/// </param>
public sealed record AppearanceSettings(
    string Theme = "classique", float? PanelOpacity = null, string? Font = null, BackgroundSettings? Background = null,
    bool? Legibility = null, bool AnimateViewSwitch = true)
{
    /// <summary>Aucun réglage : Classique tel quel.</summary>
    public static AppearanceSettings Default { get; } = new();
}
