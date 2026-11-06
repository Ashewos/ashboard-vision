using System.Collections.Concurrent;
using System.Threading.Channels;
using Trofeo.Extensions.Protocol;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Un hôte de module lancé : son processus, le verrou sur les fichiers du module, le canal et la file d'envoi. Un par lancement ;
/// quand l'hôte s'arrête, le superviseur en crée un autre. Rien ici ne bloque : <see cref="Post"/> met dans une file, et un seul fil
/// d'écriture la vide. Une écriture bloquée (hôte vivant qui ne lit plus) ne se libère qu'à la mort de l'hôte : on ne l'attend jamais.
/// </summary>
internal sealed class HostConnection
{
    private readonly Channel<(HostMessage Message, HostMessage? Fallback)> _outbox =
        Channel.CreateUnbounded<(HostMessage, HostMessage?)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostMessage?>> _pending = new();
    private int _nextRequest;
    private volatile bool _closed;
    private int _queued;
    private volatile bool _overflowed;

    /// <summary>Le plus de messages qu'on laisse attendre dans la file d'envoi : au-delà, l'hôte ne lit plus (ou inonde) et il est arrêté.</summary>
    public const int MaxQueuedMessages = 4000;

    public HostConnection(IHostProcess process, ModuleFileLock? fileLock, MessageChannel link, DateTimeOffset startedAt)
    {
        Process = process;
        FileLock = fileLock;
        Link = link;
        StartedAt = startedAt;
    }

    public IHostProcess Process { get; }

    /// <summary>Les lectures de capteurs et de musique demandées par l'hôte : un travailleur, huit demandes en attente au plus.</summary>
    public BoundedWorker DataWorker { get; } = new(8);

    /// <summary>Les demandes de secrets de l'hôte, dans l'ordre : un travailleur, soixante-quatre demandes en attente au plus.</summary>
    public BoundedWorker SecretWorker { get; } = new(64);

    /// <summary>Appelée avec ce qu'il faut journaliser quand l'écriture vers l'hôte a un souci.</summary>
    public Action<string>? Warn { get; set; }

    /// <summary>Vrai si l'hôte a été arrêté parce que la file d'envoi dépassait <see cref="MaxQueuedMessages"/>.</summary>
    public bool Overflowed => _overflowed;

    /// <summary>Le verrou sur les fichiers du module ; <c>null</c> pour un dossier de développement. Libéré à la fin du processus.</summary>
    public ModuleFileLock? FileLock { get; }

    public MessageChannel Link { get; }

    /// <summary>Le relais « Cet ordinateur » de cet hôte, créé à l'installation.</summary>
    public LocalRelay? Relay { get; set; }

    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se termine quand le processus est fini et tout nettoyé (verrou des fichiers compris). Existe dès la création de la connexion.</summary>
    public Task Done => _done.Task;

    /// <summary>Annonce que le processus est fini et tout nettoyé.</summary>
    public void MarkDone() => _done.TrySetResult();

    // Les champs qui suivent ne se touchent que sous le verrou du superviseur.
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Le superviseur a demandé l'arrêt (plus de case, relance voulue, fin du service) : ce n'est pas une panne.</summary>
    public bool ExpectedStop { get; set; }

    /// <summary>Le processus est terminé.</summary>
    public bool Ended { get; set; }

    /// <summary>Le dernier message d'un <see cref="Failure"/> de l'hôte.</summary>
    public string? FailureText { get; set; }

    /// <summary>Le minuteur qui arrête l'hôte s'il ne s'est pas arrêté de lui-même après <see cref="Shutdown"/>.</summary>
    public ITimer? KillTimer { get; set; }

    /// <summary>Met un message dans la file d'envoi. Sans effet une fois la file fermée.</summary>
    public void Post(HostMessage message, HostMessage? fallback = null)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedMessages)
        {
            Interlocked.Decrement(ref _queued);
            if (!_overflowed)
            {
                _overflowed = true;
                KillQuietly(); // un hôte qui ne lit plus ce qu'on lui envoie ne sert plus à rien, et la file ne doit pas grossir
            }

            return;
        }

        if (!_outbox.Writer.TryWrite((message, fallback)))
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>
    /// Vide la file vers l'hôte, un message après l'autre. Un message trop gros pour une trame est remplacé par son message de
    /// remplacement ; un tube coupé arrête l'écriture (et l'hôte, qui ne peut plus rien recevoir).
    /// </summary>
    public async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var (message, fallback) in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queued);
                try
                {
                    await Link.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (IOException or ObjectDisposedException or OutOfMemoryException))
                {
                    // Un message qui ne peut pas s'écrire (trop gros, JSON invalide) est perdu, sauf son message de remplacement ; les
                    // suivants partent.
                    Warn?.Invoke($"Un message pour l'hôte n'a pas pu être écrit : {ex.Message}");
                    if (fallback is not null)
                    {
                        await Link.SendAsync(fallback, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tube coupé, ou tout autre échec d'écriture : un hôte qui ne reçoit plus rien ne sert à rien, il est arrêté.
            if (ex is not (IOException or ObjectDisposedException))
            {
                Warn?.Invoke($"L'écriture vers l'hôte a échoué : {ex.Message}");
            }

            KillQuietly();
        }
    }

    /// <summary>
    /// Envoie la demande construite avec un numéro neuf et attend sa réponse. <c>null</c> si l'hôte s'arrête avant ou ne répond
    /// pas à temps. Lève <see cref="OperationCanceledException"/> si <paramref name="ct"/> est annulé.
    /// </summary>
    public async Task<HostMessage?> RequestAsync(Func<int, HostMessage> build, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequest);
        var reply = new TaskCompletionSource<HostMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            if (_closed)
            {
                return null; // l'hôte est déjà fini : inutile d'attendre
            }

            Post(build(id));
            return await reply.Task.WaitAsync(timeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Donne une réponse à celui qui l'attend (rien si personne ne l'attend plus).</summary>
    public void Complete(int id, HostMessage reply)
    {
        if (_pending.TryRemove(id, out var waiting))
        {
            waiting.TrySetResult(reply);
        }
    }

    /// <summary>Libère ceux qui attendaient une réponse de l'hôte (qui ne viendra plus) et ferme la file d'envoi.</summary>
    public void Close()
    {
        _closed = true;
        _outbox.Writer.TryComplete();
        DataWorker.Complete();
        SecretWorker.Complete();
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetResult(null);
        }
    }

    /// <summary>Arrête le processus, sans lever d'exception (déjà fini, déjà libéré).</summary>
    public void KillQuietly()
    {
        try
        {
            Process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // déjà arrêté : c'est ce qu'on voulait
        }
    }
}
