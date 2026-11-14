using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;

namespace AshboardVision.Sdk.Tests;

/// <summary>Le contenu exact du paquet <c>AshboardVision.Sdk.Testing</c> et ce que son .nuspec annonce.</summary>
public sealed class TestingPackageTests(PackedSdkTesting packed) : IClassFixture<PackedSdkTesting>
{
    // Les entrées que NuGet ajoute toujours à un paquet, sans rapport avec notre contenu.
    private static readonly string[] NuGetBoilerplate = ["[Content_Types].xml", "_rels/.rels"];

    private static readonly string[] ExpectedEntries =
    [
        "AshboardVision.Sdk.Testing.nuspec",
        "README.md",
        "lib/net10.0/AshboardVision.Sdk.Testing.dll",
        "lib/net10.0/AshboardVision.Sdk.Testing.xml",
        "lib/net10.0/Trofeo.Core.dll",
        "licenses/OFL-BarlowSemiCondensed.txt",
        "licenses/OFL-Manrope.txt",
        "licenses/OFL-PixelifySans.txt",
        "licenses/OFL-VT323.txt",
    ];

    [Fact]
    public void Pack_ContainsTestingAndCoreButNeitherModuleKitNorSdk()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        var entries = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => !NuGetBoilerplate.Contains(name) && !name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // ModuleKit et le SDK viennent du paquet AshboardVision.Sdk, dont celui-ci dépend : ils ne sont pas embarqués une seconde fois.
        Assert.Equal(ExpectedEntries.Order(StringComparer.Ordinal), entries);
        Assert.Equal("AshboardVision.Sdk.Testing.1.1.0.nupkg", Path.GetFileName(packed.PackagePath));
    }

    [Fact]
    public void Pack_Nuspec_DeclaresIdentityAndLicence()
    {
        var metadata = Child(ReadNuspec().Root!, "metadata");

        Assert.Equal("AshboardVision.Sdk.Testing", Text(metadata, "id"));
        Assert.Equal("1.1.0", Text(metadata, "version"));
        Assert.Equal("Ashewos", Text(metadata, "authors"));
        Assert.Equal("README.md", Text(metadata, "readme"));

        var license = Child(metadata, "license");
        Assert.Equal("expression", (string?)license.Attribute("type"));
        Assert.Equal("GPL-3.0-or-later", license.Value);

        var repository = Child(metadata, "repository");
        Assert.Equal("git", (string?)repository.Attribute("type"));
        Assert.Equal("https://github.com/Ashewos/ashboard-vision", (string?)repository.Attribute("url"));
    }

    [Fact]
    public void Pack_Nuspec_DependsOnTheSdkPackageAndFakeTimeProviderOnly()
    {
        var dependencies = Child(Child(ReadNuspec().Root!, "metadata"), "dependencies")
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => ((string?)element.Attribute("id"), (string?)element.Attribute("version")))
            .Order()
            .ToArray();

        // Core est embarqué (lib/), pas référencé : aucune dépendance Trofeo.*, ni celles de Core (ProtectedData). SkiaSharp arrive par le paquet du SDK.
        Assert.Equal([("AshboardVision.Sdk", "1.1.0"), ("Microsoft.Extensions.TimeProvider.Testing", "10.0.0")], dependencies);
        Assert.DoesNotContain(dependencies, dependency => dependency.Item1!.StartsWith("Trofeo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_Readme_IsIncludedAndSaysItIsNotOnNuGetOrg()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
        var readme = reader.ReadToEnd();

        Assert.Contains("ModuleTestHost", readme, StringComparison.Ordinal);
        Assert.Contains("nuget.org", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_TestingDll_HasAssemblyVersion1_0_0_0()
    {
        // La DLL du paquet elle-même (extraite dans un dossier temporaire), pas celle que la solution a construite.
        var directory = Path.Combine(Path.GetTempPath(), "ashboard-sdk-testing-dll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = ZipFile.OpenRead(packed.PackagePath);
            var dll = Path.Combine(directory, "AshboardVision.Sdk.Testing.dll");
            archive.GetEntry("lib/net10.0/AshboardVision.Sdk.Testing.dll")!.ExtractToFile(dll);

            var name = AssemblyName.GetAssemblyName(dll);
            Assert.Equal("AshboardVision.Sdk.Testing", name.Name);
            Assert.Equal(new Version(1, 0, 0, 0), name.Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private XDocument ReadNuspec()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var stream = archive.GetEntry("AshboardVision.Sdk.Testing.nuspec")!.Open();
        return XDocument.Load(stream);
    }

    // Le .nuspec change d'espace de noms selon les fonctionnalités utilisées : on compare les noms locaux.
    private static XElement Child(XElement parent, string name) =>
        parent.Elements().Single(element => element.Name.LocalName == name);

    private static string Text(XElement parent, string name) => Child(parent, name).Value;
}
