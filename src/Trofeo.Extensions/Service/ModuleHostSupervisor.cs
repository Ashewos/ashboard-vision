using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Core.Logging;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Le superviseur d'un type de module de la communauté : <b>un seul hôte (un processus) pour toutes les cases</b> de ce module.
/// Il ne lance l'hôte qu'à la première case (<see cref="Open"/>) et l'arrête quand la dernière se ferme : sans case, rien ne tourne
/// (ni processus, ni minuteur). Avant chaque lancement, il prend le verrou d'exécution sur les fichiers du module
/// (<see cref="ModuleFileLock"/>) et refuse de lancer des fichiers qui ne sont plus ceux que l'utilisateur a validés ; le
/// verrou dure autant que l'hôte.
/// <para>
/// <b>Pannes.</b> Un hôte qui s'arrête tout seul est relancé après 1, 2, 4, 8, 16, 32 puis 60 s (<see cref="BackoffFor"/>) ; les
/// cases affichent « Module arrêté, relance… » et gardent leur dernière image. Au troisième arrêt en 10 minutes, le module est mis
/// de côté (<c>setAside</c>) et ne repart plus avant qu'on le relance à la main.
/// </para>
/// <para>
/// <b>Fils.</b> Tout l'état est sous un verrou qui ne couvre jamais d'attente : lancement, écriture et lecture se font sur des
/// fils du pool. Ce que l'hôte écrit est une entrée hostile (voir <see cref="HostMessage.IsFromHost"/>) : un message de l'autre
/// sens est ignoré, un indice d'image hors bornes aussi, un flux illisible fait arrêter l'hôte comme une panne.
/// </para>
/// </summary>
public sealed partial class ModuleHostSupervisor : IAsyncDisposable
{
    /// <summary>Ce que montrent les cases quand l'hôte s'est arrêté et va être relancé.</summary>
    public const string HostStoppedText = "Module arrêté, relance…";

    /// <summary>Ce que montrent les cases quand les fichiers du module ne sont plus ceux qui ont été validés.</summary>
    public const string FilesChangedText = "Fichiers du module modifiés : valide-le de nouveau";

    /// <summary>La fenêtre de temps où on compte les arrêts : au troisième, le module est mis de côté.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    /// <summary>Le nombre d'arrêts dans <see cref="CrashWindow"/> qui met le module de côté.</summary>
    public const int MaxStops = 3;

    internal static readonly JsonElement EmptySettings = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Ce que montrent les cases quand les fichiers du module ne s'ouvrent pas pour le moment (antivirus, OneDrive…).</summary>
    public const string FilesBusyText = "Fichiers du module indisponibles (OneDrive ?), nouvel essai…";

    /// <summary>Le délai entre deux essais quand les fichiers du module sont occupés : plus long qu'une relance, et sans compter comme un arrêt.</summary>
    public static readonly TimeSpan FilesBusyRetry = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ce que le superviseur lance : le module tel que le catalogue le voit, son manifeste, les permissions accordées (celles que le
    /// manifeste déclare) et les champs « port » de son schéma. Remplacé d'un bloc par <see cref="ApplyEntryAsync"/> ; chaque lecture
    /// prend l'instantané une seule fois.
    /// </summary>
    private sealed record Target(
        CommunityEntry Entry, ModuleManifest Manifest, IReadOnlyList<ModulePermission> Granted, IReadOnlyList<PortField> PortFields)
    {
        public static Target Of(CommunityEntry entry, IReadOnlyList<ModulePermission> granted)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(granted);
            var manifest = entry.Manifest ?? throw new ArgumentException("Le module n'a pas de manifeste lisible.", nameof(entry));
            return new Target(
                entry, manifest, [.. granted.Distinct().Where(manifest.Permissions.Contains)], [.. manifest.Settings.Fields.OfType<PortField>()]);
        }
    }

    private readonly string _hostExecutable;
    private readonly ISecretStore _secrets;
    private readonly IServiceDataBroker _data;
    private readonly TimeProvider _time;
    private readonly Action<string, string> _setAside;
    private readonly IHostProcessFactory _launcher;
    private readonly IModuleLog _log;
    private readonly Action<bool>? _hostRunning;
    private volatile Target _target;

