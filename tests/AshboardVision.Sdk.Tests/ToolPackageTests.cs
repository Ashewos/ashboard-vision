using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;

namespace AshboardVision.Sdk.Tests;

/// <summary>Le paquet <c>AshboardVision.Sdk.Tool</c> : la commande, l'hôte de module embarqué avec ce dont il a besoin, et ce que son .nuspec annonce.</summary>
[Collection(PackedTool.Name)]
public sealed class ToolPackageTests(PackedTool packed)
{
    private const string Tool = PackedTool.ToolPathInPackage;

    // Les entrées que NuGet ajoute toujours à un paquet, sans rapport avec notre contenu.
    private static readonly string[] NuGetBoilerplate = ["[Content_Types].xml", "_rels/.rels"];

    private static readonly string[] ToolEntries =
    [
        Tool + "DotnetToolSettings.xml",
        Tool + "ashboard-module.dll",
        Tool + "ashboard-module.deps.json",
        Tool + "ashboard-module.runtimeconfig.json",
        Tool + "System.CommandLine.dll",
        Tool + "Trofeo.Extensions.dll",
        // Les dépendances d'Extensions : elles n'arrivent que par la découverte des DLL voisines (voir le .csproj de l'outil).
        Tool + "Trofeo.Control.dll",
        Tool + "Trofeo.Distribution.dll",
        Tool + "Trofeo.Modules.Agents.dll",
        Tool + "System.Security.Cryptography.ProtectedData.dll",
        Tool + "Trofeo.Core.dll",
        Tool + "Trofeo.ModuleKit.dll",
        Tool + "AshboardVision.Sdk.dll",
        Tool + "SkiaSharp.dll",
        Tool + "runtimes/win-x64/native/libSkiaSharp.dll",
    ];

    // L'hôte et tout ce qu'il charge : Trofeo.ModuleHost.exe seul ne démarre pas.
    private static readonly string[] HostEntries =
    [
        Tool + "host/Trofeo.ModuleHost.exe",
        Tool + "host/Trofeo.ModuleHost.dll",
        Tool + "host/Trofeo.ModuleHost.deps.json",
        Tool + "host/Trofeo.ModuleHost.runtimeconfig.json",
        Tool + "host/Trofeo.Extensions.dll",
        Tool + "host/Trofeo.Control.dll",
        Tool + "host/Trofeo.Distribution.dll",
        Tool + "host/Trofeo.Modules.Agents.dll",
        Tool + "host/System.Security.Cryptography.ProtectedData.dll",
        Tool + "host/Trofeo.Core.dll",
        Tool + "host/Trofeo.ModuleKit.dll",
        Tool + "host/AshboardVision.Sdk.dll",
        Tool + "host/SkiaSharp.dll",
        Tool + "host/runtimes/win-x64/native/libSkiaSharp.dll",
    ];

    [WindowsToolFact]
    public void Pack_ContainsTheCommandTheEmbeddedHostAndWhatTheyNeed()
    {
        var entries = Entries();

        Assert.Equal("AshboardVision.Sdk.Tool.1.0.0.nupkg", Path.GetFileName(packed.PackagePath));
        Assert.Contains("README.md", entries);
        Assert.Contains("AshboardVision.Sdk.Tool.nuspec", entries);
        Assert.All(ToolEntries, entry => Assert.Contains(entry, entries));
        Assert.All(HostEntries, entry => Assert.Contains(entry, entries));
        Assert.Contains("licenses/OFL-Manrope.txt", entries);

        // Le .exe de l'hôte n'est qu'à sa place, dans « host » (sans sa DLL il ne démarre pas), et rien ne pèse pour rien : ni symboles, ni
        // bibliothèques natives d'autres plateformes que Windows 64 bits.
        Assert.DoesNotContain(Tool + "Trofeo.ModuleHost.exe", entries);
        Assert.DoesNotContain(entries, entry => entry.Contains("/runtimes/", StringComparison.Ordinal) && !entry.Contains("/win-x64/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("/runtimes/", StringComparison.Ordinal) && entry.EndsWith(".pdb", StringComparison.Ordinal));
        Assert.True(new FileInfo(packed.PackagePath).Length < 40L * 1024 * 1024, "Le paquet de l'outil dépasse 40 Mo.");

        // Rien en dehors du dossier de l'outil, de son README, des licences et des fichiers de NuGet.
        Assert.All(
            entries.Where(entry => !NuGetBoilerplate.Contains(entry) && !entry.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)),
            entry => Assert.True(
                entry.StartsWith(Tool, StringComparison.Ordinal) || entry.StartsWith("licenses/", StringComparison.Ordinal)
                || entry is "README.md" or "AshboardVision.Sdk.Tool.nuspec",
                "Entrée inattendue : " + entry));
    }

    [WindowsToolFact]
    public void Pack_ToolSettings_DeclareTheCommand()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var stream = archive.GetEntry(Tool + "DotnetToolSettings.xml")!.Open();
        var command = XDocument.Load(stream).Descendants().Single(element => element.Name.LocalName == "Command");

        Assert.Equal("ashboard-module", (string?)command.Attribute("Name"));
        Assert.Equal("ashboard-module.dll", (string?)command.Attribute("EntryPoint"));
        Assert.Equal("dotnet", (string?)command.Attribute("Runner"));
    }

