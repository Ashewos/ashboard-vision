using System.Threading.Channels;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Un seul travailleur, qui fait ses tâches dans l'ordre, avec une file bornée : au-delà de <c>capacity</c> tâches en attente,
/// <see cref="TryEnqueue"/> refuse (l'appelant répond « indisponible »). Sert à ne pas laisser un hôte qui inonde le service de
/// demandes lui imposer un fil du pool par demande. Le fil n'existe qu'à partir de la première tâche.
/// </summary>
internal sealed class BoundedWorker
{
    private readonly Channel<Action> _queue;
    private int _started;

    public BoundedWorker(int capacity) =>
        _queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Met la tâche en file. Faux si la file est pleine ou fermée.</summary>
    public bool TryEnqueue(Action work)
    {
        if (!_queue.Writer.TryWrite(work))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(RunAsync);
        }

        return true;
    }

    /// <summary>Ferme la file : le travailleur finit ce qui est déjà en attente puis s'arrête.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // une tâche qui échoue ne prive pas les suivantes de leur travailleur (chacune répond déjà d'elle-même)
            }
        }
    }
}
