using Trofeo.Core.Rendering;

namespace Trofeo.Display;

/// <summary>
/// Ce que le service sait demander à Direct3D 12 : les cartes graphiques de ce PC et un appareil sur l'une d'elles. Sépare le
/// choix de la carte (<see cref="AccelerationController"/>, testé sans carte) de Direct3D 12 lui-même (<c>Trofeo.Display</c>).
/// </summary>
public interface IGpuDeviceFactory
{
    /// <summary>Les cartes matérielles, dans l'ordre de préférence de Windows (la moins gourmande d'abord). Vide : aucune carte utilisable.</summary>
    IReadOnlyList<GpuAdapterInfo> ListAdapters();

    /// <summary>Ouvre un appareil sur <paramref name="adapter"/>. Lève une exception si la carte ne démarre pas.</summary>
    IGpuDevice Create(GpuAdapterInfo adapter);
}
