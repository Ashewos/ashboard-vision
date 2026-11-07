using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Trofeo.Distribution.Tests;

/// <summary>Un test qui lance Windows PowerShell 5.1 (powershell.exe) : ignoré ailleurs que sous Windows.</summary>
public sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    public WindowsPowerShellFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows PowerShell 5.1 n'existe que sous Windows.";
        }
    }
}

/// <summary>
/// installer/dotnet-sdk.ps1 : l'aide de l'installateur pour le SDK .NET 10 (lecture de releases.json, vérification du fichier téléchargé).
/// Les tests lancent le vrai script avec Windows PowerShell 5.1, sur des fichiers locaux : aucun accès au réseau.
/// </summary>
public sealed class DotnetSdkScriptTests : IDisposable
{
    private const string Version = "10.0.403";
    private const string GoodUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.exe";
    private static readonly string GoodHash = new('a', 128);
    private static readonly string OtherHash = new('b', 128);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // --- releases.json (forme réelle, raccourcie) ------------------------------------------------------------------------------

    private static string FileEntry(string name, string rid, string url, string hash) =>
        $$"""{ "name": "{{name}}", "rid": "{{rid}}", "url": "{{url}}", "hash": "{{hash}}" }""";

    /// <summary>Les fichiers d'un SDK : d'autres plateformes, un .zip et un .exe x86 qui ne doivent pas être retenus, puis le bon.</summary>
    private static string SdkFiles(string winX64Url = GoodUrl, string winX64Hash = "") =>
        "[" + string.Join(",",
            FileEntry("dotnet-sdk-linux-x64.tar.gz", "linux-x64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-linux-x64.tar.gz", OtherHash),
            FileEntry("dotnet-sdk-win-arm64.exe", "win-arm64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-arm64.exe", OtherHash),
            FileEntry("dotnet-sdk-win-x64.zip", "win-x64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip", OtherHash),
            FileEntry("dotnet-sdk-win-x86.exe", "win-x86", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x86.exe", OtherHash),
            FileEntry("dotnet-sdk-win-x64.exe", "win-x64", winX64Url, winX64Hash.Length == 0 ? GoodHash : winX64Hash)) + "]";

    private static string SdkBlock(string version, string files) =>
        $$"""{ "version": "{{version}}", "version-display": "{{version}}", "runtime-version": "10.0.7", "files": {{files}} }""";

    private static string Releases(string latestSdk, string releases) =>
        $$"""{ "channel-version": "10.0", "latest-release": "10.0.7", "latest-sdk": "{{latestSdk}}", "support-phase": "active", "releases": [ {{releases}} ] }""";

    /// <summary>Deux publications : la dernière (avec « sdk » et « sdks »), puis une plus ancienne dont le SDK a une autre empreinte.</summary>
    private static string Release(string releaseVersion, string sdkBlock, string sdksBlocks = "") =>
        $$"""{ "release-date": "2026-10-13", "release-version": "{{releaseVersion}}", "sdk": {{sdkBlock}}, "sdks": [ {{sdksBlocks}} ] }""";

    /// <summary>Deux publications : la dernière (avec « sdk » et « sdks »), puis une plus ancienne dont le SDK a une autre empreinte.</summary>
    private static string RealisticReleases(string files)
    {
        var older = SdkBlock("10.0.300", SdkFiles(winX64Hash: OtherHash));
        return Releases(Version, Release("10.0.7", SdkBlock(Version, files), SdkBlock(Version, files) + "," + older) + "," + Release("10.0.6", older));
    }

    // --- lancement du script ---------------------------------------------------------------------------------------------------

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "installer", "dotnet-sdk.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("installer/dotnet-sdk.ps1 est introuvable au-dessus de " + AppContext.BaseDirectory);
    }

