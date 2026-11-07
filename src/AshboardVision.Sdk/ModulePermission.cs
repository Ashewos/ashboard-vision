namespace AshboardVision.Sdk;

/// <summary>
/// Ce qu'un module de la communauté peut demander en plus du strict nécessaire. Chaque permission est validée une par
/// une par l'utilisateur ; une permission refusée renvoie « refusé » au module, jamais une panne.
/// </summary>
public enum ModulePermission
{
    /// <summary>Les appareils et serveurs du réseau local (<c>reseau-local</c>).</summary>
    LocalNetwork,

    /// <summary>Les services sur Internet (<c>internet</c>).</summary>
    Internet,

    /// <summary>Un service lancé sur ce PC, sur les ports choisis par l'utilisateur dans les réglages (<c>ce-pc</c>).</summary>
    ThisComputer,

    /// <summary>Les températures, charges et mémoire lues par le service (<c>capteurs</c>).</summary>
    Sensors,

    /// <summary>Titre, artiste et pochette de la musique en cours (<c>musique</c>).</summary>
    NowPlaying,
}

/// <summary>Le nom d'une permission dans le manifeste d'un module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
public static class ModulePermissions
{
    /// <summary>Le nom écrit dans le manifeste : <c>reseau-local</c>, <c>internet</c>, <c>ce-pc</c>, <c>capteurs</c>, <c>musique</c>.</summary>
    public static string ToManifest(ModulePermission permission) => permission switch
    {
        ModulePermission.LocalNetwork => "reseau-local",
        ModulePermission.Internet => "internet",
        ModulePermission.ThisComputer => "ce-pc",
        ModulePermission.Sensors => "capteurs",
        ModulePermission.NowPlaying => "musique",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Permission inconnue."),
    };

    /// <summary>Lit le nom d'une permission écrit dans un manifeste (exactement, sans tolérance de casse).</summary>
    public static bool TryParse(string? text, out ModulePermission permission)
    {
        switch (text)
        {
            case "reseau-local": permission = ModulePermission.LocalNetwork; return true;
            case "internet": permission = ModulePermission.Internet; return true;
            case "ce-pc": permission = ModulePermission.ThisComputer; return true;
            case "capteurs": permission = ModulePermission.Sensors; return true;
            case "musique": permission = ModulePermission.NowPlaying; return true;
            default: permission = default; return false;
        }
    }
}
