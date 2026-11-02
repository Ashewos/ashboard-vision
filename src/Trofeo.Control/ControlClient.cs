using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using AshboardVision.Sdk;
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

    // Installer, désinstaller, relancer un module : copie et empreinte des fichiers, ou arrêt d'un hôte qui a quelques secondes pour finir.
    private static readonly TimeSpan ModuleOperationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Le délai d'une requête selon son type : 10 minutes pour installer une mise à jour, 2 minutes pour installer, retirer ou relancer
    /// un module, 150 s pour une action, 45 s pour vérifier les mises à jour (du logiciel ou des modules) ou régler leur canal, 20 s
    /// pour un rechargement ou un redémarrage, 10 s sinon.
    /// </summary>
    internal static TimeSpan TimeoutFor(string requestType) => requestType switch
    {
        MessageTypes.InstallUpdate => InstallTimeout,
        MessageTypes.InstallPackage or MessageTypes.InstallModuleUpdate or MessageTypes.SetModulePermissions
            or MessageTypes.UninstallModule or MessageTypes.RelaunchModule or MessageTypes.ReloadDevModule => ModuleOperationTimeout,
        MessageTypes.CheckUpdates or MessageTypes.SetUpdatePreferences or MessageTypes.CheckModuleUpdates => UpdateCheckTimeout,
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
                    throw new ProtocolMismatchException(
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

    // ---- Modules complémentaires ----

    /// <summary>Les types de modules connus du service maintenant (la liste de <see cref="Welcome"/> date de la connexion).</summary>
    public async Task<IReadOnlyList<ModuleTypeInfo>> GetTypesAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Types), TimeoutFor(MessageTypes.Types), ct).ConfigureAwait(false)).Types
        ?? throw Incomplete();

    public async Task<CommunityOverview> GetCommunityAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Community), TimeoutFor(MessageTypes.Community), ct).ConfigureAwait(false)).Community
        ?? throw Incomplete();

    /// <summary>Ce que le service voit dans un <c>.avmod</c>, sans rien installer.</summary>
    public async Task<PackageInfo> InspectPackageAsync(string path, CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.InspectPackage) { Path = path }, TimeoutFor(MessageTypes.InspectPackage), ct)
            .ConfigureAwait(false)).Package
        ?? throw Incomplete();

    public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.InstallPackage) { Path = path, Granted = granted }, ct);

    public Task<ActionResult> ApproveModuleAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.ApproveModule) { ModuleId = id, Granted = granted }, ct);

    public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.SetModulePermissions) { ModuleId = id, Granted = granted }, ct);

    public Task<ActionResult> UninstallModuleAsync(string id, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.UninstallModule) { ModuleId = id }, ct);

    public Task<ActionResult> RelaunchModuleAsync(string id, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.RelaunchModule) { ModuleId = id }, ct);

    public Task<ActionResult> SetModulesRootAsync(string path, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.SetModulesRoot) { Path = path }, ct);

    public Task<ActionResult> SetDeveloperModeAsync(bool on, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.SetDeveloperMode) { On = on }, ct);

    public Task<ActionResult> AddDevFolderAsync(string path, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.AddDevFolder) { Path = path, Granted = granted }, ct);

    public Task<ActionResult> RemoveDevFolderAsync(string path, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.RemoveDevFolder) { Path = path }, ct);

    public Task<ActionResult> ReloadDevModuleAsync(string id, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.ReloadDevModule) { ModuleId = id }, ct);

    /// <summary>Demande la recherche des mises à jour des modules ; le résultat se lit avec <see cref="GetCommunityAsync"/>.</summary>
    public async Task CheckModuleUpdatesAsync(CancellationToken ct) =>
        await SendAsync(new Request(NextId(), MessageTypes.CheckModuleUpdates), TimeoutFor(MessageTypes.CheckModuleUpdates), ct).ConfigureAwait(false);

    public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct) =>
        SendActionAsync(new Request(NextId(), MessageTypes.InstallModuleUpdate) { ModuleId = id, Granted = granted }, ct);

    public async Task<IReadOnlyList<ModuleConsumption>> GetConsumptionAsync(CancellationToken ct) =>
        (await SendAsync(new Request(NextId(), MessageTypes.Consumption), TimeoutFor(MessageTypes.Consumption), ct).ConfigureAwait(false)).Consumption
        ?? throw Incomplete();

    /// <summary>Une requête dont la réponse est un <see cref="ActionResult"/>, avec le délai de son type.</summary>
    private async Task<ActionResult> SendActionAsync(Request request, CancellationToken ct) =>
        (await SendAsync(request, TimeoutFor(request.Type), ct).ConfigureAwait(false)).Action
        ?? throw Incomplete();

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
