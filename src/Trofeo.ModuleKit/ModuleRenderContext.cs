using SkiaSharp;

namespace Trofeo.ModuleKit;

/// <summary>Contexte de dessin d'un module. Les tailles de police sont à multiplier par <see cref="TextScale"/>.</summary>
public sealed record ModuleRenderContext(
    SKRect Bounds,
    GridSize Size,
    Theme Theme,
    float TextScale,
    DateTimeOffset Now)
{
    /// <summary>
    /// Où <see cref="ModuleDrawing.DrawSelection"/> note les éléments choisis quand le service dessine leur pastille ;
    /// <c>null</c> sans service (un banc d'essai, par exemple). Interne : les modules ne le voient pas.
    /// </summary>
    internal ISelectionSink? Selections { get; init; }
}
