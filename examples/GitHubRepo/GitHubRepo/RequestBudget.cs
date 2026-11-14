namespace GitHubRepo;

/// <summary>
/// Le nombre de requêtes que le module a vraiment envoyées à GitHub pendant la dernière <paramref name="window"/> (une fenêtre glissante :
/// une requête sort du compte quand elle a une heure). Il sert à refuser un « Rafraîchir » qui ferait dépasser <paramref name="limit"/>,
/// pour rester sous les 60 requêtes par heure que GitHub accorde sans jeton.
/// </summary>
/// <param name="limit">Le nombre de requêtes qu'on s'accorde dans la fenêtre.</param>
/// <param name="window">La durée de la fenêtre.</param>
/// <param name="time">L'horloge du module (celle des tests peut avancer).</param>
internal sealed class RequestBudget(int limit, TimeSpan window, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>Le nombre de requêtes envoyées dans la fenêtre.</summary>
    public int Used
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _sent.Count;
            }
        }
    }

    /// <summary>Note qu'une requête part maintenant. À appeler juste avant l'envoi, pour chaque requête (une page de versions compte pour une).</summary>
    public void Record()
    {
        lock (_gate)
        {
            Prune();
            _sent.Enqueue(time.GetUtcNow());
        }
    }

    /// <summary>Vrai si <paramref name="requests"/> requêtes de plus tiennent encore dans la limite.</summary>
    public bool CanAfford(int requests) => Used + requests <= limit;

    private void Prune()
    {
        var now = time.GetUtcNow();
        while (_sent.Count > 0 && now - _sent.Peek() >= window)
        {
            _sent.Dequeue();
        }
    }
}
