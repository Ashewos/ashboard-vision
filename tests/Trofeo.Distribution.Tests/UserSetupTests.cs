using System.Text.Json.Nodes;
using Trofeo.Modules.Agents;

namespace Trofeo.Distribution.Tests;

public sealed class UserSetupTests : IDisposable
{
    private const string ServiceExe = @"C:\Program Files\Ashboard Vision\Trofeo.Display.exe";

    // Le format réel de « claude plugin list --json » : un tableau, et un même id deux fois s'il est installé aux portées user et project.
    private const string OtherPlugin =
        """{"id":"claude-security@claude-plugins-official","version":"0.12.0","scope":"user","enabled":true,"installPath":"C:\\Users\\x\\.claude\\plugins\\cache\\claude-security","installedAt":"2026-09-30T21:20:25.475Z","lastUpdated":"2026-10-01T08:00:00.000Z","projectEnabled":true}""";
    private const string LegacyList =
        $$"""[{{OtherPlugin}},{"id":"trofeo-quotas@trofeo-hb-dashboard","version":"0.5.0","scope":"user","enabled":true,"installPath":"C:\\x"},{"id":"trofeo-quotas@trofeo-hb-dashboard","version":"0.5.0","scope":"project","enabled":true,"installPath":"C:\\x"}]""";
    private const string NewModList =
        $$"""[{{OtherPlugin}},{"id":"ashboard-quotas@ashboard-vision","version":"1.0.0","scope":"user","enabled":true,"installPath":"C:\\y"},{"id":"ashboard-quotas@ashboard-vision","version":"1.0.0","scope":"project","enabled":true,"installPath":"C:\\y"}]""";
    private const string NoModList = $"[{OtherPlugin}]";

    private static readonly AppVersion Current = new(1, 0, 0, "beta.1");

    private readonly TempDir _dir = new();
    private readonly FakeRunKey _runKey = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsDir => _dir.Combine("reglages");
    private string ModsDir => _dir.Combine("mods");
    private string ClaudeHome => _dir.Combine("claude");
    private string StatePath => Path.Combine(SettingsDir, "installation.json");

    private UserSetup Setup(FakeClaude claude, FakeRunKey? runKey = null)
    {
        Directory.CreateDirectory(ModsDir);
        return new UserSetup(runKey ?? _runKey, claude.Installer(ModsDir), new StatusLineInstaller(ClaudeHome), SettingsDir, ModsDir, ServiceExe);
    }

