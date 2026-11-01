namespace Trofeo.Modules.Agents.Tests;

public sealed class ClaudeCliLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("trofeo-claude-cli-").FullName;
    private string Profile => Path.Combine(_root, "user");
    private string Local => Path.Combine(_root, "local");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Executable(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture, never executed");
        return path;
    }

    [Fact]
    public void FindsDesktopBundledCliOutsidePath()
    {
        var cli = Executable(Local, "Claude-Data", "claude_package", "claude-code", "2.1.309", "build", "claude.exe");
        Assert.Equal([cli], ClaudeCliLocator.FindCandidates(Profile, Local));
    }

    [Fact]
    public void PrefersNativeInstallThenNewestDesktopVersionNumerically()
    {
        var old = Executable(Local, "Claude-Data", "package-a", "claude-code", "2.1.99", "build", "claude.exe");
        var latest = Executable(Local, "Claude-Data", "package-b", "claude-code", "2.1.309", "build", "claude.exe");
        var native = Executable(Profile, ".local", "bin", "claude.exe");
        Assert.Equal([native, latest, old], ClaudeCliLocator.FindCandidates(Profile, Local));
    }

    [Fact]
    public void IgnoresDesktopGuiAndUnrelatedExecutables()
    {
        Executable(Local, "Claude-Data", "claude.exe");
        Executable(Local, "Claude-Data", "package", "other", "2.1.309", "build", "claude.exe");
        Executable(Local, "Claude-Data", "package", "claude-code", "not-a-version", "build", "claude.exe");
        Assert.Empty(ClaudeCliLocator.FindCandidates(Profile, Local));
    }

    [Fact]
    public void MissingFoldersAreAllowed() => Assert.Empty(ClaudeCliLocator.FindCandidates(Profile, Local));

    [Fact]
    public void IgnoresIncompleteNewVersion()
    {
        var cli = Executable(Local, "Claude-Data", "package", "claude-code", "2.1.309", "build", "claude.exe");
        Directory.CreateDirectory(Path.Combine(Local, "Claude-Data", "package", "claude-code", "2.1.310", "build"));
        Assert.Equal([cli], ClaudeCliLocator.FindCandidates(Profile, Local));
    }
}
