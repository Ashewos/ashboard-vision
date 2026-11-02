namespace Trofeo.Distribution.Tests;

// Les versions d'un dépôt nommé par un module : le dépôt et la réponse de GitHub sont des données non fiables.
public sealed class ReleaseFeedRepositoryTests
{
    private static string Release(
        string tag, bool prerelease = false, bool draft = false, string? body = "Notes.", string assets = "[]") =>
        $$"""
        {
          "tag_name": {{System.Text.Json.JsonSerializer.Serialize(tag)}},
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "published_at": "2026-11-02T10:00:00Z",
          "assets": {{assets}}
        }
        """;

    private static string Asset(string name, string url, long size = 1234) =>
        $$"""{ "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "size": {{size}}, "browser_download_url": {{System.Text.Json.JsonSerializer.Serialize(url)}} }""";

    [Theory]
    [InlineData("alex/demo")]
    [InlineData("Ashewos/ashboard-vision")]
    [InlineData("a-b/c_d.e")]
    public void UrlFor_ValidRepository_BuildsTheReleasesAddress(string repository)
    {
        Assert.Equal($"https://api.github.com/repos/{repository}/releases?per_page=10", ReleaseFeed.UrlFor(repository));
        Assert.True(ReleaseFeed.IsRepository(repository));
    }

    [Theory]
    [InlineData("")]
    [InlineData("alex")]
    [InlineData("alex/")]
    [InlineData("/demo")]
    [InlineData("alex/demo/extra")]
    [InlineData("alex/..")]
    [InlineData("alex/.")]
    [InlineData("../demo")]
    [InlineData("alex/demo?per_page=100")]
    [InlineData("alex/demo#x")]
    [InlineData("alex/de mo")]
    [InlineData("alex\\demo")]
    [InlineData("al.ex/demo")]
    [InlineData("-alex/demo")]
    [InlineData("alex/demo\n")]
    [InlineData("alex@evil.example/demo")]
    [InlineData("alex%2Fdemo/x")]
    public void UrlFor_HostileRepository_Throws(string repository)
    {
        Assert.False(ReleaseFeed.IsRepository(repository));
        Assert.Throws<ArgumentException>(() => ReleaseFeed.UrlFor(repository));
    }

    [Fact]
    public void UrlFor_Null_IsNotARepository()
    {
        Assert.False(ReleaseFeed.IsRepository(null));
    }

    [Fact]
    public void ParseRepository_ReadsTagNotesAssetsAndDate()
    {
        var json = $"[{Release("v1.2.0", body: "Ajoute une caméra.", assets: $"[{Asset("alex.demo-1.2.0.avmod", "https://github.com/alex/demo/releases/download/v1.2.0/alex.demo-1.2.0.avmod", 4096)}]")}]";

        var release = Assert.Single(ReleaseFeed.ParseRepository(json));

        Assert.Equal("v1.2.0", release.Tag);
        Assert.Equal("1.2.0", release.Version.ToString());
        Assert.False(release.Prerelease);
        Assert.Equal("Ajoute une caméra.", release.Notes);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(("alex.demo-1.2.0.avmod", "https://github.com/alex/demo/releases/download/v1.2.0/alex.demo-1.2.0.avmod", 4096L),
            (asset.Name, asset.Url, asset.Size));
    }

