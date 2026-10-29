using System.Net;

namespace Trofeo.Distribution.Tests;

public sealed class UpdateCheckerTests
{
    private static string Exemple() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "releases-github.json"));

    [Fact]
    public async Task Fetch_Ok_ParsesTheFeed()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(Exemple()));
        var checker = new UpdateChecker(handler.Client());

        var releases = await checker.FetchAsync(CancellationToken.None);

        Assert.Equal(["1.1.0-beta.1", "1.0.0"], releases.Select(r => r.Version.ToString()));
        Assert.Equal([new Uri(ReleaseFeed.Url)], handler.Requests);
    }

    [Fact]
    public async Task Fetch_Forbidden_SaysGitHubLimitsChecks_AndCitesTheCode()
    {
        // Le corps d'une erreur ne passe jamais par la lecture des versions, même s'il y ressemble.
        var handler = new FakeHandler(_ => FakeHandler.Text(Exemple(), HttpStatusCode.Forbidden));
        var checker = new UpdateChecker(handler.Client());

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.FetchAsync(CancellationToken.None));

        Assert.Contains("GitHub limite les vérifications pour l'instant", ex.Message);
        Assert.Contains("403", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Fetch_OtherStatus_Throws_AndCitesTheCode(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(Exemple(), status));
        var checker = new UpdateChecker(handler.Client());

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.FetchAsync(CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task Fetch_NetworkDown_ThrowsUpdateException()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Hôte inconnu."));
        var checker = new UpdateChecker(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() => checker.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_GarbledBody_ThrowsUpdateException()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("<html>pas du json</html>"));
        var checker = new UpdateChecker(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() => checker.FetchAsync(CancellationToken.None));
    }
}
