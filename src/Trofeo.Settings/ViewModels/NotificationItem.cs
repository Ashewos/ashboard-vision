namespace Trofeo.Settings.ViewModels;

/// <summary>Ce que la notification annonce : une nouveauté (une mise à jour) ou un problème (une panne).</summary>
public enum NotificationKind { News, Problem }

/// <summary>Où mène le lien d'une notification : l'onglet Paramètres, ou sa section « Modules complémentaires ».</summary>
public enum NotificationTarget { Settings, CommunityModules }

/// <summary>
/// Une notification de la cloche. <paramref name="Key"/> dit ce qui la rend unique : une fois supprimée, elle reste supprimée tant que sa
/// clé ne change pas (une autre version, une autre panne). <paramref name="At"/> est l'heure de la panne (<c>null</c> : une nouveauté,
/// ou une heure inconnue) ; elle ordonne la liste.
/// </summary>
public sealed record NotificationItem(
    string Key, NotificationKind Kind, string Text, string LinkText, NotificationTarget Target, DateTimeOffset? At);
