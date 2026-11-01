namespace Trofeo.Distribution;

/// <summary>La version proposée. <paramref name="IsDowngrade"/> : elle est plus ancienne que celle qui est installée.</summary>
public sealed record UpdateOffer(GitHubRelease Release, bool IsDowngrade);

/// <summary>
/// Ce que la vérification conclut. <paramref name="Offer"/> est nulle s'il n'y a rien à proposer ;
/// <paramref name="NoStableYet"/> dit alors que tu es sur une bêta et que le canal stable n'a encore aucune version.
/// </summary>
public sealed record UpdateDecision(UpdateOffer? Offer, bool NoStableYet);

/// <summary>Choisit la version à proposer. Ne regarde ni l'ordre du flux, ni l'horloge, ni le réseau.</summary>
public static class UpdatePolicy
{
    public static UpdateDecision Choose(IReadOnlyList<GitHubRelease> releases, AppVersion installed, UpdatePreferences prefs)
    {
        var stableChannel = prefs.Channel == UpdateChannel.Stable;
        var candidates = releases
            .Where(r => !stableChannel || !IsPrerelease(r))
            .ToList();

        if (candidates.Count == 0)
        {
            return new UpdateDecision(null, stableChannel && installed.IsPrerelease);
        }

        var best = candidates.MaxBy(r => r.Version, Comparer<AppVersion>.Default)!;
        var order = best.Version.CompareTo(installed);

        if (order > 0)
        {
            return new UpdateDecision(new UpdateOffer(best, false), false);
        }

        // Une bêta installée est plus récente que la dernière stable : revenir en arrière est une offre à part,
        // que la préférence peut refuser.
        if (order < 0 && stableChannel && installed.IsPrerelease && !prefs.WaitForNextStable)
        {
            return new UpdateDecision(new UpdateOffer(best, true), false);
        }

        return new UpdateDecision(null, false);
    }

    private static bool IsPrerelease(GitHubRelease release) => release.Prerelease || release.Version.IsPrerelease;
}
