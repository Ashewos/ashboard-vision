namespace Trofeo.ModuleKit;

/// <summary>L'état d'un module, tel qu'il le dit à l'utilisateur (<c>SetStatus</c>).</summary>
public enum ModuleHealth
{
    /// <summary>Le module attend un réglage de l'utilisateur (une adresse, un jeton…) et n'a rien tenté.</summary>
    NotConfigured,

    /// <summary>Le module fonctionne.</summary>
    Online,

    /// <summary>Le module n'arrive pas à faire son travail (service injoignable, permission retirée…) ; son message dit pourquoi.</summary>
    Offline,
}

/// <summary>État d'un module, avec un éventuel message d'action pour l'utilisateur.</summary>
public sealed record ModuleStatus(ModuleHealth Health, string? ActionMessage = null);
