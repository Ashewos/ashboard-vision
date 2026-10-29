using System.Text.Json;
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

/// <summary>Lit la réponse de l'API GitHub (<c>GET /repos/…/releases</c>).</summary>
public static class ReleaseFeed
{
    /// <summary>Les 30 dernières versions du dépôt (<see cref="ProductIdentity.Repository"/>).</summary>
    public const string Url = "https://api.github.com/repos/Ashewos/ashboard-vision/releases?per_page=30";

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
                setupSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
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
