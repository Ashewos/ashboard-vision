namespace Trofeo.Display;

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
public sealed record AccelerationStatus(
    bool Available, bool Enabled, GpuAdapterInfo? Adapter, bool Drawing, bool VideoDecoding, string? Problem,
    IReadOnlyList<GpuAdapterInfo> Adapters);
