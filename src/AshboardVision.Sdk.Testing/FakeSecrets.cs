using System.Collections.Concurrent;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Testing;

/// <summary>
/// Un coffre à secrets en mémoire, pour les tests : ce que le module enregistre avec <see cref="ModuleServices.Secrets"/> s'y
/// retrouve, et ce que le test y met (avant <see cref="ModuleTestHost{TModule}.StartAsync"/>, par exemple un jeton) est lu par le module.
/// Rien n'est chiffré ni écrit sur le disque. Utilisable depuis n'importe quel thread.
/// </summary>
public sealed class FakeSecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _values.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public void Set(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
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
