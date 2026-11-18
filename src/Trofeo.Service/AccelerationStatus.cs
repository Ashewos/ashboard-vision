namespace Trofeo.Display;

/// <summary>Le genre de problème de la carte graphique, d'après la phrase publiée : seule une panne mérite une notification.</summary>
public enum AccelerationProblemKind
{
    /// <summary>La carte n'a pas pu démarrer, ou a eu un problème en route.</summary>
    Failure,

    /// <summary>La carte est trop lente pour tenir le rythme.</summary>
    Slow,

    /// <summary>La carte choisie n'est plus là.</summary>
    Gone,
}

/// <summary>
/// L'état de l'accélération matérielle, tel que la fenêtre de réglage le montre. Un instantané : il ne change plus une fois publié.
/// </summary>
/// <param name="Available">Il existe au moins une carte graphique utilisable ; faux : la fenêtre grise l'interrupteur.</param>
/// <param name="Enabled">L'interrupteur général est allumé.</param>
/// <param name="Adapter">La carte réellement utilisée ; <c>null</c> : tout se fait sur le processeur.</param>
/// <param name="Drawing">Le dessin de l'image passe vraiment par la carte.</param>
/// <param name="VideoDecoding">Le décodage des vidéos passe vraiment par la carte.</param>
/// <param name="Problem">La phrase à montrer telle quelle (panne, carte absente, carte trop lente) ; <c>null</c> sans problème.</param>
/// <param name="Adapters">Les cartes que l'on peut choisir.</param>
/// <param name="ProblemKind">Le genre de <paramref name="Problem"/> ; <c>null</c> sans problème.</param>
/// <param name="ProblemAt">L'heure (UTC) où la phrase en cours a été posée ; un nouvel échec, même au même texte, donne une nouvelle heure ; <c>null</c> sans problème.</param>
/// <param name="DrawingWaitsForAnimation">
/// Le réglage voudrait la carte pour dessiner (interrupteur allumé, « Dessin de l'image » cochée, une carte voulue), mais le fond de
/// l'écran est fixe : le processeur dessine, la carte prend le relais sur un GIF ou une vidéo. Ce n'est pas un problème.
/// </param>
public sealed record AccelerationStatus(
    bool Available, bool Enabled, GpuAdapterInfo? Adapter, bool Drawing, bool VideoDecoding, string? Problem,
    IReadOnlyList<GpuAdapterInfo> Adapters, AccelerationProblemKind? ProblemKind = null, DateTimeOffset? ProblemAt = null,
    bool DrawingWaitsForAnimation = false);
