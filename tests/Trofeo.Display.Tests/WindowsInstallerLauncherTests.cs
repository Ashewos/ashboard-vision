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
    public void Launch_LetsTheInstallerAskForAdministratorRightsItself()
    {
        // Sans « runas » : l'installateur demande lui-même l'autorisation et garde ainsi l'utilisateur d'origine, sous lequel il
        // relance le service (lancé déjà élevé, il le relançait en administrateur).
        ProcessStartInfo? seen = null;
        var launcher = new WindowsInstallerLauncher(info => { seen = info; return null; });

        var result = launcher.Launch(@"C:\Temp\Ashboard-Vision-Setup-1.0.0.exe");

        Assert.Equal(InstallerLaunch.Started, result);
        Assert.NotNull(seen);
        Assert.Equal(@"C:\Temp\Ashboard-Vision-Setup-1.0.0.exe", seen.FileName);
        Assert.Equal(WindowsInstallerLauncher.Arguments, seen.Arguments);
        Assert.True(seen.UseShellExecute);
        Assert.Equal("", seen.Verb);
    }

    [Fact]
    public void Launch_InstallerEndsWithCode0_IsStarted()
    {
        var launcher = new WindowsInstallerLauncher(_ => 0);

        Assert.Equal(InstallerLaunch.Started, launcher.Launch("setup.exe"));
    }

    [Fact]
    public void Launch_InstallerCancelledBeforeInstalling_IsCancelled()
    {
        // Code 2 d'Inno Setup : annulé avant l'installation (l'autorisation de Windows refusée).
        var launcher = new WindowsInstallerLauncher(_ => 2);

        Assert.Equal(InstallerLaunch.Cancelled, launcher.Launch("setup.exe"));
    }

    [Theory]
    [InlineData(1)] // Setup n'a pas pu démarrer
    [InlineData(4)] // erreur pendant l'installation
    [InlineData(7)] // la préparation a refusé de continuer
    public void Launch_InstallerEndsWithAnotherCode_IsFailed(int code)
    {
        var launcher = new WindowsInstallerLauncher(_ => code);

        Assert.Equal(InstallerLaunch.Failed, launcher.Launch("setup.exe"));
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
