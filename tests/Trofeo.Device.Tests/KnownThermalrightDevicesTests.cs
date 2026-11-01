namespace Trofeo.Device.Tests;

public sealed class KnownThermalrightDevicesTests
{
    [Theory]
    [InlineData("USB\\VID_0416&PID_5302&REV_0100", 0x0416, 0x5302)]
    [InlineData("USB\\VID_87ad&PID_70db", 0x87AD, 0x70DB)] // minuscules acceptées
    [InlineData("USB\\VID_0416&PID_5302&MI_00", 0x0416, 0x5302)] // interface d'un périphérique composite
    public void TryParseHardwareId_ReadsVidAndPid(string id, int vid, int pid)
    {
        Assert.True(KnownThermalrightDevices.TryParseHardwareId(id, out var parsedVid, out var parsedPid));

        Assert.Equal((ushort)vid, parsedVid);
        Assert.Equal((ushort)pid, parsedPid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACPI\\PNP0A08")]
    [InlineData("USB\\VID_04&PID_5302")]
    [InlineData("USB\\VID_ZZZZ&PID_5302")]
    public void TryParseHardwareId_NotAnUsbId_ReturnsFalse(string id)
    {
        Assert.False(KnownThermalrightDevices.TryParseHardwareId(id, out _, out _));
    }

    [Fact]
    public void Describe_UnknownDevice_ReturnsNull()
    {
        Assert.Null(KnownThermalrightDevices.Describe(0x1234, 0x5678));
        Assert.Null(KnownThermalrightDevices.Examples(0x1234, 0x5678));
    }

    [Theory]
    [InlineData(0x0416, 0x5302, "HID type 2")]
    [InlineData(0x0416, 0x5406, "HID type 3")]
    [InlineData(0x0418, 0x5303, "HID type 3")]
    [InlineData(0x0418, 0x5304, "HID type 3")]
    [InlineData(0x0416, 0x5409, "LY1")]
    [InlineData(0x87AD, 0x70DB, "Bulk")]
    [InlineData(0x87CD, 0x70DB, "SCSI")]
    [InlineData(0x0402, 0x3922, "SCSI")]
    [InlineData(0x0416, 0x8001, "HID, série Digital")]
    public void Describe_KnownDevice_NamesTheFamily(int vid, int pid, string family)
    {
        Assert.Equal(family, KnownThermalrightDevices.Describe((ushort)vid, (ushort)pid));
        Assert.False(string.IsNullOrWhiteSpace(KnownThermalrightDevices.Examples((ushort)vid, (ushort)pid)));
    }

    [Fact]
    public void Describe_TheSupportedScreenItself_IsNotInTheTable()
    {
        // 0416:5408 est l'écran pris en charge : le motif « détecté mais non pris en charge » ne le vise jamais.
        Assert.Null(KnownThermalrightDevices.Describe(0x0416, 0x5408));
    }
}