    [WindowsToolFact]
    public void Pack_Nuspec_DeclaresIdentityLicenceAndToolType_WithoutDependencies()
    {
        var metadata = Child(ReadNuspec().Root!, "metadata");

        Assert.Equal("AshboardVision.Sdk.Tool", Text(metadata, "id"));
        Assert.Equal("1.0.0", Text(metadata, "version"));
        Assert.Equal("Ashewos", Text(metadata, "authors"));
        Assert.Equal("README.md", Text(metadata, "readme"));

        var license = Child(metadata, "license");
        Assert.Equal("expression", (string?)license.Attribute("type"));
        Assert.Equal("GPL-3.0-or-later", license.Value);

        var repository = Child(metadata, "repository");
        Assert.Equal("git", (string?)repository.Attribute("type"));
        Assert.Equal("https://github.com/Ashewos/ashboard-vision", (string?)repository.Attribute("url"));

        Assert.Equal("DotnetTool", (string?)Child(Child(metadata, "packageTypes"), "packageType").Attribute("name"));

        // Un outil emporte tout ce dont il a besoin : aucune dépendance NuGet à résoudre (le banc d'essai marche hors ligne), ni nuget.org.
        Assert.DoesNotContain(metadata.Elements(), element => element.Name.LocalName == "dependencies");
    }

    [WindowsToolFact]
    public void Pack_CarriesSystemCommandLine_AsAStable2xRelease()
    {
        var dll = Path.Combine(packed.ToolFolder, "System.CommandLine.dll");
        var version = FileVersionInfo.GetVersionInfo(dll).ProductVersion!;

        Assert.StartsWith("2.0.", version, StringComparison.Ordinal);
        Assert.DoesNotContain("-", version.Split('+')[0], StringComparison.Ordinal); // pas de préversion
    }

    [WindowsToolFact]
    public void Pack_ToolDll_HasAssemblyVersion1_0_0_0()
    {
        var name = AssemblyName.GetAssemblyName(Path.Combine(packed.ToolFolder, "ashboard-module.dll"));

        Assert.Equal("ashboard-module", name.Name);
        Assert.Equal(new Version(1, 0, 0, 0), name.Version);
    }

    [WindowsToolFact]
    public void Pack_Readme_IsIncludedAndSaysItIsNotOnNuGetOrg()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
        var readme = reader.ReadToEnd();

        Assert.Contains("ashboard-module check", readme, StringComparison.Ordinal);
        Assert.Contains("ashboard-module pack", readme, StringComparison.Ordinal);
        Assert.Contains("nuget.org", readme, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Tool_Help_ListsBothCommandsInFrench()
    {
        var run = packed.Run("--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("check", run.Output, StringComparison.Ordinal);
        Assert.Contains("pack", run.Output, StringComparison.Ordinal);
        Assert.Contains("Affiche l'aide.", run.Output, StringComparison.Ordinal);
    }

    private string[] Entries()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        return [.. archive.Entries.Select(entry => entry.FullName)];
    }

    private XDocument ReadNuspec()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var stream = archive.GetEntry("AshboardVision.Sdk.Tool.nuspec")!.Open();
        return XDocument.Load(stream);
    }

    // Le .nuspec change d'espace de noms selon les fonctionnalités utilisées : on compare les noms locaux.
    private static XElement Child(XElement parent, string name) =>
        parent.Elements().Single(element => element.Name.LocalName == name);

    private static string Text(XElement parent, string name) => Child(parent, name).Value;
}
