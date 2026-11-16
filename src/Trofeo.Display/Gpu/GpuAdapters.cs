using Trofeo.Core.Layout;
using Vortice.DXGI;

namespace Trofeo.Display.Gpu;

/// <summary>Les cartes graphiques de ce PC, vues par DXGI.</summary>
public static class GpuAdapters
{
    /// <summary>
    /// Les cartes matérielles, dans l'ordre <c>MinimumPower</c> de DXGI (la carte intégrée d'abord). Les cartes logicielles
    /// (« Microsoft Basic Render Driver ») sont ignorées. Une liste vide veut dire qu'il n'y a pas de carte utilisable,
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

    /// <summary>La description d'une carte, ou <c>null</c> pour une carte logicielle.</summary>
    internal static GpuAdapterInfo? Describe(IDXGIAdapter1 adapter)
    {
        var d = adapter.Description1;
        return (d.Flags & AdapterFlags.Software) != 0 ? null : Build(d);
    }

    /// <summary>La description d'une carte, logicielle ou non (WARP, pour les tests).</summary>
    internal static GpuAdapterInfo DescribeAny(IDXGIAdapter1 adapter) => Build(adapter.Description1);

    private static GpuAdapterInfo Build(AdapterDescription1 d)
    {
        var id = new GpuAdapterId(unchecked((int)d.VendorId), unchecked((int)d.DeviceId), unchecked((int)d.SubsystemId), d.Description);
        long luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
        return new GpuAdapterInfo(id, luid, (long)d.DedicatedVideoMemory);
    }
}
