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
                ("claude", "plugin install trofeo-quotas@trofeo-hb-dashboard"),
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
            (cli, "plugin install trofeo-quotas@trofeo-hb-dashboard"),
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

    private static ModuleKitResult Convert(Trofeo.ModuleKit.ActionResult r) => new(r.Ok, r.Message);
}