    /// <summary>Lance le script avec Windows PowerShell 5.1, comme l'installateur ; rend le code de sortie et le texte du fichier de résultat.</summary>
    private (int ExitCode, string Result) Run(params string[] arguments)
    {
        var resultPath = _dir.Combine("resultat-" + Guid.NewGuid().ToString("N") + ".txt");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath() }.Concat(arguments).Concat(["-Output", resultPath]))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "Le script ne s'est pas terminé à temps.");
        Task.WaitAll(stdout, stderr);
        var result = File.Exists(resultPath) ? File.ReadAllText(resultPath) : "";
        return (process.ExitCode, result);
    }

    private (int ExitCode, string Result) Resolve(string releasesJson)
    {
        var path = _dir.Combine("releases.json");
        File.WriteAllText(path, releasesJson, new UTF8Encoding(false));
        return Run("-Resolve", "-ReleasesSource", path);
    }

    private static Dictionary<string, string> Lines(string result) =>
        result.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "");

    // --- -Resolve ---------------------------------------------------------------------------------------------------------------

    [WindowsPowerShellFact]
    public void Resolve_RealShape_KeepsTheWinX64ExeUrlAndHash()
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles()));

        Assert.Equal(0, code);
        var lines = Lines(result);
        Assert.Equal(Version, lines["version"]);
        Assert.Equal(GoodUrl, lines["url"]);
        Assert.Equal(GoodHash, lines["sha512"]);
    }

    [WindowsPowerShellFact]
    public void Resolve_VersionOnlyInTheSdksArray_IsFound()
    {
        var older = SdkBlock("10.0.300", SdkFiles(winX64Hash: OtherHash));
        var releases = Releases(Version, Release("10.0.7", older, older + "," + SdkBlock(Version, SdkFiles())));

        var (code, result) = Resolve(releases);

        Assert.Equal(0, code);
        Assert.Equal(GoodHash, Lines(result)["sha512"]);
    }

    [WindowsPowerShellFact]
    public void Resolve_UppercaseHash_IsKeptInLowerCase()
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles(winX64Hash: new string('A', 128))));

        Assert.Equal(0, code);
        Assert.Equal(new string('a', 128), Lines(result)["sha512"]);
    }

    [WindowsPowerShellFact]
    public void Resolve_HttpUrl_IsRefused()
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles(winX64Url: GoodUrl.Replace("https://", "http://", StringComparison.Ordinal))));

        Assert.Equal(4, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
        Assert.DoesNotContain("url=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellTheory]
    [InlineData("https://example.com/dotnet-sdk-win-x64.exe")]
    [InlineData("https://builds.dotnet.microsoft.com.evil.example/dotnet-sdk-win-x64.exe")]
    [InlineData("https://evil.example/builds.dotnet.microsoft.com/dotnet-sdk-win-x64.exe")]
    [InlineData("https://builds.dotnet.microsoft.com@evil.example/dotnet-sdk-win-x64.exe")]
    [InlineData("https://user:secret@builds.dotnet.microsoft.com/dotnet-sdk-win-x64.exe")]
    [InlineData("https://builds.dotnet.microsoft.com:8443/dotnet-sdk-win-x64.exe")]
    [InlineData("ftp://builds.dotnet.microsoft.com/dotnet-sdk-win-x64.exe")]
    [InlineData("file:///C:/dotnet-sdk-win-x64.exe")]
    [InlineData("dotnet-sdk-win-x64.exe")]
    public void Resolve_HostOutsideTheList_IsRefused(string url)
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles(winX64Url: url)));

        Assert.Equal(4, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellTheory]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.exe")]
    [InlineData("https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.exe")]
    [InlineData("https://download.visualstudio.microsoft.com/download/pr/x/dotnet-sdk-10.0.403-win-x64.exe")]
    [InlineData("https://BUILDS.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.exe")]
    public void Resolve_EachListedHost_IsAccepted(string url)
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles(winX64Url: url)));

        Assert.Equal(0, code);
        Assert.Equal(url, Lines(result)["url"]);
    }

    [WindowsPowerShellFact]
    public void Resolve_LatestSdkAbsentFromReleases_IsRefused()
    {
        var releases = Releases("10.0.999", Release("10.0.7", SdkBlock(Version, SdkFiles())));

        var (code, result) = Resolve(releases);

        Assert.Equal(3, code);
        Assert.Contains("10.0.999", result, StringComparison.Ordinal);
        Assert.DoesNotContain("url=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Resolve_NoLatestSdk_IsRefused()
    {
        var (code, _) = Resolve("{ \"releases\": [ " + Release("10.0.7", SdkBlock(Version, SdkFiles())) + " ] }");

        Assert.Equal(3, code);
    }

    [WindowsPowerShellFact]
    public void Resolve_NoWinX64Exe_IsRefused()
    {
        var files = "[" + FileEntry("dotnet-sdk-win-x64.zip", "win-x64", GoodUrl.Replace(".exe", ".zip", StringComparison.Ordinal), GoodHash) + "]";

        var (code, _) = Resolve(RealisticReleases(files));

        Assert.Equal(3, code);
    }

    [WindowsPowerShellTheory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz")]
    public void Resolve_BadHash_IsRefused(string hash)
    {
        var (code, result) = Resolve(RealisticReleases(SdkFiles(winX64Hash: hash.Length == 0 ? "-" : hash)));

        Assert.Equal(3, code);
        Assert.DoesNotContain("url=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellTheory]
    [InlineData("pas du json")]
    [InlineData("{ \"latest-sdk\": ")]
    [InlineData("")]
    public void Resolve_UnreadableJson_IsRefusedCleanly(string content)
    {
        var (code, result) = Resolve(content);

        Assert.Equal(2, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Resolve_MissingFile_IsRefusedCleanly()
    {
        var (code, result) = Run("-Resolve", "-ReleasesSource", _dir.Combine("n-existe-pas.json"));

        Assert.Equal(2, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Resolve_HttpSource_IsRefusedWithoutAnyRequest()
    {
        var (code, result) = Run("-Resolve", "-ReleasesSource", "http://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json");

        Assert.Equal(4, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    // --- -Verify ----------------------------------------------------------------------------------------------------------------

    private static string Sha512Of(string path) => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ProgramFilesPath(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), .. parts]);

    /// <summary>Un exécutable signé par Microsoft qui existe sur toute machine de développement (le SDK .NET), ou null.</summary>
    internal static string? MicrosoftSignedFile()
    {
        var path = ProgramFilesPath("dotnet", "dotnet.exe");
        return OperatingSystem.IsWindows() && File.Exists(path) ? path : null;
    }

    /// <summary>Un exécutable avec une signature valide d'un autre éditeur que Microsoft (Node.js, OpenJS Foundation ; Git, Johannes Schindelin), ou null.</summary>
    internal static string? OtherPublisherSignedFile()
    {
        string[] candidates = [ProgramFilesPath("nodejs", "node.exe"), ProgramFilesPath("Git", "cmd", "git.exe")];
        return OperatingSystem.IsWindows() ? candidates.FirstOrDefault(File.Exists) : null;
    }

    [MicrosoftSignedFileFact]
    public void Verify_FileSignedByMicrosoftWithTheRightHash_IsAccepted()
    {
        var signed = MicrosoftSignedFile()!;

        var (code, result) = Run("-Verify", signed, "-Sha512", Sha512Of(signed));

        Assert.Equal(0, code);
        Assert.Equal("ok", result.Trim());
    }

    [MicrosoftSignedFileFact]
    public void Verify_UppercaseExpectedHash_IsAccepted()
    {
        var signed = MicrosoftSignedFile()!;

        var (code, _) = Run("-Verify", signed, "-Sha512", Sha512Of(signed).ToUpperInvariant());

        Assert.Equal(0, code);
    }

    [MicrosoftSignedFileFact]
    public void Verify_WrongHash_IsRefusedEvenWhenSignedByMicrosoft()
    {
        var signed = MicrosoftSignedFile()!;

        var (code, result) = Run("-Verify", signed, "-Sha512", OtherHash);

        Assert.Equal(11, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Verify_UnsignedFileWithTheRightHash_IsRefused()
    {
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ pas un vrai programme, et sans signature"));

        var (code, result) = Run("-Verify", path, "-Sha512", Sha512Of(path));

        Assert.Equal(12, code);
        Assert.StartsWith("error=", result, StringComparison.Ordinal);
    }

    [MicrosoftSignedFileFact]
    public void Verify_SignedFileAlteredAfterSigning_IsRefused()
    {
        var signed = MicrosoftSignedFile()!;

        // Un octet de plus en fin de fichier : l'empreinte donnée est celle du fichier modifié, mais la signature ne tient plus.
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.Copy(signed, path);
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.WriteByte(0);
        }
        var (code, _) = Run("-Verify", path, "-Sha512", Sha512Of(path));

        Assert.Equal(12, code);
    }

    [OtherPublisherSignedFileFact]
    public void Verify_ValidSignatureFromAnotherPublisher_IsRefused()
    {
        var other = OtherPublisherSignedFile()!;

        var (code, result) = Run("-Verify", other, "-Sha512", Sha512Of(other));

        Assert.Equal(13, code);
        Assert.Contains("pas signé par Microsoft", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Verify_MissingFile_IsRefusedCleanly()
    {
        var (code, _) = Run("-Verify", _dir.Combine("absent.exe"), "-Sha512", GoodHash);

        Assert.Equal(10, code);
    }

    [WindowsPowerShellFact]
    public void Verify_MalformedExpectedHash_IsRefused()
    {
        var path = _dir.Combine("fichier.exe");
        File.WriteAllText(path, "x");

        var (code, _) = Run("-Verify", path, "-Sha512", "abc");

        Assert.Equal(11, code);
    }
}

/// <summary>Comme <see cref="WindowsPowerShellFactAttribute"/>, pour un test à paramètres.</summary>
public sealed class WindowsPowerShellTheoryAttribute : TheoryAttribute
{
    public WindowsPowerShellTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows PowerShell 5.1 n'existe que sous Windows.";
        }
    }
}

/// <summary>Un test qui a besoin d'un fichier signé par Microsoft (C:\Program Files\dotnet\dotnet.exe) : ignoré sans lui, avec la raison.</summary>
public sealed class MicrosoftSignedFileFactAttribute : FactAttribute
{
    public MicrosoftSignedFileFactAttribute()
    {
        if (DotnetSdkScriptTests.MicrosoftSignedFile() is null)
        {
            Skip = @"Pas de C:\Program Files\dotnet\dotnet.exe (signé par Microsoft) sur cette machine, ou pas Windows.";
        }
    }
}

/// <summary>Un test qui a besoin d'un fichier signé par un autre éditeur que Microsoft (node.exe ou git.exe) : ignoré sans lui, avec la raison.</summary>
public sealed class OtherPublisherSignedFileFactAttribute : FactAttribute
{
    public OtherPublisherSignedFileFactAttribute()
    {
        if (DotnetSdkScriptTests.OtherPublisherSignedFile() is null)
        {
            Skip = "Ni node.exe ni git.exe (signés par un autre éditeur que Microsoft) dans Program Files, ou pas Windows.";
        }
    }
}
