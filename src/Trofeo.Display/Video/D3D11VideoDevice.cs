using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Trofeo.Display.Gpu;

namespace Trofeo.Display.Video;

/// <summary>
/// L'appareil Direct3D 11 d'une carte pour décoder une vidéo : Media Foundation y dépose ses images (par un gestionnaire d'appareil
/// DXGI) au lieu de les rapatrier en mémoire. Protégé contre les accès de plusieurs fils, comme Media Foundation l'exige : le
/// décodeur travaille sur ses propres fils pendant que l'affichage copie les images.
/// </summary>
internal sealed class D3D11VideoDevice : IDisposable
{
    private readonly IMFDXGIDeviceManager _manager;
    private bool _disposed;

    private D3D11VideoDevice(long luid, ID3D11Device device, ID3D11DeviceContext context, ID3D11Multithread multithread, IMFDXGIDeviceManager manager)
    {
        Luid = luid;
        Device = device;
        Context = context;
        Multithread = multithread;
        _manager = manager;
    }

    /// <summary>L'identifiant de la carte (le même que celui de l'appareil Direct3D 12 de Skia sur cette carte).</summary>
    public long Luid { get; }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public ID3D11Multithread Multithread { get; }

    /// <summary>Le gestionnaire que le lecteur de source reçoit (<c>SourceReaderAttributeKeys.D3DManager</c>).</summary>
    public IMFDXGIDeviceManager Manager => _manager;

    /// <summary>
    /// Ouvre l'appareil sur la carte de LUID <paramref name="luid"/>. Lève si la carte n'existe plus ou si Direct3D 11 ne s'y
    /// installe pas (l'appelant décode alors sur le processeur).
    /// </summary>
    public static D3D11VideoDevice Create(long luid)
    {
        using var adapter = GpuAdapters.OpenByLuid(luid) ?? throw new InvalidOperationException("la carte graphique n'existe plus");

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        ID3D11Multithread? multithread = null;
        IMFDXGIDeviceManager? manager = null;
        try
        {
            multithread = device!.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);

            manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device!).CheckError();

            var result = new D3D11VideoDevice(luid, device, context!, multithread, manager);
            device = null;
            context = null;
            multithread = null;
            manager = null;
            return result;
        }
        finally
        {
            manager?.Dispose();
            multithread?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.Dispose();
        Multithread.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
