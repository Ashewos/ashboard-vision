using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Trofeo.Core;

namespace Trofeo.Distribution;

/// <summary>L'installateur téléchargé et son empreinte SHA-256 (hexadécimal) vérifiée contre celle qui est publiée.</summary>
public sealed record DownloadedSetup(string Path, string Sha256);

/// <summary>
/// Télécharge l'installateur d'une version et vérifie son empreinte SHA-256 avant de le rendre. Rien n'est lancé ici,
/// et un fichier qui n'a pas passé toutes les vérifications n'existe sous son vrai nom à aucun moment.
/// </summary>
/// <param name="http">
/// Le client HTTP de l'appelant : c'est lui qui suit les redirections (GitHub renvoie vers ses serveurs de fichiers)
/// et qui décide du délai d'attente.
/// </param>
/// <param name="idleTimeout">
/// Le silence maximal de la connexion, relancé à chaque bloc reçu (<see cref="DefaultIdleTimeout"/> par défaut) : le délai du client
/// HTTP ne couvre que l'arrivée des en-têtes, pas la lecture du corps, et un serveur qui se tait ne doit pas figer les mises à jour.
/// </param>
public sealed partial class UpdateDownloader(HttpClient http, TimeSpan? idleTimeout = null)
{
    /// <summary>Soixante secondes sans un octet : le téléchargement est abandonné.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdleTimeout;

    // Une empreinte SHA-256 en hexadécimal : 64 caractères.
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();

    private const int ChecksumFileLimit = 4096;

    /// <summary>
    /// Vide <paramref name="targetDir"/> (seulement les fichiers qu'il contient), télécharge l'empreinte puis
    /// l'installateur, et rend le chemin de ce dernier une fois la taille et l'empreinte vérifiées. Lève
    /// <see cref="UpdateException"/> dans tous les cas d'échec, sans rien laisser dans le dossier : adresse qui n'est
    /// pas en https vers github.com, empreinte non publiée ou illisible, réponse d'erreur, connexion coupée, taille
    /// différente de celle de la version, empreinte différente. Une annulation lève <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="progress">Reçoit la part téléchargée, de 0 à 1.</param>
    public async Task<string> DownloadAsync(GitHubRelease release, string targetDir, IProgress<double>? progress, CancellationToken ct) =>
        (await DownloadVerifiedAsync(release, targetDir, progress, ct).ConfigureAwait(false)).Path;

    /// <summary>Comme <see cref="DownloadAsync"/>, et rend aussi l'empreinte vérifiée : de quoi s'en assurer de nouveau juste avant le lancement.</summary>
    public async Task<DownloadedSetup> DownloadVerifiedAsync(GitHubRelease release, string targetDir, IProgress<double>? progress, CancellationToken ct)
    {
        RequireGitHubUrl(release.SetupUrl);
        if (release.ChecksumUrl is null)
        {
            throw new UpdateException("Cette version ne publie pas d'empreinte de vérification : par prudence, elle n'est pas installée.");
        }

        RequireGitHubUrl(release.ChecksumUrl);
        if (release.SetupSize <= 0)
        {
            throw new UpdateException("La taille de l'installateur n'est pas connue : par prudence, il n'est pas installé.");
        }

        var setupPath = Path.Combine(targetDir, ProductIdentity.SetupFileName(release.Version.ToString()));
        var partPath = setupPath + ".part";
        var verified = false;
        try
        {
            PrepareDirectory(targetDir);
            var expected = await FetchChecksumAsync(release.ChecksumUrl, ct).ConfigureAwait(false);
            var actual = await DownloadSetupAsync(release, partPath, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("L'empreinte de l'installateur téléchargé ne correspond pas à celle qui est publiée : il n'est pas installé.");
            }

            File.Move(partPath, setupPath, overwrite: true);
            verified = true;
            return new DownloadedSetup(setupPath, actual);
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
                DeleteQuietly(setupPath);
            }
        }
    }

    /// <summary>Seul github.com, en https, est une source acceptable (les redirections ensuite sont celles de GitHub).</summary>
    private static void RequireGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("L'installateur n'est pas publié sur github.com en https : par prudence, il n'est pas téléchargé.");
        }
    }

    private static void PrepareDirectory(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new UpdateException("Le dossier de téléchargement n'est pas défini.");
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(targetDir))
        {
            File.Delete(file);
        }
    }

    private async Task<string> FetchChecksumAsync(string url, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);
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
                idle.CancelAfter(_idleTimeout);
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
            throw new UpdateException("Le fichier d'empreinte publié est illisible : l'installateur n'est pas installé.");
        }

        return token;
    }

    private async Task<string> DownloadSetupAsync(GitHubRelease release, string partPath, IProgress<double>? progress, CancellationToken ct)
    {
        // Le silence est mesuré sur la connexion (en-têtes, puis chaque lecture du corps), pas sur l'écriture du disque ni sur la durée totale.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            using var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            EnsureSuccess(response);

            await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
            {
                idle.CancelAfter(_idleTimeout);
                total += read;
                if (total > release.SetupSize)
                {
                    throw SizeMismatch();
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report((double)total / release.SetupSize);
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Interrupted();
        }

        if (total != release.SetupSize)
        {
            throw SizeMismatch();
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static UpdateException Interrupted() => new("Le téléchargement s'est interrompu.");

    private static UpdateException SizeMismatch() =>
        new("L'installateur téléchargé n'a pas la taille annoncée par la version : il n'est pas installé.");

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
