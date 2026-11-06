using System.Net;
using System.Text.Json;
using AshboardVision.Sdk;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace GitHubRepo.Tests;

/// <summary>
/// Les tests du module « Dépôt GitHub ». Ils ne touchent jamais la vraie API : le module reçoit de fausses réponses
/// (<see cref="FakeGitHubHandler"/>). Le module actualise ses données en tâche de fond, comme dans le logiciel : les tests attendent donc
/// ce qu'ils vérifient (<see cref="WaitUntilAsync"/>) au lieu de supposer que c'est déjà là.
/// </summary>
public sealed class GitHubRepoTests
{
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Repo(string repo = "Ashewos/ashboard-vision") => Settings($$"""{ "repo": "{{repo}}" }""");

    private static bool HasDrawing(SKBitmap image) => image.Pixels.Any(pixel => pixel.Alpha > 0);

    /// <summary>Attend (cinq secondes au plus) qu'une condition devienne vraie : le module travaille sur un autre thread.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condition attendue n'est pas arrivée en cinq secondes.");
    }

    private static ModuleTestHost<TestableGitHubRepoModule> Host(FakeGitHubHandler github, JsonElement? settings, IReadOnlyList<ModulePermission>? granted = null)
    {
        TestableGitHubRepoModule.Handler.Value = github;
        return new ModuleTestHost<TestableGitHubRepoModule>(ManifestPath, settings, granted);
    }

    [Fact]
    public async Task GitHub_ShowsStarsAndLatestRelease()
    {
        var github = FakeGitHubHandler.Working();
        await using var host = Host(github, Repo());
        await host.StartAsync();

        // Tant que GitHub n'a pas répondu, rien n'est dessiné ; ensuite, la case montre le dépôt.
        await WaitUntilAsync(() =>
        {
            using var image = host.Render(new GridSize(3, 2), "classique");
            return HasDrawing(image);
        });

        Assert.Equal(ModuleHealth.Online, host.Health);
        Assert.Equal(
            ["https://api.github.com/repos/Ashewos/ashboard-vision", "https://api.github.com/repos/Ashewos/ashboard-vision/releases?per_page=100"],
            github.Requests.Select(request => request.Uri.ToString()));
    }

    [Fact]
    public void RepoStats_ReadStarsLatestReleaseAndDownloads()
    {
        var stats = RepoStats.Parse(Settings(FakeGitHubHandler.Repository), Settings(FakeGitHubHandler.Releases));

        Assert.Equal(1234, stats.Stars);
        Assert.Equal("1 234 étoiles", stats.StarsText);

        // La préversion est plus récente mais n'est pas « la dernière version ».
        Assert.Equal("v1.0.0 · 01/11/2026", stats.ReleaseText(TimeZoneInfo.Utc));

        // Les fichiers de toutes les versions publiées comptent (10 + 100 + 5 + 1000) ; le brouillon (999) ne compte pas.
        Assert.Equal(1115, stats.Downloads);
        Assert.Equal("1 115 téléchargements", stats.DownloadsText);
    }

    [Fact]
    public void RepoStats_NoRelease_SaysSo()
    {
        var stats = RepoStats.Parse(Settings("""{ "stargazers_count": 1 }"""), Settings("[]"));

        Assert.Equal("1 étoile", stats.StarsText);
        Assert.Equal("Aucune version publiée", stats.ReleaseText(TimeZoneInfo.Utc));
        Assert.Equal("0 téléchargement", stats.DownloadsText);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "repo": "" }""")]
    [InlineData("""{ "repo": "pas un dépôt" }""")]
    [InlineData("""{ "repo": "Ashewos/.." }""")]
    public async Task GitHub_NoRepository_NotConfigured(string settings)
    {
        var github = FakeGitHubHandler.Working();
        await using var host = Host(github, Settings(settings));

        await host.StartAsync();

        Assert.Equal(ModuleHealth.NotConfigured, host.Health);
        Assert.Contains("propriétaire/nom", host.StatusMessage);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task GitHub_InternetDenied_SaysSo()
    {
        var github = FakeGitHubHandler.Working();
        await using var host = Host(github, Repo(), granted: []);

        await host.StartAsync();

        Assert.Equal(ModuleHealth.Offline, host.Health);
        Assert.Contains("Internet", host.StatusMessage);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task GitHub_NetworkError_Offline()
    {
        await using var host = Host(FakeGitHubHandler.Unreachable(), Repo());

        await host.StartAsync();

        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        Assert.Equal("GitHub est injoignable pour le moment.", host.StatusMessage);
    }

    [Fact]
    public async Task GitHub_UnknownRepository_SaysSo()
    {
        await using var host = Host(FakeGitHubHandler.Failing(HttpStatusCode.NotFound), Repo("Ashewos/n-existe-pas"));

        await host.StartAsync();

        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        Assert.Contains("introuvable", host.StatusMessage);
    }

    [Fact]
    public async Task GitHub_WithToken_SendsItAndWithoutToken_DoesNot()
    {
        var withoutToken = FakeGitHubHandler.Working();
        await using (var host = Host(withoutToken, Repo()))
        {
            await host.StartAsync();
            await WaitUntilAsync(() => withoutToken.Requests.Count == 2);
        }

        var withToken = FakeGitHubHandler.Working();
        await using (var host = Host(withToken, Repo()))
        {
            host.Secrets.Set("token", "ghp_jeton-de-test");
            await host.StartAsync();
            await WaitUntilAsync(() => withToken.Requests.Count == 2);
        }

        Assert.All(withoutToken.Requests, request => Assert.Null(request.Authorization));
        Assert.All(withToken.Requests, request => Assert.Equal("Bearer ghp_jeton-de-test", request.Authorization));
    }

    [Fact]
    public async Task GitHub_RefreshesEveryTenMinutes()
    {
        var github = FakeGitHubHandler.Working();
        await using var host = Host(github, Repo());
        await host.StartAsync();
        await WaitUntilAsync(() => github.Requests.Count == 2);

        host.Time.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(100);
        Assert.Equal(2, github.Requests.Count);

        host.Time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => github.Requests.Count == 4);
    }
}
