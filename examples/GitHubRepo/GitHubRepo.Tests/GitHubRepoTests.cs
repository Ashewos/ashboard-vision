using System.Net;
using System.Text.Json;
using AshboardVision.Sdk;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace GitHubRepo.Tests;

/// <summary>
/// Les tests du module « Dépôt GitHub ». Ils ne touchent jamais la vraie API : le module reçoit de fausses réponses
/// (<see cref="FakeGitHubHandler"/>). Le module actualise ses données en tâche de fond, comme dans le logiciel : les tests attendent donc
/// ce qu'ils vérifient (<see cref="WaitUntilAsync"/>) au lieu de supposer que c'est déjà là.
/// </summary>
public sealed class GitHubRepoTests
{
    private const string RepositoryName = "Ashewos/ashboard-vision";

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Repo(string repo = RepositoryName) => Settings($$"""{ "repo": "{{repo}}" }""");

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

    private static string Page(HttpRequestMessage request) => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? "2" : "1";

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
            [$"https://api.github.com/repos/{RepositoryName}", $"https://api.github.com/repos/{RepositoryName}/releases?per_page=100&page=1"],
            github.Requests.Select(request => request.Uri.ToString()));

        // Ce que la case écrit, ligne par ligne, avec ce que ces réponses disent : le dépôt, les étoiles, la dernière version (la préversion
        // plus récente ne compte pas) avec sa date, puis le total des téléchargements (le brouillon ne compte pas). Render dessine ces lignes.
        var stats = RepoStats.Parse(Settings(FakeGitHubHandler.Repository), Settings(FakeGitHubHandler.Releases).EnumerateArray());
        var lines = GitHubRepoModule.Lines(RepositoryName, stats, Themes.Find("classique")!.Theme, TimeZoneInfo.Utc);
        Assert.Equal([RepositoryName, "1 234 étoiles", "v1.0.0 · 01/11/2026", "1 115 téléchargements"], lines.Select(line => line.Text));
    }

    [Fact]
    public void RepoStats_ReadStarsLatestReleaseAndDownloads()
    {
        var stats = RepoStats.Parse(Settings(FakeGitHubHandler.Repository), Settings(FakeGitHubHandler.Releases).EnumerateArray());

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
        var stats = RepoStats.Parse(Settings("""{ "stargazers_count": 1 }"""), Settings("[]").EnumerateArray());

        Assert.Equal("1 étoile", stats.StarsText);
        Assert.Equal("Aucune version publiée", stats.ReleaseText(TimeZoneInfo.Utc));
        Assert.Equal("0 téléchargement", stats.DownloadsText);
    }

    [Fact]
    public void RepoStats_TruncatedReleases_SaysSo()
    {
        var stats = RepoStats.Parse(Settings(FakeGitHubHandler.Repository), Settings(FakeGitHubHandler.Releases).EnumerateArray(), releasesCap: 500);

        Assert.Equal("1 115 téléchargements (500+ versions)", stats.DownloadsText);
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

    // Sans jeton : chaque refus de GitHub a sa phrase, qui dit quoi faire.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "introuvable")]
    [InlineData(HttpStatusCode.Unauthorized, "refuse ton jeton")]
    [InlineData(HttpStatusCode.Forbidden, "ajoute un jeton")]
    [InlineData(HttpStatusCode.TooManyRequests, "ajoute un jeton")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task GitHub_ErrorStatus_SaysWhatToDo(HttpStatusCode status, string expected)
    {
        await using var host = Host(FakeGitHubHandler.Failing(status), Repo());

        await host.StartAsync();

        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        Assert.Contains(expected, host.StatusMessage);
    }

    [Fact]
    public async Task GitHub_RateLimitedWithToken_DoesNotAskForOne_AndNeverShowsTheToken()
    {
        await using var host = Host(FakeGitHubHandler.Failing(HttpStatusCode.Forbidden), Repo());
        host.Secrets.Set("token", "ghp_jeton-de-test");

        await host.StartAsync();

        // Avec un jeton, un 403 est la limite ou un manque de droits : la phrase dit les deux, et ne recopie jamais le jeton.
        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        Assert.Contains("droits", host.StatusMessage);
        Assert.DoesNotContain("ghp_", host.StatusMessage);
    }

    // GitHub a répondu, mais pas comme prévu : un message à part, pas « injoignable ».
    [Theory]
    [InlineData("[]", FakeGitHubHandler.Releases)] // le dépôt n'est pas un objet
    [InlineData(FakeGitHubHandler.Repository, "{}")] // les versions ne sont pas un tableau
    [InlineData(FakeGitHubHandler.Repository, """[ { "tag_name": "v1" } ]""")] // une version sans ses champs
    [InlineData(FakeGitHubHandler.Repository, """[ { "tag_name": "v1", "draft": false, "prerelease": false, "published_at": null, "assets": [] } ]""")] // publiée sans date
    [InlineData(FakeGitHubHandler.Repository, "pas du JSON")]
    public async Task GitHub_UnexpectedJson_SaysTheAnswerIsUnreadable(string repository, string releases)
    {
        var github = FakeGitHubHandler.Answering(request => FakeGitHubHandler.Json(
            HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) ? releases : repository));
        await using var host = Host(github, Repo());

        await host.StartAsync();

        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        Assert.Contains("illisible", host.StatusMessage);
    }

    [Fact]
    public async Task GitHub_AfterAnOutage_TriesAgainAndComesBackOnline()
    {
        var github = FakeGitHubHandler.Failing(HttpStatusCode.TooManyRequests);
        await using var host = Host(github, Repo());
        await host.StartAsync();
        await WaitUntilAsync(() => host.Health == ModuleHealth.Offline);
        await Task.Delay(100); // la première actualisation a fini de se ranger : la minuterie peut en lancer une autre

        github.Respond = FakeGitHubHandler.Working().Respond;
        host.Time.Advance(TimeSpan.FromMinutes(10));

        await WaitUntilAsync(() => host.Health == ModuleHealth.Online);
        Assert.Null(host.StatusMessage);
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
        await Task.Delay(100); // on attend « pour de vrai » qu'une actualisation à tort ait eu le temps de partir
        Assert.Equal(2, github.Requests.Count);

        host.Time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => github.Requests.Count == 4);
    }

    [Fact]
    public async Task GitHub_MoreThanOnePageOfReleases_ReadsEveryPage()
    {
        // 100 versions sur la première page : il y en a peut-être d'autres, le module demande la page suivante (2 versions), qui n'est pas pleine : fin.
        var github = FakeGitHubHandler.Answering(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)
                ? FakeGitHubHandler.Json(HttpStatusCode.OK, FakeGitHubHandler.ReleasePage(Page(request) == "1" ? 100 : 2, "v"))
                : FakeGitHubHandler.Json(HttpStatusCode.OK, FakeGitHubHandler.Repository));
        await using var host = Host(github, Repo());

        await host.StartAsync();

        await WaitUntilAsync(() => github.Requests.Count == 3);
        await Task.Delay(100);
        Assert.Equal(
            [
                $"https://api.github.com/repos/{RepositoryName}",
                $"https://api.github.com/repos/{RepositoryName}/releases?per_page=100&page=1",
                $"https://api.github.com/repos/{RepositoryName}/releases?per_page=100&page=2",
            ],
            github.Requests.Select(request => request.Uri.ToString()));
        Assert.Equal(ModuleHealth.Online, host.Health);
    }

    [Fact]
    public async Task GitHub_EndlessReleases_StopsAtFivePages()
    {
        // Chaque page est pleine : le module s'arrête à 5 pages (le dépôt + 5 = 6 requêtes), pour rester sous la limite de GitHub sans jeton.
        var github = FakeGitHubHandler.Answering(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)
                ? FakeGitHubHandler.Json(HttpStatusCode.OK, FakeGitHubHandler.ReleasePage(100, "v"))
                : FakeGitHubHandler.Json(HttpStatusCode.OK, FakeGitHubHandler.Repository));
        await using var host = Host(github, Repo());

        await host.StartAsync();

        await WaitUntilAsync(() => github.Requests.Count == 6);
        await Task.Delay(100);
        Assert.Equal(6, github.Requests.Count);
        Assert.Equal(ModuleHealth.Online, host.Health);
    }

    [Fact]
    public async Task GitHub_DisposedDuringARefresh_EndsCleanly()
    {
        // La fermeture arrive pendant que GitHub n'a pas répondu : elle interrompt la requête, rend la main, et aucune exception ne reste
        // dans une tâche sans que personne la lise (l'hôte du logiciel s'arrêterait sur une exception non gérée d'un thread du module).
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var github = FakeGitHubHandler.Hanging();
            var host = Host(github, Repo());
            await host.StartAsync();
            await WaitUntilAsync(() => github.Requests.Count == 1); // la requête est partie et ne reviendra pas

            var closing = host.DisposeAsync().AsTask();
            Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(5))));
            await closing;

            // Une minuterie qui se déclenche après la fermeture ne relance rien.
            host.Time.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(100);
            Assert.Single(github.Requests);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }
}