    private readonly object _gate = new();
    private readonly List<RemoteModule> _instances = [];
    private readonly ConcurrentDictionary<Guid, InstanceSlot> _slots = new();
    private readonly Dictionary<Guid, JsonElement> _actionSettings = [];
    private readonly List<(DateTimeOffset At, bool Memory)> _stops = [];
    private HostConnection? _session;
    private readonly List<HostConnection> _connections = [];
    private bool _launching;
    private int _launchGeneration;
    private Task _launchTask = Task.CompletedTask;
    private ITimer? _restartTimer;
    private int _consecutiveStops;
    private string? _setAsideReason;
    private string? _permanentFailure;
    private string? _waitMessage;
    private long _messageVersion;
    private bool _disposed;
    private int _maxFps = 12;

    /// <summary>Le temps laissé à un hôte pour finir après <see cref="Shutdown"/> avant qu'on le tue (réglable pour les tests).</summary>
    internal TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Le temps que <see cref="DisposeAsync"/> attend un hôte avant de le tuer (réglable pour les tests).</summary>
    internal TimeSpan DisposeGrace { get; set; } = TimeSpan.FromSeconds(3);
    private JsonElement _appearance = HostPayloads.AppearanceToJson(AppearanceSettings.Default);

    /// <param name="entry">Le module trouvé par le catalogue (son manifeste est obligatoire).</param>
    /// <param name="granted">Les permissions accordées (celles que le manifeste ne déclare pas sont ignorées).</param>
    /// <param name="hostExecutable">Le chemin complet de <c>Trofeo.ModuleHost.exe</c>.</param>
    /// <param name="secrets">Le coffre à secrets de ce module (propre à son identifiant).</param>
    /// <param name="data">Ce que le service lit pour les modules qui en ont la permission.</param>
    /// <param name="time">La source de temps (relances, délai de réponse, fenêtre des arrêts).</param>
    /// <param name="setAside">Appelée avec l'identifiant du module et la raison quand il est mis de côté.</param>
    /// <param name="launcher">Lance les hôtes ; par défaut, dans l'AppContainer et le job du module (les tests en fournissent d'autres).</param>
    /// <param name="log">Le journal du module ; par défaut, la trace du service.</param>
    /// <param name="hostRunning">
    /// Appelée avec <c>true</c> quand un hôte est installé et avec <c>false</c> quand il n'y en a plus (arrêt voulu, panne, libération),
    /// jamais deux fois de suite la même valeur. Appelée sous le verrou du superviseur : elle doit être brève et ne rappeler personne.
    /// </param>
    public ModuleHostSupervisor(
        CommunityEntry entry, IReadOnlyList<ModulePermission> granted, string hostExecutable, ISecretStore secrets,
        IServiceDataBroker data, TimeProvider time, Action<string, string> setAside, IHostProcessFactory? launcher = null,
        IModuleLog? log = null, Action<bool>? hostRunning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        _target = Target.Of(entry, granted);
        _hostExecutable = hostExecutable;
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _setAside = setAside ?? throw new ArgumentNullException(nameof(setAside));
        _launcher = launcher ?? new SandboxedHostProcessFactory();
        _log = log ?? new TraceModuleLog(entry.Id, Guid.Empty, time);
        _hostRunning = hostRunning;
    }

    /// <summary>Le nombre de cases ouvertes (qui font tourner l'hôte).</summary>
    public int ActiveInstances
    {
        get
        {
            lock (_gate)
            {
                return _instances.Count;
            }
        }
    }

    /// <summary>Ce que l'hôte en cours a consommé (job object) ; <c>null</c> s'il n'y a pas d'hôte.</summary>
    public JobUsage? Usage
    {
        get
        {
            HostConnection? session;
            lock (_gate)
            {
                session = _session;
            }

            return session?.Process.ReadUsage();
        }
    }

    /// <summary>
    /// Ce que l'hôte en cours consomme : son job object (temps processeur cumulé) et sa mémoire privée. <c>null</c> s'il n'y a pas
    /// d'hôte ou si l'un des deux ne se mesure pas. Ne lève jamais ; ne touche pas au verrou du superviseur plus qu'un instant.
    /// </summary>
    public (JobUsage Usage, long PrivateBytes)? Measure()
    {
        HostConnection? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return null;
        }

