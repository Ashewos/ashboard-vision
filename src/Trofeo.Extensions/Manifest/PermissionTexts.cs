using AshboardVision.Sdk;

namespace Trofeo.Extensions;

/// <summary>Les textes montrés à l'utilisateur quand il valide les permissions d'un module (conception, tableau 5.2).</summary>
public static class PermissionTexts
{
    /// <summary>Le nom court de la permission.</summary>
    public static string Label(ModulePermission permission) => permission switch
    {
        ModulePermission.LocalNetwork => "Réseau local",
        ModulePermission.Internet => "Internet",
        ModulePermission.ThisComputer => "Cet ordinateur",
        ModulePermission.Sensors => "Capteurs",
        ModulePermission.NowPlaying => "Musique en cours",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Permission inconnue."),
    };

    /// <summary>Ce que la permission ouvre au module, en une phrase qui tutoie.</summary>
    public static string Explanation(ModulePermission permission) => permission switch
    {
        ModulePermission.LocalNetwork =>
            "Le module peut parler aux appareils et aux serveurs de ton réseau.",
        ModulePermission.Internet =>
            "Le module peut contacter des services sur Internet.",
        ModulePermission.ThisComputer =>
            "Le module peut se connecter à un service lancé sur ce PC, mais seulement sur les ports que tu choisis dans ses réglages.",
        ModulePermission.Sensors =>
            "Le module peut lire les températures, les charges et la mémoire de ton PC.",
        ModulePermission.NowPlaying =>
            "Le module peut lire le titre, l'artiste et la pochette de la musique en cours.",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Permission inconnue."),
    };
}
