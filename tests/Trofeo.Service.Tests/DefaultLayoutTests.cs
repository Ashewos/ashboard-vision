using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Modules.Agents;
using Trofeo.Modules.Clock;
using Trofeo.Modules.Monitoring;
using Trofeo.Modules.Network;
using Trofeo.Modules.NowPlaying;

namespace Trofeo.Display.Tests;

public sealed class DefaultLayoutTests
{
    /// <summary>
    /// Les descripteurs viennent des vraies fabriques. Elles sont construites sans leurs dépendances : seul le
    /// descripteur est lu, aucun module n'est créé, donc ni matériel, ni réseau, ni contrôles média.
    /// </summary>
    private static IReadOnlyDictionary<string, ModuleDescriptor> RealDescriptors() =>
        new ModuleRegistry(
            [
                new ClockFactory(null!),
                new MonitoringFactory(null!),
                new NowPlayingFactory(null!),
                new NetworkFactory(null!),
                new ClaudeCodeFactory(null!),
                new CodexFactory(null!),
            ],
            _ => null!).Descriptors;

    [Fact]
    public void Default_PassesValidationAgainstRealFactories()
    {
        var types = RealDescriptors();

        foreach (var placement in DashboardLayout.Default.Views[0].Placements)
        {
            Assert.Equal(PlacementError.None, LayoutValidator.Check(DashboardLayout.Default.Views[0].Placements, placement, types));
        }
    }

    [Fact]
    public void Default_HasFiveModules_NoOverlap()
    {
        var layout = DashboardLayout.Default;

        Assert.Equal(["claudecode", "monitoring", "nowplaying", "clock", "network"], layout.Views[0].Placements.Select(p => p.TypeId));
        Assert.Equal(layout.Views[0].Placements.Count, layout.Views[0].Placements.Select(p => p.InstanceId).Distinct().Count());
        var types = RealDescriptors();
        Assert.All(layout.Views[0].Placements, p => Assert.Equal(PlacementError.None, LayoutValidator.Check(layout.Views[0].Placements, p, types)));
    }
}
