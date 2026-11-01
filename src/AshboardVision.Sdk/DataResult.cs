namespace AshboardVision.Sdk;

/// <summary>Comment le service a répondu à une demande de données.</summary>
public enum DataStatus
{
    /// <summary>La donnée est là.</summary>
    Ok,

    /// <summary>La permission n'est pas accordée à ce module : ce n'est pas une panne, la case peut le dire.</summary>
    Denied,

    /// <summary>La donnée n'est pas disponible pour le moment (capteur absent, aucune musique, service injoignable).</summary>
    Unavailable,
}

/// <summary>Le résultat d'une demande de données au service. <c>Value</c> n'est renseignée que pour <see cref="DataStatus.Ok"/>.</summary>
public sealed record DataResult<T>(DataStatus Status, T? Value);
