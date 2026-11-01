using System.Text.Json.Nodes;
using AshboardVision.Sdk;
using Trofeo.Extensions;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

public class ManifestReaderTests
{
    private const string Valid = """
        {
          "id": "ashewos.github-repo",
          "name": "Dépôt GitHub",
          "description": "Les étoiles et les dernières versions d'un dépôt.",
          "author": "Ashewos",
          "version": "1.2.3-beta.1",
          "sdk": "1.0",
          "entry": "GithubRepo.dll",
          "permissions": ["reseau-local", "internet"],
          "repository": "Ashewos/github-repo",
          "minSize": { "columns": 3, "rows": 1 },
          "presets": { "compact": { "columns": 3, "rows": 1 }, "ideal": { "columns": 4, "rows": 2 } },
          "settings": [
            { "kind": "text", "key": "repo", "label": "Dépôt", "maxLength": 80 },
            { "kind": "number", "key": "days", "label": "Jours", "min": 1, "max": 30, "default": 7 }
          ],
          "icon": "icon.png"
        }
        """;

    /// <summary>Le manifeste valide, modifié par <paramref name="change"/> (un objet JSON modifiable).</summary>
    private static string Edit(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(Valid)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static ManifestReadResult ReadEdited(Action<JsonObject> change) => ManifestReader.Read(Edit(change));

    private static void AssertRefused(ManifestReadResult result, string messagePart)
    {
        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, error => error.Contains(messagePart, StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ValidManifest_ReturnsAllFields()
    {
        var result = ManifestReader.Read(Valid);

        Assert.Empty(result.Errors);
        var manifest = Assert.IsType<ModuleManifest>(result.Manifest);
        Assert.Equal("ashewos.github-repo", manifest.Id);
        Assert.Equal("Dépôt GitHub", manifest.Name);
        Assert.Equal("Les étoiles et les dernières versions d'un dépôt.", manifest.Description);
        Assert.Equal("Ashewos", manifest.Author);
        Assert.Equal("1.2.3-beta.1", manifest.Version);
        Assert.Equal(new SdkVersion(1, 0), manifest.Sdk);
        Assert.Equal("GithubRepo.dll", manifest.Entry);
        Assert.Equal([ModulePermission.LocalNetwork, ModulePermission.Internet], manifest.Permissions);
        Assert.Equal("Ashewos/github-repo", manifest.Repository);
        Assert.Equal(new GridSize(3, 1), manifest.MinSize);
        Assert.Equal(2, manifest.Presets.Count);
        Assert.Equal(new GridSize(3, 1), manifest.Presets[SizePreset.Compact]);
        Assert.Equal(new GridSize(4, 2), manifest.Presets[SizePreset.Ideal]);
        Assert.False(manifest.Presets.ContainsKey(SizePreset.Large));
        Assert.Equal(2, manifest.Settings.Fields.Count);
        Assert.IsType<TextField>(manifest.Settings.Fields[0]);
        var number = Assert.IsType<NumberField>(manifest.Settings.Fields[1]);
        Assert.Equal((1, 30, 7), (number.Min, number.Max, number.Default));
        Assert.Equal("icon.png", manifest.Icon);
    }

    [Fact]
    public void Read_MinimalManifest_UsesDefaults()
    {
        var result = ReadEdited(m =>
        {
            m.Remove("repository");
            m.Remove("presets");
            m.Remove("settings");
            m.Remove("icon");
            m.Remove("permissions");
        });

        var manifest = Assert.IsType<ModuleManifest>(result.Manifest);
        Assert.Null(manifest.Repository);
        Assert.Null(manifest.Icon);
        Assert.Empty(manifest.Permissions);
        Assert.Empty(manifest.Presets);
        Assert.Empty(manifest.Settings.Fields);
    }

    [Fact]
    public void ToDescriptor_UsesIdNameAndSizes()
    {
        var descriptor = ManifestReader.Read(Valid).Manifest!.ToDescriptor();

        Assert.Equal("ashewos.github-repo", descriptor.TypeId);
        Assert.Equal("Dépôt GitHub", descriptor.DisplayName);
        Assert.Equal(new GridSize(3, 1), descriptor.MinSize);
        Assert.Equal(new GridSize(4, 2), descriptor.Presets[SizePreset.Ideal]);
        Assert.False(descriptor.AcceptsCommands);
    }

    [Fact]
    public void Read_IdWithoutDot_IsRefused()
    {
        AssertRefused(ReadEdited(m => m["id"] = "clock"), "L'identifiant doit avoir la forme auteur.module");
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("a/b.c")]
    [InlineData("a\\b.c")]
    [InlineData("A.b")]
    [InlineData("a.b.c")]
    [InlineData("-a.b")]
    [InlineData("a-.b")]
    [InlineData("a.b ")]
    [InlineData("")]
    public void Read_IdWithPathCharacters_IsRefused(string id)
    {
        AssertRefused(ReadEdited(m => m["id"] = id), "L'identifiant doit avoir la forme auteur.module");
    }

    [Fact]
    public void Read_IdTooLong_IsRefused()
    {
        var id = new string('a', 20) + "." + new string('b', 20); // 41 caractères
        Assert.Equal(41, id.Length);
        AssertRefused(ReadEdited(m => m["id"] = id), "40 caractères");

        var ok = new string('a', 20) + "." + new string('b', 19); // 40
        Assert.NotNull(ReadEdited(m => m["id"] = ok).Manifest);
    }

    [Fact]
    public void Read_UnknownPermission_IsRefused()
    {
        var result = ReadEdited(m => m["permissions"] = new JsonArray("internet", "fichiers"));
        AssertRefused(result, "fichiers");
    }

    [Fact]
    public void Read_DuplicatePermissions_AreKeptOnce()
    {
        var result = ReadEdited(m => m["permissions"] = new JsonArray("internet", "internet"));
        Assert.Equal([ModulePermission.Internet], result.Manifest!.Permissions);
    }

    [Fact]
    public void Read_ThisComputerWithoutPortField_IsRefused()
    {
        var result = ReadEdited(m => m["permissions"] = new JsonArray("ce-pc"));
        AssertRefused(result, "Un module qui demande \"Cet ordinateur\" doit proposer un champ port dans ses réglages");
    }

    [Fact]
    public void Read_ThisComputerWithPortField_IsAccepted()
    {
        var result = ReadEdited(m =>
        {
            m["permissions"] = new JsonArray("ce-pc");
            m["settings"] = JsonNode.Parse("""[ { "kind": "port", "key": "port", "label": "Port", "default": 25565 } ]""");
        });

        var port = Assert.IsType<PortField>(Assert.Single(result.Manifest!.Settings.Fields));
        Assert.Equal(25565, port.Default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Read_PortFieldDefaultOutOfRange_IsRefused(int value)
    {
        var result = ReadEdited(m =>
            m["settings"] = JsonNode.Parse($$"""[ { "kind": "port", "key": "port", "label": "Port", "default": {{value}} } ]"""));
        AssertRefused(result, "port");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Read_PortFieldDefaultAtBounds_IsAccepted(int value)
    {
        var result = ReadEdited(m =>
            m["settings"] = JsonNode.Parse($$"""[ { "kind": "port", "key": "port", "label": "Port", "default": {{value}} } ]"""));
        Assert.NotNull(result.Manifest);
    }

    [Theory]
    [InlineData(0, 10, 11)]
    [InlineData(5, 10, 4)]
    [InlineData(10, 1, 5)]
    public void Read_NumberFieldDefaultOutsideMinMax_IsRefused(int min, int max, int value)
    {
        var result = ReadEdited(m =>
            m["settings"] = JsonNode.Parse($$"""[ { "kind": "number", "key": "nb", "label": "N", "min": {{min}}, "max": {{max}}, "default": {{value}} } ]"""));
        AssertRefused(result, "nb");
    }

    [Fact]
    public void Read_DuplicateSettingKeys_AreRefused()
    {
        var result = ReadEdited(m => m["settings"] = JsonNode.Parse(
            """[ { "kind": "text", "key": "a", "label": "A" }, { "kind": "toggle", "key": "a", "label": "B", "default": true } ]"""));
        AssertRefused(result, "\"a\"");
    }

    [Fact]
    public void Read_UnknownSettingKind_IsRefusedWithoutThrowing()
    {
        var result = ReadEdited(m => m["settings"] = JsonNode.Parse("""[ { "kind": "slider", "key": "a", "label": "A" } ]"""));
        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("bin/x.dll")]
    [InlineData("bin\\x.dll")]
    [InlineData("..\\x.dll")]
    [InlineData("C:\\x.dll")]
    [InlineData("x.exe")]
    [InlineData("x")]
    [InlineData("")]
    public void Read_EntryWithFolder_IsRefused(string entry)
    {
        AssertRefused(ReadEdited(m => m["entry"] = entry), "entry");
    }

    [Theory]
    [InlineData(17, 1)]
    [InlineData(1, 5)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Read_SizeOutsideGrid_IsRefused(int columns, int rows)
    {
        var result = ReadEdited(m => m["minSize"] = JsonNode.Parse($$"""{ "columns": {{columns}}, "rows": {{rows}} }"""));
        AssertRefused(result, "minSize");
    }

    [Fact]
    public void Read_PresetOutsideGrid_IsRefused()
    {
        var result = ReadEdited(m => m["presets"] = JsonNode.Parse("""{ "large": { "columns": 17, "rows": 4 } }"""));
        AssertRefused(result, "presets");
    }

    [Fact]
    public void Read_FullGridSize_IsAccepted()
    {
        var result = ReadEdited(m => m["minSize"] = JsonNode.Parse("""{ "columns": 16, "rows": 4 }"""));
        Assert.Equal(new GridSize(16, 4), result.Manifest!.MinSize);
    }

    [Fact]
    public void Read_TextLengths_AreLimited()
    {
        Assert.NotNull(ReadEdited(m => m["name"] = new string('n', 40)).Manifest);
        AssertRefused(ReadEdited(m => m["name"] = new string('n', 41)), "name");
        Assert.NotNull(ReadEdited(m => m["author"] = new string('a', 40)).Manifest);
        AssertRefused(ReadEdited(m => m["author"] = new string('a', 41)), "author");
        Assert.NotNull(ReadEdited(m => m["description"] = new string('d', 300)).Manifest);
        AssertRefused(ReadEdited(m => m["description"] = new string('d', 301)), "description");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("author")]
    [InlineData("version")]
    [InlineData("sdk")]
    [InlineData("entry")]
    [InlineData("minSize")]
    [InlineData("id")]
    public void Read_MissingRequiredField_IsRefused(string field)
    {
        AssertRefused(ReadEdited(m => m.Remove(field)), field);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("abc")]
    [InlineData("1.2.3\n")]
    [InlineData("1.2.3\r\n")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\t")]
    [InlineData("1.2.3-beta\u0000")]
    [InlineData("1.2.3\\..\\x")]
    public void Read_VersionNotSemVer_IsRefused(string version)
    {
        AssertRefused(ReadEdited(m => m["version"] = version), "version");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("v1.0")]
    public void Read_SdkVersionBadForm_IsRefused(string sdk)
    {
        AssertRefused(ReadEdited(m => m["sdk"] = sdk), "sdk");
    }

    [Theory]
    [InlineData("seulement-un-nom")]
    [InlineData("a/b/c")]
    [InlineData("a b/c")]
    public void Read_RepositoryBadForm_IsRefused(string repository)
    {
        AssertRefused(ReadEdited(m => m["repository"] = repository), "repository");
    }

    [Theory]
    [InlineData("img/icon.png")]
    [InlineData("icon.jpg")]
    [InlineData("..\\icon.png")]
    public void Read_IconNotAPngAtRoot_IsRefused(string icon)
    {
        AssertRefused(ReadEdited(m => m["icon"] = icon), "icon");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Read_InvalidJson_ReturnsOneError(string json)
    {
        var result = ManifestReader.Read(json);

        Assert.Null(result.Manifest);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Read_WrongTypes_ReturnErrorsWithoutThrowing()
    {
        var result = ReadEdited(m =>
        {
            m["name"] = 42;
            m["permissions"] = "internet";
            m["minSize"] = "3x1";
        });

        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Read_SeveralProblems_ReportsThemAll()
    {
        var result = ReadEdited(m =>
        {
            m["id"] = "clock";
            m["entry"] = "bin/x.dll";
        });

        Assert.Null(result.Manifest);
        Assert.True(result.Errors.Count >= 2);
    }
}
