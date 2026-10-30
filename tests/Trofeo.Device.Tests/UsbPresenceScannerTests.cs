namespace Trofeo.Device.Tests;

public sealed class UsbPresenceScannerTests
{
    [Fact]
    public void Scan_NeverThrows_AndReturnsAList()
    {
        // Sous Windows, liste les périphériques USB présents ; ailleurs (ou en cas d'erreur), une liste vide.
        var found = UsbPresenceScanner.Scan();

        Assert.NotNull(found);
    }
}
