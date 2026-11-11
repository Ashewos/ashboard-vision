using System.IO.Pipes;
using System.Text.Json;
using AshboardVision.Sdk;

namespace Trofeo.Control;

/// <summary>
/// Côté service : écoute le tube nommé et répond aux requêtes de la fenêtre de réglage en s'appuyant sur un
/// <see cref="IControlHandler"/>. Jusqu'à <see cref="MaxClients"/> clients à la fois ; les suivants attendent
/// qu'un des premiers parte.
/// </summary>
public sealed class ControlServer(string pipeName, IControlHandler handler) : IDisposable
{
    public const int MaxClients = 4;

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);
    private readonly object _gate = new();
    private readonly HashSet<Session> _sessions = [];
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>Commence à écouter ; rend la main tout de suite.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acceptLoop is not null)
                throw new InvalidOperationException("Le serveur est déjà démarré.");
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
            handler.ViewChanged += OnViewChanged;
        }
    }

    /// <summary>La vue affichée a changé : chaque client connecté le sait, sans que l'appelant (le fil d'affichage ou celui de l'interface) attende.</summary>
    private void OnViewChanged(Guid viewId)
    {
        Session[] sessions;
        lock (_gate)
            sessions = [.. _sessions];

        foreach (var session in sessions)
            session.PushView(viewId);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var slotTaken = false;
            NamedPipeServerStream? pipe = null;
            try
            {
                // Un emplacement par client : tant que les 4 sont pris, aucune instance du tube n'écoute et le suivant patiente.
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                slotTaken = true;
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var session = new Session(pipe, handler);
                lock (_gate)
                {
                    if (_disposed)
                        throw new OperationCanceledException(ct);
                    _sessions.Add(session);
                }
                _ = RunSessionAsync(session);
                // La session possède désormais le tube et l'emplacement.
                pipe = null;
                slotTaken = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Un client qui part avant d'être accepté, ou un tube impossible à créer : on réessaie sans boucler à vide.
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
                if (slotTaken)
                    _slots.Release();
            }
        }
    }

    private async Task RunSessionAsync(Session session)
    {
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            lock (_gate)
                _sessions.Remove(session);
            try { _slots.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        Session[] sessions;
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            sessions = [.. _sessions];
            loop = _acceptLoop;
        }

        handler.ViewChanged -= OnViewChanged;
        _stop.Cancel();
        foreach (var session in sessions)
            session.Dispose();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    /// <summary>Une connexion cliente : un fil de lecture, des écritures sérialisées, une file d'une image.</summary>
    private sealed class Session : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly IControlHandler _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ImageSlot _slot = new();
        private readonly ViewSlot _viewSlot = new();
        private readonly object _previewGate = new();
        private IDisposable? _preview;
        private int _disposed;

        public Session(NamedPipeServerStream pipe, IControlHandler handler)
        {
            _pipe = pipe;
            _handler = handler;
        }

        public async Task RunAsync()
        {
            var ct = _cts.Token;
            var pump = Task.Run(() => PumpImagesAsync(ct));
            var viewPump = Task.Run(() => PumpViewsAsync(ct));
            try
            {
                while (true)
                {
                    var frame = await Framing.ReadAsync(_pipe, ct).ConfigureAwait(false);
                    if (frame is null)
                        break;
                    if (frame.Value.Kind != FrameKind.Json)
                        continue;

                    var request = JsonSerializer.Deserialize<Request>(frame.Value.Payload, ControlJson.Options)
                        ?? throw new InvalidDataException("Requête vide.");
                    // L'abonnement se règle ici, dans l'ordre de réception ; le reste s'exécute en parallèle.
                    var work = request.Type == MessageTypes.Preview
                        ? Task.FromResult(HandlePreview(request))
                        : Task.Run(() => HandleAsync(request, ct), ct);
                    _ = RespondAsync(request.Id, work, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                          or JsonException or InvalidDataException)
            {
                // Connexion coupée ou protocole violé : on referme.
            }
            finally
            {
                Dispose();
                try { await pump.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                try { await viewPump.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        private async Task<Response> HandleAsync(Request request, CancellationToken ct)
        {
            switch (request.Type)
            {
                case MessageTypes.Hello:
                    return new Response(request.Id, request.Type) { Welcome = _handler.Hello() };
                case MessageTypes.Status:
                    return new Response(request.Id, request.Type) { Status = _handler.GetStatus() };
                case MessageTypes.Reload:
                    return new Response(request.Id, request.Type) { Reloaded = await _handler.ReloadAsync(ct).ConfigureAwait(false) };
                case MessageTypes.Restart:
                    return new Response(request.Id, request.Type)
                    {
                        Restarted = await _handler.RestartModuleAsync(Required(request.InstanceId, "instanceId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.Lookup:
                    return new Response(request.Id, request.Type)
                    {
                        Results = await _handler.LookupAsync(Required(request.TypeId, "typeId"), Required(request.FieldKey, "fieldKey"),
                            request.Query ?? "", ct).ConfigureAwait(false),
                    };
                case MessageTypes.Action:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.RunActionAsync(Required(request.InstanceId, "instanceId"), Required(request.ActionKey, "actionKey"), ct)
                            .ConfigureAwait(false),
                    };
                case MessageTypes.ShowView:
                    return new Response(request.Id, request.Type)
                    {
                        Shown = await _handler.ShowViewAsync(Required(request.ViewId, "viewId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.GetUpdate:
                    return new Response(request.Id, request.Type) { Update = _handler.GetUpdate() };
                case MessageTypes.CheckUpdates:
                    await _handler.CheckUpdatesAsync(ct).ConfigureAwait(false);
                    return new Response(request.Id, request.Type);
                case MessageTypes.SetUpdatePreferences:
                    await _handler.SetUpdatePreferencesAsync(
                        Required(request.Channel, "channel"), Required(request.WaitForNextStable, "waitForNextStable"), ct).ConfigureAwait(false);
                    return new Response(request.Id, request.Type);
                case MessageTypes.InstallUpdate:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.InstallUpdateAsync(Required(request.ConfirmDowngrade, "confirmDowngrade"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.Types:
                    return new Response(request.Id, request.Type) { Types = _handler.GetTypes() };
                case MessageTypes.Community:
                    return new Response(request.Id, request.Type) { Community = _handler.GetCommunity() };
                case MessageTypes.InspectPackage:
                    return new Response(request.Id, request.Type) { Package = _handler.InspectPackage(Required(request.Path, "path")) };
                case MessageTypes.InstallPackage:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.InstallPackageAsync(Required(request.Path, "path"), Permissions(request), ct).ConfigureAwait(false),
                    };
                case MessageTypes.ApproveModule:
                    return new Response(request.Id, request.Type)
                    {
                        Action = _handler.ApproveModule(Required(request.ModuleId, "moduleId"), Permissions(request)),
                    };
                case MessageTypes.SetModulePermissions:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.SetModulePermissionsAsync(Required(request.ModuleId, "moduleId"), Permissions(request), ct)
                            .ConfigureAwait(false),
                    };
                case MessageTypes.UninstallModule:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.UninstallModuleAsync(Required(request.ModuleId, "moduleId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.RelaunchModule:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.RelaunchModuleAsync(Required(request.ModuleId, "moduleId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.SetModulesRoot:
                    return new Response(request.Id, request.Type) { Action = _handler.SetModulesRoot(Required(request.Path, "path")) };
                case MessageTypes.SetDeveloperMode:
                    return new Response(request.Id, request.Type) { Action = _handler.SetDeveloperMode(Required(request.On, "on")) };
                case MessageTypes.AddDevFolder:
                    return new Response(request.Id, request.Type)
                    {
                        Action = _handler.AddDevFolder(Required(request.Path, "path"), Permissions(request)),
                    };
                case MessageTypes.InspectDevFolder:
                    return new Response(request.Id, request.Type) { Package = _handler.InspectDevFolder(Required(request.Path, "path")) };
                case MessageTypes.RemoveDevFolder:
                    return new Response(request.Id, request.Type) { Action = _handler.RemoveDevFolder(Required(request.Path, "path")) };
                case MessageTypes.ReloadDevModule:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.ReloadDevModuleAsync(Required(request.ModuleId, "moduleId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.CheckModuleUpdates:
                    await _handler.CheckModuleUpdatesAsync(ct).ConfigureAwait(false);
                    return new Response(request.Id, request.Type);
                case MessageTypes.PrepareModuleUpdate:
                    return new Response(request.Id, request.Type)
                    {
                        Preparation = await _handler.PrepareModuleUpdateAsync(Required(request.ModuleId, "moduleId"), ct).ConfigureAwait(false),
                    };
                case MessageTypes.CancelModuleUpdate:
                    _handler.CancelModuleUpdate(Required(request.ModuleId, "moduleId"));
                    return new Response(request.Id, request.Type);
                case MessageTypes.InstallModuleUpdate:
                    return new Response(request.Id, request.Type)
                    {
                        Action = await _handler.InstallModuleUpdateAsync(Required(request.ModuleId, "moduleId"), Permissions(request), ct)
                            .ConfigureAwait(false),
                    };
                case MessageTypes.Consumption:
                    return new Response(request.Id, request.Type) { Consumption = _handler.GetConsumption() };
                default:
                    throw new InvalidOperationException($"Requête inconnue : « {request.Type} ».");
            }
        }

        /// <summary>
        /// Les permissions de la requête. Un nom inconnu est déjà refusé à la lecture du JSON (la connexion se referme) ; un nombre qui
        /// n'est celui d'aucune permission est refusé ici, par une erreur.
        /// </summary>
        private static IReadOnlyList<ModulePermission> Permissions(Request request)
        {
            var granted = request.Granted ?? throw new InvalidOperationException("Paramètre manquant : granted.");
            foreach (var permission in granted)
            {
                if (!Enum.IsDefined(permission))
                    throw new InvalidOperationException($"Permission inconnue : {(int)permission}.");
            }

            return granted;
        }

        private Response HandlePreview(Request request)
        {
            var maxFps = request.MaxFps ?? 0;
            lock (_previewGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return new Response(request.Id, request.Type);

                var previous = _preview;
                _preview = null;
                previous?.Dispose();
                _slot.Clear();
                if (maxFps > 0)
                {
                    try { _preview = _handler.SubscribePreview(maxFps, request.ViewId, _slot.Offer); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
                    {
                        // Une erreur du service revient au client en « error », comme pour les autres requêtes.
                        return new Response(request.Id, MessageTypes.Error) { Message = ex.Message };
                    }
                }
            }
            return new Response(request.Id, request.Type);
        }

        private async Task RespondAsync(int id, Task<Response> work, CancellationToken ct)
        {
            Response response;
            try
            {
                response = await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                response = new Response(id, MessageTypes.Error) { Message = ex.Message };
            }

            try
            {
                await WriteJsonAsync(response, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Le client est parti.
            }
        }

        private async Task WriteJsonAsync(Response response, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ControlJson.Options);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await Framing.WriteJsonAsync(_pipe, bytes, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        /// <summary>Une seule image en cours d'écriture ; pendant ce temps, la dernière publiée attend dans l'emplacement.</summary>
        private async Task PumpImagesAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var frame = await _slot.TakeAsync(ct).ConfigureAwait(false);
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try { await Framing.WriteImageAsync(_pipe, frame.Rotation, frame.Jpeg, ct).ConfigureAwait(false); }
                    finally { _writeLock.Release(); }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
            {
                // Connexion fermée, ou image trop grosse : on referme.
                Dispose();
            }
        }

        /// <summary>
        /// Annonce à ce client la vue affichée. Ne bloque jamais : seule la dernière vue annoncée compte, une annonce qui n'est pas
        /// encore partie est remplacée par la suivante.
        /// </summary>
        public void PushView(Guid viewId)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _viewSlot.Offer(viewId);
        }

        /// <summary>Écrit les annonces de vue, une à la fois, dans l'ordre d'arrivée des changements (la dernière valeur seulement si le client est lent).</summary>
        private async Task PumpViewsAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var viewId = await _viewSlot.TakeAsync(ct).ConfigureAwait(false);
                    await WriteJsonAsync(new Response(0, MessageTypes.ViewShown) { ViewId = viewId }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
            {
                // Connexion fermée : on referme.
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cts.Cancel();
            lock (_previewGate)
            {
                var preview = _preview;
                _preview = null;
                try { preview?.Dispose(); }
                catch { /* un abonnement qui échoue à se libérer ne doit pas empêcher la fermeture */ }
            }
            _slot.Clear();
            _pipe.Dispose();
        }

        private static T Required<T>(T? value, string name) where T : struct =>
            value ?? throw new InvalidOperationException($"Paramètre manquant : {name}.");

        private static string Required(string? value, string name) =>
            value ?? throw new InvalidOperationException($"Paramètre manquant : {name}.");
    }
}

/// <summary>
/// Emplacement d'une image en attente. <see cref="Offer"/> ne bloque jamais (il remplace l'image en attente, qui est perdue) :
/// il s'appelle depuis le fil d'affichage.
/// </summary>
internal sealed class ImageSlot
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private PreviewFrame? _pending;

    public void Offer(PreviewFrame frame)
    {
        Volatile.Write(ref _pending, frame);
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public async Task<PreviewFrame> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            var frame = Interlocked.Exchange(ref _pending, null);
            if (frame is not null)
                return frame;
        }
    }

    public void Clear() => Interlocked.Exchange(ref _pending, null);
}

/// <summary>
/// Emplacement de la dernière vue annoncée. <see cref="Offer"/> ne bloque jamais (il remplace la vue en attente) : il s'appelle
/// depuis le fil qui change la vue.
/// </summary>
internal sealed class ViewSlot
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _gate = new();
    private Guid? _pending;

    public void Offer(Guid viewId)
    {
        lock (_gate)
            _pending = viewId;

        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public async Task<Guid> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending is { } viewId)
                {
                    _pending = null;
                    return viewId;
                }
            }
        }
    }
}
