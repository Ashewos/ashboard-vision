using Trofeo.ModuleKit;
using Trofeo.Shared;

namespace AshboardVision.Sdk.Testing;

/// <summary>
/// Un coffre à secrets en mémoire, pour les tests : ce que le module enregistre avec <see cref="ModuleServices.Secrets"/> s'y
/// retrouve, et ce que le test y met (avant <see cref="ModuleTestHost{TModule}.StartAsync"/>, par exemple un jeton) est lu par le module.
/// Rien n'est chiffré ni écrit sur le disque. Utilisable depuis n'importe quel thread.
/// <para>
/// Il applique les limites du service : une clé vide ou de plus de 200 caractères, ou une valeur de plus de 64 Kio (65 536 caractères),
/// est ignorée en écriture (rien n'est enregistré, l'ancienne valeur reste) et se lit comme <c>null</c>, sans exception.
/// Le coffre est aussi plafonné : 64 clés au plus et 256 Kio en tout (la taille en octets UTF-8 de toutes les clés et de toutes les
/// valeurs). Une écriture qui dépasserait est ignorée de la même façon ; remplacer une clé existante par une valeur de même taille
/// ou plus courte reste permis, et effacer l'est toujours.
/// </para>
/// </summary>
public sealed class FakeSecrets : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > SecretLimits.MaxKeyLength)
        {
            return null;
        }

        lock (_values)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    /// <inheritdoc />
    public void Set(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > SecretLimits.MaxKeyLength || (value is not null && value.Length > SecretLimits.MaxValueLength))
        {
            return;
        }

        lock (_values)
        {
            if (!SecretLimits.Fits(_values, key, value))
            {
                return;
            }

            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
        }
    }
}
