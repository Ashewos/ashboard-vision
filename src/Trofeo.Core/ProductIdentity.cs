namespace Trofeo.Core;

/// <summary>
/// Le nom du produit sous toutes ses formes. « Ashboard Vision » est ce qu'on voit, <c>ashboard-vision</c>
/// ce que les dossiers, l'installateur et le dépôt utilisent. L'ancien identifiant reste connu pour retrouver
/// les réglages d'avant le changement de nom.
/// </summary>
public static class ProductIdentity
{
    /// <summary>Le nom affiché partout où quelqu'un le voit.</summary>
    public const string DisplayName = "Ashboard Vision";

    /// <summary>L'identifiant technique (dossiers, installateur, dépôt).</summary>
    public const string Id = "ashboard-vision";

    /// <summary>L'identifiant d'avant le changement de nom (<c>%AppData%\trofeo-HB-dashboard</c>).</summary>
    public const string LegacyId = "trofeo-HB-dashboard";

    /// <summary>L'éditeur, tel qu'il apparaît dans l'installateur.</summary>
    public const string Publisher = "Ashewos";

    /// <summary>Le dépôt GitHub qui publie les versions, sous la forme <c>propriétaire/nom</c>.</summary>
    public const string Repository = "Ashewos/ashboard-vision";

    /// <summary>Le nom du fichier d'installation d'une version, par exemple <c>Ashboard-Vision-Setup-1.0.0.exe</c>.</summary>
    public static string SetupFileName(string version) => $"Ashboard-Vision-Setup-{version}.exe";
}
