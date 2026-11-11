using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.Services;

/// <summary>
/// La liaison réelle avec le service, par le tube de contrôle. Elle essaie de se connecter dès sa création, puis toutes les
/// 2 s tant que ça échoue ; au troisième échec de suite l'état passe à <see cref="LinkState.ServiceStopped"/> (elle continue
/// d'essayer). Une coupure fait repasser à <see cref="LinkState.Connecting"/>, sans exception vers l'interface. Une fois
/// connectée, elle s'abonne à l'aperçu (4 i/s, ou 12) et relit l'état du service chaque seconde.
/// </summary>
public sealed class ServiceLink : IServiceLink
{
    public const int FailuresBeforeStopped = 3;
    public const int NormalFps = 4;
    public const int RealTimeFps = 12;

    private readonly string _pipeName;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _statusInterval;
    private readonly ServiceStarter _starter;
    private readonly Action<Action>? _post;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private ControlClient? _client;
    private bool _realTime;
    private bool _paused;
    private Guid? _previewViewId;
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private long _subscribeSeq;
    private long _shownGeneration;
    private PreviewFrame? _pendingFrame;
    private int _failures;
    private int _disposed;
    private long _updatesGeneration;
    private long _typesGeneration;

    /// <param name="pipeName">Le tube du service.</param>
    /// <param name="retryDelay">Délai entre deux tentatives de connexion (2 s).</param>
    /// <param name="connectTimeout">Durée d'une tentative (1 s).</param>
    /// <param name="statusInterval">Délai entre deux lectures de l'état (1 s).</param>
    /// <param name="starter">Lanceur du service (celui de l'application par défaut).</param>
    /// <param name="post">
    /// Comment lever les événements sur le fil de l'interface (<c>Dispatcher.UIThread.Post</c> dans l'application).
    /// Sans lui, ils sont levés en ligne sur le fil de la liaison : réservé aux tests.
    /// </param>
    public ServiceLink(
        string pipeName = ControlPipe.Name, TimeSpan? retryDelay = null, TimeSpan? connectTimeout = null,
        TimeSpan? statusInterval = null, ServiceStarter? starter = null, Action<Action>? post = null)
    {
        _pipeName = pipeName;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1);
        _statusInterval = statusInterval ?? TimeSpan.FromSeconds(1);
        _starter = starter ?? ServiceStarter.Default;
        _post = post;
        _loop = Task.Run(RunAsync);
    }

    public LinkState State { get; private set; } = LinkState.Connecting;
    public Welcome? Welcome { get; private set; }
    public ServiceStatus? Status { get; private set; }
    public UpdateInfo? Updates { get; private set; }
    public IReadOnlyList<ModuleTypeInfo>? Types { get; private set; }
    public Guid? ShownViewId { get; private set; }

    /// <summary>Comment les types sont relus ; les tests le remplacent pour simuler un service qui ne répond pas (le délai réel est de 10 s).</summary>
    internal Func<ControlClient, CancellationToken, Task<IReadOnlyList<ModuleTypeInfo>>> TypesReader { get; set; } =
        (client, ct) => client.GetTypesAsync(ct);

    public event Action? Changed;
    public event Action<PreviewFrame>? FrameReceived;

    public void SetRealTimePreview(bool realTime)
    {
        lock (_gate)
            _realTime = realTime;
        Resubscribe();
    }

    public void SetPreviewPaused(bool paused)
    {
        lock (_gate)
            _paused = paused;
        Resubscribe();
    }

    public Guid? PreviewViewId
    {
        get { lock (_gate) return _previewViewId; }
    }

    public void SetPreviewView(Guid? viewId)
    {
        lock (_gate)
        {
            if (_previewViewId == viewId)
                return; // déjà celle de l'aperçu : pas de deuxième abonnement à la même vue
            _previewViewId = viewId;
        }

        Resubscribe();
    }

    /// <summary>Images par seconde demandées : 0 en pause, sinon 4 ou 12. Appelé sous <see cref="_gate"/>.</summary>
    private int WantedFps() => _paused ? 0 : _realTime ? RealTimeFps : NormalFps;

    private void Resubscribe()
    {
        ControlClient? client;
        lock (_gate)
            client = _client;

        if (client is not null)
            _ = SubscribeLatestAsync(client, Interlocked.Increment(ref _subscribeSeq));
    }

    /// <summary>
    /// Les demandes d'abonnement partent une à une, la dernière gagne : une demande qui en trouve une plus récente derrière elle ne part
    /// pas, et celle qui part lit au dernier moment le débit et la vue voulus. Sans cela, deux changements rapides pouvaient arriver
    /// dans le désordre au service et laisser l'aperçu sur la mauvaise vue.
    /// </summary>
    private async Task SubscribeLatestAsync(ControlClient client, long sequence)
    {
        try
        {
            await _subscribeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // la liaison se ferme
        }

        try
        {
            if (sequence != Volatile.Read(ref _subscribeSeq))
                return; // une demande plus récente attend son tour
            int fps;
            Guid? viewId;
            lock (_gate)
            {
                fps = WantedFps();
                viewId = _previewViewId;
            }

            await SubscribeQuietlyAsync(client, fps, viewId).ConfigureAwait(false);
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    public Task<Reloaded> ReloadAsync() => RequestAsync((client, ct) => client.ReloadAsync(ct));

    public Task<IReadOnlyList<LookupResult>> LookupAsync(string typeId, string fieldKey, string query) =>
        RequestAsync((client, ct) => client.LookupAsync(typeId, fieldKey, query, ct));

    public Task<ActionResult> RunActionAsync(Guid instanceId, string actionKey) =>
        RequestAsync((client, ct) => client.RunActionAsync(instanceId, actionKey, ct));

    public Task<bool> RestartModuleAsync(Guid instanceId) =>
        RequestAsync((client, ct) => client.RestartModuleAsync(instanceId, ct));

    // Pas de relecture après la demande : le service annonce lui-même la vue affichée (ViewShown).
    public Task<bool> ShowViewAsync(Guid viewId) =>
        RequestAsync((client, ct) => client.ShowViewAsync(viewId, ct));

    public Task CheckUpdatesAsync() => RequestAsync(async (client, ct) =>
    {
        await client.CheckUpdatesAsync(ct).ConfigureAwait(false);
        await RefreshUpdatesAsync(client, ct).ConfigureAwait(false);
        return true;
    });

    public Task SetUpdatePreferencesAsync(LinkUpdateChannel channel, bool waitForNextStable) => RequestAsync(async (client, ct) =>
    {
        await client.SetUpdatePreferencesAsync(channel, waitForNextStable, ct).ConfigureAwait(false);
        await RefreshUpdatesAsync(client, ct).ConfigureAwait(false);
        return true;
    });

    // Pas de relecture après l'installation : l'installateur arrête le service, la fenêtre se ferme avec lui.
    public Task<ActionResult> InstallUpdateAsync(bool confirmDowngrade) =>
        RequestAsync((client, ct) => client.InstallUpdateAsync(confirmDowngrade, ct));

    /// <summary>
    /// Une demande au service. Une liaison fermée ou coupée en route (annulation, objet libéré) devient une
    /// <see cref="ControlException"/> en français : l'appelant (fermeture de la fenêtre comprise) ne voit jamais l'exception technique.
    /// Un service qui ne répond pas dans le délai (<see cref="TimeoutException"/>) devient lui aussi une <see cref="ControlException"/>
    /// (« Le service d'affichage ne répond pas. ») : aucun appelant n'a à connaître l'exception du délai.
    /// </summary>
    internal async Task<T> RequestAsync<T>(Func<ControlClient, CancellationToken, Task<T>> request)
    {
        var client = Current();
        try
        {
            return await request(client, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            throw new ControlException(Texts.LinkCut);
        }
        catch (TimeoutException)
        {
            throw new ControlException(Texts.LinkLost);
        }
    }

    // ---- Modules complémentaires ----

    public Task<CommunityOverview> GetCommunityAsync() => RequestAsync((client, ct) => client.GetCommunityAsync(ct));

    public Task<PackageInfo> InspectPackageAsync(string path) => RequestAsync((client, ct) => client.InspectPackageAsync(path, ct));

    public Task<ActionResult> InstallPackageAsync(string path, IReadOnlyList<ModulePermission> granted) =>
        ChangeModulesAsync((client, ct) => client.InstallPackageAsync(path, granted, ct));

    public Task<ActionResult> ApproveModuleAsync(string id, IReadOnlyList<ModulePermission> granted) =>
        ChangeModulesAsync((client, ct) => client.ApproveModuleAsync(id, granted, ct));

    public Task<ActionResult> SetModulePermissionsAsync(string id, IReadOnlyList<ModulePermission> granted) =>
        ChangeModulesAsync((client, ct) => client.SetModulePermissionsAsync(id, granted, ct));

    public Task<ActionResult> UninstallModuleAsync(string id) =>
        ChangeModulesAsync((client, ct) => client.UninstallModuleAsync(id, ct));

    public Task<ActionResult> RelaunchModuleAsync(string id) =>
        ChangeModulesAsync((client, ct) => client.RelaunchModuleAsync(id, ct));

    public Task<ActionResult> SetModulesRootAsync(string path) =>
        ChangeModulesAsync((client, ct) => client.SetModulesRootAsync(path, ct));

    public Task<ActionResult> SetDeveloperModeAsync(bool on) =>
        ChangeModulesAsync((client, ct) => client.SetDeveloperModeAsync(on, ct));

    public Task<ActionResult> AddDevFolderAsync(string path, IReadOnlyList<ModulePermission> granted) =>
        ChangeModulesAsync((client, ct) => client.AddDevFolderAsync(path, granted, ct));

    public Task<PackageInfo> InspectDevFolderAsync(string path) => RequestAsync((client, ct) => client.InspectDevFolderAsync(path, ct));

    public Task<ActionResult> RemoveDevFolderAsync(string path) =>
        ChangeModulesAsync((client, ct) => client.RemoveDevFolderAsync(path, ct));

    public Task<ActionResult> ReloadDevModuleAsync(string id) =>
        ChangeModulesAsync((client, ct) => client.ReloadDevModuleAsync(id, ct));

    public Task CheckModuleUpdatesAsync() => RequestAsync(async (client, ct) =>
    {
        await client.CheckModuleUpdatesAsync(ct).ConfigureAwait(false);
        return true;
    });

    // Préparer ne change aucun module (le fichier reste chez le service) : pas de relecture des types, contrairement à l'installation.
    public Task<ModuleUpdatePreparation> PrepareModuleUpdateAsync(string id) =>
        RequestAsync((client, ct) => client.PrepareModuleUpdateAsync(id, ct));

    public Task CancelModuleUpdateAsync(string id) => RequestAsync(async (client, ct) =>
    {
        await client.CancelModuleUpdateAsync(id, ct).ConfigureAwait(false);
        return true;
    });

    public Task<ActionResult> InstallModuleUpdateAsync(string id, IReadOnlyList<ModulePermission> granted) =>
        ChangeModulesAsync((client, ct) => client.InstallModuleUpdateAsync(id, granted, ct));

    public Task<IReadOnlyList<ModuleConsumption>> GetConsumptionAsync() => RequestAsync((client, ct) => client.GetConsumptionAsync(ct));

    /// <summary>
    /// Une demande qui change les modules complémentaires : au retour, les types sont relus et publiés, qu'elle ait réussi, été refusée
    /// ou même levé une erreur (un délai peut passer pendant que le service finit son travail : la liste doit dire ce qui s'est passé).
    /// </summary>
    private Task<ActionResult> ChangeModulesAsync(Func<ControlClient, CancellationToken, Task<ActionResult>> request) =>
        RequestAsync(async (client, ct) =>
        {
            try
            {
                return await request(client, ct).ConfigureAwait(false);
            }
            finally
            {
                await RefreshTypesAsync(client, ct).ConfigureAwait(false);
            }
        });

    /// <summary>
    /// Relit les types de modules et les publie tout de suite. Le compteur change avant la lecture : une lecture plus ancienne qui arrive
    /// après une plus récente est écartée, comme pour les mises à jour. Une lecture qui échoue (service qui refuse, délai, liaison
    /// fermée) ne change rien et ne fait pas échouer la demande qui l'a précédée : on garde ce qu'on avait.
    /// </summary>
    private async Task RefreshTypesAsync(ControlClient client, CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _typesGeneration);
        IReadOnlyList<ModuleTypeInfo> types;
        try { types = await TypesReader(client, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ControlException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        Update(() =>
        {
            if (generation == Volatile.Read(ref _typesGeneration) && State == LinkState.Connected)
                Types = types;
        });
    }

    public Task<bool> StartServiceAsync()
    {
        var started = _starter.TryStart();
        if (started)
        {
            // Le service met quelques secondes à ouvrir son tube : on repart de zéro avant de le déclarer arrêté.
            Volatile.Write(ref _failures, 0);
            Update(() => State = LinkState.Connecting, onlyIf: () => State == LinkState.ServiceStopped);
        }

        return Task.FromResult(started);
    }

    private ControlClient Current()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ControlException(Texts.LinkCut);
        lock (_gate)
            return _client ?? throw new ControlException(Texts.LinkLost);
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var attemptStarted = DateTime.UtcNow;
            ControlClient? client = null;
            try
            {
                client = await ControlClient.ConnectAsync(_pipeName, _connectTimeout, ct).ConfigureAwait(false);
                Volatile.Write(ref _failures, 0);
                await ServeAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break; // La liaison se ferme : une erreur de dernière minute n'intéresse personne.
            }
            catch (ProtocolMismatchException)
            {
                // Le service tourne mais parle une autre version : ce n'est pas un service arrêté. On continue d'essayer (il sera peut-être
                // mis à jour entre-temps), sans compter ces essais comme des échecs de connexion.
                Volatile.Write(ref _failures, 0);
                Update(() => { State = LinkState.VersionMismatch; Welcome = null; Status = null; Updates = null; Types = null; ShownViewId = null; }, onlyIf: () => State != LinkState.VersionMismatch);
            }
            catch (Exception)
            {
                // Pas de service, version de protocole différente, tube qui se ferme, erreur inattendue du système : un échec
                // de plus. Aucune exception ne remonte vers l'interface.
                if (client is null && Interlocked.Increment(ref _failures) >= FailuresBeforeStopped)
                    Update(() => { State = LinkState.ServiceStopped; Welcome = null; Status = null; Updates = null; Types = null; ShownViewId = null; }, onlyIf: () => State != LinkState.ServiceStopped);
            }
            finally
            {
                if (client is not null)
                {
                    lock (_gate)
                        _client = null;
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            // Connectée puis coupée : on repart à « Connecting » ; ServiceStopped ne se déclare qu'après trois échecs de connexion.
            if (client is not null && !ct.IsCancellationRequested)
                Update(() => { State = LinkState.Connecting; Welcome = null; Status = null; Updates = null; Types = null; ShownViewId = null; });

            var wait = _retryDelay - (DateTime.UtcNow - attemptStarted);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Connectée : abonne l'aperçu, publie l'état, puis relit l'état jusqu'à la coupure.</summary>
    private async Task ServeAsync(ControlClient client, CancellationToken ct)
    {
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => lost.TrySetResult();
        client.FrameReceived += ReceiveFrame;
        client.ViewShown += ReceiveViewShown;

        lock (_gate)
            _client = client;

        // Comme les changements de la fenêtre, la première demande passe par la file : elle lit au dernier moment ce qui est voulu.
        await _subscribeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int fps;
            Guid? previewView;
            lock (_gate)
            {
                fps = WantedFps();
                previewView = _previewViewId;
            }

            await SubscribeAsync(client, fps, previewView, ct).ConfigureAwait(false);
        }
        finally
        {
            _subscribeGate.Release();
        }

        var shownGeneration = Volatile.Read(ref _shownGeneration);
        var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
        var generation = Volatile.Read(ref _updatesGeneration);
        var updates = await TryGetUpdateAsync(client, ct).ConfigureAwait(false);
        Update(() =>
        {
            ApplyUpdates(updates, generation);
            State = LinkState.Connected;
            Welcome = client.Welcome;
            Types = client.Welcome.Types;
            Status = status;
            ApplyShownView(status, shownGeneration);
        });

        while (true)
        {
            var finished = await Task.WhenAny(lost.Task, Task.Delay(_statusInterval, ct)).ConfigureAwait(false);
            if (finished == lost.Task)
                return;
            shownGeneration = Volatile.Read(ref _shownGeneration);
            status = await client.GetStatusAsync(ct).ConfigureAwait(false);
            generation = Volatile.Read(ref _updatesGeneration);
            updates = await TryGetUpdateAsync(client, ct).ConfigureAwait(false);
            Update(() => { Status = status; ApplyUpdates(updates, generation); ApplyShownView(status, shownGeneration); });
        }
    }

    /// <summary>Le service annonce une nouvelle vue affichée (fil de lecture) : publiée sur le fil de l'interface.</summary>
    private void ReceiveViewShown(Guid viewId)
    {
        // Le compteur change avant la publication : une lecture de l'état partie plus tôt sera écartée à son arrivée.
        Interlocked.Increment(ref _shownGeneration);
        Update(() => ShownViewId = viewId, onlyIf: () => ShownViewId != viewId);
    }

    /// <summary>La vue affichée d'après une lecture de l'état, sauf si une annonce du service l'a dépassée depuis.</summary>
    private void ApplyShownView(ServiceStatus status, long generation)
    {
        if (generation == Volatile.Read(ref _shownGeneration) && status.CurrentViewId != Guid.Empty)
            ShownViewId = status.CurrentViewId;
    }

    /// <summary>
    /// Publie une lecture des mises à jour (sur le fil de l'interface), sauf si elle est périmée : une demande de la fenêtre
    /// (<see cref="RefreshUpdatesAsync"/>) a relu l'état depuis, et cette lecture-ci a pu partir avant. Une valeur égale à l'actuelle
    /// n'est pas republiée.
    /// </summary>
    private void ApplyUpdates(UpdateInfo? updates, long generation)
    {
        if (updates is null || generation != Volatile.Read(ref _updatesGeneration) || Equals(Updates, updates))
            return;
        Updates = updates;
    }

    /// <summary>Relit l'état des mises à jour et le publie tout de suite (sans attendre la relecture de la seconde suivante).</summary>
    private async Task RefreshUpdatesAsync(ControlClient client, CancellationToken ct)
    {
        // Le compteur change avant la lecture : toute lecture de la boucle déjà partie sera écartée à son arrivée.
        var generation = Interlocked.Increment(ref _updatesGeneration);
        var updates = await TryGetUpdateAsync(client, ct).ConfigureAwait(false);
        Update(() => ApplyUpdates(updates, generation));
    }

    // Un service qui refuse la demande (version plus ancienne) ne coupe pas la liaison : on garde ce qu'on avait.
    private static async Task<UpdateInfo?> TryGetUpdateAsync(ControlClient client, CancellationToken ct)
    {
        try { return await client.GetUpdateAsync(ct).ConfigureAwait(false); }
        catch (ControlException) { return null; }
    }

    private async Task SubscribeQuietlyAsync(ControlClient client, int fps, Guid? viewId)
    {
        try { await SubscribeAsync(client, fps, viewId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ControlException or TimeoutException or OperationCanceledException)
        {
            // La liaison est coupée ou le service ne répond pas : la boucle de connexion s'en occupe.
        }
    }

    /// <summary>
    /// Abonne l'aperçu à la vue voulue. Une vue que le service ne connaît pas (retirée entre-temps) lui fait refuser la demande,
    /// et il se désabonne alors de l'ancienne : on retombe sur la vue affichée plutôt que de laisser la bande figée.
    /// </summary>
    private async Task SubscribeAsync(ControlClient client, int fps, Guid? viewId, CancellationToken ct)
    {
        try
        {
            await client.SubscribePreviewAsync(fps, viewId, ct).ConfigureAwait(false);
        }
        catch (ControlException) when (viewId is not null)
        {
            await client.SubscribePreviewAsync(fps, null, ct).ConfigureAwait(false);
            // L'aperçu est bien sur la vue affichée : ce que la liaison dit demander doit le dire aussi (la fenêtre s'en sert pour redemander).
            lock (_gate)
            {
                if (_previewViewId == viewId)
                    _previewViewId = null;
            }
        }
    }

    /// <summary>Change l'état sur le fil de l'interface, puis lève <see cref="Changed"/>.</summary>
    private void Update(Action change, Func<bool>? onlyIf = null)
    {
        Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0 || (onlyIf is not null && !onlyIf()))
                return;
            change();
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Une image arrive du service (fil de lecture). Au plus un envoi est en attente vers l'interface : une image plus
    /// récente remplace celle qui attend (la dernière gagne), si bien qu'une interface bloquée n'empile jamais d'images.
    /// </summary>
    internal void ReceiveFrame(PreviewFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (Interlocked.Exchange(ref _pendingFrame, frame) is null)
            Post(DeliverFrame);
    }

    private void DeliverFrame()
    {
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is null || Volatile.Read(ref _disposed) != 0)
            return;
        FrameReceived?.Invoke(frame);
    }

    private void Post(Action action)
    {
        if (_post is null)
            action();
        else
            _post(action);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Volatile.Write(ref _pendingFrame, null);
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { /* la boucle se termine d'elle-même ; rien à remonter à la fermeture */ }
        _cts.Dispose();
    }
}
