using System.Collections.Concurrent;
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
/// </para>
/// </summary>
public sealed class FakeSecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length is > 0 and <= SecretLimits.MaxKeyLength && _values.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public void Set(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > SecretLimits.MaxKeyLength || (value is not null && value.Length > SecretLimits.MaxValueLength))
        {
            return;
        }

        if (value is null)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = value;
        }
    }
}
