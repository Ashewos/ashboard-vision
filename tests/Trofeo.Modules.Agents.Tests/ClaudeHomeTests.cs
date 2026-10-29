namespace Trofeo.Modules.Agents.Tests;

public sealed class ClaudeHomeTests
{
    [Fact]
    public void StatusLineDir_IsInTheNewFolder() =>
        Assert.Equal(Path.Combine("h", "ashboard-vision", "statusline"), ClaudeHome.StatusLineDir("h"));

    [Fact]
    public void LegacyStatusLineDir_IsInTheFolderOfTheOldName() =>
        Assert.Equal(Path.Combine("h", "trofeo-HB-dashboard", "statusline"), ClaudeHome.LegacyStatusLineDir("h"));
}
