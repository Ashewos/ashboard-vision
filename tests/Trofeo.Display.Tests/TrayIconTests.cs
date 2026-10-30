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

    [Fact]
    public void Tooltip_UpdateAvailable_MentionsVersion()
    {
        var tooltip = TrayIcon.Tooltip(ScreenState.Ready, null, "1.2.0");

        Assert.StartsWith("Ashboard Vision — écran : prêt", tooltip);
        Assert.EndsWith("Mise à jour 1.2.0 disponible", tooltip);
    }

    [Fact]
    public void Tooltip_UpdateAvailable_LongReason_KeepsTheUpdateWithin127Chars()
    {
        var tooltip = TrayIcon.Tooltip(ScreenState.Disconnected, new string('x', 500), "1.2.0-beta.3");

        Assert.Equal(127, tooltip.Length);
        Assert.StartsWith("Ashboard Vision — écran : xxx", tooltip);
        Assert.EndsWith("Mise à jour 1.2.0-beta.3 disponible", tooltip);
    }

    [Fact]
    public void Menu_UpdateAvailable_ShowsInstallItem()
    {
        var entries = TrayIcon.MenuEntries("1.2.0");

        Assert.Equal(
            [
                (TrayMenuAction.InstallUpdate, "Installer la mise à jour 1.2.0"),
                (TrayMenuAction.OpenSettings, "Ouvrir les réglages"),
                (TrayMenuAction.Quit, "Quitter"),
            ],
            entries);
    }

    [Fact]
    public void Menu_NoUpdate_HasNoInstallItem()
    {
        var entries = TrayIcon.MenuEntries(null);

        Assert.Equal([(TrayMenuAction.OpenSettings, "Ouvrir les réglages"), (TrayMenuAction.Quit, "Quitter")], entries);
    }
}
