using Trofeo.Core.Secrets;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// Le coffre à secrets de l'essai : en mémoire, perdu à la fin. Jamais le coffre chiffré du logiciel. C'est un coffre « gardé », comme celui
/// du logiciel : le superviseur lui donne la règle des plafonds du service (64 clés, 256 Kio) à chaque écriture, de sorte que le banc refuse
/// exactement ce que le service refuse (sans erreur, voir <c>docs/sdk/contrat.md</c>).
/// </summary>
internal sealed class InMemorySecrets : IGuardedSecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string? value) => TrySet(key, value, _ => true);

    public bool TrySet(string key, string? value, Func<IReadOnlyDictionary<string, string>, bool> accept)
    {
        lock (_values)
        {
            if (!accept(_values))
            {
                return false;
            }

            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return true;
        }
    }
}
