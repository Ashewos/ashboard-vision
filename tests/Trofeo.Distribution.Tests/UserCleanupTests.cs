namespace Trofeo.Distribution.Tests;

public sealed class UserCleanupTests
{
    private static string Settings(TempDir dir)
    {
        var settings = dir.Combine("ashboard-vision");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "layout.json"), "{}");
        return settings;
    }

    [Fact]
    public async Task Run_RemovesRunValueAndMod()
    {
        using var dir = new TempDir();
        var runKey = new FakeRunKey();
        runKey.Values["ashboard-vision"] = "\"C:\\x\\Trofeo.Display.exe\"";
        runKey.Values["trofeo-HB-dashboard"] = "ancien";
        runKey.Values["autre"] = "garde";
        var claude = new FakeClaude();

        await new UserCleanup(runKey, claude.Installer(null)).RunAsync(Settings(dir), deleteSettings: false, CancellationToken.None);

        Assert.Equal(["autre"], runKey.Values.Keys);
        Assert.Equal(
            ["plugin uninstall ashboard-quotas@ashboard-vision", "plugin marketplace remove ashboard-vision"],
            claude.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Run_DeleteSettings_RemovesFolderOnlyWhenAsked(bool deleteSettings, bool folderRemains)
    {
        using var dir = new TempDir();
        var settings = Settings(dir);

        await new UserCleanup(new FakeRunKey(), new FakeClaude().Installer(null)).RunAsync(settings, deleteSettings, CancellationToken.None);

        Assert.Equal(folderRemains, Directory.Exists(settings));
    }

    [Fact]
    public async Task Run_ClaudeMissing_StillCleansTheRest()
    {
        using var dir = new TempDir();
        var runKey = new FakeRunKey();
        runKey.Values["ashboard-vision"] = "x";
        var settings = Settings(dir);

        await new UserCleanup(runKey, new FakeClaude(missing: true).Installer(null)).RunAsync(settings, deleteSettings: true, CancellationToken.None);

        Assert.Empty(runKey.Values);
        Assert.False(Directory.Exists(settings));
    }
}
