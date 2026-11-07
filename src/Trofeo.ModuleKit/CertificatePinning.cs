using System.Net.Security;

namespace Trofeo.ModuleKit;

/// <summary>
/// Épinglage d'un certificat « maison » (console UniFi, serveur local d'un module de la communauté). Un appareil local présente un certificat qu'aucune autorité ne
/// signe : on retient l'empreinte du premier vu, puis on refuse tout autre. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.
/// </summary>
public static class CertificatePinning
{
    /// <summary>
    /// Décide si le certificat présenté est accepté. Sans erreur de validation (certificat signé par une autorité
    /// reconnue) il l'est toujours ; sinon il l'est si aucune empreinte n'est mémorisée (<c>NewPin</c> est alors
    /// l'empreinte à mémoriser) ou si elle est identique, sans tenir compte de la casse. Une empreinte différente est refusée.
    /// </summary>
    /// <param name="pinnedSha256">Empreinte SHA-256 hexadécimale mémorisée, ou <c>null</c>.</param>
    /// <param name="presentedSha256">Empreinte SHA-256 hexadécimale du certificat présenté.</param>
    /// <param name="errors">Erreurs de validation de la chaîne de certification.</param>
    public static (bool Accept, string? NewPin) Check(string? pinnedSha256, string presentedSha256, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return (true, null);
        if (string.IsNullOrEmpty(presentedSha256)) return (false, null);
        if (string.IsNullOrEmpty(pinnedSha256)) return (true, presentedSha256);
        return (string.Equals(pinnedSha256, presentedSha256, StringComparison.OrdinalIgnoreCase), null);
    }
}
