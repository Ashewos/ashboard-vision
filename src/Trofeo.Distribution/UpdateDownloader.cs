using Trofeo.Core;

namespace Trofeo.Distribution;

/// <summary>L'installateur téléchargé et son empreinte SHA-256 (hexadécimal) vérifiée contre celle qui est publiée.</summary>
public sealed record DownloadedSetup(string Path, string Sha256);

/// <summary>
/// Télécharge l'installateur d'une version et vérifie son empreinte SHA-256 avant de le rendre. Rien n'est lancé ici,
/// et un fichier qui n'a pas passé toutes les vérifications n'existe sous son vrai nom à aucun moment. Le téléchargement
/// lui-même est celui de <see cref="VerifiedDownload"/>, que les mises à jour des modules utilisent aussi.
/// </summary>
/// <param name="http">
/// Le client HTTP de l'appelant : c'est lui qui suit les redirections (GitHub renvoie vers ses serveurs de fichiers)
/// et qui décide du délai d'attente.
/// </param>
/// <param name="idleTimeout">
/// Le silence maximal de la connexion, relancé à chaque bloc reçu (<see cref="DefaultIdleTimeout"/> par défaut) : le délai du client
/// HTTP ne couvre que l'arrivée des en-têtes, pas la lecture du corps, et un serveur qui se tait ne doit pas figer les mises à jour.
/// </param>
public sealed class UpdateDownloader(HttpClient http, TimeSpan? idleTimeout = null)
{
    /// <summary>Soixante secondes sans un octet : le téléchargement est abandonné.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = VerifiedDownload.DefaultIdleTimeout;

    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdleTimeout;

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
        var setupUrl = RequireGitHubUrl(release.SetupUrl);
        if (release.ChecksumUrl is null)
        {
            throw new UpdateException("Cette version ne publie pas d'empreinte de vérification : par prudence, elle n'est pas installée.");
        }

        var checksumUrl = RequireGitHubUrl(release.ChecksumUrl);
        if (release.SetupSize <= 0)
        {
            throw new UpdateException("La taille de l'installateur n'est pas connue : par prudence, il n'est pas installé.");
        }

        var setupPath = Path.Combine(targetDir, ProductIdentity.SetupFileName(release.Version.ToString()));
        try
        {
            PrepareDirectory(targetDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Le téléchargement a échoué ({ex.Message.TrimEnd('.')}).");
        }

        var plan = new VerifiedDownload.Plan(release.SetupSize, release.SetupSize, _idleTimeout, "l'installateur", "de l'installateur");
        var actual = await VerifiedDownload.RunAsync(
            http, setupUrl, checksumUrl, setupPath, plan,
            progress is null ? null : (received, _) => progress.Report((double)received / release.SetupSize), ct).ConfigureAwait(false);
        return new DownloadedSetup(setupPath, actual);
    }

    /// <summary>Seul github.com, en https, est une source acceptable (les redirections ensuite sont celles de GitHub).</summary>
    private static Uri RequireGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !VerifiedDownload.IsGitHubUrl(uri))
        {
            throw new UpdateException("L'installateur n'est pas publié sur github.com en https : par prudence, il n'est pas téléchargé.");
        }

        return uri;
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
}
