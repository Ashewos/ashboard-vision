using System.Globalization;
using System.Text.Json;

namespace GitHubRepo;

/// <summary>
/// Ce que le module affiche d'un dépôt : ses étoiles, sa dernière version et le total des téléchargements des fichiers de toutes ses versions.
/// Cette classe ne connaît que le JSON de l'API GitHub et le français : elle ne dessine rien et n'appelle pas le réseau.
/// </summary>
internal sealed record RepoStats(int Stars, string? ReleaseTag, DateTimeOffset? ReleaseDate, long Downloads)
{
    /// <summary>Lit la réponse de <c>GET /repos/{dépôt}</c> et celle de <c>GET /repos/{dépôt}/releases</c> (les plus récentes d'abord).</summary>
    public static RepoStats Parse(JsonElement repository, JsonElement releases)
    {
        long downloads = 0;
        string? tag = null;
        DateTimeOffset? date = null;

        foreach (var release in releases.EnumerateArray())
        {
            // Un brouillon n'est visible que de son auteur : il ne compte pour rien.
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                downloads += asset.GetProperty("download_count").GetInt64();
            }

            // La « dernière version » est la plus récente qui n'est pas une préversion (comme sur la page du dépôt).
            if (tag is null && !release.GetProperty("prerelease").GetBoolean())
            {
                tag = release.GetProperty("tag_name").GetString();
                date = release.GetProperty("published_at").GetDateTimeOffset();
            }
        }

        return new RepoStats(repository.GetProperty("stargazers_count").GetInt32(), tag, date, downloads);
    }

    /// <summary>« 1 234 étoiles ».</summary>
    public string StarsText => Counted(Stars, "étoile");

    /// <summary>« 5 678 téléchargements ».</summary>
    public string DownloadsText => Counted(Downloads, "téléchargement");

    /// <summary>« v1.2.0 · 01/11/2026 » (la date dans le fuseau donné), ou « Aucune version publiée ».</summary>
    public string ReleaseText(TimeZoneInfo zone) =>
        ReleaseTag is null || ReleaseDate is not { } date
            ? "Aucune version publiée"
            : $"{ReleaseTag} · {TimeZoneInfo.ConvertTime(date, zone).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    // Les milliers sont séparés par une espace ordinaire : la police du thème n'a pas forcément l'espace fine insécable du français.
    private static string Counted(long count, string noun) =>
        count.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ') + " " + noun + (count > 1 ? "s" : "");
}
