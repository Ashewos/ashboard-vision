using System.IO.Pipes;
using System.Security.Principal;

namespace Trofeo.Sensors.Tests;

public class HelperIdentityTests
{
    [Fact]
    public void InstallDir_IsProgramFilesSensorsFolder_WithTrailingSeparator()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ashboard-vision", "sensors")
            + Path.DirectorySeparatorChar;
        Assert.Equal(expected, HelperIdentity.InstallDir);
    }

    [Fact]
    public void InstallDir_IsNotInsideTheApplicationFolder()
    {
        // L'installateur met l'application dans « Program Files\Ashboard Vision » : l'assistant ne doit pas s'y installer,
        // sinon la copie de l'application y descendrait sans fin.
        var appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ashboard Vision") + Path.DirectorySeparatorChar;
        Assert.False(HelperIdentity.InstallDir.StartsWith(appFolder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LegacyInstallDirs_AreTheFoldersOfTheOldNameAndOfTheApplicationSubfolder_WithTrailingSeparator()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(
        [
            Path.Combine(programFiles, "trofeo-HB-dashboard", "sensors") + Path.DirectorySeparatorChar,
            Path.Combine(programFiles, "Ashboard Vision", "sensors") + Path.DirectorySeparatorChar,
        ], HelperIdentity.LegacyInstallDirs);
    }

    [Theory]
    [InlineData("S-1-5-18", true)] // SYSTEM
    [InlineData("S-1-5-32-544", true)] // BUILTIN\Administrators
    [InlineData("S-1-5-32-545", false)] // BUILTIN\Users
    [InlineData("S-1-1-0", false)] // Everyone
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001", false)] // un utilisateur quelconque
    public void IsTrustedOwner_AcceptsOnlySystemAndAdministrators(string sid, bool expected) =>
        Assert.Equal(expected, HelperIdentity.IsTrustedOwner(new SecurityIdentifier(sid)));

    [Fact]
    public void IsTrustedOwner_CurrentUserAndNull_AreFalse()
    {
        Assert.False(HelperIdentity.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(HelperIdentity.IsTrustedOwner(null));
    }

    [Fact]
    public async Task IsTrusted_PipeCreatedByThisProcess_FollowsElevation()
    {
        // Sans élévation (le PC d'Alex), le propriétaire par défaut du tube est l'utilisateur courant : pas digne de confiance.
        // Élevé (les machines de GitHub Actions), Windows le donne au groupe Administrateurs, que la règle accepte comme pour
        // l'assistant des capteurs.
        var current = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
        var name = "trofeo-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        await using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connecting = server.WaitForConnectionAsync(cts.Token);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, cts.Token);
        await connecting;

        var owner = HelperIdentity.ReadOwner(client); // la lecture du propriétaire fonctionne vraiment
        if (elevated)
        {
            Assert.True(owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
            Assert.True(HelperIdentity.IsTrusted(client));
        }
        else
        {
            Assert.Equal(current.User, owner);
            Assert.False(HelperIdentity.IsTrusted(client));
        }
    }

    [Fact]
    public void IsTrusted_UnconnectedPipe_IsFalse()
    {
        using var client = new NamedPipeClientStream(".", "trofeo-test-absent", PipeDirection.In);
        Assert.False(HelperIdentity.IsTrusted(client));
    }
}
