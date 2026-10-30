using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Control;

/// <summary>
/// Côté fenêtre de réglage : parle au service par le tube nommé. Une requête dépassant son délai lève
/// <see cref="TimeoutException"/> ; une erreur du service, ou une connexion perdue, lève <see cref="ControlException"/>.
/// </summary>
public sealed class ControlClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(20);

    // Une action peut lancer deux commandes de 60 s (installation du mod des quotas) : son délai passe au-dessus.
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(150);

    // Une vérification interroge GitHub (30 s de délai côté service) et peut attendre une opération en cours.
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(45);

    // Le téléchargement de l'installateur, puis la réponse de l'utilisateur à la demande d'autorisation de Windows.
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Le délai d'une requête selon son type : 10 minutes pour installer une mise à jour, 150 s pour une action, 45 s pour vérifier les
    /// mises à jour ou régler leur canal, 20 s pour un rechargement ou un redémarrage, 10 s sinon.
    /// </summary>
    internal static TimeSpan TimeoutFor(string requestType) => requestType switch
    {
        MessageTypes.InstallUpdate => InstallTimeout,
        MessageTypes.CheckUpdates or MessageTypes.SetUpdatePreferences => UpdateCheckTimeout,
        MessageTypes.Action => ActionTimeout,
        MessageTypes.Reload or MessageTypes.Restart => LongTimeout,
        _ => DefaultTimeout,
    };

    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private int _nextId;
    private int _disposed;
    private int _lost;
    private Task _readLoop = Task.CompletedTask;

    private ControlClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Ce que le service a annoncé à la connexion.</summary>
    public Welcome Welcome { get; private set; } = null!;

    /// <summary>Une image d'aperçu est arrivée. Appelé depuis le fil de lecture : à garder court.</summary>
    public event Action<PreviewFrame>? FrameReceived;

    /// <summary>La connexion s'est coupée (le service s'est arrêté, par exemple). Pas déclenché par <see cref="DisposeAsync"/>.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Se connecte et échange la poignée de main. <paramref name="timeout"/> couvre l'ensemble : si le service est absent
    /// ou déjà servi par 4 clients, lève <see cref="TimeoutException"/>. Lève <see cref="ControlException"/> si le service parle une autre version du protocole.
    /// </summary>
    public static async Task<ControlClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new ControlClient(pipe);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
                client._readLoop = Task.Run(client.ReadLoopAsync);
                var response = await client.SendAsync(new Request(client.NextId(), MessageTypes.Hello), timeout, ct).ConfigureAwait(false);
                var welcome = response.Welcome ?? throw new ControlException("Réponse du service incomplète.");
                if (welcome.ProtocolVersion != ControlPipe.ProtocolVersion)
                    throw new ControlException(
                        $"Le service parle la version {welcome.ProtocolVersion} du protocole, cette fenêtre la version {ControlPipe.ProtocolVersion} : mets les deux à jour.");
                client.Welcome = welcome;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Le service d'affichage ne répond pas.");
            }
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Status), DefaultTimeout, ct).ConfigureAwait(false)).Status
        ?? throw Incomplete();

    public async Task<Reloaded> ReloadAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Reload), LongTimeout, ct).ConfigureAwait(false)).Reloaded
        ?? throw Incomplete();

    /// <summary>Demande au service de recréer le module : vrai si l'instance existait.</summary>
    public async Task<bool> RestartModuleAsync(Guid instanceId, CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Restart) { InstanceId = instanceId }, LongTimeout, ct).ConfigureAwait(false)).Restarted
        ?? throw Incomplete();

    public async Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query, CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Lookup) { TypeId = typeId, FieldKey = fieldKey, Query = query }, DefaultTimeout, ct)
            .ConfigureAwait(false)).Results
        ?? throw Incomplete();

    public async Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey, CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Action) { InstanceId = instanceId, ActionKey = actionKey }, TimeoutFor(MessageTypes.Action), ct)
            .ConfigureAwait(false)).Action
        ?? throw Incomplete();

    public async Task<UpdateInfo> GetUpdateAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.GetUpdate), TimeoutFor(MessageTypes.GetUpdate), ct).ConfigureAwait(false)).Update
        ?? throw Incomplete();

    /// <summary>Demande une vérification tout de suite ; le résultat se lit avec <see cref="GetUpdateAsync"/>.</summary>
    public async Task CheckUpdatesAsync(CancellationToken ct) =>
        await SendAsync(new Request(NextId(), MessageTypes.CheckUpdates), TimeoutFor(MessageTypes.CheckUpdates), ct).ConfigureAwait(false);

    public async Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable, CancellationToken ct) =>
        await SendAsync(
            new Request(NextId(), MessageTypes.SetUpdatePreferences) { Channel = channel, WaitForNextStable = waitForNextStable },
            TimeoutFor(MessageTypes.SetUpdatePreferences), ct).ConfigureAwait(false);

    /// <summary>Rend <c>Ok</c> vrai quand l'installateur est lancé (il arrête ensuite le service) ; sinon le message dit pourquoi.</summary>
    public async Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade, CancellationToken ct) =>
        (await SendAsync(
            new Request(NextId(), MessageTypes.InstallUpdate) { ConfirmDowngrade = confirmDowngrade },
            TimeoutFor(MessageTypes.InstallUpdate), ct).ConfigureAwait(false)).Action
        ?? throw Incomplete();

    /// <summary>S'abonne aux images de l'écran (<see cref="FrameReceived"/>), au plus <paramref name="maxFps"/> par seconde ; 0 : arrêt.</summary>
    public async Task SubscribePreviewAsync(int maxFps, CancellationToken ct) =>
        await SendAsync(new Request(NextId(), MessageTypes.Preview) { MaxFps = maxFps }, DefaultTimeout, ct).ConfigureAwait(false);

    private static ControlException Incomplete() => new("Réponse du service incomplète.");

    private int NextId() => Interlocked.Increment(ref _nextId);

    private async Task<Response> SendAsync(Request request, TimeSpan timeout, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _lost) != 0)
            throw new ControlException("La connexion au service est coupée.");

        var reply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = reply;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        limit.CancelAfter(timeout);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ControlJson.Options);
            await _writeLock.WaitAsync(limit.Token).ConfigureAwait(false);
            try { await Framing.WriteJsonAsync(_pipe, bytes, limit.Token).ConfigureAwait(false); }
            finally { _writeLock.Release(); }

            // Si la perte de connexion survient entre l'ajout et ici, la lecture a déjà vidé la table : on revérifie.
            if (Volatile.Read(ref _lost) != 0)
                reply.TrySetException(LostConnection());

            var response = await reply.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            if (response.Type == MessageTypes.Error)
                throw new ControlException(response.Message ?? "Erreur du service.");
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Le service n'a pas répondu à « {request.Type} » dans le délai.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw LostConnection();
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private static ControlException LostConnection() => new("La connexion au service est coupée.");

    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (true)
            {
                var frame = await Framing.ReadAsync(_pipe, ct).ConfigureAwait(false);
                if (frame is null)
                    break;

                if (frame.Value.Kind == FrameKind.Image)
                {
                    var (rotation, jpeg) = Framing.ParseImage(frame.Value.Payload);
                    RaiseFrame(new PreviewFrame(jpeg, rotation));
                }
                else
                {
                    var response = JsonSerializer.Deserialize<Response>(frame.Value.Payload, ControlJson.Options);
                    if (response is not null && _pending.TryRemove(response.ReplyTo, out var waiting))
                        waiting.TrySetResult(response);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                      or JsonException or InvalidDataException)
        {
            // Connexion coupée ou protocole violé.
        }
        finally
        {
            Volatile.Write(ref _lost, 1);
            foreach (var waiting in _pending.Values)
                waiting.TrySetException(LostConnection());
            _pending.Clear();
            if (Volatile.Read(ref _disposed) == 0)
                RaiseDisconnected();
        }
    }

    private void RaiseFrame(PreviewFrame frame)
    {
        try { FrameReceived?.Invoke(frame); }
        catch { /* un abonné défaillant ne doit pas couper la lecture */ }
    }

    private void RaiseDisconnected()
    {
        try { Disconnected?.Invoke(); }
        catch { /* idem */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _cts.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); }
        catch { /* la boucle a déjà tout rattrapé */ }
    }
}
