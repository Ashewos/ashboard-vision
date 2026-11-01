using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>Le coffre à secrets du module : chaque lecture et chaque écriture passe par le service, qui chiffre et range.</summary>
internal sealed class RemoteSecretStore : ISecretStore
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly HostLink _link;

    public RemoteSecretStore(HostLink link) => _link = link;

    // L'interface est synchrone : on attend la réponse, que la boucle de lecture livre sur un autre fil (jamais depuis elle).
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var reply = _link.RequestAsync(id => new SecretGet(id, key), Patience, CancellationToken.None).GetAwaiter().GetResult();
        return reply is SecretValue value ? value.Value : throw new InvalidOperationException("Réponse inattendue du service à la lecture d'un secret.");
    }

    public void Set(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var reply = _link.RequestAsync(id => new SecretSet(id, key, value), Patience, CancellationToken.None).GetAwaiter().GetResult();
        if (reply is not SecretValue)
        {
            throw new InvalidOperationException("Réponse inattendue du service à l'écriture d'un secret.");
        }
    }
}
