using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trofeo.Core;

namespace Trofeo.Distribution;

/// <summary>Une version publiée sur GitHub, avec son installateur.</summary>
/// <param name="Prerelease">Le drapeau « pré-version » de GitHub (une bêta d'essai).</param>
/// <param name="SetupUrl">Où télécharger <c>Ashboard-Vision-Setup-&lt;version&gt;.exe</c>.</param>
/// <param name="SetupSize">La taille annoncée de l'installateur, en octets.</param>
/// <param name="ChecksumUrl">Le fichier <c>.sha256</c> de même nom, ou null s'il n'est pas publié.</param>
public sealed record GitHubRelease(
    AppVersion Version,
    bool Prerelease,
    string SetupUrl,
    long SetupSize,
    string? ChecksumUrl,
    DateTimeOffset PublishedAt);

/// <summary>Un fichier joint à une version GitHub.</summary>
/// <param name="Name">Le nom du fichier.</param>
/// <param name="Url">Où le télécharger (<c>browser_download_url</c>) : un texte de l'internet, à valider avant tout usage.</param>
/// <param name="Size">La taille annoncée, en octets (0 si GitHub ne la donne pas).</param>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>
/// Une version publiée dans le dépôt d'un module, quels que soient ses fichiers. Tout ce qu'elle porte vient de l'internet : le nom de
/// l'étiquette et les notes sont bornés en longueur (<see cref="ReleaseFeed.MaxTagLength"/>, <see cref="ReleaseFeed.MaxNotesLength"/>),
/// les notes sont débarrassées des caractères de contrôle et de ceux qui inversent le sens de lecture.
/// </summary>
/// <param name="Tag">L'étiquette telle que GitHub la donne (<c>v1.2.0</c>).</param>
/// <param name="Version">La version lue dans l'étiquette.</param>
/// <param name="Prerelease">Le drapeau « pré-version » de GitHub.</param>
/// <param name="Notes">Les notes de la version, nettoyées et tronquées ; vides s'il n'y en a pas.</param>
/// <param name="Assets">Les fichiers joints (<see cref="ReleaseFeed.MaxAssets"/> au plus).</param>
public sealed record PublishedRelease(
    string Tag,
    AppVersion Version,
    bool Prerelease,
    string Notes,
    IReadOnlyList<ReleaseAsset> Assets,
    DateTimeOffset PublishedAt);

/// <summary>Lit la réponse de l'API GitHub (<c>GET /repos/…/releases</c>).</summary>
public static partial class ReleaseFeed
{
    /// <summary>Les 30 dernières versions du dépôt (<see cref="ProductIdentity.Repository"/>).</summary>
    public const string Url = "https://api.github.com/repos/Ashewos/ashboard-vision/releases?per_page=30";

    /// <summary>Longueur maximale d'une étiquette de version ; une plus longue écarte la version.</summary>
    public const int MaxTagLength = 64;

    /// <summary>Longueur maximale des notes d'une version (le texte est coupé, avec « … »).</summary>
    public const int MaxNotesLength = 2000;

    /// <summary>Nombre maximal de fichiers lus par version.</summary>
    public const int MaxAssets = 100;

    /// <summary>Nombre maximal de versions lues dans une réponse.</summary>
    public const int MaxReleases = 30;

    private const int MaxAssetNameLength = 200;
    private const int MaxAssetUrlLength = 2048;

    // Un propriétaire GitHub (lettres, chiffres, tirets, 39 au plus, jamais de tiret en tête) et un nom de dépôt (lettres, chiffres, « - _ . »).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    /// <summary>
    /// Vrai si <paramref name="repository"/> a la forme <c>propriétaire/nom</c> d'un dépôt GitHub. Le nom d'un dépôt vient du manifeste
    /// d'un module : il n'entre dans une adresse qu'après ce contrôle.
    /// </summary>
    public static bool IsRepository(string? repository) =>
        repository is not null
        && RepositoryPattern().IsMatch(repository)
        && !repository.EndsWith("/.", StringComparison.Ordinal)
        && !repository.EndsWith("/..", StringComparison.Ordinal);

    /// <summary>L'adresse des 10 dernières versions de <paramref name="repository"/>. Lève <see cref="ArgumentException"/> si ce n'est pas un dépôt (<see cref="IsRepository"/>).</summary>
    public static string UrlFor(string repository)
    {
        if (!IsRepository(repository))
        {
            throw new ArgumentException("Le dépôt doit avoir la forme propriétaire/nom.", nameof(repository));
        }

        return $"https://api.github.com/repos/{repository}/releases?per_page=10";
    }

