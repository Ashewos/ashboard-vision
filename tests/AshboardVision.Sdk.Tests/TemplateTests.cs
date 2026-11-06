using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Xunit.Abstractions;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Le modèle <c>dotnet new ashboard-module</c>, de bout en bout : on installe le paquet du modèle, on crée un module, on le construit, on lance
/// ses tests, puis on le passe au banc d'essai (<c>ashboard-module check</c>, enfermé pour de vrai) installé comme outil.
/// <para>
/// L'essai est hermétique : il ne touche à rien des réglages ni des caches de l'utilisateur. Le modèle s'installe dans un dossier de modèles
/// temporaire (<c>--debug:custom-hive</c>), les paquets se restaurent dans un dossier temporaire (<c>NUGET_PACKAGES</c>), l'outil s'installe
/// dans un dossier temporaire (<c>--tool-path</c>), et un <c>nuget.config</c> temporaire n'a que deux sources locales, jamais nuget.org : le
/// dossier des paquets <b>fraîchement fabriqués</b> (réservé aux paquets <c>AshboardVision.*</c>, pour qu'une copie périmée de la même version 1.0.0 dans
/// le cache de l'utilisateur ne soit jamais servie) et le dossier des paquets de l'utilisateur, utilisé comme source locale pour SkiaSharp, xunit
/// et consorts (il faut qu'ils y soient déjà : c'est le cas après toute restauration de ce dépôt). Tout est supprimé à la fin, même en cas d'échec.
/// </para>
/// </summary>
[Collection(PackedTool.Name)]
[Trait("Windows", "Sandbox")]
[Trait("Duree", "Lent")]
public sealed class TemplateTests : IClassFixture<PackedSdk>, IClassFixture<PackedSdkTesting>, IClassFixture<PackedTemplates>, IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(6);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "atpl-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PackedTool _tool;
    private readonly PackedSdk _sdk;
    private readonly PackedSdkTesting _testing;
    private readonly PackedTemplates _templates;
    private readonly ITestOutputHelper _output;

    // L'identifiant du module généré, connu dès que l'essai le choisit : Dispose en déduit le conteneur d'essai à supprimer.
    private string? _containerId;

    public TemplateTests(PackedTool tool, PackedSdk sdk, PackedSdkTesting testing, PackedTemplates templates, ITestOutputHelper output)
    {
        _output = output;
        _tool = tool;
        _sdk = sdk;
        _testing = testing;
        _templates = templates;
    }

    public void Dispose()
    {
        // Filet de sécurité, comme celui de ToolTests : l'outil supprime déjà son conteneur d'essai, mais un essai qui échoue en route, ou dont
        // l'outil est tué sur délai, ne doit rien laisser dans le profil de l'utilisateur (profil AppContainer et son dossier). Jamais d'exception.
        if (_containerId is not null)
        {
            try
            {
                DeleteAppContainerProfile("ashboard-vision.check." + _containerId);
                var container = ContainerFolder(_containerId);
                if (Directory.Exists(container))
                {
                    Directory.Delete(container, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report("Le conteneur d'essai « ashboard-vision.check." + _containerId + " » n'a pas pu être supprimé : " + ex.Message);
            }
        }

        // Le dossier contient le module construit, les paquets restaurés et l'outil installé : ne rien laisser, même après un échec.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }

        Report("Le dossier temporaire « " + _root + " » n'a pas pu être supprimé (un fichier est encore tenu) : à effacer à la main.");
    }

    /// <summary>Écrit dans la sortie du test (Dispose ne doit jamais lever, même si le test est déjà fini).</summary>
    private void Report(string message)
    {
        try
        {
            _output.WriteLine(message);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [WindowsToolFact]
    public void NewModule_BuildsTestsAndChecks()
    {
        var id = "test.tpl" + Guid.NewGuid().ToString("N")[..10];
        _containerId = id;
        var author = "Moi \"le Grand\"";
        var paths = Prepare();
        var project = CreateModule(paths, "Essai", id, author);

        // Le manifeste porte l'identifiant et l'auteur donnés (le guillemet de l'auteur est échappé : le JSON reste valide), et rien d'autre à remplacer.
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "Essai", "manifest.json"))))
        {
            Assert.Equal(id, manifest.RootElement.GetProperty("id").GetString());
            Assert.Equal(author, manifest.RootElement.GetProperty("author").GetString());
            Assert.Equal("Essai", manifest.RootElement.GetProperty("name").GetString());
            Assert.Equal("Essai.dll", manifest.RootElement.GetProperty("entry").GetString());
            Assert.Empty(manifest.RootElement.GetProperty("permissions").EnumerateArray());
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories), file => new[] { "MonModule", "ModuleClassName", "ModuleNamespace" }.Any(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal)));

        // 3. Il se construit et ses tests passent (hors ligne : les paquets viennent de la source locale).
        var build = Dotnet(paths, project, "build", "-c", "Debug", "-nodeReuse:false", "-p:UseSharedCompilation=false", "--nologo");
        Assert.True(build.ExitCode == 0, build.Output);
        var test = Dotnet(paths, project, "test", "-c", "Debug", "-nodeReuse:false", "-p:UseSharedCompilation=false", "--nologo");
        Assert.True(test.ExitCode == 0, test.Output);

        // 4. Le banc d'essai (installé comme outil) le déclare compatible.
        var install2 = Dotnet(paths, paths.Work, "tool", "install", "AshboardVision.Sdk.Tool", "--version", "1.0.0", "--tool-path", paths.ToolPath, "--configfile", paths.NuGetConfig);
        Assert.True(install2.ExitCode == 0, install2.Output);

        var built = Path.Combine(project, "Essai", "bin", "Debug", "net10.0");
        Assert.True(File.Exists(Path.Combine(built, "manifest.json")), "manifest.json doit être copié à côté de la DLL du module.");
        var check = Run(paths, Path.Combine(paths.ToolPath, "ashboard-module.exe"), paths.Work, "check", built, "--out", paths.CheckOut, "--mesure", "3");

        Assert.True(check.ExitCode == 0, check.Output);
        Assert.Contains($"✔ Manifeste : {id} 1.0.0", check.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Démarrage enfermé", check.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Arrêt propre", check.Output, StringComparison.Ordinal);
        Assert.EndsWith("Compatible avec Ashboard Vision (SDK 1.0)." + Environment.NewLine, check.Output, StringComparison.Ordinal);

        // Une image par taille (minSize et idéal : le préréglage compact est la taille minimale) et par thème, avec du dessin dedans.
        var images = Directory.GetFiles(paths.CheckOut, "*.png");
        Assert.Equal(6, images.Length);
        foreach (var file in images)
        {
            using var image = SKBitmap.Decode(file);
            Assert.True(image.Pixels.Any(pixel => pixel.Alpha > 0), "L'image est vide : " + file);
        }

        // 5. Le banc d'essai ne laisse rien : ni conteneur d'essai, ni hôte de module lancé depuis l'outil.
        Assert.False(Directory.Exists(ContainerFolder(id)), "Le conteneur d'essai n'a pas été supprimé.");
        Assert.Empty(HostsRunningFrom(paths.ToolPath));
    }

    [WindowsToolFact]
    public void NewModule_WithADottedName_Builds()
    {
        // Un nom avec un point ne peut pas servir tel quel de nom de classe : le modèle en tire un identifiant C# valide (Moi_Essai) et un
        // espace de noms (Moi.Essai), le projet et la DLL gardent le nom donné.
        var paths = Prepare();
        var project = CreateModule(paths, "Moi.Essai", "test.tpl" + Guid.NewGuid().ToString("N")[..10], "Moi");

        var source = File.ReadAllText(Path.Combine(project, "Moi.Essai", "Moi.Essai.cs"));
        Assert.Contains("namespace Moi.Essai;", source, StringComparison.Ordinal);
        Assert.Contains("class Moi_Essai :", source, StringComparison.Ordinal);

        var build = Dotnet(paths, project, "build", "-c", "Debug", "-nodeReuse:false", "-p:UseSharedCompilation=false", "--nologo");
        Assert.True(build.ExitCode == 0, build.Output);
        Assert.True(File.Exists(Path.Combine(project, "Moi.Essai", "bin", "Debug", "net10.0", "Moi.Essai.dll")));
    }

    // ---- aides --------------------------------------------------------------------------------------------------------------------

    private sealed record Paths(string Feed, string Packages, string Hive, string ToolPath, string Work, string CheckOut, string NuGetConfig, string Home);

    /// <summary>Installe le paquet du modèle (dossier de modèles à part) puis crée un module de ce nom ; rend le dossier du projet créé.</summary>
    private string CreateModule(Paths paths, string name, string id, string author)
    {
        // 1. Le modèle s'installe depuis son paquet, dans un dossier de modèles à part.
        var install = Dotnet(paths, paths.Work, "new", "install", _templates.PackagePath, "--debug:custom-hive", paths.Hive);
        Assert.True(install.ExitCode == 0, install.Output);

        // 2. Un module en sort. « --author » est réservé par « dotnet new » (c'est un filtre de « dotnet new search ») : le paramètre s'écrit « --param:author ».
        var create = Dotnet(paths, paths.Work, "new", "ashboard-module", "-n", name, "--id", id, "--param:author", author, "--debug:custom-hive", paths.Hive);
        Assert.True(create.ExitCode == 0, create.Output);
        var project = Path.Combine(paths.Work, name);
        Assert.True(File.Exists(Path.Combine(project, name, name + ".csproj")), create.Output);
        Assert.True(File.Exists(Path.Combine(project, name + ".Tests", name + ".Tests.csproj")), create.Output);
        Assert.True(File.Exists(Path.Combine(project, "README.md")), create.Output);
        return project;
    }

    /// <summary>Les dossiers temporaires, le dossier des paquets fraîchement fabriqués et le <c>nuget.config</c> à deux sources locales.</summary>
    private Paths Prepare()
    {
        var paths = new Paths(
            Feed: Path.Combine(_root, "feed"),
            Packages: Path.Combine(_root, "pkg"),
            Hive: Path.Combine(_root, "hive"),
            ToolPath: Path.Combine(_root, "tool"),
            Work: Path.Combine(_root, "work"),
            CheckOut: Path.Combine(_root, "png"),
            NuGetConfig: Path.Combine(_root, "work", "nuget.config"),
            Home: Path.Combine(_root, "home"));
        foreach (var folder in new[] { paths.Feed, paths.Packages, paths.Hive, paths.Work, paths.Home })
        {
            Directory.CreateDirectory(folder);
        }

        foreach (var package in new[] { _sdk.PackagePath, _testing.PackagePath, _templates.PackagePath, _tool.PackagePath })
        {
            File.Copy(package, Path.Combine(paths.Feed, Path.GetFileName(package)));
        }

        // Deux sources locales, « <clear /> » pour écarter celles de l'utilisateur (nuget.org compris) : les paquets AshboardVision.* ne
        // viennent que du dossier frais, tout le reste du dossier des paquets de l'utilisateur.
        var userPackages = UserGlobalPackagesFolder();
        File.WriteAllText(paths.NuGetConfig, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{paths.Packages}" />
              </config>
              <packageSources>
                <clear />
                <add key="ashboard-frais" value="{paths.Feed}" />
                <add key="paquets-de-l-utilisateur" value="{userPackages}" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="ashboard-frais">
                  <package pattern="AshboardVision.*" />
                </packageSource>
                <packageSource key="paquets-de-l-utilisateur">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        return paths;
    }

    /// <summary>Le dossier des paquets de l'utilisateur, tel que NuGet le désigne (une source locale hors ligne pour le reste des paquets).</summary>
    private static string UserGlobalPackagesFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var start = new ProcessStartInfo("dotnet", ["nuget", "locals", "global-packages", "--list"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start);
        if (process is not null)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(TimeSpan.FromSeconds(60)) && process.ExitCode == 0)
            {
                // « global-packages: C:\Users\…\.nuget\packages\ »
                var line = output.Result.Split('\n').FirstOrDefault(l => l.StartsWith("global-packages:", StringComparison.Ordinal));
                if (line is not null && line["global-packages:".Length..].Trim() is { Length: > 0 } found)
                {
                    folder = found;
                }
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.True(Directory.Exists(folder), "Le dossier des paquets de l'utilisateur est introuvable (« " + folder + " ») : restaure d'abord la solution.");
        return Path.TrimEndingDirectorySeparator(folder);
    }

    private static CommandResult Dotnet(Paths paths, string workingDirectory, params string[] arguments) => Run(paths, "dotnet", workingDirectory, arguments);

    /// <summary>Lance un programme avec l'environnement de l'essai : caches, dossier personnel et modèles de <c>dotnet</c> dans le dossier temporaire.</summary>
    private static CommandResult Run(Paths paths, string fileName, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NUGET_PACKAGES"] = paths.Packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(paths.Home, "http-cache");
        start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(paths.Home, "plugins-cache");
        start.Environment["DOTNET_CLI_HOME"] = paths.Home;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        // Garde-fou : rien ne doit sortir vers le réseau (les sources sont des dossiers). Un accès involontaire à nuget.org ou ailleurs échouerait
        // franchement, vers un port où personne n'écoute, au lieu de réussir en silence quand l'ordinateur est connecté.
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:9";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9";
        start.Environment["NO_PROXY"] = "";

        using var process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " n'a pas démarré.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} a dépassé {CommandTimeout.TotalMinutes} minutes :\n{stdout.Result}{stderr.Result}");
        }

        // WaitForExit() sans délai : attend la fin de la lecture des sorties redirigées.
        process.WaitForExit();
        return new CommandResult(process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed record CommandResult(int ExitCode, string Output);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    private static string ContainerFolder(string id) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "ashboard-vision.check." + id);

    /// <summary>
    /// Les processus <c>Trofeo.ModuleHost</c> encore vivants dont le programme est sous <paramref name="folder"/> (l'hôte que l'outil embarque).
    /// On ne regarde que ceux-là : d'autres tests de la solution lancent leurs propres hôtes en parallèle. Laisse une seconde ou deux
    /// à un hôte qui finit de s'éteindre.
    /// </summary>
    private static List<string> HostsRunningFrom(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var found = new List<string>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            found.Clear();
            foreach (var process in Process.GetProcessesByName("Trofeo.ModuleHost"))
            {
                using (process)
                {
                    if (ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(path + " (pid " + process.Id + ")");
                    }
                }
            }

            if (found.Count == 0)
            {
                break;
            }

            Thread.Sleep(200);
        }

        return found;
    }

    /// <summary>Le chemin du programme d'un processus (droit de requête limité : marche aussi pour un processus enfermé), ou <c>null</c>.</summary>
    private static string? ImagePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
}
