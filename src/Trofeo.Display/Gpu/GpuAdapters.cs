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
        // Le test du matériel passe avant l'appel à Direct3D 12 : inutile de sonder une carte qu'on écarte de toute façon.
        if (!IsHardware(d.Flags, d.VendorId, d.DeviceId)) return null;

        return IsUsable(d.Flags, d.VendorId, d.DeviceId, SupportsDirect3D12(adapter)) ? Build(d) : null;
    }

    /// <summary>
    /// Vrai si la carte est bien matérielle. DXGI marque « Software » les cartes logicielles, mais pas toujours : sur une
    /// machine virtuelle sans carte (celle de la recette GitHub, par exemple), le « Microsoft Basic Render Driver » (WARP,
    /// 0x1414 / 0x008C) arrive sans ce drapeau et sait Direct3D 12 au niveau 11_0 ; il dessine pourtant sur le processeur,
    /// bien trop lentement pour l'écran. On l'écarte donc aussi par son identité.
    /// </summary>
    internal static bool IsHardware(AdapterFlags flags, uint vendorId, uint deviceId) =>
        (flags & AdapterFlags.Software) == 0 && !(vendorId == MicrosoftVendorId && deviceId == BasicRenderDriverDeviceId);

    /// <summary>Une carte se garde si elle est matérielle et sait Direct3D 12 au niveau 11_0.</summary>
    internal static bool IsUsable(AdapterFlags flags, uint vendorId, uint deviceId, bool direct3D12) =>
        direct3D12 && IsHardware(flags, vendorId, deviceId);

    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDriverDeviceId = 0x008C;

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
