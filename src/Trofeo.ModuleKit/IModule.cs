using SkiaSharp;

namespace Trofeo.ModuleKit;

/// <summary>
/// Un module du tableau de bord. Règles de threads : <see cref="Invalidated"/> et <see cref="StatusChanged"/>
/// peuvent être levés depuis n'importe quel thread (le minuteur du module, par exemple) ; <see cref="Render"/>
/// est appelé sur le thread d'affichage, qui n'est pas celui du minuteur du module, donc l'état lu au rendu
/// doit pouvoir être lu en concurrence.
/// </summary>
public interface IModule : IDisposable
{
    /// <summary>Ce qui décrit le type de module (identifiant, nom, tailles). Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    ModuleDescriptor Descriptor { get; }

    /// <summary>L'état du module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    ModuleStatus Status { get; }

    /// <summary>Vrai tant que le module veut être redessiné en continu. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    bool IsAnimating { get; }

    /// <summary>
    /// Levé quand le module demande à être redessiné, depuis n'importe quel thread. Un dernier <c>Invalidated</c>
    /// tardif reste possible après <see cref="IDisposable.Dispose"/>.
    /// </summary>
    event EventHandler? Invalidated;

    /// <summary>
    /// Levé quand <see cref="Status"/> change, depuis n'importe quel thread. L'hôte journalise le nouvel état
    /// et redemande une image. Un dernier <c>StatusChanged</c> tardif reste possible après <c>Dispose</c>.
    /// </summary>
    event EventHandler? StatusChanged;

    /// <summary>Démarre le module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Dessine dans <c>ctx.Bounds</c>, le panneau étant déjà peint. Appelé sur le thread d'affichage, pas sur celui
    /// du minuteur du module : l'état lu ici doit être sûr en lecture concurrente.
    /// </summary>
    void Render(SKCanvas canvas, ModuleRenderContext ctx);
}
