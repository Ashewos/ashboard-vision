using System.Net;

namespace Trofeo.Distribution.Tests;

// Les versions d'un dépôt de module : mêmes erreurs que pour le logiciel, une réponse bornée, et aucune requête pour un dépôt douteux.
public sealed class UpdateCheckerRepositoryTests
{
    private const string OneRelease = """[ { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "body": "Première.", "assets": [] } ]""";

    [Fact]
    public async Task Fetch_Repository_AsksForThatRepositoryAndParsesTheFeed()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(OneRelease));
        var checker = new UpdateChecker(handler.Client());

        var releases = await checker.FetchAsync("alex/demo", CancellationToken.None);

        Assert.Equal("v1.0.0", Assert.Single(releases).Tag);
        Assert.Equal([new Uri("https://api.github.com/repos/alex/demo/releases?per_page=10")], handler.Requests);
    }

    [Fact]
    public async Task Fetch_Repository_SendsAUserAgent()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return FakeHandler.Text(OneRelease);
        });

        await new UpdateChecker(handler.Client()).FetchAsync("alex/demo", CancellationToken.None);

        Assert.NotEmpty(seen!.Headers.UserAgent);
    }

    [Theory]
    [InlineData("alex/..")]
    [InlineData("alex/demo?x=1")]
    [InlineData("")]
    public async Task Fetch_HostileRepository_ThrowsBeforeAnyRequest(string repository)
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(OneRelease));

        await Assert.ThrowsAsync<ArgumentException>(() => new UpdateChecker(handler.Client()).FetchAsync(repository, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetch_Repository_ForbiddenSaysGitHubLimitsChecks()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(OneRelease, HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => new UpdateChecker(handler.Client()).FetchAsync("alex/demo", CancellationToken.None));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Fetch_Repository_NetworkDown_ThrowsUpdateException()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Hôte inconnu."));

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateChecker(handler.Client()).FetchAsync("alex/demo", CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_Repository_GarbledBody_ThrowsUpdateException()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("<html>pas du json</html>"));

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateChecker(handler.Client()).FetchAsync("alex/demo", CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_Repository_ResponseOverTheLimit_Throws()
    {
        // Une réponse énorme (dépôt hostile, ou serveur fou) n'est pas lue en entier.
        var huge = "[" + new string(' ', (int)UpdateChecker.MaxRepositoryResponseBytes + 10) + "]";
        var handler = new FakeHandler(_ => FakeHandler.Text(huge));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => new UpdateChecker(handler.Client()).FetchAsync("alex/demo", CancellationToken.None));

        Assert.Contains("trop volumineuse", ex.Message);
    }

    [Fact]
    public async Task Fetch_Repository_Cancelled_IsACancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler(_ => FakeHandler.Text(OneRelease));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateChecker(handler.Client()).FetchAsync("alex/demo", cts.Token));
    }

    [Fact]
    public async Task Fetch_Software_StillAsksForTheSoftwareFeed()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("[]"));

        await new UpdateChecker(handler.Client()).FetchAsync(CancellationToken.None);

        Assert.Equal([new Uri(ReleaseFeed.Url)], handler.Requests);
    }
}