    private void WriteState(string version, bool modInstalled)
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(StatePath, new JsonObject { ["versionPreparee"] = version, ["modInstalle"] = modInstalled }.ToJsonString());
    }

    private JsonObject ReadState() => (JsonObject)JsonNode.Parse(File.ReadAllText(StatePath))!;

    [Fact]
    public async Task Run_SameVersionAlreadyPrepared_DoesNothing()
    {
        WriteState(Current.ToString(), modInstalled: true);
        _runKey.Values["trofeo-HB-dashboard"] = "ancien";
        var claude = new FakeClaude(LegacyList);

        var report = await Setup(claude).RunAsync(Current, new InstallOptions(true), CancellationToken.None);

        Assert.False(report.Ran);
        Assert.Empty(report.Warnings);
        Assert.Empty(claude.Calls);
        Assert.Equal("ancien", _runKey.Read("trofeo-HB-dashboard")); // rien n'a bougé
        Assert.Null(_runKey.Read("ashboard-vision"));
    }

    [Fact]
    public async Task Run_LegacyRunValue_ReplacedByNewPointingToServiceExe()
    {
        _runKey.Values["trofeo-HB-dashboard"] = "\"C:\\ancien\\Trofeo.Display.exe\"";

        var report = await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);

        Assert.True(report.Ran);
        Assert.Null(_runKey.Read("trofeo-HB-dashboard"));
        Assert.Equal("\"" + ServiceExe + "\"", _runKey.Read("ashboard-vision"));
    }

    [Fact]
    public async Task Run_NoLegacy_FirstInstall_StartWithWindowsOption_WritesRunValue()
    {
        await Setup(new FakeClaude()).RunAsync(Current, new InstallOptions(StartWithWindows: true), CancellationToken.None);

        Assert.Equal("\"" + ServiceExe + "\"", _runKey.Read("ashboard-vision"));
    }

    [Fact]
    public async Task Run_StartWithWindowsFalse_NoRunValue()
    {
        await Setup(new FakeClaude()).RunAsync(Current, new InstallOptions(StartWithWindows: false), CancellationToken.None);

        Assert.Empty(_runKey.Values);
    }

    [Fact]
    public async Task Run_StateExists_InstallerOptionIsNotReapplied()
    {
        // Mise à jour : l'utilisateur a pu couper le démarrage avec Windows dans les Paramètres, l'installateur ne le rallume pas.
        WriteState("0.9.0", modInstalled: false);

        await Setup(new FakeClaude()).RunAsync(Current, new InstallOptions(StartWithWindows: true), CancellationToken.None);

        Assert.Empty(_runKey.Values);
    }

    [Fact]
    public async Task Run_NewRunValueAlreadyThere_PointsItAtTheCurrentExe()
    {
        _runKey.Values["ashboard-vision"] = "\"C:\\ailleurs\\Trofeo.Display.exe\"";

        await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);

        Assert.Equal("\"" + ServiceExe + "\"", _runKey.Read("ashboard-vision"));
    }

    [Fact]
    public async Task Run_RunKeyRefuses_WarnsAndContinues()
    {
        var runKey = new FakeRunKey { WriteFailure = new UnauthorizedAccessException("refusé") };
        var claude = new FakeClaude(LegacyList);
        runKey.Values["trofeo-HB-dashboard"] = "ancien";

        var report = await Setup(claude, runKey).RunAsync(Current, null, CancellationToken.None);

        Assert.True(report.Ran);
        Assert.Contains(report.Warnings, w => w.Contains("démarrage avec Windows"));
        Assert.Contains("plugin install ashboard-quotas@ashboard-vision", claude.Calls); // la suite a eu lieu
        Assert.Equal(Current.ToString(), ReadState()["versionPreparee"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_LegacyModInstalled_UninstallsItAndInstallsNew()
    {
        var claude = new FakeClaude(LegacyList);

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Empty(report.Warnings);
        Assert.Equal(
            [
                "plugin list --json",
                "plugin uninstall trofeo-quotas@trofeo-hb-dashboard",
                "plugin marketplace remove trofeo-hb-dashboard",
                $"plugin marketplace add \"{ModsDir}\"",
                "plugin install ashboard-quotas@ashboard-vision",
            ],
            claude.Calls);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_LegacyModUninstallFails_WarnsButInstallsNew()
    {
        var claude = new FakeClaude(LegacyList, failing: a => a.StartsWith("plugin uninstall trofeo"));

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Contains("ancien mod", report.Warnings[0]);
        Assert.Contains("plugin install ashboard-quotas@ashboard-vision", claude.Calls);
    }

    [Fact]
    public async Task Run_NoLegacyMod_NotRecorded_DoesNotInstallTheMod()
    {
        var claude = new FakeClaude(NoModList);

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Empty(report.Warnings);
        Assert.Equal(["plugin list --json"], claude.Calls); // c'est l'assistant de première ouverture qui le proposera
        Assert.False(ReadState()["modInstalle"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_ModRecordedInstalled_ReinstallsAfterUpdate()
    {
        WriteState("0.9.0", modInstalled: true);
        var claude = new FakeClaude(NoModList);

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Empty(report.Warnings);
        Assert.Equal($"plugin marketplace add \"{ModsDir}\"", claude.Calls[^2]);
        Assert.Equal("plugin install ashboard-quotas@ashboard-vision", claude.Calls[^1]);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_NewModInstalledButNothingRecorded_RefreshesIt()
    {
        // Le mod a été posé par l'assistant ou par l'action du module : aucun installation.json ne le sait.
        var claude = new FakeClaude(NewModList);

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Empty(report.Warnings);
        Assert.Equal(
            [
                "plugin list --json",
                "plugin marketplace update ashboard-vision",
                "plugin update ashboard-quotas@ashboard-vision",
            ],
            claude.Calls);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_NewModRefreshFails_WarnsAndKeepsWantingIt()
    {
        var claude = new FakeClaude(NewModList, failing: a => a.StartsWith("plugin update"));

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Contains("mis à jour", report.Warnings[0]);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Run_ModListNotJson_WarnsAndSkipsModSteps()
    {
        WriteState("0.9.0", modInstalled: true);
        var claude = new FakeClaude("pas du json");

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Contains("mods", report.Warnings[0]);
        Assert.Equal(["plugin list --json"], claude.Calls);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>()); // le souhait est gardé
        Assert.Equal(Current.ToString(), ReadState()["versionPreparee"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_ModListNotAnArray_WarnsAndSkipsModSteps()
    {
        var claude = new FakeClaude("""{"id":"ashboard-quotas@ashboard-vision"}""");

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Equal(["plugin list --json"], claude.Calls);
    }

    [Fact]
    public async Task Run_ModInstallFails_WarnsAndKeepsWantingIt()
    {
        WriteState("0.9.0", modInstalled: true);
        var claude = new FakeClaude(failing: a => a.StartsWith("plugin install"));

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Contains("boom", report.Warnings[0]);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>()); // la prochaine mise à jour réessaiera
    }

    [Fact]
    public async Task Run_ClaudeMissing_SkipsModSilently()
    {
        WriteState("0.9.0", modInstalled: true);

        var report = await Setup(new FakeClaude(missing: true)).RunAsync(Current, null, CancellationToken.None);

        Assert.True(report.Ran);
        Assert.Equal(["Claude Code est introuvable sur ce PC : le mod des quotas n'a pas été préparé."], report.Warnings);
        Assert.True(ReadState()["modInstalle"]!.GetValue<bool>()); // le souhait est gardé pour quand Claude Code sera là
        Assert.Equal(Current.ToString(), ReadState()["versionPreparee"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_OldStatusLineConfigured_UninstalledAndPreviousRestored()
    {
        Directory.CreateDirectory(Path.Combine(ClaudeHome, "trofeo-HB-dashboard"));
        File.WriteAllText(
            Path.Combine(ClaudeHome, "settings.json"),
            """{"statusLine":{"type":"command","command":"\"C:/x/Trofeo.StatusLine.exe\" --tee | ( echo ancienne )"},"theme":"dark"}""");
        File.WriteAllText(
            Path.Combine(ClaudeHome, "trofeo-HB-dashboard", "statusline-previous.json"),
            """{"previous":{"type":"command","command":"echo ancienne"}}""");

        var report = await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);

        Assert.Empty(report.Warnings);
        var settings = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(ClaudeHome, "settings.json")))!;
        Assert.Equal("echo ancienne", settings["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(ClaudeHome, "trofeo-HB-dashboard", "statusline-previous.json")));
    }

    [Fact]
    public async Task Run_StatusLineChainedButNoBackup_WarnsAndContinues()
    {
        // La commande enveloppe l'originale, mais la sauvegarde manque : rien ne doit être écrit, au risque de perdre l'originale.
        Directory.CreateDirectory(ClaudeHome);
        var settingsJson = """{"statusLine":{"type":"command","command":"\"C:/x/Trofeo.StatusLine.exe\" --tee | ( echo ancienne )"}}""";
        File.WriteAllText(Path.Combine(ClaudeHome, "settings.json"), settingsJson);
        var claude = new FakeClaude(LegacyList);

        var report = await Setup(claude).RunAsync(Current, null, CancellationToken.None);

        Assert.Single(report.Warnings);
        Assert.Contains("barre d'état", report.Warnings[0]);
        Assert.Contains("plugin install ashboard-quotas@ashboard-vision", claude.Calls);
        Assert.Equal(settingsJson, File.ReadAllText(Path.Combine(ClaudeHome, "settings.json")));
    }

    [Fact]
    public async Task Run_RecordsPreparedVersion()
    {
        var report = await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);

        Assert.True(report.Ran);
        Assert.Equal("1.0.0-beta.1", ReadState()["versionPreparee"]!.GetValue<string>());

        // Un second passage, même version : plus rien à faire.
        var second = await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);
        Assert.False(second.Ran);
    }

    [Fact]
    public async Task Run_StateUnreadable_PreparesAgain()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(StatePath, "pas du json");

        var report = await Setup(new FakeClaude()).RunAsync(Current, null, CancellationToken.None);

        Assert.True(report.Ran);
        Assert.Equal("1.0.0-beta.1", ReadState()["versionPreparee"]!.GetValue<string>());
    }
}
