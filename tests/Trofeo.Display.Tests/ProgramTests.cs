namespace Trofeo.Display.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void DeleteSettings_Silent_KeepsThemWithoutAsking()
    {
        var asked = false;

        var delete = Program.ShouldDeleteSettings(silent: true, askKeep: () =>
        {
            asked = true;
            return false;
        });

        Assert.False(delete);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true, false)] // « Oui, garder » : on ne supprime pas
    [InlineData(false, true)] // « Non » : on supprime
    public void DeleteSettings_Interactive_FollowsTheAnswer(bool keep, bool expectedDelete)
    {
        Assert.Equal(expectedDelete, Program.ShouldDeleteSettings(silent: false, askKeep: () => keep));
    }

    [Fact]
    public void HasFlag_FindsTheCommandLineOptionsCaseInsensitively()
    {
        string[] args = ["--desinstaller-utilisateur", "--SILENCIEUX"];

        Assert.True(Program.HasFlag(args, "--desinstaller-utilisateur"));
        Assert.True(Program.HasFlag(args, "--silencieux"));
        Assert.False(Program.HasFlag([], "--silencieux"));
        Assert.False(Program.HasFlag(["--autre"], "--desinstaller-utilisateur"));
    }
}
