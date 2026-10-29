namespace Trofeo.Distribution.Tests;

public sealed class InstallOptionsTests
{
    [Theory]
    [InlineData("""{"demarrerAvecWindows":true}""", true)]
    [InlineData("""{"demarrerAvecWindows":false}""", false)]
    public void TryRead_ReadsTheInstallersChoice(string json, bool expected)
    {
        using var dir = new TempDir();
        var path = dir.Combine("options-installation.json");
        File.WriteAllText(path, json);

        Assert.Equal(new InstallOptions(expected), InstallOptions.TryRead(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("""{"demarrerAvecWindows":"oui"}""")]
    [InlineData("[]")]
    public void TryRead_AbsentOrUnreadable_ReturnsNull(string? json)
    {
        using var dir = new TempDir();
        var path = dir.Combine("options-installation.json");
        if (json is not null) File.WriteAllText(path, json);

        Assert.Null(InstallOptions.TryRead(path));
    }
}
