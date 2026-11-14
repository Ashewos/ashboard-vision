using System.IO.Compression;
using System.Xml.Linq;

namespace AshboardVision.Sdk.Tests;

/// <summary>Le contenu exact du paquet <c>AshboardVision.Sdk</c> et ce que son .nuspec annonce.</summary>
public sealed class PackageTests(PackedSdk packed) : IClassFixture<PackedSdk>
{
    // Les entrées que NuGet ajoute toujours à un paquet, sans rapport avec notre contenu.
    private static readonly string[] NuGetBoilerplate = ["[Content_Types].xml", "_rels/.rels"];

    private static readonly string[] ExpectedEntries =
    [
        "AshboardVision.Sdk.nuspec",
        "README.md",
        "lib/net10.0/AshboardVision.Sdk.dll",
        "lib/net10.0/AshboardVision.Sdk.xml",
        "lib/net10.0/Trofeo.ModuleKit.dll",
        "lib/net10.0/Trofeo.ModuleKit.xml",
    ];

    [Fact]
    public void Pack_ContainsSdkAndModuleKit()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        var entries = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => !NuGetBoilerplate.Contains(name) && !name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedEntries.Order(StringComparer.Ordinal), entries);
        Assert.Equal("AshboardVision.Sdk.1.1.0.nupkg", Path.GetFileName(packed.PackagePath));
    }

    [Fact]
    public void Pack_Nuspec_DeclaresIdentityAndLicence()
    {
        var nuspec = ReadNuspec();
        var metadata = Child(nuspec.Root!, "metadata");

        Assert.Equal("AshboardVision.Sdk", Text(metadata, "id"));
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
    public void Pack_Nuspec_DependsOnlyOnSkiaSharp()
    {
        var nuspec = ReadNuspec();
        var dependencies = Child(Child(nuspec.Root!, "metadata"), "dependencies")
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => ((string?)element.Attribute("id"), (string?)element.Attribute("version")))
            .ToArray();

        // ModuleKit est embarqué dans le paquet (lib/), pas référencé : aucune dépendance Trofeo.*.
        Assert.Equal([("SkiaSharp", "3.119.4")], dependencies);
        Assert.DoesNotContain(dependencies, dependency => dependency.Item1!.StartsWith("Trofeo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_Readme_IsIncludedAndSaysItIsNotOnNuGetOrg()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
        var readme = reader.ReadToEnd();

        Assert.Contains("AshboardVision.Sdk", readme, StringComparison.Ordinal);
        Assert.Contains("nuget.org", readme, StringComparison.Ordinal);
    }

    private XDocument ReadNuspec()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var stream = archive.GetEntry("AshboardVision.Sdk.nuspec")!.Open();
        return XDocument.Load(stream);
    }

    // Le .nuspec change d'espace de noms selon les fonctionnalités utilisées : on compare les noms locaux.
    private static XElement Child(XElement parent, string name) =>
        parent.Elements().Single(element => element.Name.LocalName == name);

    private static string Text(XElement parent, string name) => Child(parent, name).Value;
}
