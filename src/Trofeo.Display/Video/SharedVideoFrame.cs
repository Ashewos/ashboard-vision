using System.Runtime.InteropServices;
using SkiaSharp;
using Trofeo.Display.Gpu;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Trofeo.Display.Video;

/// <summary>
/// L'image décodée, copiée sur la carte dans une texture que Direct3D 11 (le décodeur) et Direct3D 12 (Skia) voient toutes les deux :
/// une texture partagée par un handle NT, ouverte une fois par appareil Direct3D 12. Deux barrières partagées règlent le passage :
/// <list type="bullet">
/// <item>la barrière de copie, signalée par Direct3D 11 après chaque copie, que la file Direct3D 12 attend avant que Skia lise la texture ;</item>
/// <item>la barrière de lecture, signalée par la file Direct3D 12 une fois la lecture envoyée, que Direct3D 11 attend avant de copier l'image suivante.</item>
/// </list>
/// Aucune copie par le processeur : l'image ne quitte pas la carte. Tout se sert depuis le fil de l'affichage (le décodeur ne touche
/// que ses propres textures).
/// </summary>
internal sealed partial class SharedVideoFrame : IDisposable
{
    private readonly D3D11VideoDevice _video;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11Device5 _device5;
    private readonly ID3D11DeviceContext4 _context4;
    private readonly ID3D11Fence _copyFence;    // créée par Direct3D 11, signalée après chaque copie
    private ulong _copyValue;
    private ID3D11Texture2D? _staging;

    // Ce que Direct3D 12 en a ouvert, pour un appareil à la fois.
    private D3D12GpuDevice? _opened;
    private ID3D12Resource? _resource;
    private ID3D12Fence? _copyFence12;
    private ID3D12Fence? _readFence12;           // créée par Direct3D 12, signalée une fois la lecture envoyée
    private ID3D11Fence? _readFence11;
    private GRBackendTexture? _backend;
    private ulong _readRequested, _readWaited;
    private ulong _lastSubmittedCopy;
    private bool _disposed;

