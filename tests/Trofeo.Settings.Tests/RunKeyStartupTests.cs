using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

public sealed class RunKeyStartupTests
{
    [Fact]
    public void ValueName_IsTheNewIdentifier_AndTheOldOneStaysKnown()
    {
        Assert.Equal("ashboard-vision", RunKeyStartup.ValueName);
        Assert.Equal("trofeo-HB-dashboard", RunKeyStartup.LegacyValueName);
    }
}
