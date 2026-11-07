using System.Text;

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

    /// <summary>Le plus de clés qu'un module peut garder dans son coffre.</summary>
    public const int MaxKeys = 64;

    /// <summary>Le plus que le coffre d'un module peut peser en tout : la somme des tailles, en octets UTF-8, de toutes les clés et de toutes les valeurs (256 Kio).</summary>
    public const int MaxTotalBytes = 256 * 1024;

    /// <summary>
    /// Vrai si l'écriture de <paramref name="value"/> sous <paramref name="key"/> tient dans les plafonds du coffre de ce module
    /// (<paramref name="current"/> : ce qu'il contient déjà). Effacer (<c>null</c>) est toujours permis. Remplacer une clé existante par
    /// une valeur de même taille ou plus courte l'est aussi, même si le plafond est atteint ; une nouvelle clé ou une valeur plus
    /// longue doit tenir dans <see cref="MaxKeys"/> et <see cref="MaxTotalBytes"/>.
    /// </summary>
    public static bool Fits(IReadOnlyDictionary<string, string> current, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(key);
        if (value is null)
        {
            return true;
        }

        var newSize = (long)Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
        var total = 0L;
        foreach (var (k, v) in current)
        {
            total += (long)Encoding.UTF8.GetByteCount(k) + Encoding.UTF8.GetByteCount(v);
        }

        if (current.TryGetValue(key, out var existing))
        {
            var oldSize = (long)Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(existing);
            return newSize <= oldSize || total - oldSize + newSize <= MaxTotalBytes;
        }

        return current.Count < MaxKeys && total + newSize <= MaxTotalBytes;
    }
}
