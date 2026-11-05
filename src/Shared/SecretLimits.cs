namespace Trofeo.Shared;

/// <summary>
/// Les limites que le service applique au coffre à secrets d'un module. Un seul fichier source, inclus (<c>Compile Include</c>) par
/// le superviseur (<c>Trofeo.Extensions</c>) et par les aides de test (<c>AshboardVision.Sdk.Testing</c>), pour que le faux coffre
/// refuse exactement ce que le vrai refuse.
/// </summary>
internal static class SecretLimits
{
    /// <summary>La longueur maximale d'une clé (une clé vide est refusée aussi).</summary>
    public const int MaxKeyLength = 200;

    /// <summary>La longueur maximale d'une valeur, en caractères.</summary>
    public const int MaxValueLength = 64 * 1024;
}