    /// <summary>
    /// Les versions d'un dépôt de module, dans l'ordre du flux : un brouillon et une étiquette qui n'est pas une version (ou trop longue)
    /// sont écartés, les fichiers joints sont gardés tels quels (c'est l'appelant qui cherche les siens). Lève
    /// <see cref="UpdateException"/> si le texte n'est pas une liste de versions.
    /// </summary>
    public static IReadOnlyList<PublishedRelease> ParseRepository(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateException("La réponse de GitHub n'est pas une liste de versions.");
            }

            var releases = new List<PublishedRelease>();
            foreach (var element in document.RootElement.EnumerateArray().Take(MaxReleases))
            {
                if (ReadPublished(element) is { } release)
                {
                    releases.Add(release);
                }
            }

            return releases;
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"La réponse de GitHub est illisible ({ex.Message}).");
        }
    }

    private static PublishedRelease? ReadPublished(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || GetBool(element, "draft"))
        {
            return null;
        }

        var tag = GetString(element, "tag_name");
        if (tag is not { Length: <= MaxTagLength } || !AppVersion.TryParse(tag, out var version))
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray().Take(MaxAssets))
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && GetString(asset, "name") is { Length: > 0 and <= MaxAssetNameLength } name
                    && GetString(asset, "browser_download_url") is { Length: > 0 and <= MaxAssetUrlLength } url)
                {
                    var size = asset.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes) ? bytes : 0;
                    assets.Add(new ReleaseAsset(name, url, size));
                }
            }
        }

        var publishedAt = element.TryGetProperty("published_at", out var published)
            && published.ValueKind == JsonValueKind.String
            && published.TryGetDateTimeOffset(out var date)
                ? date
                : default;

        return new PublishedRelease(tag, version, GetBool(element, "prerelease"), CleanNotes(GetString(element, "body")), assets, publishedAt);
    }

    /// <summary>Les notes telles qu'on peut les afficher : sans caractère de contrôle (hors saut de ligne et tabulation), sans marque de sens de lecture, coupées à <see cref="MaxNotesLength"/>.</summary>
    private static string CleanNotes(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var cleaned = new StringBuilder(Math.Min(raw.Length, MaxNotesLength + 1));
        foreach (var c in raw)
        {
            if (c == '\r' || IsDirectionMark(c) || (char.IsControl(c) && c is not ('\n' or '\t')))
            {
                continue;
            }

            cleaned.Append(c);
            if (cleaned.Length > MaxNotesLength)
            {
                break;
            }
        }

        if (cleaned.Length <= MaxNotesLength)
        {
            return cleaned.ToString();
        }

        // Coupé : on ne laisse pas la moitié d'une paire de substitution.
        var keep = MaxNotesLength - 1;
        if (char.IsHighSurrogate(cleaned[keep - 1]))
        {
            keep--;
        }

        return cleaned.ToString(0, keep) + "…";
    }

    private static bool IsDirectionMark(char c) =>
        c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// <summary>
    /// Les versions utilisables, dans l'ordre du flux. Un brouillon, une étiquette qui n'est pas une version
    /// et une version sans installateur sont écartés. Lève <see cref="UpdateException"/> si le texte n'est pas
    /// une liste de versions.
    /// </summary>
    public static IReadOnlyList<GitHubRelease> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateException("La réponse de GitHub n'est pas une liste de versions.");
            }

            var releases = new List<GitHubRelease>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadRelease(element) is { } release)
                {
                    releases.Add(release);
                }
            }

            return releases;
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"La réponse de GitHub est illisible ({ex.Message}).");
        }
    }

    private static GitHubRelease? ReadRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetBool(element, "draft")
            || !AppVersion.TryParse(GetString(element, "tag_name"), out var version)
            || !element.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var setupName = ProductIdentity.SetupFileName(version.ToString());
        var checksumName = setupName + ".sha256";
        string? setupUrl = null;
        long setupSize = 0;
        string? checksumUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(asset, "name");
            if (name == setupName)
            {
                setupUrl = GetString(asset, "browser_download_url");
                setupSize = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : 0;
            }
            else if (name == checksumName)
            {
                checksumUrl = GetString(asset, "browser_download_url");
            }
        }

        if (setupUrl is null)
        {
            return null;
        }

        var publishedAt = element.TryGetProperty("published_at", out var published)
            && published.ValueKind == JsonValueKind.String
            && published.TryGetDateTimeOffset(out var date)
                ? date
                : default;

        return new GitHubRelease(version, GetBool(element, "prerelease"), setupUrl, setupSize, checksumUrl, publishedAt);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
