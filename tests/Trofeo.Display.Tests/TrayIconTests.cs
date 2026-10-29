using Trofeo.Device;

namespace Trofeo.Display.Tests;

public sealed class TrayIconTests
{
    [Theory]
    [InlineData(ScreenState.Ready, null, "Ashboard Vision — écran : prêt")]
    [InlineData(ScreenState.Ready, "ignoré", "Ashboard Vision — écran : prêt")]
    [InlineData(ScreenState.Recovering, null, "Ashboard Vision — écran : reprise…")]
    [InlineData(ScreenState.Disconnected, null, "Ashboard Vision — écran : non branché")]
    [InlineData(ScreenState.Disconnected, "Écran non branché", "Ashboard Vision — écran : Écran non branché")]
    [InlineData(ScreenState.Disconnected, ScreenDevice.BusyReason, "Ashboard Vision — écran : " + ScreenDevice.BusyReason)]
    public void Tooltip_MapsScreenStateAndReason(ScreenState state, string? reason, string expected)
    {
        Assert.Equal(expected, TrayIcon.Tooltip(state, reason));
    }

    [Fact]
    public void Tooltip_LongReason_IsTruncatedTo127Chars()
    {
        var tooltip = TrayIcon.Tooltip(ScreenState.Disconnected, new string('x', 500));

        Assert.Equal(127, tooltip.Length);
        Assert.StartsWith("Ashboard Vision — écran : xxx", tooltip);
    }
}
