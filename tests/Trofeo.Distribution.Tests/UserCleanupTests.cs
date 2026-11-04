using Trofeo.Modules.Agents;

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

    private static string Leftover(TempDir dir, string name)
    {
        var folder = dir.Combine(name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.dat"), "secret");
        return folder;
    }

    [Fact]
    public async Task Run_DeleteSettings_AlsoRemovesTheLeftoverFolders()
    {
        // Les copies d'avant un retour en arrière (avec secrets.dat), l'ancien dossier orphelin et le téléchargement de la mise à jour.
        using var dir = new TempDir();
        var backups = Leftover(dir, "ashboard-vision-sauvegardes");
        var legacy = Leftover(dir, "trofeo-HB-dashboard");
        var download = Leftover(dir, "ashboard-vision-mise-a-jour");
        var other = Leftover(dir, "autre-dossier");
        var settings = Settings(dir);

        await new UserCleanup(new FakeRunKey(), new FakeClaude().Installer(null))
            .RunAsync(settings, deleteSettings: true, CancellationToken.None, [backups, legacy, download]);

        Assert.False(Directory.Exists(settings));
        Assert.False(Directory.Exists(backups));
        Assert.False(Directory.Exists(legacy));
        Assert.False(Directory.Exists(download));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task Run_KeepSettings_KeepsTheLeftoverFolders()
    {
        using var dir = new TempDir();
        var backups = Leftover(dir, "ashboard-vision-sauvegardes");
        var settings = Settings(dir);

        await new UserCleanup(new FakeRunKey(), new FakeClaude().Installer(null))
            .RunAsync(settings, deleteSettings: false, CancellationToken.None, [backups]);

        Assert.True(Directory.Exists(backups));
        Assert.True(File.Exists(Path.Combine(settings, "layout.json")));
    }

    [Fact]
    public async Task Run_KeepSettings_StillRemovesInstallationState()
    {
        using var dir = new TempDir();
        var settings = Settings(dir);
        File.WriteAllText(Path.Combine(settings, "installation.json"), """{"versionPreparee":"1.0.0","modInstalle":true}""");

        await new UserCleanup(new FakeRunKey(), new FakeClaude().Installer(null)).RunAsync(settings, deleteSettings: false, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(settings, "installation.json")));
        Assert.True(File.Exists(Path.Combine(settings, "layout.json"))); // les réglages restent
    }

    [Fact]
    public async Task Uninstall_ThenReinstall_AppliesTheInstallerChoiceAgain()
    {
        using var dir = new TempDir();
        var settings = Settings(dir);
        var runKey = new FakeRunKey();
        var modsDir = dir.Combine("mods");
        Directory.CreateDirectory(modsDir);
        var version = new AppVersion(1, 0, 0, null);
        var serviceExe = @"C:\Program Files\Ashboard Vision\Trofeo.Display.exe";
        UserSetup NewSetup(FakeClaude claude) =>
            new(runKey, claude.Installer(modsDir), new StatusLineInstaller(dir.Combine("claude")), settings, modsDir, serviceExe);

        await NewSetup(new FakeClaude()).RunAsync(version, new InstallOptions(StartWithWindows: true), CancellationToken.None);
        Assert.NotNull(runKey.Read("ashboard-vision"));

        // Désinstallation en gardant les réglages (ou en silence), puis réinstallation de la même version.
        await new UserCleanup(runKey, new FakeClaude().Installer(modsDir)).RunAsync(settings, deleteSettings: false, CancellationToken.None);
        Assert.Null(runKey.Read("ashboard-vision"));

        var report = await NewSetup(new FakeClaude()).RunAsync(version, new InstallOptions(StartWithWindows: true), CancellationToken.None);

        Assert.True(report.Ran); // la préparation n'est pas sautée
        Assert.Equal("\"" + serviceExe + "\"", runKey.Read("ashboard-vision")); // la case de l'installateur est appliquée de nouveau
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CallsTheModulesCleanup_BeforeTheSettingsFolderGoes(bool deleteSettings)
    {
        // Le nettoyage des modules relit la liste scellée, qui est dans le dossier des réglages : il passe avant l'effacement.
        using var dir = new TempDir();
        var settings = Settings(dir);
        var calls = new List<(bool Erase, bool SettingsStillThere)>();
        var cleanup = new UserCleanup(new FakeRunKey(), new FakeClaude().Installer(null), (erase, _) =>
        {
            calls.Add((erase, Directory.Exists(settings)));
            return Task.CompletedTask;
        });

        await cleanup.RunAsync(settings, deleteSettings, CancellationToken.None);

        Assert.Equal([(deleteSettings, true)], calls);
        Assert.Equal(!deleteSettings, Directory.Exists(settings));
    }

    [Fact]
    public async Task Run_ModulesCleanupFailing_StillCleansTheRest()
    {
        using var dir = new TempDir();
        var runKey = new FakeRunKey();
        runKey.Values["ashboard-vision"] = "x";
        var settings = Settings(dir);
        var cleanup = new UserCleanup(runKey, new FakeClaude().Installer(null), (_, _) => throw new IOException("verrou"));

        await cleanup.RunAsync(settings, deleteSettings: true, CancellationToken.None);

        Assert.Empty(runKey.Values);
        Assert.False(Directory.Exists(settings));
    }
}
