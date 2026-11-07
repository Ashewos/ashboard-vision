using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Trofeo.Distribution;

/// <summary>
/// Télécharge un fichier publié sur GitHub avec son <c>.sha256</c>, et ne le rend sous son vrai nom qu'une fois son empreinte vérifiée :
/// le logiciel s'en sert pour son installateur (<see cref="UpdateDownloader"/>), les mises à jour de modules pour leurs paquets. Rien n'est
/// lancé ici. Aucune adresse ne sort de github.com en https (les redirections ensuite sont celles de GitHub, suivies par le client HTTP
/// de l'appelant), et la taille est bornée : un fichier plus gros que prévu est abandonné en cours de route.
/// </summary>
public static partial class VerifiedDownload
{
    /// <summary>Soixante secondes sans un octet : le téléchargement est abandonné.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>La taille maximale d'un fichier quand l'appelant n'en choisit pas (256 Mio).</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private const int ChecksumFileLimit = 4096;

    // Une empreinte SHA-256 en hexadécimal : 64 caractères.
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();

    /// <summary>
    /// Télécharge l'empreinte puis le fichier, et rend le fichier à <paramref name="destination"/> (dossier créé au besoin) une fois son
    /// empreinte SHA-256 vérifiée ; rend l'empreinte, en hexadécimal. Lève <see cref="UpdateException"/> dans tous les cas d'échec, sans
    /// rien laisser sur le disque : adresse qui n'est pas en https sur github.com (avant toute requête), empreinte illisible, réponse
    /// d'erreur, connexion coupée ou muette, fichier plus gros que <paramref name="maxBytes"/>, empreinte différente. Une annulation
    /// lève <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="progress">Reçoit la part téléchargée en pour cent (0 à 100), quand la taille est connue.</param>
    /// <param name="maxBytes">La taille maximale du fichier.</param>
    /// <param name="idleTimeout">Le silence maximal de la connexion (<see cref="DefaultIdleTimeout"/> par défaut).</param>
    public static Task<string> DownloadAsync(
        HttpClient http, Uri asset, Uri sha256, string destination, IProgress<int>? progress, CancellationToken ct,
        long maxBytes = DefaultMaxBytes, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return RunAsync(
            http, asset, sha256, destination, new Plan(null, maxBytes, idleTimeout ?? DefaultIdleTimeout, "le fichier", "du fichier"),
            progress is null ? null : (received, total) => progress.Report(total is > 0 ? (int)Math.Min(100, received * 100 / total.Value) : 0), ct);
    }

    /// <summary>Ce que le téléchargement doit respecter : la taille exacte annoncée (ou, à défaut, un maximum), le silence toléré et de quoi parler dans les messages.</summary>
    internal sealed record Plan(long? ExactSize, long MaxBytes, TimeSpan IdleTimeout, string Subject, string OfSubject);

    /// <summary>Seul github.com, en https, est une source acceptable (les redirections ensuite sont celles de GitHub).</summary>
    public static bool IsGitHubUrl(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);

    internal static async Task<string> RunAsync(
        HttpClient http, Uri asset, Uri sha256, string destination, Plan plan, Action<long, long?>? onProgress, CancellationToken ct)
    {
        if (!IsGitHubUrl(asset) || !IsGitHubUrl(sha256))
        {
            throw new UpdateException("Le fichier n'est pas publié sur github.com en https : par prudence, il n'est pas téléchargé.");
        }

        var partPath = destination + ".part";
        var verified = false;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(destination)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var expected = await FetchChecksumAsync(http, sha256, plan, ct).ConfigureAwait(false);
            var actual = await DownloadFileAsync(http, asset, partPath, plan, onProgress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"L'empreinte {plan.OfSubject} téléchargé ne correspond pas à celle qui est publiée : il n'est pas installé.");
            }

            File.Move(partPath, destination, overwrite: true);
            verified = true;
            return actual;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Le téléchargement a échoué ({ex.Message.TrimEnd('.')}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Le téléchargement a échoué ({ex.Message.TrimEnd('.')}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Le délai du client HTTP est écoulé, ce n'est pas une annulation demandée.
            throw new UpdateException("Le téléchargement a pris trop de temps.");
        }
        finally
        {
            if (!verified)
            {
                DeleteQuietly(partPath);
                DeleteQuietly(destination);
            }
        }
    }

    private static async Task<string> FetchChecksumAsync(HttpClient http, Uri url, Plan plan, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(plan.IdleTimeout);
        var buffer = new byte[ChecksumFileLimit];
        var length = 0;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            EnsureSuccess(response);

            // « <empreinte>  <nom> » (ou l'empreinte seule) : quelques dizaines d'octets. On n'en lit pas plus.
            await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            int read;
            while (length < buffer.Length
                   && (read = await stream.ReadAsync(buffer.AsMemory(length), idle.Token).ConfigureAwait(false)) > 0)
            {
                length += read;
                idle.CancelAfter(plan.IdleTimeout);
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Interrupted();
        }

        var text = Encoding.UTF8.GetString(buffer, 0, length).TrimStart('﻿');
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var token = firstLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!ChecksumPattern().IsMatch(token))
        {
            throw new UpdateException($"Le fichier d'empreinte publié est illisible : {plan.Subject} n'est pas installé.");
        }

        return token;
    }

    private static async Task<string> DownloadFileAsync(
        HttpClient http, Uri url, string partPath, Plan plan, Action<long, long?>? onProgress, CancellationToken ct)
    {
        // Le silence est mesuré sur la connexion (en-têtes, puis chaque lecture du corps), pas sur l'écriture du disque ni sur la durée totale.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(plan.IdleTimeout);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var limit = plan.ExactSize ?? plan.MaxBytes;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > limit)
            {
                throw Oversized(plan);
            }

            var announced = plan.ExactSize ?? response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
            {
                idle.CancelAfter(plan.IdleTimeout);
                total += read;
                if (total > limit)
                {
                    throw Oversized(plan);
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onProgress?.Invoke(total, announced);
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Interrupted();
        }

        if (plan.ExactSize is { } exact && total != exact)
        {
            throw Oversized(plan);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static UpdateException Interrupted() => new("Le téléchargement s'est interrompu.");

    private static UpdateException Oversized(Plan plan) => plan.ExactSize is not null
        ? new UpdateException($"{Capitalize(plan.Subject)} téléchargé n'a pas la taille annoncée par la version : il n'est pas installé.")
        : new UpdateException($"{Capitalize(plan.Subject)} téléchargé est trop gros ({plan.MaxBytes / (1024 * 1024)} Mio au plus) : il n'est pas installé.");

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new UpdateException($"Le téléchargement a échoué (GitHub a répondu par le code {(int)response.StatusCode}).");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier qu'on ne peut pas supprimer ne change pas le résultat : l'échec est déjà annoncé.
        }
    }
}
