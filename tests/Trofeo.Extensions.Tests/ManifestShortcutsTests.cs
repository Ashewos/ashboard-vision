using System.Text.Json.Nodes;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public class ManifestShortcutsTests
{
    /// <summary>Un manifeste qui demande le SDK 1.1 (le minimum pour déclarer des raccourcis).</summary>
    private const string Base = """
        {
          "id": "ashewos.github-repo",
          "name": "Dépôt GitHub",
          "description": "Les étoiles d'un dépôt.",
          "author": "Ashewos",
          "version": "1.2.3",
          "sdk": "1.1",
          "entry": "GithubRepo.dll",
          "minSize": { "columns": 3, "rows": 1 }
        }
        """;

    private static ManifestReadResult ReadWith(JsonNode? shortcuts, string sdk = "1.1")
    {
        var node = JsonNode.Parse(Base)!.AsObject();
        node["sdk"] = sdk;
        if (shortcuts is not null)
        {
            node["shortcuts"] = shortcuts;
        }

        return ManifestReader.Read(node.ToJsonString());
    }

    private static ManifestReadResult ReadWith(string shortcutsJson, string sdk = "1.1") => ReadWith(JsonNode.Parse(shortcutsJson), sdk);

    private static void AssertRefused(ManifestReadResult result, string messagePart)
    {
        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, error => error.Contains(messagePart, StringComparison.Ordinal));
    }

    [Fact]
    public void NoShortcuts_IsEmpty()
    {
        var result = ReadWith((JsonNode?)null, sdk: "1.0");

        Assert.Empty(result.Errors);
        Assert.Empty(result.Manifest!.Shortcuts);
    }

    [Fact]
    public void ValidShortcut_IsRead()
    {
        var result = ReadWith("""
            [
              { "id": "refresh", "name": "Rafraîchir", "keys": "Ctrl+Alt+Shift+R" },
              { "id": "open", "name": "Ouvrir la page" }
            ]
            """);

        Assert.Empty(result.Errors);
        var shortcuts = result.Manifest!.Shortcuts;
        Assert.Equal(2, shortcuts.Count);
        Assert.Equal(new ModuleShortcut("refresh", "Rafraîchir", "Ctrl+Alt+Shift+R"), shortcuts[0]);
        Assert.Equal(new ModuleShortcut("open", "Ouvrir la page", null), shortcuts[1]);
    }

    [Fact]
    public void TenShortcuts_AreAccepted_AndElevenAreRefused()
    {
        static JsonArray Many(int count)
        {
            var array = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                array.Add(new JsonObject { ["id"] = $"action-{i}", ["name"] = $"Action {i}" });
            }

            return array;
        }

        Assert.Equal(10, ReadWith(Many(10)).Manifest!.Shortcuts.Count);
        AssertRefused(ReadWith(Many(11)), "10 raccourcis au plus");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("é")]
    [InlineData("-start")]
    [InlineData("0123456789012345678901234567890123456789x")]
    public void BadId_IsRefused(string id)
    {
        var shortcuts = new JsonArray(new JsonObject { ["id"] = id, ["name"] = "Action" });

        AssertRefused(ReadWith(shortcuts), "shortcuts");
    }

    [Fact]
    public void MissingIdOrName_IsRefused()
    {
        AssertRefused(ReadWith("""[ { "name": "Sans identifiant" } ]"""), "shortcuts");
        AssertRefused(ReadWith("""[ { "id": "sans-nom" } ]"""), "shortcuts");
        AssertRefused(ReadWith("""[ { "id": "nom-vide", "name": " \u200e " } ]"""), "shortcuts");
    }

    [Fact]
    public void DuplicateId_IsRefused()
    {
        var result = ReadWith("""
            [
              { "id": "refresh", "name": "Rafraîchir" },
              { "id": "refresh", "name": "Encore" }
            ]
            """);

        AssertRefused(result, "Raccourci \"refresh\" : cet identifiant est utilisé par plusieurs raccourcis.");
    }

    [Theory]
    [InlineData("Ctrl+Alt+Nope")]
    [InlineData("R")]
    [InlineData("")]
    [InlineData("Ctrl+Alt+")]
    public void UnreadableKeys_IsRefused(string keys)
    {
        var shortcuts = new JsonArray(new JsonObject { ["id"] = "refresh", ["name"] = "Rafraîchir", ["keys"] = keys });

        AssertRefused(ReadWith(shortcuts), "shortcuts");
    }

    [Fact]
    public void ShortcutsNotAnArray_OrEntryNotAnObject_IsRefused()
    {
        AssertRefused(ReadWith("""{ "id": "refresh" }"""), "\"shortcuts\" : doit être un tableau");
        AssertRefused(ReadWith("""[ "refresh" ]"""), "shortcuts");
    }

    [Fact]
    public void NameWithControlOrBidiChars_IsCleaned()
    {
        var result = ReadWith("""[ { "id": "refresh", "name": "\u202eRafra\u0007\u200bîchir\n" } ]""");

        Assert.Empty(result.Errors);
        Assert.Equal("Rafraîchir", result.Manifest!.Shortcuts[0].Name);
    }

    [Fact]
    public void LongName_IsCutAtFortyCharacters()
    {
        var result = ReadWith($$"""[ { "id": "refresh", "name": "{{new string('x', 100)}}" } ]""");

        Assert.Empty(result.Errors);
        Assert.Equal(40, result.Manifest!.Shortcuts[0].Name.Length);
    }

    [Fact]
    public void ShortcutsWithSdk10_AreRefused()
    {
        var result = ReadWith("""[ { "id": "refresh", "name": "Rafraîchir" } ]""", sdk: "1.0");

        AssertRefused(result, "Ce module déclare des raccourcis : il doit demander le SDK 1.1 au moins.");
    }

    [Fact]
    public void EmptyShortcutsWithSdk10_IsAccepted()
    {
        var result = ReadWith("[]", sdk: "1.0");

        Assert.Empty(result.Errors);
        Assert.Empty(result.Manifest!.Shortcuts);
    }

    [Fact]
    public void Sdk11Module_IsCompatible()
    {
        Assert.Equal(new SdkVersion(1, 1), Compatibility.Current);
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 1)));
        Assert.Equal(CompatibilityKind.Compatible, Compatibility.Check(new SdkVersion(1, 0)));
        Assert.Equal(CompatibilityKind.NeedsNewerApp, Compatibility.Check(new SdkVersion(1, 2)));
    }
}