        try
        {
            return session.Process.ReadUsage() is { } usage && session.Process.ReadPrivateBytes() is { } bytes ? (usage, bytes) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            return null; // l'hôte se ferme pendant la mesure
        }
    }

    /// <summary>Change la connexion à l'hôte en cours et annonce, s'il y a lieu, qu'un hôte tourne ou ne tourne plus. À appeler sous le verrou.</summary>
    private void SetSession(HostConnection? session)
    {
        var wasRunning = _session is not null;
        _session = session;
        if (wasRunning != (session is not null))
        {
            try
            {
                _hostRunning?.Invoke(session is not null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warning($"Le suivi de la consommation a échoué : {ex.Message}");
            }
        }
    }

    /// <summary>L'attente avant la relance qui suit le <paramref name="consecutiveStops"/>-ième arrêt de suite : 1, 2, 4, 8, 16, 32 puis 60 s.</summary>
    public static TimeSpan BackoffFor(int consecutiveStops) => consecutiveStops switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(4),
        4 => TimeSpan.FromSeconds(8),
        5 => TimeSpan.FromSeconds(16),
        6 => TimeSpan.FromSeconds(32),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Ouvre une case. Rend la main tout de suite : le premier appel fait lancer l'hôte (sur un fil du pool), la case ne dessine
    /// rien avant sa première image. Un module mis de côté, ou dont les fichiers ont changé, ouvre une case qui le dit.
    /// </summary>
    public RemoteModule Open(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = new RemoteModule(this, _target.Manifest.ToDescriptor(), context, _time);
        string? message;
        long version = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instances.Add(module);
            message = _setAsideReason ?? _permanentFailure ?? (_restartTimer is not null ? _waitMessage ?? HostStoppedText : null);
            if (message is null && _session is null && !_launching)
            {
                StartLaunchLocked();
            }

            if (message is not null)
            {
                version = ++_messageVersion;
            }
        }

        if (message is not null)
        {
            module.SetSupervisorMessage(message, version);
        }

        return module;
    }

    /// <summary>
    /// Arrête l'hôte (même mis de côté) et le relance tout de suite avec l'état courant : nouvelles permissions, fichiers
    /// rechargés. Remet à zéro les arrêts comptés et lève la mise de côté. Rend la main une fois le nouvel hôte lancé.
    /// </summary>
    public Task RestartAsync() => RestartCoreAsync(null);

    /// <summary>
    /// Remplace le module que ce superviseur lance (nouvelle version, nouveau dossier, permissions changées) puis fait comme
    /// <see cref="RestartAsync"/> : l'hôte en cours est arrêté et un neuf est lancé avec ce qui est accordé maintenant (les
    /// capacités d'un AppContainer sont fixées au lancement). Les cases ouvertes restent ouvertes ; elles gardent le descripteur
    /// qu'elles ont reçu à leur création. Sans case ouverte, rien n'est lancé. Lève <see cref="ArgumentException"/> si l'identifiant
    /// n'est pas celui du module supervisé.
    /// </summary>
    /// <param name="entry">Le module tel que le catalogue le voit maintenant (son manifeste est obligatoire).</param>
    /// <param name="granted">Les permissions accordées (celles que le manifeste ne déclare pas sont ignorées).</param>
    public Task ApplyEntryAsync(CommunityEntry entry, IReadOnlyList<ModulePermission> granted)
    {
        var replacement = Target.Of(entry, granted);
        if (!string.Equals(replacement.Entry.Id, _target.Entry.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Un superviseur ne change pas de module.", nameof(entry));
        }

        return RestartCoreAsync(replacement);
    }

    private async Task RestartCoreAsync(Target? replacement)
    {
        HostConnection? old;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (replacement is not null)
            {
                _target = replacement; // dans le même verrou que le changement de génération : aucun lancement ne mélange l'ancien et le neuf
            }

            _setAsideReason = null;
            _permanentFailure = null;
            _stops.Clear();
            _consecutiveStops = 0;
            CancelRestartTimerLocked();
            _launchGeneration++;
            _launching = false;
            old = _session;
            SetSession(null);
            if (old is not null)
            {
                old.ExpectedStop = true;
            }
        }

        if (old is not null)
        {
            old.KillQuietly();
            await WaitQuietlyAsync(old.Done, ShutdownGrace).ConfigureAwait(false);
        }

        Task launch;
        Broadcast stopped;
        lock (_gate)
        {
            if (_disposed || _instances.Count == 0)
            {
                return;
            }

            stopped = DecideLocked(HostStoppedText);
            if (_session is null && !_launching)
            {
                StartLaunchLocked();
            }

            launch = _launchTask;
        }

        stopped.Apply();
        await launch.ConfigureAwait(false);
    }

    /// <summary>Change le plafond d'images par seconde (l'hôte est prévenu s'il tourne ; sinon il l'apprend à son lancement).</summary>
    public void SetMaxFps(int fps)
    {
        lock (_gate)
        {
            var value = Math.Max(1, fps);
            if (value == _maxFps)
            {
                return;
            }

            _maxFps = value;
            _session?.Post(new Limits(value));
        }
    }

    /// <summary>
    /// Change l'apparence (<see cref="HostPayloads.AppearanceToJson"/>). Si l'hôte tourne, il redessine toutes les cases, et chacune
    /// attend une image neuve.
    /// </summary>
    public void SetAppearance(JsonElement appearance)
    {
        lock (_gate)
        {
            _appearance = appearance.ValueKind == JsonValueKind.Undefined ? HostPayloads.AppearanceToJson(AppearanceSettings.Default) : appearance.Clone();
            if (_session is { } session)
            {
                // On arme l'attente avant d'écrire : l'image qui répond ne peut pas arriver avant.
                foreach (var module in _instances)
                {
                    if (module.Slot is not null)
                    {
                        module.BeginAwaitingFrame();
                    }
                }

                session.Post(new AppearanceChanged(_appearance));
            }
        }
    }

    /// <summary>
    /// Arrête l'hôte proprement (il a quelques secondes pour finir, sinon il est tué) et libère les images partagées. Les cases
    /// encore ouvertes ne dessinent plus rien de neuf.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        HostConnection? session;
        HostConnection[] others;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _launchGeneration++;
            CancelRestartTimerLocked();
            session = _session;
            SetSession(null);
            if (session is not null)
            {
                session.ExpectedStop = true;
                session.Post(new Shutdown());
            }

            others = [.. _connections.Where(connection => !ReferenceEquals(connection, session))];
        }

        // Les autres connexions vivantes (dernière case fermée, lancement périmé, panne en cours de nettoyage) ne sont plus la session :
        // personne ne les écoute, on ne leur laisse pas leur minuteur de 5 s.
        foreach (var connection in others)
        {
            connection.KillQuietly();
        }

        if (session is not null)
        {
            if (!await WaitQuietlyAsync(session.Done, DisposeGrace).ConfigureAwait(false))
            {
                session.KillQuietly();
                await WaitQuietlyAsync(session.Done, ShutdownGrace).ConfigureAwait(false);
            }
        }

        await WaitQuietlyAsync(_launchTask, ShutdownGrace).ConfigureAwait(false);

        // Un lancement qui finissait pendant l'attente a pu créer une connexion neuve (arrêtée par Install) : on attend aussi toutes celles-là,
        // pour que plus aucun fichier du module ne soit tenu quand on rend la main.
        HostConnection[] remaining;
        lock (_gate)
        {
            remaining = [.. _connections];
        }

        foreach (var connection in remaining)
        {
            connection.KillQuietly();
        }

        foreach (var connection in remaining)
        {
            await WaitQuietlyAsync(connection.Done, ShutdownGrace).ConfigureAwait(false);
        }

        foreach (var connection in others)
        {
            await WaitQuietlyAsync(connection.Done, ShutdownGrace).ConfigureAwait(false);
        }

        foreach (var slot in _slots.Values)
        {
            slot.Dispose();
        }

        _slots.Clear();
    }

    // --- Ce que les cases demandent ---------------------------------------------------------------------------------------------

    /// <summary>Une case a une taille (la première fois) ou une autre : on (ré)ouvre son instance dans l'hôte, avec de nouvelles images.</summary>
    internal void GeometryChanged(RemoteModule module, FrameGeometry geometry)
    {
        InstanceSlot slot;
        try
        {
            slot = new InstanceSlot(module, geometry);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _log.Error($"Impossible de créer les images partagées ({geometry.Width}×{geometry.Height}) : {ex.Message}");
            module.SetSupervisorMessage("Images partagées indisponibles");
            return;
        }

        InstanceSlot? old = null;
        var keep = false;
        lock (_gate)
        {
            if (!_disposed && _instances.Contains(module))
            {
                keep = true;
                old = module.Slot;
                module.Slot = slot;
                _slots[slot.Id] = slot;
                if (old is not null)
                {
                    _slots.TryRemove(old.Id, out _);
                }

                module.BeginAwaitingFrame(); // avant d'écrire : l'image qui répond ne peut pas arriver avant
                if (_session is { } session)
                {
                    if (old is not null)
                    {
                        session.Post(new Close(old.Id));
                    }

                    SendOpen(session, module, slot);
                }
            }
        }

        if (!keep)
        {
            slot.Dispose();
            return;
        }

        old?.Dispose();
    }

    /// <summary>Une case se ferme : son instance est refermée dans l'hôte, et l'hôte lui-même s'arrête avec la dernière.</summary>
    internal void Release(RemoteModule module)
    {
        InstanceSlot? slot;
        lock (_gate)
        {
            if (!_instances.Remove(module))
            {
                return;
            }

            slot = module.Slot;
            module.Slot = null;
            if (slot is not null)
            {
                _slots.TryRemove(slot.Id, out _);
                _session?.Post(new Close(slot.Id));
            }

            if (_instances.Count == 0)
            {
                _launchGeneration++; // un lancement en cours s'abandonne
                _launching = false;
                CancelRestartTimerLocked();
                if (_session is { } session)
                {
                    SetSession(null);
                    StopGracefullyLocked(session);
                }
            }
        }

        slot?.Dispose();
    }

    // --- Lancement --------------------------------------------------------------------------------------------------------------

    private void StartLaunchLocked()
    {
        _launching = true;
        var generation = ++_launchGeneration;
        _launchTask = Task.Run(() => Launch(generation));
    }

    private static string? LaunchRefusal(CommunityEntry entry)
    {
        if (entry.State != CommunityState.Ready || (!entry.FromDevFolder && entry.Approval is null))
        {
            return "À valider dans la fenêtre de réglage";
        }

        return null;
    }

    private void Launch(int generation)
    {
        ModuleFileLock? fileLock = null;
        AnonymousPipeServerStream? toHost = null;
        AnonymousPipeServerStream? fromHost = null;
        IHostProcess? process = null;
        HostConnection connection;
        var target = _target; // un seul instantané : un remplacement en cours de lancement abandonne ce lancement (génération)
        var entry = target.Entry;
        try
        {
            if (LaunchRefusal(entry) is { } refusal)
            {
                EndLaunchWithFailure(generation, refusal);
                return;
            }

            if (!entry.FromDevFolder)
            {
                // Les fichiers sont verrouillés d'abord, puis l'empreinte est calculée sur ces fichiers ouverts : ce qu'on vérifie
                // est exactement ce que l'hôte va charger.
                try
                {
                    fileLock = ModuleFileLock.Acquire(entry.Directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FilesBusy(generation, ex); // pas une panne du module : on réessaie plus tard, sans compter d'arrêt
                    return;
                }
                catch (ArgumentException)
                {
                    EndLaunchWithFailure(generation, "Le dossier du module contient deux fichiers dont les noms ne diffèrent que par la casse.");
                    return;
                }

                if (!string.Equals(fileLock.ContentHash, entry.Approval!.ContentHash, StringComparison.Ordinal))
                {
                    fileLock.Dispose();
                    fileLock = null;
                    EndLaunchWithFailure(generation, FilesChangedText);
                    return;
                }
            }

            toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            var arguments = $"--in {toHost.GetClientHandleAsString()} --out {fromHost.GetClientHandleAsString()}";
            if (!StillWanted(generation))
            {
                // La dernière case s'est refermée (ou le service s'arrête) pendant la préparation : on ne lance rien.
                toHost.Dispose();
                fromHost.Dispose();
                fileLock?.Dispose();
                return;
            }

            process = _launcher.Start(new HostStart(
                _hostExecutable, arguments, [toHost.ClientSafePipeHandle, fromHost.ClientSafePipeHandle], entry.Id, entry.Directory, target.Granted));

            // Le service n'a plus besoin de ses copies des bouts « client » : si l'hôte meurt, la lecture se termine enfin.
            toHost.DisposeLocalCopyOfClientHandle();
            fromHost.DisposeLocalCopyOfClientHandle();
            connection = new HostConnection(process, fileLock, new MessageChannel(fromHost, toHost), _time.GetUtcNow()) { Warn = _log.Warning };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                process?.Kill();
            }
            catch (Exception killError) when (killError is InvalidOperationException or Win32Exception)
            {
                // il ne démarrera pas
            }

            process?.Dispose();
            toHost?.Dispose();
            fromHost?.Dispose();
            fileLock?.Dispose();
            LaunchFailed(generation, ex);
            return;
        }

        lock (_gate)
        {
            _connections.Add(connection); // suivie de sa création à la fin de son nettoyage (Done), quel que soit son sort
        }

        _ = WatchAsync(connection);
        Install(connection, generation);
    }

    /// <summary>Le lancement ne se refera pas tout seul (fichiers modifiés, module non validé) : les cases le disent.</summary>
    private void EndLaunchWithFailure(int generation, string message)
    {
        Broadcast failure;
        lock (_gate)
        {
            if (generation == _launchGeneration)
            {
                _launching = false;
            }

            if (_disposed || generation != _launchGeneration)
            {
                return;
            }

            _permanentFailure = message;
            failure = DecideLocked(message);
        }

        _log.Warning("Le module n'est pas lancé : " + message);
        failure.Apply();
    }

    /// <summary>Le lancement est-il encore voulu ? Faux si une autre génération l'a remplacé (dernière case fermée, redémarrage) ou si le superviseur est libéré.</summary>
    private bool StillWanted(int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _launchGeneration)
            {
                if (generation == _launchGeneration)
                {
                    _launching = false;
                }

                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Les fichiers du module ne s'ouvrent pas pour le moment (un antivirus, OneDrive…) : ce n'est pas une panne du module. Nouvel essai
    /// dans <see cref="FilesBusyRetry"/>, sans compter d'arrêt (jamais de mise de côté pour ça).
    /// </summary>
    private void FilesBusy(int generation, Exception exception)
    {
        _log.Warning("Fichiers du module indisponibles : " + exception.Message);
        Broadcast waiting;
        lock (_gate)
        {
            if (generation == _launchGeneration)
            {
                _launching = false;
            }

            if (_disposed || generation != _launchGeneration)
            {
                return;
            }

            CancelRestartTimerLocked();
            _waitMessage = FilesBusyText;
            _restartTimer = _time.CreateTimer(_ => OnRestartTimer(), null, FilesBusyRetry, Timeout.InfiniteTimeSpan);
            waiting = DecideLocked(FilesBusyText);
        }

        waiting.Apply();
    }

    /// <summary>Le lancement a échoué (fichiers verrouillés, processus refusé) : compté comme un arrêt, avec relance ou mise de côté.</summary>
    private void LaunchFailed(int generation, Exception exception)
    {
        _log.Error("Impossible de lancer le module : " + exception.Message);
        string? asideReason;
        Broadcast stopped;
        lock (_gate)
        {
            if (generation == _launchGeneration)
            {
                _launching = false;
            }

            if (_disposed || generation != _launchGeneration)
            {
                return;
            }

            asideReason = RegisterStopLocked(_time.GetUtcNow(), _time.GetUtcNow(), memory: false, detail: exception.Message);
            stopped = DecideLocked(asideReason ?? HostStoppedText);
        }

        stopped.Apply();
        ReportAside(asideReason);
    }

    /// <summary>L'hôte est lancé : on lui dit bonjour, on lui ouvre les cases déjà dimensionnées, et on se met à l'écouter.</summary>
    private void Install(HostConnection connection, int generation)
    {
        Broadcast running;
        lock (_gate)
        {
            if (generation == _launchGeneration)
            {
                _launching = false;
            }

            if (_disposed || generation != _launchGeneration || _instances.Count == 0)
            {
                connection.ExpectedStop = true;
                connection.KillQuietly(); // son suivi (WatchAsync) nettoie
                return;
            }

            if (connection.Ended)
            {
                return; // mort avant d'être installé : son arrêt est déjà compté
            }

            SetSession(connection);
            connection.StartedAt = _time.GetUtcNow();
            var installed = _target;
            connection.Relay = new LocalRelay(installed.PortFields, installed.Granted.Contains(ModulePermission.ThisComputer),
                handle => connection.Process.DuplicateInto(handle), message => connection.Post(message));
            connection.Post(new Hello(
                HostMessage.ProtocolVersion, installed.Entry.Directory, installed.Manifest.Entry, installed.Entry.Id, installed.Granted, connection.Process.DataDirectory,
                _appearance, _maxFps));
            foreach (var module in _instances)
            {
                if (module.Slot is { } slot)
                {
                    // L'attente est armée avant que l'écriture ne parte : l'image qui répond ne peut pas arriver avant.
                    module.BeginAwaitingFrame();
                    SendOpen(connection, module, slot);
                }
            }

            _waitMessage = null;
            running = DecideLocked(null); // l'hôte est là : fini « Module arrêté, relance… »
            _ = Task.Run(connection.WriteLoopAsync);
            _ = Task.Run(() => ReadLoopAsync(connection));
        }

        running.Apply();
    }

    /// <summary>Ouvre l'instance d'une case dans l'hôte. À appeler sous le verrou.</summary>
    private void SendOpen(HostConnection connection, RemoteModule module, InstanceSlot slot)
    {
        long handle;
        try
        {
            handle = connection.Process.DuplicateInto(slot.Buffers.Section);
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            _log.Warning("Impossible de passer les images partagées à l'hôte : " + ex.Message);
            return;
        }

        var geometry = slot.Geometry;
        connection.Post(new Open(
            slot.Id, module.Settings.Clone(), geometry.Width, geometry.Height, geometry.TextScale, geometry.Columns, geometry.Rows, handle, 0));
    }

    // --- Arrêt de l'hôte, pannes ---------------------------------------------------------------------------------------------------

    /// <summary>Attend la fin du processus, puis libère tout ce qui lui était lié, puis compte l'arrêt si personne ne l'a voulu.</summary>
    private async Task WatchAsync(HostConnection connection)
    {
        try
        {
            await WatchCoreAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            connection.MarkDone();
        }
    }

    private async Task WatchCoreAsync(HostConnection connection)
    {
        int exitCode;
        try
        {
            exitCode = await connection.Process.Exited.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exitCode = -1;
        }

        JobUsage? usage = null;
        try
        {
            usage = connection.Process.ReadUsage();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            // pas de mesure : le job est déjà fermé
        }

        connection.KillTimer?.Dispose();
        connection.Relay?.Dispose();
        connection.Close();
        connection.Process.Dispose();
        connection.FileLock?.Dispose(); // les fichiers du module sont libres dès que l'hôte est fini
        try
        {
            await connection.Link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // le tube est déjà cassé
        }

        string? asideReason;
        Broadcast stopped;
        lock (_gate)
        {
            connection.Ended = true;
            if (ReferenceEquals(_session, connection))
            {
                SetSession(null);
            }

            if (connection.ExpectedStop || _disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var memory = usage is { MemoryLimitHit: true };
            _log.Warning($"L'hôte du module s'est arrêté (code {exitCode:X8}){(memory ? ", mémoire dépassée" : "")}.");
            if (connection.Overflowed)
            {
                _log.Error("L'hôte du module ne lit plus ce que le service lui envoie : il a été arrêté.");
            }

            asideReason = RegisterStopLocked(now, connection.StartedAt, memory, connection.FailureText);
            stopped = DecideLocked(asideReason ?? HostStoppedText);
        }

        stopped.Apply();
        ReportAside(asideReason);
    }

    /// <summary>
    /// Compte un arrêt que personne n'a voulu. Au troisième en <see cref="CrashWindow"/>, le module est mis de côté (la raison est
    /// rendue) ; sinon une relance est programmée si des cases attendent. À appeler sous le verrou.
    /// </summary>
    private string? RegisterStopLocked(DateTimeOffset now, DateTimeOffset startedAt, bool memory, string? detail)
    {
        if (now - startedAt >= CrashWindow)
        {
            _consecutiveStops = 0; // l'hôte a tenu longtemps : ce n'est plus une série
        }

        _consecutiveStops++;
        _stops.RemoveAll(stop => now - stop.At > CrashWindow);
        _stops.Add((now, memory));
        if (_stops.Count >= MaxStops)
        {
            var reason = $"Mis de côté : le module s'est arrêté {MaxStops} fois en {(int)CrashWindow.TotalMinutes} minutes";
            if (_stops.Any(stop => stop.Memory))
            {
                reason += " (512 Mo dépassés)";
            }

            reason += detail is { Length: > 0 } ? $". Dernière erreur : {HostText.Clean(detail, 150)}" : ".";
            _setAsideReason = reason;
            CancelRestartTimerLocked();
            return reason;
        }

        if (_instances.Count > 0 && !_disposed)
        {
            CancelRestartTimerLocked();
            _waitMessage = HostStoppedText;
            _restartTimer = _time.CreateTimer(_ => OnRestartTimer(), null, BackoffFor(_consecutiveStops), Timeout.InfiniteTimeSpan);
        }

        return null;
    }

    /// <summary>Une décision du superviseur sur le message de toutes les cases, numérotée sous le verrou et appliquée hors du verrou.</summary>
    private readonly record struct Broadcast(RemoteModule[] Targets, string? Message, long Version)
    {
        /// <summary>L'applique aux cases (hors du verrou : les événements de case sont levés ici). Une décision plus récente l'emporte, quel que soit l'ordre d'arrivée.</summary>
        public void Apply()
        {
            foreach (var module in Targets ?? [])
            {
                module.SetSupervisorMessage(Message, Version);
            }
        }
    }

    /// <summary>
    /// Décide du message de toutes les cases (<c>null</c> : plus de message). À appeler sous le verrou ; le numéro de version prend l'ordre
    /// du verrou, de sorte qu'un message de panne et l'effacement par un nouvel hôte ne s'inversent jamais.
    /// </summary>
    private Broadcast DecideLocked(string? message) => new([.. _instances], message, ++_messageVersion);

    private void ReportAside(string? asideReason)
    {
        if (asideReason is null)
        {
            return;
        }

        _log.Error(asideReason);
        try
        {
            _setAside(_target.Entry.Id, asideReason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("La mise de côté du module n'a pas pu être enregistrée : " + ex.Message);
        }
    }

    private void OnRestartTimer()
    {
        lock (_gate)
        {
            _restartTimer?.Dispose();
            _restartTimer = null;
            if (_disposed || _setAsideReason is not null || _permanentFailure is not null || _instances.Count == 0
                || _session is not null || _launching)
            {
                return;
            }

            StartLaunchLocked();
        }
    }

    private void CancelRestartTimerLocked()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    /// <summary>
    /// Demande à l'hôte de finir (<see cref="Shutdown"/>) et le tue s'il traîne. Le délai court sur l'horloge du système, pas sur
    /// celle qu'on a injectée : c'est un filet de sécurité, il doit se déclencher même si l'horloge des tests est figée.
    /// </summary>
    private void StopGracefullyLocked(HostConnection connection)
    {
        connection.ExpectedStop = true;
        connection.Post(new Shutdown());
        connection.KillTimer = TimeProvider.System.CreateTimer(_ => connection.KillQuietly(), null, ShutdownGrace, Timeout.InfiniteTimeSpan);
    }

    private static async Task<bool> WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
