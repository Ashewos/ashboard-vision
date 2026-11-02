using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Trofeo.Distribution;

/// <summary>
/// Interroge l'API publique de GitHub (sans jeton) pour connaître les versions publiées. Le client HTTP est
/// celui de l'appelant : c'est lui qui décide du délai d'attente.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    /// <summary>
    /// La taille maximale d'une réponse lue pour un dépôt de module (4 Mio) : dix versions avec leurs notes en tiennent largement
    /// dans moins, et une réponse plus grosse n'est pas lue jusqu'au bout.
    /// </summary>
    public const long MaxRepositoryResponseBytes = 4L * 1024 * 1024;

    /// <summary>Le temps total laissé à GitHub pour répondre sur un dépôt de module, corps compris.</summary>
    private static readonly TimeSpan RepositoryFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Les versions utilisables, dans l'ordre du flux (voir <see cref="ReleaseFeed.Parse"/>). Lève
    /// <see cref="UpdateException"/> si GitHub ne répond pas, répond autre chose que 200 (le message cite le code)
    /// ou répond autre chose qu'une liste de versions. Le corps d'une réponse d'erreur n'est jamais lu comme des versions.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRelease>> FetchAsync(CancellationToken ct) =>
        ReleaseFeed.Parse(await GetJsonAsync(ReleaseFeed.Url, null, ct).ConfigureAwait(false));

    /// <summary>
    /// Les 10 dernières versions du dépôt d'un module (voir <see cref="ReleaseFeed.ParseRepository"/>), mêmes erreurs que
    /// <see cref="FetchAsync(CancellationToken)"/>, et une réponse de <see cref="MaxRepositoryResponseBytes"/> au plus. Lève
    /// <see cref="ArgumentException"/>, sans aucune requête, si <paramref name="repository"/> n'a pas la forme <c>propriétaire/nom</c>.
    /// </summary>
    public async Task<IReadOnlyList<PublishedRelease>> FetchAsync(string repository, CancellationToken ct) =>
        ReleaseFeed.ParseRepository(await GetJsonAsync(ReleaseFeed.UrlFor(repository), MaxRepositoryResponseBytes, ct).ConfigureAwait(false));

    private async Task<string> GetJsonAsync(string url, long? maxBytes, CancellationToken ct)
    {
        // Pour un dépôt de module, la lecture du corps est bornée en octets et en temps (le délai du client HTTP ne couvre que les en-têtes).
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxBytes is not null)
        {
            bounded.CancelAfter(RepositoryFetchTimeout);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub refuse les requêtes sans agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ashboard-vision", null));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.SendAsync(
                request, maxBytes is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, bounded.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException(DescribeStatus(response.StatusCode));
            }

            return maxBytes is { } limit
                ? await ReadLimitedAsync(response.Content, limit, bounded.Token).ConfigureAwait(false)
                : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"GitHub est injoignable ({ex.Message.TrimEnd('.')}).");
        }
        catch (IOException ex)
        {
            throw new UpdateException($"La réponse de GitHub a été coupée ({ex.Message.TrimEnd('.')}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Le délai du client HTTP est écoulé, ce n'est pas une annulation demandée.
            throw new UpdateException("GitHub ne répond pas.");
        }
    }

    /// <summary>Lit le corps en texte UTF-8, sans jamais en garder plus de <paramref name="limit"/> octets : au-delà, la réponse est refusée.</summary>
    private static async Task<string> ReadLimitedAsync(HttpContent content, long limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit)
        {
            throw TooLarge();
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static UpdateException TooLarge() => new("La réponse de GitHub est trop volumineuse : elle est ignorée.");

    private static string DescribeStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? $"GitHub limite les vérifications pour l'instant (code {code})."
            : $"GitHub a répondu par une erreur (code {code}).";
    }
}
