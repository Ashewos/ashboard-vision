namespace Trofeo.Distribution.Tests;

public sealed class SettingsBackupTests
{
    private static readonly AppVersion From = new(1, 0, 0, "beta.2");
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 14, 5, 9, TimeSpan.Zero);

    private static string Settings(TempDir dir)
    {
        var settings = dir.Combine("ashboard-vision");
        Directory.CreateDirectory(Path.Combine(settings, "logs"));
        Directory.CreateDirectory(Path.Combine(settings, "videos", "boucles"));
        File.WriteAllText(Path.Combine(settings, "layout.json"), "{}");
        File.WriteAllText(Path.Combine(settings, "videos", "boucles", "fond.txt"), "fond");
        File.WriteAllText(Path.Combine(settings, "logs", "service.log"), "journal");
        return settings;
    }

    [Fact]
    public void Create_CopiesEverythingExceptLogs()
    {
        using var dir = new TempDir();
        var settings = Settings(dir);
        var root = dir.Combine("sauvegardes");

        var backup = SettingsBackup.Create(settings, root, From, Now);

        Assert.Equal(Path.Combine(root, "2026-10-29-140509-1.0.0-beta.2"), backup);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(backup, "layout.json")));
        Assert.Equal("fond", File.ReadAllText(Path.Combine(backup, "videos", "boucles", "fond.txt")));
        Assert.False(Directory.Exists(Path.Combine(backup, "logs")));
        // L'original est intact, journaux compris.
        Assert.True(File.Exists(Path.Combine(settings, "logs", "service.log")));
    }

    [Fact]
    public void Create_TwoBackupsSameSecond_DoNotOverwrite()
    {
        using var dir = new TempDir();
        var settings = Settings(dir);
        var root = dir.Combine("sauvegardes");

        var first = SettingsBackup.Create(settings, root, From, Now);
        File.WriteAllText(Path.Combine(settings, "layout.json"), """{"v":2}""");
        var second = SettingsBackup.Create(settings, root, From, Now);
        var third = SettingsBackup.Create(settings, root, From, Now);

        Assert.Equal(first + "-2", second);
        Assert.Equal(first + "-3", third);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(first, "layout.json")));
        Assert.Equal("""{"v":2}""", File.ReadAllText(Path.Combine(second, "layout.json")));
    }

    [Fact]
    public void Create_MissingSettingsDir_MakesEmptyBackup()
    {
        using var dir = new TempDir();

        var backup = SettingsBackup.Create(dir.Combine("absent"), dir.Combine("sauvegardes"), From, Now);

        Assert.True(Directory.Exists(backup));
        Assert.Empty(Directory.EnumerateFileSystemEntries(backup));
    }

    [Fact]
    public void Create_BackupsRootInsideSettings_IsNotCopiedIntoItself()
    {
        using var dir = new TempDir();
        var settings = Settings(dir);
        var root = Path.Combine(settings, "sauvegardes");

        var first = SettingsBackup.Create(settings, root, From, Now);
        var second = SettingsBackup.Create(settings, root, From, Now.AddSeconds(1));

        Assert.False(Directory.Exists(Path.Combine(second, "sauvegardes")));
        Assert.True(File.Exists(Path.Combine(second, "layout.json")));
        Assert.NotEqual(first, second);
    }
}
