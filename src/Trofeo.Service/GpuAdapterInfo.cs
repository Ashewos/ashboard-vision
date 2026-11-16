using Trofeo.Core.Layout;

namespace Trofeo.Display;

/// <summary>
/// Une carte graphique matérielle vue par DXGI. <paramref name="Id"/> est ce que l'on enregistre dans les réglages ;
/// <paramref name="Luid"/> ne sert qu'à apparier les appareils Direct3D 11 et 12 pendant une même séance, jamais à l'enregistrement.
/// </summary>
public sealed record GpuAdapterInfo(GpuAdapterId Id, long Luid, long DedicatedMemoryBytes);
