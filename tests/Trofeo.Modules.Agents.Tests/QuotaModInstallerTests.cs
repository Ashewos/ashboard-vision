using System.ComponentModel;

namespace Trofeo.Modules.Agents.Tests;

public class QuotaModInstallerTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("trofeo-mods-").FullName;

    public void Dispose() => Directory.Delete(_mods, recursive: true);

    private static QuotaModInstaller Installer(
        string? mods, List<(string File, string Args)> calls, Func<int, (int, string)> answer) =>
        new((file, args, _) =>
        {
            calls.Add((file, args));
            return Task.FromResult(answer(calls.Count));
        }, mods);

    [Fact]
    public async Task QuotaMod_RunsMarketplaceThenInstall()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(_mods, calls, _ => (0, "ok")).InstallAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(true, "Mod installé : il sera chargé par les nouvelles sessions de Claude Code."), Convert(result));
        Assert.Equal(
            [
                ("claude", $"plugin marketplace add \"{_mods}\""),
                ("claude", "plugin install ashboard-quotas@ashboard-vision"),
            ],
            calls);
    }

    [Fact]
    public async Task QuotaMod_ClaudeMissing_SaysSo()
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new Win32Exception(2, "introuvable"), _mods);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, "Claude Code est introuvable sur ce PC."), Convert(result));
    }

    [Fact]
    public async Task QuotaMod_FileNotFound_SaysClaudeMissing()
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new FileNotFoundException(), _mods);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal("Claude Code est introuvable sur ce PC.", result.Message);
    }

    [Fact]
    public async Task QuotaMod_Failure_ReturnsTruncatedOutput()
    {
        var calls = new List<(string File, string Args)>();
        var output = new string('x', 500);

        var result = await Installer(_mods, calls, _ => (1, output)).InstallAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("L'installation a échoué : " + new string('x', 300), result.Message);
        Assert.Single(calls); // l'échec de la première commande arrête la suite
    }

    [Fact]
    public async Task QuotaMod_InstallStepFails_ReportsIt()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(_mods, calls, n => n == 1 ? (0, "") : (2, "  boom  ")).InstallAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("L'installation a échoué : boom", result.Message);
        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/ce/dossier/n/existe/pas")]
    public async Task QuotaMod_ModsFolderMissing_SaysSo(string? mods)
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(mods, calls, _ => (0, "")).InstallAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, "Le dossier du mod est introuvable."), Convert(result));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task List_RunsPluginListAsJsonAndReturnsItsOutput()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(_mods, calls, _ => (0, "trofeo-quotas@trofeo-hb-dashboard")).ListAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(true, "trofeo-quotas@trofeo-hb-dashboard"), Convert(result));
        Assert.Equal([("claude", "plugin list --json")], calls);
    }

    [Fact]
    public async Task List_CommandFails_ReportsTruncatedOutput()
    {
        var result = await Installer(_mods, [], _ => (1, "  boom  ")).ListAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, "La liste des mods n'a pas pu être lue : boom"), Convert(result));
    }

    [Fact]
    public async Task List_ClaudeMissing_SaysSo()
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new Win32Exception(2, "introuvable"), null);

        var result = await installer.ListAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, QuotaModInstaller.ClaudeMissingMessage), Convert(result));
    }

    [Fact]
    public async Task Update_RunsMarketplaceUpdateThenPluginUpdate()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(null, calls, _ => (0, "ok")).UpdateAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(true, "Mod mis à jour : il sera rechargé par les nouvelles sessions de Claude Code."), Convert(result));
        Assert.Equal(
            [
                ("claude", "plugin marketplace update ashboard-vision"),
                ("claude", "plugin update ashboard-quotas@ashboard-vision"),
            ],
            calls);
    }

    [Fact]
    public async Task Update_FirstFails_StillRunsSecondAndReportsFirstOutput()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(null, calls, n => n == 1 ? (1, "  boom  ") : (2, "autre")).UpdateAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, "La mise à jour a échoué : boom"), Convert(result));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Update_ClaudeMissing_SaysSo()
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new Win32Exception(2, "introuvable"), null);

        var result = await installer.UpdateAsync(CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, QuotaModInstaller.ClaudeMissingMessage), Convert(result));
    }

    [Fact]
    public async Task Uninstall_RunsPluginUninstallThenMarketplaceRemove()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(_mods, calls, _ => (0, "ok"))
            .UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(
            [
                ("claude", "plugin uninstall trofeo-quotas@trofeo-hb-dashboard"),
                ("claude", "plugin marketplace remove trofeo-hb-dashboard"),
            ],
            calls);
    }

    [Fact]
    public async Task Uninstall_NewMod_UsesTheNewNames()
    {
        var calls = new List<(string File, string Args)>();

        await Installer(_mods, calls, _ => (0, "ok"))
            .UninstallAsync(QuotaModInstaller.PluginName, "ashboard-vision", CancellationToken.None);

        Assert.Equal(
            [
                ("claude", "plugin uninstall ashboard-quotas@ashboard-vision"),
                ("claude", "plugin marketplace remove ashboard-vision"),
            ],
            calls);
    }

    [Fact]
    public async Task Uninstall_DoesNotNeedTheModsFolder()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(null, calls, _ => (0, ""))
            .UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Uninstall_ClaudeMissing_SaysSo()
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new Win32Exception(2, "introuvable"), _mods);

        var result = await installer.UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, CancellationToken.None);

        Assert.Equal(new ModuleKitResult(false, "Claude Code est introuvable sur ce PC."), Convert(result));
    }

    [Fact]
    public async Task Uninstall_PluginAlreadyGone_StillRemovesTheMarketplace()
    {
        var calls = new List<(string File, string Args)>();

        // Le mod n'est pas installé (le premier retrait échoue) : la deuxième commande a lieu quand même.
        var result = await Installer(_mods, calls, n => n == 1 ? (1, "Plugin not found") : (0, "ok"))
            .UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.False(result.Ok);
        Assert.Equal("Le retrait a échoué : Plugin not found", result.Message);
    }

    [Fact]
    public async Task Uninstall_Failure_ReturnsTruncatedOutput()
    {
        var calls = new List<(string File, string Args)>();

        var result = await Installer(_mods, calls, _ => (1, new string('x', 500)))
            .UninstallAsync(QuotaModInstaller.LegacyPluginName, QuotaModInstaller.LegacyMarketplace, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Le retrait a échoué : " + new string('x', 300), result.Message);
    }

    [Fact]
    public void Names_NewModAndLegacyMod()
    {
        Assert.Equal("ashboard-quotas@ashboard-vision", QuotaModInstaller.PluginName);
        Assert.Equal("trofeo-quotas@trofeo-hb-dashboard", QuotaModInstaller.LegacyPluginName);
        Assert.Equal("trofeo-hb-dashboard", QuotaModInstaller.LegacyMarketplace);
    }

    private sealed record ModuleKitResult(bool Ok, string Message);

    [Fact]
    public async Task QuotaMod_PathMissing_UsesBundledCliForBothCommands()
    {
        var calls = new List<(string File, string Args)>();
        var cli = Path.Combine(_mods, "desktop", "claude.exe");
        var installer = new QuotaModInstaller((file, args, _) =>
        {
            calls.Add((file, args));
            if (file == "claude") throw new Win32Exception(2);
            return Task.FromResult((0, "ok"));
        }, _mods, () => [cli]);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[]
        {
            ("claude", $"plugin marketplace add \"{_mods}\""),
            (cli, $"plugin marketplace add \"{_mods}\""),
            (cli, $"plugin install {QuotaModInstaller.PluginName}"),
        }, calls);
    }

    [Fact]
    public async Task QuotaMod_PathWorks_DoesNotSearchDesktop()
    {
        var installer = new QuotaModInstaller((_, _, _) => Task.FromResult((0, "")), _mods,
            () => throw new InvalidOperationException("Fallback should not be searched"));
        Assert.True((await installer.InstallAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task QuotaMod_CommandFails_DoesNotTryAnotherInstallation()
    {
        var installer = new QuotaModInstaller((_, _, _) => Task.FromResult((1, "command failed")), _mods,
            () => throw new InvalidOperationException("A failed command is not a missing executable"));
        var result = await installer.InstallAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("command failed", result.Message);
    }

    [Fact]
    public async Task QuotaMod_StaleDesktopCandidate_TriesNext()
    {
        var cli = Path.Combine(_mods, "working", "claude.exe");
        var installer = new QuotaModInstaller((file, _, _) =>
            file == cli ? Task.FromResult((0, "")) : throw new FileNotFoundException(),
            _mods, () => [Path.Combine(_mods, "removed", "claude.exe"), cli]);
        Assert.True((await installer.InstallAsync(CancellationToken.None)).Ok);
    }

    // Les commandes du plan 6 (liste, mise à jour, retrait) cherchent Claude Code comme l'installation : sinon la préparation et
    // la désinstallation diraient « introuvable » sur un PC où seule l'application Claude le fournit.
    public static TheoryData<string> OtherCommands => ["list", "update", "uninstall"];

    private static Task<Trofeo.ModuleKit.ActionResult> RunCommand(QuotaModInstaller installer, string command) => command switch
    {
        "list" => installer.ListAsync(CancellationToken.None),
        "update" => installer.UpdateAsync(CancellationToken.None),
        _ => installer.UninstallAsync(QuotaModInstaller.PluginName, QuotaModInstaller.Marketplace, CancellationToken.None),
    };

    [Theory]
    [MemberData(nameof(OtherCommands))]
    public async Task OtherCommands_PathMissing_UseBundledCli(string command)
    {
        var calls = new List<(string File, string Args)>();
        var cli = Path.Combine(_mods, "desktop", "claude.exe");
        var installer = new QuotaModInstaller((file, args, _) =>
        {
            calls.Add((file, args));
            if (file == "claude") throw new Win32Exception(2);
            return Task.FromResult((0, "[]"));
        }, null, () => [cli]);

        var result = await RunCommand(installer, command);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("claude", calls[0].File);
        Assert.All(calls.Skip(1), call => Assert.Equal(cli, call.File));
        Assert.Equal(command == "list" ? 2 : 3, calls.Count); // « claude » refusé, puis chaque commande avec le CLI trouvé
    }

    [Theory]
    [MemberData(nameof(OtherCommands))]
    public async Task OtherCommands_NoInstallationFound_SayClaudeMissing(string command)
    {
        var installer = new QuotaModInstaller((_, _, _) => throw new Win32Exception(2), null, () => []);

        var result = await RunCommand(installer, command);

        Assert.Equal(new ModuleKitResult(false, QuotaModInstaller.ClaudeMissingMessage), Convert(result));
    }

    private static ModuleKitResult Convert(Trofeo.ModuleKit.ActionResult r) => new(r.Ok, r.Message);
}
