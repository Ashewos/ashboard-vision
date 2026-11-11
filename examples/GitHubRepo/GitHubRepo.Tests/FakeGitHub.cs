using System.Net;
using System.Text;

namespace GitHubRepo.Tests;

/// <summary>
/// De fausses réponses de GitHub pour les tests : aucun test n'appelle la vraie API. Le module reçoit ce gestionnaire à la place de son vrai client
/// HTTP (voir <see cref="TestableGitHubRepoModule"/>), et le gestionnaire se souvient de ce qu'on lui a demandé. <see cref="Respond"/> peut changer
/// en cours de test (une panne, puis le retour à la normale).
/// </summary>
internal sealed class FakeGitHubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(Uri Uri, string? Authorization)> _requests = [];

    /// <summary>Un dépôt à 1 234 étoiles.</summary>
    public const string Repository = """{ "stargazers_count": 1234 }""";

    /// <summary>Quatre versions : une préversion récente, la v1.0.0 (la dernière version), un brouillon et la v0.8.0.</summary>
    public const string Releases = """
        [
          { "tag_name": "v2.0.0-beta.1", "draft": false, "prerelease": true,  "published_at": "2026-11-03T10:00:00Z", "assets": [ { "download_count": 10 } ] },
          { "tag_name": "v1.0.0",        "draft": false, "prerelease": false, "published_at": "2026-11-01T12:00:00Z", "assets": [ { "download_count": 100 }, { "download_count": 5 } ] },
          { "tag_name": "v0.9.0",        "draft": true,  "prerelease": false, "published_at": null,                   "assets": [ { "download_count": 999 } ] },
          { "tag_name": "v0.8.0",        "draft": false, "prerelease": false, "published_at": "2026-10-01T12:00:00Z", "assets": [ { "download_count": 1000 } ] }
        ]
        """;

    /// <summary>Comment le faux GitHub répond ; remplaçable à tout moment.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = respond;

    /// <summary>Ce que le module a demandé, dans l'ordre : l'adresse et l'en-tête <c>Authorization</c>.</summary>
    public IReadOnlyList<(Uri Uri, string? Authorization)> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    /// <summary>Une réponse JSON.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Une page de <paramref name="count"/> versions publiées, chacune avec un fichier téléchargé une fois.</summary>
    public static string ReleasePage(int count, string tagPrefix) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$"""{ "tag_name": "{{tagPrefix}}{{i}}", "draft": false, "prerelease": false, "published_at": "2026-11-01T12:00:00Z", "assets": [ { "download_count": 1 } ] }""")) + "]";

    /// <summary>GitHub qui répond à chaque requête avec <paramref name="answer"/>.</summary>
    public static FakeGitHubHandler Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new((request, _) => Task.FromResult(answer(request)));

    /// <summary>GitHub qui va bien : le dépôt et ses versions (une seule page, quelle que soit celle qu'on demande).</summary>
    public static FakeGitHubHandler Working() => Answering(Healthy);

    /// <summary>La réponse d'un GitHub qui va bien.</summary>
    public static HttpResponseMessage Healthy(HttpRequestMessage request) =>
        Json(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) ? Releases : Repository);

    /// <summary>GitHub qui répond toujours cette erreur.</summary>
    public static FakeGitHubHandler Failing(HttpStatusCode status) => Answering(_ => Json(status, """{ "message": "erreur" }"""));

    /// <summary>Un réseau coupé : la requête échoue avant toute réponse.</summary>
    public static FakeGitHubHandler Unreachable() => Answering(_ => throw new HttpRequestException("Réseau coupé."));

    /// <summary>Un réseau qui ne répond jamais : la requête dure jusqu'à ce que le module l'annule.</summary>
    public static FakeGitHubHandler Hanging() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("Inatteignable.");
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
        }

        return Respond(request, cancellationToken);
    }
}

/// <summary>
/// Le module tel que les tests le font tourner : le même, avec le client HTTP du test à la place du client du logiciel. <c>ModuleTestHost</c>
/// crée le module lui-même (constructeur sans paramètre), donc le gestionnaire ne peut pas passer par le constructeur : il passe par un
/// <see cref="AsyncLocal{T}"/>. La valeur posée par un test, avant <c>StartAsync</c>, suit ce test seul : deux tests qui tournent en même temps ne
/// se voient pas.
/// </summary>
internal sealed class TestableGitHubRepoModule : GitHubRepoModule
{
    public static readonly AsyncLocal<HttpMessageHandler?> Handler = new();

    internal override HttpClient CreateHttpClient() =>
        new(Handler.Value ?? throw new InvalidOperationException("Pose TestableGitHubRepoModule.Handler avant StartAsync."), disposeHandler: false);
}
