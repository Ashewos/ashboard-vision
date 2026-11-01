using System.Collections.Concurrent;
using System.Threading.Channels;
using Trofeo.Extensions.Protocol;

namespace Trofeo.ModuleHost;

/// <summary>
/// Le côté « écriture » de l'hôte : une file d'envoi vidée par un seul fil (jamais depuis la boucle de lecture, qu'un service
/// qui ne lit plus gèlerait), et les demandes à réponse (secrets, données, relais) rapprochées de leur réponse par numéro.
/// </summary>
internal sealed class HostLink : IAsyncDisposable
{
    /// <summary>Plafond d'un texte envoyé (journal, message d'état) : une trame ne dépasse pas 1 Mio.</summary>
    public const int MaxTextLength = 2000;

    private readonly MessageChannel _channel;
    private readonly Channel<(HostMessage Message, HostMessage? Fallback)> _outbox = Channel.CreateUnbounded<(HostMessage, HostMessage?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostMessage>> _pending = new();
    private readonly Task _writer;
    private int _nextId;

    public HostLink(MessageChannel channel)
    {
        _channel = channel;
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>Met un message dans la file d'envoi. Sans effet une fois la file fermée.</summary>
    public void Post(HostMessage message) => _outbox.Writer.TryWrite((message, null));

    /// <summary>
    /// Comme <see cref="Post(HostMessage)"/>, avec un message de remplacement court pour le cas où le premier ne peut pas s'écrire
    /// (réponse de plus d'1 Mio, JSON invalide) : celui qui attend la réponse la reçoit au lieu d'attendre son délai.
    /// </summary>
    public void Post(HostMessage message, HostMessage fallback) => _outbox.Writer.TryWrite((message, fallback));

    /// <summary>
    /// Envoie la demande construite avec un numéro neuf et attend la réponse portant ce numéro. Lève
    /// <see cref="TimeoutException"/> si elle ne vient pas à temps et <see cref="OperationCanceledException"/> si <paramref name="ct"/> est annulé.
    /// </summary>
    public async Task<HostMessage> RequestAsync(Func<int, HostMessage> build, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        await using var registration = timeoutSource.Token.Register(() => reply.TrySetCanceled(timeoutSource.Token));
        try
        {
            Post(build(id));
            return await reply.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Le service n'a pas répondu à temps.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Donne une réponse à celui qui l'attend. Faux si personne ne l'attend (ou plus : annulée, expirée).</summary>
    public bool TryComplete(int id, HostMessage reply) =>
        _pending.TryRemove(id, out var waiting) && waiting.TrySetResult(reply);

    /// <summary>Ferme la file, laisse partir ce qui est déjà dedans (au plus <paramref name="patience"/>) et libère ceux qui attendaient une réponse.</summary>
    public async Task CompleteAsync(TimeSpan patience)
    {
        _outbox.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(patience).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // le service ne lit plus : on n'attend pas davantage
        }

        foreach (var (id, waiting) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                waiting.TrySetCanceled();
            }
        }
    }

    public async ValueTask DisposeAsync() => await CompleteAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

    /// <summary>Raccourcit un texte venu d'un module.</summary>
    public static string Clip(string? text) =>
        text is null ? "" : text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength), "…");

    /// <summary>
    /// Envoie un message. Faux s'il ne s'écrit pas (trop gros pour une trame, valeur JSON vide venue d'un module) : il est perdu, mais
    /// l'écriture des autres ne s'arrête jamais. Un tube fermé (<see cref="IOException"/>, <see cref="ObjectDisposedException"/>) remonte.
    /// </summary>
    private async Task<bool> TrySendAsync(HostMessage message)
    {
        try
        {
            await _channel.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var (message, fallback) in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!await TrySendAsync(message).ConfigureAwait(false) && fallback is not null)
                {
                    await TrySendAsync(fallback).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Le service a fermé son bout : plus personne à qui écrire. La lecture s'arrête aussi, et l'hôte avec.
            _outbox.Writer.TryComplete();
        }
    }
}
