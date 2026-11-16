using SkiaSharp;
using Trofeo.Core.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Trofeo.Display.Gpu;

/// <summary>
/// L'appareil Direct3D 12 d'une carte, avec le contexte de Skia posé dessus (<c>GRContext.CreateDirect3D</c>). Un appareil
/// se sert depuis un seul fil, celui de la boucle d'affichage. Direct3D 12 au niveau de fonctionnalités 11_0.
/// </summary>
public sealed class D3D12GpuDevice : IGpuDevice
{
    /// <summary>Le cache de textures de Skia garde au plus 48 Mo (la valeur par défaut, 96 Mo, est trop large pour une image de 1920 × 462).</summary>
    private const long CacheLimitBytes = 48L * 1024 * 1024;

    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly GRD3DBackendContext _backend;
    private readonly GRContext _context;
    private string? _lostReason;
    private bool _disposed;

    /// <summary>Ouvre l'appareil sur <paramref name="adapter"/> ; il en devient propriétaire, même quand la création échoue.</summary>
    private D3D12GpuDevice(GpuAdapterInfo info, IDXGIAdapter1 adapter)
    {
        Adapter = info;
        AdapterName = adapter.Description1.Description;
        _adapter = adapter;
        try
        {
            D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
            _device = device!;
            try
            {
                _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
                try
                {
                    _backend = new GRD3DBackendContext
                    {
                        Adapter = adapter.NativePointer,
                        Device = _device.NativePointer,
                        Queue = _queue.NativePointer,
                    };
                    _context = GRContext.CreateDirect3D(_backend)
                        ?? throw new InvalidOperationException("Skia n'a pas pu s'installer sur Direct3D 12.");
                    _context.SetResourceCacheLimit(CacheLimitBytes);
                }
                catch
                {
                    _backend?.Dispose();
                    _queue.Dispose();
                    throw;
                }
            }
            catch
            {
                _device.Dispose();
                throw;
            }
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    /// <summary>La carte sur laquelle l'appareil est ouvert.</summary>
    public GpuAdapterInfo Adapter { get; }

    public ID3D12Device Device => _device;

    public ID3D12CommandQueue Queue => _queue;

    public GRContext Context => _context;

    public string AdapterName { get; }

    /// <summary>
    /// Ouvre un appareil sur <paramref name="adapter"/>. Lève <see cref="GpuLostException"/> si la carte n'existe plus ou
    /// si Direct3D 12 ou Skia ne peuvent pas s'y installer.
    /// </summary>
    public static D3D12GpuDevice Create(GpuAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.MinimumPower, out IDXGIAdapter1? candidate).Success; i++)
            {
                var info = GpuAdapters.Describe(candidate!);
                if (info is not null && info.Luid == adapter.Luid)
                    return new D3D12GpuDevice(info, candidate!);
                candidate!.Dispose();
            }
        }
        catch (Exception e)
        {
            throw new GpuLostException($"Impossible d'ouvrir la carte graphique « {adapter.Id.Name} » : {e.Message}");
        }
        throw new GpuLostException($"La carte graphique « {adapter.Id.Name} » n'est plus là.");
    }

    /// <summary>
    /// Ouvre un appareil sur la carte logicielle WARP de Windows (pour les tests : la recette GitHub n'a pas de carte graphique).
    /// <c>null</c> si Direct3D 12 n'existe pas sur cette machine.
    /// </summary>
    public static D3D12GpuDevice? CreateWarp()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            var adapter = factory.EnumWarpAdapter<IDXGIAdapter1>();
            return new D3D12GpuDevice(GpuAdapters.DescribeAny(adapter), adapter);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsLost
    {
        get
        {
            if (_lostReason is not null) return true;
            if (_disposed) return false;
            if (_context.IsAbandoned)
            {
                _lostReason = "Skia a abandonné le contexte de la carte graphique.";
                return true;
            }
            var removed = _device.DeviceRemovedReason;
            if (removed.Failure)
            {
                _lostReason = $"La carte graphique a été retirée ou réinitialisée (code 0x{removed.Code:X8}).";
                return true;
            }
            return false;
        }
    }

    public string? LostReason => IsLost ? _lostReason : null;

    public int CacheResources
    {
        get
        {
            _context.GetResourceCacheUsage(out var count, out _);
            return count;
        }
    }

    public long CacheBytes
    {
        get
        {
            _context.GetResourceCacheUsage(out _, out var bytes);
            return bytes;
        }
    }

    public SKSurface CreateSurface(SKImageInfo info)
    {
        ThrowIfLost();
        var surface = SKSurface.Create(_context, false, info);
        if (surface is null)
        {
            // Skia refuse la surface : soit la carte vient de tomber, soit la mémoire de la carte est pleine.
            ThrowIfLost();
            throw new GpuLostException($"La carte graphique n'a pas pu réserver une image de {info.Width} × {info.Height}.");
        }
        return surface;
    }

    public void Submit()
    {
        ThrowIfLost();
        _context.Flush(true, false);
    }

    public void PurgeUnused()
    {
        if (!_disposed && !IsLost) _context.PurgeUnlockedResources(false);
    }

    private void ThrowIfLost()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsLost) throw new GpuLostException(_lostReason!);
    }

    /// <summary>Libère, dans l'ordre : le contexte de Skia, le contexte de Direct3D, la file, l'appareil et la carte. Un second appel ne fait rien.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        var lost = IsLost;
        _disposed = true;
        try
        {
            if (lost)
                _context.AbandonContext(); // la carte ne répond plus : pas de travail à finir
            else
                _context.Flush(true, true); // le travail déjà envoyé se termine avant de libérer ses textures
        }
        catch
        {
            // Une carte qui tombe pendant la libération ne doit pas empêcher de libérer le reste.
        }
        _context.Dispose();
        _backend.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _adapter.Dispose();
    }
}
