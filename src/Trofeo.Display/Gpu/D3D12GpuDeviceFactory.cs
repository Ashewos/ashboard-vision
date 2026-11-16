using Trofeo.Core.Rendering;

namespace Trofeo.Display.Gpu;

/// <summary>La fabrique de production : les cartes vues par DXGI et des appareils Direct3D 12.</summary>
public sealed class D3D12GpuDeviceFactory : IGpuDeviceFactory
{
    public IReadOnlyList<GpuAdapterInfo> ListAdapters() => GpuAdapters.List();

    public IGpuDevice Create(GpuAdapterInfo adapter) => D3D12GpuDevice.Create(adapter);
}
