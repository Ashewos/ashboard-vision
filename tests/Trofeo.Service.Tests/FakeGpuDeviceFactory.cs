using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

/// <summary>Une fabrique de cartes de test : les cartes à lister, les appareils créés (suivis) et des créations qui échouent à la demande.</summary>
internal sealed class FakeGpuDeviceFactory : IGpuDeviceFactory
{
    public List<GpuAdapterInfo> Adapters { get; } = [];

    /// <summary>Les appareils créés, dans l'ordre, avec la carte pour laquelle chacun l'a été.</summary>
    public List<(GpuAdapterInfo Adapter, FakeGpuDevice Device)> Created { get; } = [];

    /// <summary>Vrai : chaque création est refusée (<see cref="GpuLostException"/>).</summary>
    public bool FailCreate { get; set; }

    public string FailReason { get; set; } = "pilote absent";

    /// <summary>Combien d'appareils créés n'ont pas encore été libérés.</summary>
    public int Alive => Created.Count(c => !c.Device.IsDisposed);

    public static GpuAdapterInfo Card(string name, int vendor = 0x10DE, int device = 1, int subSystem = 1) =>
        new(new GpuAdapterId(vendor, device, subSystem, name), Luid: device, DedicatedMemoryBytes: 1L << 30);

    public IReadOnlyList<GpuAdapterInfo> ListAdapters() => [.. Adapters];

    public IGpuDevice Create(GpuAdapterInfo adapter)
    {
        if (FailCreate) throw new GpuLostException(FailReason);

        var device = new FakeGpuDevice();
        Created.Add((adapter, device));
        return device;
    }
}
