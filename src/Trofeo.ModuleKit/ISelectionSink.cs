using SkiaSharp;

namespace Trofeo.ModuleKit;

/// <summary>
/// Reçoit les rectangles des éléments choisis d'un module, quand c'est le service qui dessine leur pastille (thème
/// « Liquid Glass avancé ») : <see cref="ModuleDrawing.DrawSelection"/> les y note au lieu de dessiner.
/// </summary>
internal interface ISelectionSink
{
    /// <summary>Note un élément choisi. <paramref name="caseLocalRect"/> est relatif au coin haut gauche de la case du module.</summary>
    void Add(SKRect caseLocalRect);
}