    /// <summary>La copie de l'image décodée : même taille que la sortie du décodeur, B8G8R8A8.</summary>
    public SharedVideoFrame(D3D11VideoDevice video, int width, int height)
    {
        _video = video;
        Width = width;
        Height = height;
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
        };
        _texture = video.Device.CreateTexture2D(description);
        try
        {
            _device5 = video.Device.QueryInterface<ID3D11Device5>();
            _context4 = video.Context.QueryInterface<ID3D11DeviceContext4>();
            _copyFence = _device5.CreateFence(0, Vortice.Direct3D11.FenceFlags.Shared);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>La texture vue par Skia, valable une fois <see cref="OpenOn"/> réussi.</summary>
    public GRBackendTexture BackendTexture => _backend ?? throw new InvalidOperationException("La texture n'est pas ouverte sur Direct3D 12.");

    /// <summary>
    /// Copie l'image d'un échantillon du décodeur dans la texture partagée, sans passer par la mémoire de l'ordinateur. Lève si
    /// l'échantillon n'est pas une texture Direct3D 11 B8G8R8A8 de la bonne taille.
    /// </summary>
    public void CopyFrom(IMFSample sample)
    {
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        var pointer = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
        using var source = new ID3D11Texture2D(pointer);
        var description = source.Description;
        if (description.Format != Format.B8G8R8A8_UNorm)
            throw new NotSupportedException($"le décodeur donne des images {description.Format}, pas B8G8R8A8");
        if (description.Width != (uint)Width || description.Height != (uint)Height)
            throw new NotSupportedException($"l'image décodée fait {description.Width} × {description.Height}, pas {Width} × {Height}");

        // La lecture de l'image précédente par Direct3D 12 doit être finie avant qu'on l'écrase.
        if (_readRequested > _readWaited && _readFence11 is not null)
        {
            _context4.Wait(_readFence11, _readRequested);
            _readWaited = _readRequested;
        }

        _video.Context.CopySubresourceRegion(_texture, 0, 0, 0, 0, source, dxgi.SubresourceIndex, null);
        _context4.Signal(_copyFence, ++_copyValue);

        // Sans Flush, la copie et le signal pourraient rester dans la file du contexte tant que le pilote n'a rien d'autre à faire :
        // la file Direct3D 12 attendrait la barrière, et la lecture des pixels (sans délai) ne finirait jamais, écran figé.
        _video.Context.Flush();
    }

    /// <summary>
    /// Rapatrie l'image sur le processeur (un seul passage par une texture de lecture) et la montre à <paramref name="use"/> :
    /// pointeur sur la première ligne, foulée en octets. L'image est celle de la dernière copie.
    /// </summary>
    public void ReadBack(Action<IntPtr, int> use)
    {
        _staging ??= _video.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _video.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _texture, 0, null);
        var mapped = _video.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            use(mapped.DataPointer, (int)mapped.RowPitch);
        }
        finally
        {
            _video.Context.Unmap(_staging, 0);
        }
    }

    /// <summary>Vrai si la texture est déjà ouverte sur cet appareil Direct3D 12.</summary>
    public bool IsOpenOn(D3D12GpuDevice device) => ReferenceEquals(_opened, device) && _backend is not null;

    /// <summary>
    /// Ouvre la texture et les barrières sur l'appareil Direct3D 12 de Skia (l'ouverture précédente, pour un autre appareil, est
    /// fermée). Lève si Direct3D 12 refuse la texture partagée.
    /// </summary>
    public void OpenOn(D3D12GpuDevice device)
    {
        if (IsOpenOn(device)) return;
        CloseD3D12();

        try
        {
            using (var dxgiResource = _texture.QueryInterface<IDXGIResource1>())
            {
                var handle = dxgiResource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null!);
                try
                {
                    _resource = device.Device.OpenSharedHandle<ID3D12Resource>(handle);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            var copyHandle = _copyFence.CreateSharedHandle(null, null!);
            try
            {
                _copyFence12 = device.Device.OpenSharedHandle<ID3D12Fence>(copyHandle);
            }
            finally
            {
                CloseHandle(copyHandle);
            }

            _readFence12 = device.Device.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared);
            var readHandle = device.Device.CreateSharedHandle(_readFence12, null, null!);
            try
            {
                _readFence11 = _device5.OpenSharedFence<ID3D11Fence>(readHandle);
            }
            finally
            {
                CloseHandle(readHandle);
            }

            _readRequested = _readWaited = 0;

            using var info = new GRD3DTextureResourceInfo
            {
                Resource = _resource.NativePointer,
                ResourceState = (uint)ResourceStates.Common,
                Format = (uint)Format.B8G8R8A8_UNorm,
                SampleCount = 1,
                LevelCount = 1,
                SampleQualityPattern = 0,
                Protected = false,
            };
            _backend = new GRBackendTexture(Width, Height, info);
            _opened = device;
        }
        catch
        {
            CloseD3D12();
            throw;
        }
    }

    /// <summary>Fait attendre la file de l'appareil la dernière copie : à appeler avant que Skia lise la texture.</summary>
    public void WaitForCopy(D3D12GpuDevice device)
    {
        if (!IsOpenOn(device)) throw new InvalidOperationException("La texture n'est pas ouverte sur cet appareil.");
        if (_copyValue == _lastSubmittedCopy) return;

        device.Queue.Wait(_copyFence12!, _copyValue).CheckError();
        _lastSubmittedCopy = _copyValue;
    }

    /// <summary>Note, dans la file de l'appareil, la fin de ce que Skia vient d'envoyer : Direct3D 11 pourra alors réécrire la texture.</summary>
    public void SignalRead(D3D12GpuDevice device)
    {
        if (!IsOpenOn(device)) return;

        device.Queue.Signal(_readFence12!, ++_readRequested).CheckError();
    }

    private void CloseD3D12()
    {
        _backend?.Dispose();
        _backend = null;
        _readFence11?.Dispose();
        _readFence11 = null;
        _readFence12?.Dispose();
        _readFence12 = null;
        _copyFence12?.Dispose();
        _copyFence12 = null;
        _resource?.Dispose();
        _resource = null;
        _opened = null;
        _readRequested = _readWaited = 0;
        _lastSubmittedCopy = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseD3D12();
        _staging?.Dispose();
        _copyFence?.Dispose();
        _context4?.Dispose();
        _device5?.Dispose();
        _texture?.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
