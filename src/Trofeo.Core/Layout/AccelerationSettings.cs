namespace Trofeo.Core.Layout;

/// <summary>
/// Une carte graphique, reconnue d'un démarrage à l'autre : l'identifiant du fabricant, celui du modèle et celui du
/// sous-système (ce qui la distingue d'une carte du même modèle chez un autre constructeur), plus son nom pour l'affichage.
/// </summary>
public sealed record GpuAdapterId(int VendorId, int DeviceId, int SubSysId, string Name);

/// <summary>
/// Accélération matérielle (Direct3D 12) : propre à ce PC, donc jamais exportée ni importée avec une disposition.
/// </summary>
/// <param name="Enabled">Interrupteur général ; coupé, tout se fait sur le processeur.</param>
/// <param name="Adapter">Carte choisie ; <c>null</c> : Automatique (la première carte matérielle, la moins gourmande).</param>
/// <param name="Drawing">Le dessin de l'image passe par la carte (« Dessin de l'image »).</param>
/// <param name="VideoDecoding">Le décodage des vidéos passe par la carte (« Décodage des vidéos »).</param>
public sealed record AccelerationSettings(
    bool Enabled = true, GpuAdapterId? Adapter = null, bool Drawing = true, bool VideoDecoding = true)
{
    /// <summary>Réglages d'une disposition qui n'en a pas : tout activé, carte Automatique.</summary>
    public static AccelerationSettings Default { get; } = new();
}
