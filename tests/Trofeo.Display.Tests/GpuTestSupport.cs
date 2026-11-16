using Trofeo.Display.Gpu;

namespace Trofeo.Display.Tests;

/// <summary>
/// Les tests Direct3D 12 tournent sur WARP, la carte logicielle de Windows (la recette GitHub n'a pas de carte graphique).
/// Là où Direct3D 12 n'existe pas, ils sont sautés avec un message clair.
/// </summary>
internal static class GpuTestSupport
{
    internal const string SkipMessage = "Direct3D 12 absent";

    private static readonly Lazy<bool> WarpAvailable = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var device = D3D12GpuDevice.CreateWarp();
        return device is not null;
    });

    internal static bool IsWarpAvailable => WarpAvailable.Value;

    /// <summary>Un appareil WARP neuf (à libérer par l'appelant). À n'appeler que depuis un test marqué <see cref="WarpFactAttribute"/>.</summary>
    internal static D3D12GpuDevice Warp() =>
        D3D12GpuDevice.CreateWarp() ?? throw new InvalidOperationException(SkipMessage);
}

/// <summary>Un test qui a besoin de Direct3D 12 : sauté quand WARP ne peut pas être créé (xunit 2 n'a pas <c>Assert.Skip</c>).</summary>
internal sealed class WarpFactAttribute : FactAttribute
{
    public WarpFactAttribute()
    {
        if (!GpuTestSupport.IsWarpAvailable) Skip = GpuTestSupport.SkipMessage;
    }
}