    [Fact]
    public void ParseRepository_KeepsReleasesWithoutAnInstaller_UnlikeParse()
    {
        // Parse (le logiciel) écarte une version sans installateur ; un dépôt de module n'en a jamais.
        var json = $"[{Release("v1.0.0")}]";

        Assert.Single(ReleaseFeed.ParseRepository(json));
        Assert.Empty(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void ParseRepository_DraftsAndIllegibleTagsAreDropped()
    {
        var json = $"[{Release("v1.0.0", draft: true)}, {Release("nightly")}, {Release("v1.1.0")}, 42, null, \"x\"]";

        Assert.Equal(["v1.1.0"], ReleaseFeed.ParseRepository(json).Select(r => r.Tag));
    }

    [Fact]
    public void ParseRepository_PrereleaseFlagOrPrereleaseVersion_IsAPrerelease()
    {
        var json = $"[{Release("v1.1.0-beta.1")}, {Release("v1.0.0", prerelease: true)}, {Release("v0.9.0")}]";

        Assert.Equal([true, true, false], ReleaseFeed.ParseRepository(json).Select(r => r.Prerelease || r.Version.IsPrerelease));
        Assert.True(ReleaseFeed.ParseRepository(json)[1].Prerelease);
    }

    [Fact]
    public void ParseRepository_TagLongerThanTheLimit_IsDropped()
    {
        var json = $"[{Release("v1.0.0-" + new string('a', 100))}, {Release("v1.0.0")}]";

        Assert.Equal(["v1.0.0"], ReleaseFeed.ParseRepository(json).Select(r => r.Tag));
    }

    [Fact]
    public void ParseRepository_LongNotes_AreCutAtTheLimit()
    {
        var json = $"[{Release("v1.0.0", body: new string('x', 50_000))}]";

        var notes = ReleaseFeed.ParseRepository(json).Single().Notes;

        Assert.True(notes.Length <= ReleaseFeed.MaxNotesLength);
        Assert.StartsWith("xxx", notes);
        Assert.EndsWith("…", notes);
    }

    [Fact]
    public void ParseRepository_Notes_LoseControlAndBidirectionalCharacters_KeepLineBreaks()
    {
        var json = $"[{Release("v1.0.0", body: "Ligne 1\r\nLigne 2\u0007\u202E\u2066fin\u001b[31m\ttab")}]";

        var notes = ReleaseFeed.ParseRepository(json).Single().Notes;

        Assert.Equal("Ligne 1\nLigne 2fin[31m\ttab", notes);
    }

    [Fact]
    public void ParseRepository_MissingOrNullBody_GivesEmptyNotes()
    {
        var json = $"[{Release("v1.0.0", body: null)}]";

        Assert.Equal("", ReleaseFeed.ParseRepository(json).Single().Notes);
    }

    [Fact]
    public void ParseRepository_AssetsOfTheWrongShape_AreIgnored()
    {
        var json = $"[{Release("v1.0.0", assets: $"[42, null, {{\"name\": 3}}, {{\"name\": \"a\"}}, {Asset("ok.avmod", "https://github.com/a/b/releases/download/v1.0.0/ok.avmod")}]")}]";

        var assets = ReleaseFeed.ParseRepository(json).Single().Assets;

        Assert.Equal(["ok.avmod"], assets.Select(a => a.Name));
    }

    [Fact]
    public void ParseRepository_MoreThanTheLimitOfAssets_KeepsTheFirstOnes()
    {
        var many = string.Join(",", Enumerable.Range(0, 500).Select(i => Asset($"a{i}.avmod", $"https://github.com/a/b/releases/download/v1.0.0/a{i}.avmod")));
        var json = $"[{Release("v1.0.0", assets: $"[{many}]")}]";

        Assert.Equal(ReleaseFeed.MaxAssets, ReleaseFeed.ParseRepository(json).Single().Assets.Count);
    }

    [Fact]
    public void ParseRepository_MoreThanTheLimitOfReleases_KeepsTheFirstOnes()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => Release($"v1.0.{i}"))) + "]";

        Assert.Equal(ReleaseFeed.MaxReleases, ReleaseFeed.ParseRepository(json).Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"texte\"")]
    public void ParseRepository_NotAList_Throws(string json)
    {
        Assert.Throws<UpdateException>(() => ReleaseFeed.ParseRepository(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[{")]
    [InlineData("pas du json")]
    public void ParseRepository_Garbled_ThrowsUpdateException(string json)
    {
        Assert.Throws<UpdateException>(() => ReleaseFeed.ParseRepository(json));
    }
}
