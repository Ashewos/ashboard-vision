namespace Trofeo.Modules.Agents;

/// <summary>Un quota (fenêtre de 5 h ou hebdomadaire) tel que Claude Code le transmet.</summary>
public sealed record CapturedQuota(double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>Ce que l'on garde d'une mesure de Claude Code : une session, son modèle, son contexte, ses quotas.</summary>
public sealed record StatusLineRecord(
    DateTimeOffset CapturedAt,
    string SessionId,
    string? ModelId,
    long? ContextWindowSize,
    double? ContextUsedPercent,
    CapturedQuota? FiveHour,
    CapturedQuota? SevenDay);
