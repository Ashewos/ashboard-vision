using System.ComponentModel;
using System.Diagnostics;
using Trofeo.Distribution;

namespace Trofeo.Display.Tests;

public sealed class WindowsInstallerLauncherTests
{
    [Fact]
    public void Arguments_AreSilent()
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS", WindowsInstallerLauncher.Arguments);
    }

    [Fact]
    public void Launch_AsksWindowsForAdministratorRights()
    {
        ProcessStartInfo? seen = null;
        var launcher = new WindowsInstallerLauncher(info => seen = info);

        var result = launcher.Launch(@"C:\Temp\Ashboard-Vision-Setup-1.0.0.exe");

        Assert.Equal(InstallerLaunch.Started, result);
        Assert.NotNull(seen);
        Assert.Equal(@"C:\Temp\Ashboard-Vision-Setup-1.0.0.exe", seen.FileName);
        Assert.Equal(WindowsInstallerLauncher.Arguments, seen.Arguments);
        Assert.True(seen.UseShellExecute);
        Assert.Equal("runas", seen.Verb);
    }

    [Fact]
    public void Launch_Win32Code1223_IsCancelled()
    {
        var launcher = new WindowsInstallerLauncher(_ => throw new Win32Exception(1223, "L'opération a été annulée par l'utilisateur."));

        Assert.Equal(InstallerLaunch.Cancelled, launcher.Launch("setup.exe"));
    }

    [Theory]
    [InlineData(5)] // accès refusé
    [InlineData(2)] // fichier introuvable
    public void Launch_OtherWin32Error_IsFailed(int code)
    {
        var launcher = new WindowsInstallerLauncher(_ => throw new Win32Exception(code));

        Assert.Equal(InstallerLaunch.Failed, launcher.Launch("setup.exe"));
    }
}
