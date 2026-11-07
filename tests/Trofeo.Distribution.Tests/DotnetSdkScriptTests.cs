using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
        // La forme analysée (Uri.AbsoluteUri : hôte en minuscules, par exemple), pas la chaîne d'origine.
        Assert.Equal(new Uri(url).AbsoluteUri, Lines(result)["url"]);
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
        // Un serveur local qui note toute connexion : le script ne doit même pas le joindre.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (code, result) = Run("-Resolve", "-ReleasesSource", $"http://127.0.0.1:{port}/releases.json");

            Assert.Equal(4, code);
            Assert.Equal("error=releases.json : l'adresse doit être en https://.", result.Trim().TrimStart('\uFEFF'));
            Assert.False(listener.Pending(), "Le script a ouvert une connexion vers l'adresse refusée.");
        }
        finally
        {
            listener.Stop();
        }
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

    // dotnet.exe est signé par Microsoft (CN=.NET) mais n'est pas un setup du SDK : ses tests passent -ProductNamePrefix .NET (réservé aux tests).
    private (int ExitCode, string Result) VerifyDotnetExe(string path, string? sha512 = null, string productPrefix = ".NET") =>
        Run("-Verify", path, "-Sha512", sha512 ?? Sha512Of(path), "-ProductNamePrefix", productPrefix);

    [MicrosoftSignedFileFact]
    public void Verify_FileSignedByMicrosoftWithTheRightHash_IsAccepted()
    {
        var (code, result) = VerifyDotnetExe(MicrosoftSignedFile()!);

        Assert.Equal(0, code);
        Assert.Equal("ok", result.Trim().TrimStart('\uFEFF'));
    }

    [MicrosoftSignedFileFact]
    public void Verify_UppercaseExpectedHash_IsAccepted()
    {
        var signed = MicrosoftSignedFile()!;

        var (code, _) = VerifyDotnetExe(signed, Sha512Of(signed).ToUpperInvariant());

        Assert.Equal(0, code);
    }

    [MicrosoftSignedFileFact]
    public void Verify_WrongHash_IsRefusedEvenWhenSignedByMicrosoft()
    {
        var (code, result) = VerifyDotnetExe(MicrosoftSignedFile()!, OtherHash);

        Assert.Equal(11, code);
        Assert.Contains("n'est pas celle de releases.json", result, StringComparison.Ordinal);
    }

    [MicrosoftSignedFileFact]
    public void Verify_SignedByMicrosoftButNotADotnetSdkSetup_IsRefused()
    {
        // Sans -ProductNamePrefix, le défaut de l'installateur s'applique : « Microsoft .NET ». dotnet.exe s'appelle « .NET ».
        var signed = MicrosoftSignedFile()!;

        var (code, result) = Run("-Verify", signed, "-Sha512", Sha512Of(signed));

        Assert.Equal(14, code);
        Assert.Contains("pas un setup du SDK .NET", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Verify_UnsignedFileWithTheRightHash_IsRefused()
    {
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ pas un vrai programme, et sans signature"));

        var (code, result) = VerifyDotnetExe(path);

        Assert.Equal(12, code);
        Assert.Contains("type : None", result, StringComparison.Ordinal);
    }

    [MicrosoftSignedFileFact]
    public void Verify_SignedFileAlteredAfterSigning_IsRefusedForItsSignature()
    {
        // Un octet modifié au milieu du fichier : l'empreinte donnée est celle du fichier modifié, mais la signature ne tient plus.
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        var bytes = File.ReadAllBytes(MicrosoftSignedFile()!);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var (code, result) = VerifyDotnetExe(path);

        Assert.Equal(12, code);
        Assert.Contains("état : HashMismatch", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Verify_FileSignedByCatalogOnly_IsRefusedBecauseTheSignatureIsNotEmbedded()
    {
        // notepad.exe est signé par Microsoft, mais seulement par catalogue : Get-AuthenticodeSignature le dit « Valid ». On exige une signature intégrée.
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepad));

        var (code, result) = VerifyDotnetExe(notepad);

        Assert.Equal(12, code);
        Assert.Contains("type : Catalog", result, StringComparison.Ordinal);
    }

    [OtherPublisherSignedFileFact]
    public void Verify_ValidSignatureFromAnotherPublisher_IsRefused()
    {
        var other = OtherPublisherSignedFile()!;

        var (code, result) = VerifyDotnetExe(other);

        Assert.Equal(13, code);
        Assert.Contains("n'est pas celui de Microsoft Corporation", result, StringComparison.Ordinal);
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

    // --- le signataire : le sujet est lu dans son encodage DER, jamais découpé dans un texte ---------------------------------------

    [WindowsPowerShellTheory]
    [InlineData("CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")]
    public void CheckSubject_MicrosoftSubjects_AreAccepted(string subject)
    {
        var (code, _) = Run("-CheckSubject", subject);

        Assert.Equal(0, code);
    }

    [WindowsPowerShellTheory]
    // Le sujet piégé de la relecture : la chaîne « O=Microsoft Corporation » est DANS une valeur entre guillemets, la vraie organisation est Evil Ltd.
    [InlineData("CN=.NET, OU=\"x, O=Microsoft Corporation, y\", O=Evil Ltd, C=US")]
    [InlineData("OU=\"x, O=Microsoft Corporation, y\", O=Evil Ltd, C=US")]
    [InlineData("CN=.NET, O=\"Evil, O=Microsoft Corporation\"")]
    [InlineData("CN=.NET, O=Microsoft Corporation Fake, C=US")]
    [InlineData("CN=.NET, O=Microsoft Corp, C=US")]
    [InlineData("CN=.NET, o=microsoft corporation, C=US")]                              // la casse compte
    [InlineData("CN=.NET, O=Microsoft Corporation, O=Evil Ltd, C=US")]                  // deux organisations
    [InlineData("CN=.NET, O=Evil Ltd, O=Microsoft Corporation, C=US")]
    [InlineData("O=Microsoft Corporation, C=US")]                                       // pas de CN
    [InlineData("CN=Evil, O=Microsoft Corporation, C=US")]                              // CN autre
    [InlineData("CN=.NET, CN=Evil, O=Microsoft Corporation, C=US")]                     // deux CN
    [InlineData("CN=.NET, C=US")]                                                       // pas d'organisation
    public void CheckSubject_TrickySubjects_AreRefused(string subject)
    {
        var (code, result) = Run("-CheckSubject", subject);

        Assert.Equal(13, code);
        Assert.StartsWith("error=", result.TrimStart('\uFEFF'), StringComparison.Ordinal);
    }

    [WindowsPowerShellTheory]
    [InlineData("847DF6A78497943F27FC72EB93F9A637320A02B561D0A91B09E87A7807ED7C61")]   // Microsoft Root Certificate Authority 2011
    [InlineData("df545bf919a2439c36983b54cdfc903dfa4f37d3996d8d84b4c31eec6f3c163e")]   // Microsoft Root Certificate Authority 2010
    public void CheckRoot_KnownMicrosoftRoots_AreAccepted(string sha256)
    {
        var (code, _) = Run("-CheckRoot", sha256);

        Assert.Equal(0, code);
    }

    [WindowsPowerShellTheory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("885DE64C340E3EA70658F01E1145F957FCDA27AABEEA1AB9FAA9FDB0102D4077")]   // une autre racine de confiance de Windows (Microsoft Root Certificate Authority, 2001), hors liste
    [InlineData("abc")]
    public void CheckRoot_RootsOutsideTheList_AreRefused(string sha256)
    {
        var (code, result) = Run("-CheckRoot", sha256);

        Assert.Equal(15, code);
        Assert.StartsWith("error=", result.TrimStart('\uFEFF'), StringComparison.Ordinal);
    }

    // --- -Install : le fichier reste ouvert, sans partage en écriture, jusqu'à la fin du setup ----------------------------------

    [MicrosoftSignedFileFact]
    public void Install_VerifiedFile_IsLaunchedAndItsExitCodeReported()
    {
        // Un faux setup : dotnet.exe copié seul, sans ses bibliothèques. Il se lance, échoue (hostfxr introuvable) et rend un code de sortie.
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.Copy(MicrosoftSignedFile()!, path);

        var (code, result) = Run("-Install", path, "-Sha512", Sha512Of(path), "-ProductNamePrefix", ".NET");

        Assert.Equal(0, code);
        Assert.Matches(@"exitcode=-?\d+", result);
        File.Delete(path); // plus ouvert après la fin
    }

    [MicrosoftSignedFileFact]
    public void Install_WrongHash_IsNeverLaunched()
    {
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.Copy(MicrosoftSignedFile()!, path);

        var (code, result) = Run("-Install", path, "-Sha512", OtherHash, "-ProductNamePrefix", ".NET");

        Assert.Equal(11, code);
        Assert.DoesNotContain("exitcode=", result, StringComparison.Ordinal);
    }

    [WindowsPowerShellFact]
    public void Install_UnsignedFile_IsNeverLaunched()
    {
        var path = _dir.Combine("dotnet-sdk-win-x64.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ"));

        var (code, result) = Run("-Install", path, "-Sha512", Sha512Of(path));

        Assert.Equal(12, code);
        Assert.DoesNotContain("exitcode=", result, StringComparison.Ordinal);
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
