using System.Diagnostics;
using Trofeo.Distribution;

namespace Trofeo.Extensions.Updates;

/// <summary>
/// Une version plus récente d'un module, trouvée sur GitHub. <paramref name="Package"/> et <paramref name="Sha256"/> sont des fichiers de
/// <c>github.com</c>, dans le dépôt du module, nommés comme le veut la conception (<c>&lt;id&gt;-&lt;version&gt;.avmod</c> et son
/// <c>.sha256</c>). Rien n'est téléchargé à ce stade.
/// </summary>
/// <param name="Version">La version proposée (<c>1.2.0</c>, sans le <c>v</c> de l'étiquette).</param>
/// <param name="Notes">Les notes de la version, nettoyées et bornées (<see cref="ReleaseFeed.MaxNotesLength"/>).</param>
public sealed record ModuleUpdate(string Version, string Notes, Uri Package, Uri Sha256);

/// <summary>Le résultat d'une vérification : les propositions, et les modules dont GitHub n'a pas pu répondre.</summary>
/// <param name="Found">Les mises à jour proposées, par identifiant de module.</param>
/// <param name="Unknown">Les modules dont le dépôt n'a pas répondu comme prévu : on ne sait pas s'il y a une mise à jour.</param>
public sealed record ModuleUpdateCheck(IReadOnlyDictionary<string, ModuleUpdate> Found, IReadOnlySet<string> Unknown);

/// <summary>
/// Cherche sur GitHub, dépôt par dépôt, les nouvelles versions des modules installés. La réponse de GitHub et le dépôt nommé par un
/// module sont des données non fiables : le dépôt doit avoir la forme <c>propriétaire/nom</c> avant qu'une adresse soit construite, et les
/// deux fichiers proposés doivent être sur <c>github.com</c>, en https, dans <c>/&lt;dépôt&gt;/releases/download/&lt;étiquette&gt;/</c> du dépôt
/// attendu. Tout échec réseau (ou toute réponse illisible) laisse simplement sans proposition : aucune exception, aucun message.
/// <para>
/// Les règles : la version proposée est l'étiquette <c>v&lt;version&gt;</c> la plus récente, strictement plus récente que l'installée ;
/// une préversion n'est proposée que si la version installée en est une ; elle doit porter ses deux fichiers, sinon rien n'est proposé
/// (un dépôt qui publie ses fichiers après coup les verra à la vérification suivante). Aucune requête sans module portant un dépôt,
/// ni pour un module du mode développeur ou pas encore validé.
/// </para>
/// </summary>
/// <param name="checker">Le client des versions GitHub (celui des mises à jour du logiciel).</param>
/// <param name="time">Sert à ne pas redemander le même dépôt plus d'une fois par minute (GitHub limite les requêtes sans jeton).</param>
public sealed class ModuleUpdateChecker(UpdateChecker checker, TimeProvider time)
{
    /// <summary>La taille maximale du paquet d'une mise à jour : celle d'un paquet décompressé, plus une marge pour l'archive.</summary>
    public const long MaxPackageBytes = AvmodPackage.MaxFileBytes;

