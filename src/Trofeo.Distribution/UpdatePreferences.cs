namespace Trofeo.Distribution;

/// <summary>Les versions que tu veux suivre.</summary>
public enum UpdateChannel
{
    /// <summary>Seulement les versions stables.</summary>
    Stable,

    /// <summary>Les stables et les bêtas d'essai.</summary>
    Beta,
}

/// <summary>
/// Les choix de mise à jour. <paramref name="WaitForNextStable"/> concerne une bêta installée sur le canal stable :
/// faux, on propose de revenir à la dernière stable (plus ancienne) ; vrai, on attend qu'une stable la dépasse.
/// </summary>
public sealed record UpdatePreferences(UpdateChannel Channel, bool WaitForNextStable)
{
    /// <summary>Le canal stable, sans attendre.</summary>
    public static UpdatePreferences Default { get; } = new(UpdateChannel.Stable, false);
}
