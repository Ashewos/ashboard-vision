namespace Trofeo.Display.Video;

/// <summary>
/// Comment ouvrir une vidéo : sur quelle carte graphique la décoder (<c>null</c> : le processeur la décode) et à quel instant
/// reprendre (une vidéo rouverte après un changement de carte repart d'où elle en était).
/// </summary>
public sealed record VideoDecodeOptions(GpuAdapterInfo? Adapter, TimeSpan StartAt)
{
    /// <summary>Le processeur décode, depuis le début.</summary>
    public static VideoDecodeOptions Cpu { get; } = new(null, TimeSpan.Zero);

    /// <summary>Pour les tests : appelé quand on active le décodage par la carte ; s'il lève, l'activation échoue comme si la carte la refusait.</summary>
    internal Action? GpuFault { get; init; }
}
