using Trofeo.Core.Layout;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Trofeo.Display.Gpu;

/// <summary>Les cartes graphiques de ce PC, vues par DXGI.</summary>
public static class GpuAdapters
{
    /// <summary>
    /// Les cartes matérielles qui savent Direct3D 12 au niveau 11_0, dans l'ordre <c>MinimumPower</c> de DXGI (la carte intégrée
    /// d'abord). Les cartes logicielles (« Microsoft Basic Render Driver ») et celles qui n'ont pas Direct3D 12 (carte d'une
    /// machine virtuelle, très vieille puce intégrée, pilote ancien) sont ignorées. Une liste vide veut dire qu'il n'y a pas de carte utilisable,
    /// ou que DXGI n'est pas disponible.
    /// </summary>
    public static IReadOnlyList<GpuAdapterInfo> List()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.MinimumPower, out IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                {
                    var info = Describe(adapter!);
                    if (info is not null) result.Add(info);
                }
            }
        }
        catch (Exception)
        {
            // Pas de DXGI (système trop ancien ou très réduit) : il n'y a pas de carte à proposer.
            return [];
        }
        return result;
    }

    /// <summary>
    /// La carte matérielle de LUID <paramref name="luid"/> (à libérer par l'appelant), ou <c>null</c> si elle n'existe plus. Sert à
    /// ouvrir sur la même carte l'appareil Direct3D 11 du décodage vidéo que Direct3D 12 pour Skia.
    /// </summary>
    internal static IDXGIAdapter1? OpenByLuid(long luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.MinimumPower, out IDXGIAdapter1? adapter).Success; i++)
        {
            if (Describe(adapter!)?.Luid == luid) return adapter;
            adapter!.Dispose();
        }

        return null;
    }

    /// <summary>La description d'une carte, ou <c>null</c> pour une carte logicielle ou sans Direct3D 12 au niveau 11_0.</summary>
    internal static GpuAdapterInfo? Describe(IDXGIAdapter1 adapter)
    {
        var d = adapter.Description1;
        if ((d.Flags & AdapterFlags.Software) != 0) return null;

        return IsUsable(d.Flags, SupportsDirect3D12(adapter)) ? Build(d) : null;
    }

    /// <summary>Une carte se garde si elle est matérielle et sait Direct3D 12 au niveau 11_0.</summary>
    internal static bool IsUsable(AdapterFlags flags, bool direct3D12) => (flags & AdapterFlags.Software) == 0 && direct3D12;

    /// <summary>Vrai si Direct3D 12 sait ouvrir un appareil au niveau 11_0 sur cette carte (sans l'ouvrir vraiment).</summary>
    internal static bool SupportsDirect3D12(IDXGIAdapter1 adapter)
    {
        try
        {
            return D3D12.IsSupported(adapter, FeatureLevel.Level_11_0);
        }
        catch (Exception)
        {
            return false; // pas de d3d12.dll (système trop ancien) : pas de carte utilisable
        }
    }

    /// <summary>La description d'une carte, logicielle ou non (WARP, pour les tests).</summary>
    internal static GpuAdapterInfo DescribeAny(IDXGIAdapter1 adapter) => Build(adapter.Description1);

    private static GpuAdapterInfo Build(AdapterDescription1 d)
    {
        var id = new GpuAdapterId(unchecked((int)d.VendorId), unchecked((int)d.DeviceId), unchecked((int)d.SubsystemId), d.Description);
        long luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
        // Par ulong, pas par uint : une carte de plus de 4 Go ferait déborder la conversion (et vider toute la liste).
        return new GpuAdapterInfo(id, luid, (long)(ulong)d.DedicatedVideoMemory);
    }
}
