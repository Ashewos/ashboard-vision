using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AshboardVision.Sdk.Tests;

/// <summary>Ce qu'une exécution de <c>ashboard-module</c> a donné.</summary>
public sealed record ToolRun(int ExitCode, string Output, TimeSpan Elapsed);

/// <summary>
/// Le paquet <c>AshboardVision.Sdk.Tool</c> (le banc d'essai), fabriqué par un vrai <c>dotnet pack</c> comme celui du SDK, puis dépouillé dans un
/// dossier temporaire : les essais lancent l'outil <b>tel qu'il est dans le paquet</b> (<c>dotnet ashboard-module.dll</c>), avec l'hôte de module qu'il
/// embarque, sous le profil de l'utilisateur comme un outil installé (là où un AppContainer ne lit rien sans qu'on le lui accorde).
/// Rien n'est fait avant le premier usage : sous Linux, où l'outil ne tourne pas, les tests qui s'en servent sont ignorés et rien n'est fabriqué.
/// Une seule instance sert à toutes les classes de la collection <see cref="Name"/>.
/// </summary>
public sealed class PackedTool : IDisposable
{
    /// <summary>Le nom de la collection de tests qui partagent le paquet de l'outil.</summary>
    public const string Name = "PackedTool";

    /// <summary>Où l'outil est rangé dans le paquet (une seule cible : <c>net10.0</c> ; pas de dossier par plateforme).</summary>
    public const string ToolPathInPackage = "tools/net10.0/any/";

    private readonly Lazy<Packed> _packed = new(() => new Packed(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<string> _extracted;

    public PackedTool()
    {
        _extracted = new Lazy<string>(Extract, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Le fichier <c>.nupkg</c> fabriqué.</summary>
    public string PackagePath => _packed.Value.PackagePath;

    /// <summary>Le dossier de l'outil, dépouillé du paquet (le contenu de <see cref="ToolPathInPackage"/>).</summary>
    public string ToolFolder => _extracted.Value;

    /// <summary>Lance <c>dotnet ashboard-module.dll</c> avec ces arguments et attend la fin (3 minutes au plus).</summary>
    public ToolRun Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ToolFolder,
        };
        start.ArgumentList.Add(Path.Combine(ToolFolder, "ashboard-module.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ashboard-module n'a pas démarré.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ashboard-module a dépassé 3 minutes : " + stdout.Result + stderr.Result);
        }

        return new ToolRun(process.ExitCode, stdout.Result + stderr.Result, clock.Elapsed);
    }

    private string Extract()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ashboard-sdk-tool-" + Guid.NewGuid().ToString("N"));
        using var archive = ZipFile.OpenRead(PackagePath);
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(ToolPathInPackage, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            var target = Path.Combine(folder, entry.FullName[ToolPathInPackage.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }

        return folder;
    }

    public void Dispose()
    {
        if (_extracted.IsValueCreated)
        {
            TryDelete(_extracted.Value);
        }

        if (_packed.IsValueCreated)
        {
            _packed.Value.Dispose();
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Un antivirus peut encore tenir un fichier ; le dossier temporaire sera nettoyé plus tard par le système.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Packed() : PackedSdk("AshboardVision.Sdk.Tool", "tools");
}

[CollectionDefinition(PackedTool.Name)]
public sealed class PackedToolCollection : ICollectionFixture<PackedTool>
{
}

/// <summary>Un test qui lance le banc d'essai (un outil Windows, qui enferme le module dans un AppContainer) : ignoré ailleurs que sous Windows.</summary>
public sealed class WindowsToolFactAttribute : FactAttribute
{
    public WindowsToolFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Le banc d'essai enferme les modules : il n'existe que sous Windows.";
        }
    }
}