    /// <summary>Le nombre maximal de dépôts interrogés par vérification.</summary>
    public const int MaxRepositories = 20;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<PublishedRelease> Releases)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Les mises à jour proposées, par identifiant de module. Un module sans proposition n'y figure pas.
    /// Lève <see cref="OperationCanceledException"/> si <paramref name="ct"/> est annulé ; rien d'autre.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModuleUpdate>> CheckAsync(IReadOnlyList<CommunityEntry> entries, CancellationToken ct) =>
        (await CheckWithStatusAsync(entries, ct).ConfigureAwait(false)).Found;

    /// <summary>
    /// Comme <see cref="CheckAsync"/>, et dit aussi pour quels modules la réponse est restée inconnue (GitHub injoignable, limité ou
    /// illisible) : une proposition déjà connue pour eux ne doit pas disparaître sur une panne de réseau.
    /// </summary>
    public async Task<ModuleUpdateCheck> CheckWithStatusAsync(IReadOnlyList<CommunityEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var found = new Dictionary<string, ModuleUpdate>(StringComparer.Ordinal);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var candidates = entries.Select(UpdateSource.Of).Where(c => c is not null).Select(c => c!)
            .GroupBy(c => c.Repository, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRepositories);

        foreach (var group in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var releases = await FetchAsync(group.Key, ct).ConfigureAwait(false);
            if (releases is null)
            {
                // GitHub injoignable, limité ou illisible : pas de proposition pour ces modules, sans bruit.
                unknown.UnionWith(group.Select(c => c.Id));
                continue;
            }

            foreach (var candidate in group)
            {
                if (Propose(candidate, releases) is { } update)
                {
                    found[candidate.Id] = update;
                }
            }
        }

        return new ModuleUpdateCheck(found, unknown);
    }

    /// <summary>Un module installé et validé qui dit d'où viennent ses mises à jour (le moteur des mises à jour s'en sert aussi).</summary>
    internal sealed record UpdateSource(string Id, string Repository, AppVersion Installed)
    {
        public static UpdateSource? Of(CommunityEntry entry)
        {
            if (entry.FromDevFolder || entry.Approval is not { } approval || !ManifestReader.IsValidId(entry.Id))
            {
                return null;
            }

            // Le dépôt d'où viennent les mises à jour est celui de l'installation, enregistré dans la liste scellée : le manifeste du
            // dossier, lui, peut avoir été retouché depuis, et ne peut pas en ajouter un à un module installé sans dépôt.
            var repository = approval.Repository;
            if (!ReleaseFeed.IsRepository(repository))
            {
                return null;
            }

            AppVersion? installed = null;
            foreach (var text in new[] { approval.Version, entry.Manifest?.Version })
            {
                if (AppVersion.TryParse(text, out var parsed) && (installed is null || parsed.CompareTo(installed.Value) > 0))
                {
                    installed = parsed;
                }
            }

            return installed is { } version ? new UpdateSource(entry.Id, repository!, version) : null;
        }
    }

    private async Task<IReadOnlyList<PublishedRelease>?> FetchAsync(string repository, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(repository, out var cached) && time.GetUtcNow() - cached.At < CacheLifetime)
            {
                return cached.Releases;
            }
        }

        try
        {
            var releases = await checker.FetchAsync(repository, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _cache[repository] = (time.GetUtcNow(), releases);
            }

            return releases;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // UpdateException (réseau, limite, réponse illisible) comme toute exception inattendue d'un lecteur : ce dépôt devient « inconnu »,
            // les autres continuent. Seule une annulation demandée par l'appelant traverse.
            Trace.TraceInformation($"Mises à jour des modules : « {repository} » n'a pas répondu comme prévu : {ex.Message}");
            lock (_gate)
            {
                _cache.Remove(repository);
            }

            return null;
        }
    }

    private static ModuleUpdate? Propose(UpdateSource candidate, IReadOnlyList<PublishedRelease> releases)
    {
        var allowPrerelease = candidate.Installed.IsPrerelease;
        var best = releases
            .Where(r => r.Tag.StartsWith('v') && (allowPrerelease || (!r.Prerelease && !r.Version.IsPrerelease)))
            .MaxBy(r => r.Version, Comparer<AppVersion>.Default);
        if (best is null || best.Version.CompareTo(candidate.Installed) <= 0)
        {
            return null;
        }

        var version = best.Version.ToString();
        var packageName = $"{candidate.Id}-{version}.avmod";
        var package = best.Assets.FirstOrDefault(a => a.Name == packageName);
        var checksum = best.Assets.FirstOrDefault(a => a.Name == packageName + ".sha256");
        if (package is null || checksum is null || package.Size <= 0 || package.Size > MaxPackageBytes
            || ExpectedAsset(package.Url, candidate.Repository, best.Tag, package.Name) is not { } packageUri
            || ExpectedAsset(checksum.Url, candidate.Repository, best.Tag, checksum.Name) is not { } checksumUri)
        {
            return null;
        }

        return new ModuleUpdate(version, best.Notes, packageUri, checksumUri);
    }

    /// <summary>
    /// L'adresse si elle est exactement <c>https://github.com/&lt;dépôt&gt;/releases/download/&lt;étiquette&gt;/&lt;nom&gt;</c> (le dépôt sans
    /// distinction de casse, comme GitHub), sans identifiants, sans port, sans paramètres ; <c>null</c> sinon.
    /// </summary>
    private static Uri? ExpectedAsset(string url, string repository, string tag, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !VerifiedDownload.IsGitHubUrl(uri)
            || !uri.IsDefaultPort
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        var parts = uri.AbsolutePath.Split('/');
        var slash = repository.IndexOf('/');
        if (parts.Length != 7
            || parts[0].Length != 0
            || !string.Equals(Uri.UnescapeDataString(parts[1]), repository[..slash], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Uri.UnescapeDataString(parts[2]), repository[(slash + 1)..], StringComparison.OrdinalIgnoreCase)
            || parts[3] != "releases"
            || parts[4] != "download"
            || Uri.UnescapeDataString(parts[5]) != tag
            || Uri.UnescapeDataString(parts[6]) != name)
        {
            return null;
        }

        return uri;
    }
}
