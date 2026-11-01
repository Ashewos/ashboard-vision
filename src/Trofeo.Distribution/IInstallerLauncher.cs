namespace Trofeo.Distribution;

/// <summary>Ce qui s'est passé quand on a voulu lancer l'installateur.</summary>
public enum InstallerLaunch
{
    /// <summary>L'installateur est lancé : il va arrêter le service et le remplacer.</summary>
    Started,

    /// <summary>Tu as refusé l'autorisation de Windows : rien n'est installé.</summary>
    Cancelled,

    /// <summary>L'installateur n'a pas pu être lancé pour une autre raison.</summary>
    Failed,
}

/// <summary>Lance l'installateur vérifié. La vraie version demande l'autorisation d'administrateur à Windows.</summary>
public interface IInstallerLauncher
{
    InstallerLaunch Launch(string setupPath);
}
