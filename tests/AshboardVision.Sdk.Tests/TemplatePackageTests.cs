using System.IO.Compression;
using System.Xml.Linq;

namespace AshboardVision.Sdk.Tests;

/// <summary>Le contenu exact du paquet <c>AshboardVision.Sdk.Templates</c> (le modèle <c>dotnet new ashboard-module</c>) et ce que son .nuspec annonce.</summary>
public sealed class TemplatePackageTests(PackedTemplates packed) : IClassFixture<PackedTemplates>
{
    // Les entrées que NuGet ajoute toujours à un paquet, sans rapport avec notre contenu.
    private static readonly string[] NuGetBoilerplate = ["[Content_Types].xml", "_rels/.rels"];

    private static readonly string[] ExpectedEntries =
    [
        "AshboardVision.Sdk.Templates.nuspec",
        "README.md",
        "content/ashboard-module/.gitignore",
        "content/ashboard-module/.template.config/template.json",
        "content/ashboard-module/MonModule.slnx",
        "content/ashboard-module/README.md",
        "content/ashboard-module/MonModule/MonModule.cs",
        "content/ashboard-module/MonModule/MonModule.csproj",
        "content/ashboard-module/MonModule/manifest.json",
        "content/ashboard-module/MonModule.Tests/MonModule.Tests.csproj",
        "content/ashboard-module/MonModule.Tests/MonModuleTests.cs",
    ];

    [Fact]
    public void Pack_ContainsTheTemplateTreeAndNothingCompiled()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        var entries = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => !NuGetBoilerplate.Contains(name) && !name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // L'arborescence du modèle est gardée (pas aplatie), les dossiers cachés partent aussi, et il n'y a ni lib/ ni DLL.
        Assert.Equal(ExpectedEntries.Order(StringComparer.Ordinal), entries);
        Assert.Equal("AshboardVision.Sdk.Templates.1.1.0.nupkg", Path.GetFileName(packed.PackagePath));
    }

    [Fact]
    public void Pack_Nuspec_DeclaresATemplatePackageWithoutDependencies()
    {
        using var archive = ZipFile.OpenRead(packed.PackagePath);
        using var stream = archive.GetEntry("AshboardVision.Sdk.Templates.nuspec")!.Open();
        var metadata = XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
        string Text(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;

        Assert.Equal("AshboardVision.Sdk.Templates", Text("id"));
        Assert.Equal("1.1.0", Text("version"));
        Assert.Equal("Ashewos", Text("authors"));
        Assert.Equal("README.md", Text("readme"));
        Assert.Equal("GPL-3.0-or-later", Text("license"));
        Assert.Equal("Template", metadata.Elements().Single(e => e.Name.LocalName == "packageTypes").Elements().Single().Attribute("name")?.Value);
        Assert.Equal(
            "https://github.com/Ashewos/ashboard-vision",
            metadata.Elements().Single(e => e.Name.LocalName == "repository").Attribute("url")?.Value);

        // Le modèle ne dépend d'aucun paquet : le projet généré référence AshboardVision.Sdk lui-même.
        Assert.DoesNotContain(metadata.Elements(), e => e.Name.LocalName == "dependencies");
    }
}
