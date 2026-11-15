using SkiaSharp;

namespace Trofeo.ModuleKit;

/// <summary>
/// Reçoit les rectangles des éléments choisis d'un module, quand c'est le service qui dessine leur pastille (thème
/// « Liquid Glass avancé ») : <see cref="ModuleDrawing.DrawSelection"/> les y note au lieu de dessiner.
/// </summary>
internal interface ISelectionSink
{
    /// <summary>
    /// Note un élément choisi. <paramref name="caseLocalRect"/> est relatif au coin haut gauche de la case du module ;
    /// <paramref name="opacity"/> (de 0 à 1) est celle de la pastille : 1 pour un module qui dessine, moins quand l'image du module
    /// est voilée (module de la communauté hors ligne) et que sa pastille doit l'être comme elle.
    /// </summary>
    void Add(SKRect caseLocalRect, float opacity = 1f);
}
