using System.Net;
using System.Net.Http.Headers;

namespace Trofeo.Distribution;

/// <summary>
/// Interroge l'API publique de GitHub (sans jeton) pour connaître les versions publiées. Le client HTTP est
/// celui de l'appelant : c'est lui qui décide du délai d'attente.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    /// <summary>
    /// Les versions utilisables, dans l'ordre du flux (voir <see cref="ReleaseFeed.Parse"/>). Lève
    /// <see cref="UpdateException"/> si GitHub ne répond pas, répond autre chose que 200 (le message cite le code)
    /// ou répond autre chose qu'une liste de versions. Le corps d'une réponse d'erreur n'est jamais lu comme des versions.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRelease>> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.Url);
            // GitHub refuse les requêtes sans agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ashboard-vision", null));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException(DescribeStatus(response.StatusCode));
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ReleaseFeed.Parse(json);
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

    private static string DescribeStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? $"GitHub limite les vérifications pour l'instant (code {code})."
            : $"GitHub a répondu par une erreur (code {code}).";
    }
}
