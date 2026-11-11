using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Device;
using Trofeo.ModuleKit;

namespace Trofeo.Display;

/// <summary>
/// La boucle d'affichage : reconnecte l'écran quand il est absent, puis rend et envoie une image
/// quand un module en a demandé une. Possède les modules qu'elle crée, ainsi que le fond et le compositeur d'images ;
/// <see cref="Tick"/> s'appelle depuis un seul thread. La disposition, le thème et le fond se changent à chaud
/// (<see cref="ApplyAsync"/>, appliqué au début du <see cref="Tick"/> suivant), et chaque image rendue nourrit l'aperçu
/// de la fenêtre de réglage (<see cref="SubscribePreview"/>), même sans écran (mode émulé).
/// </summary>
public sealed class DisplayLoop : IDisposable
{
    private const string ResumeReason = "Reprise après veille";
    private const int DefaultMaxFps = 12;
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(1);

    /// <summary>Plus longue attente renvoyée par <see cref="Tick"/> : la boucle se réveille au moins une fois par seconde.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// L'écran repasse au logo Thermalright environ 3 s après la dernière image reçue : sans nouvelle image,
    /// on lui renvoie la dernière (déjà encodée) à ce rythme.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Les modules en place et la disposition qui va avec, remplacés d'un bloc : <see cref="FindCommandTarget"/> et
    /// <see cref="Status"/> les lisent depuis un autre fil, sans jamais voir un état à moitié changé. Jamais modifié après coup :
    /// changer de vue en fait un nouveau (<see cref="WithView"/>). Toutes les cases de toutes les vues ont leur module ; seules
    /// celles de la vue affichée sont dessinées.
    /// </summary>
    private sealed class Live
    {
        public static Live Empty { get; } = new(
            DashboardLayout.SingleView(0, []), FrozenDictionary<Guid, IModule>.Empty, [], [], [], Guid.Empty);

        private Live(
            DashboardLayout layout, FrozenDictionary<Guid, IModule> modules, KeyValuePair<Guid, IModule>[] entries,
            IModule[] list, Dictionary<Guid, ModulePlacement> placed, Guid viewId)
        {
            Layout = layout;
            Modules = modules;
            Entries = entries;
            List = list;
            Placed = placed;
            var view = layout.Views.FirstOrDefault(v => v.Id == viewId) ?? layout.Views[0];
            ViewId = view.Id;
            Placements = view.Placements;

            // Les modules de la vue affichée : ceux dont l'emplacement accepté est bien l'une de ses cases (un identifiant en
            // double dans une autre vue ne prête pas son module).
            var displayed = new List<IModule>();
            var seen = new HashSet<Guid>();
            foreach (var placement in view.Placements)
            {
                if (placed.TryGetValue(placement.InstanceId, out var accepted) && ReferenceEquals(accepted, placement)
                    && modules.TryGetValue(placement.InstanceId, out var module) && seen.Add(placement.InstanceId))
                {
                    displayed.Add(module);
                }
            }

            Displayed = [.. displayed];
            DisplayedSet = new HashSet<IModule>(displayed, ReferenceEqualityComparer.Instance);
        }

        public DashboardLayout Layout { get; }
        public FrozenDictionary<Guid, IModule> Modules { get; }

        /// <summary>Les modules de toutes les vues, dans l'ordre de la disposition.</summary>
        public KeyValuePair<Guid, IModule>[] Entries { get; }

        public IModule[] List { get; }

        /// <summary>L'emplacement dont chaque module est issu : de quoi décider s'il reste en place au changement suivant.</summary>
        public Dictionary<Guid, ModulePlacement> Placed { get; }

        /// <summary>La vue affichée.</summary>
        public Guid ViewId { get; }

        /// <summary>Les cases de la vue affichée : ce que le compositeur dessine.</summary>
        public IReadOnlyList<ModulePlacement> Placements { get; }

        /// <summary>Les modules de la vue affichée, dans l'ordre de ses cases.</summary>
        public IModule[] Displayed { get; }

        public HashSet<IModule> DisplayedSet { get; }

        public static Live Of(DashboardLayout layout, Dictionary<Guid, IModule> modules, Dictionary<Guid, ModulePlacement> placed, Guid viewId)
        {
            var entries = new List<KeyValuePair<Guid, IModule>>(modules.Count);
            var seen = new HashSet<Guid>();
            foreach (var placement in layout.AllPlacements)
            {
                if (modules.TryGetValue(placement.InstanceId, out var module) && seen.Add(placement.InstanceId))
                {
                    entries.Add(new(placement.InstanceId, module));
                }
            }

            var array = entries.ToArray();
            return new Live(layout, modules.ToFrozenDictionary(), array, [.. array.Select(e => e.Value)], placed, viewId);
        }

        /// <summary>Le même état, avec une autre vue affichée.</summary>
        public Live WithView(Guid viewId) => new(Layout, Modules, Entries, List, Placed, viewId);
    }

    /// <summary>Ce qu'un module cachable a appris pour la dernière fois : de quoi ne le prévenir que quand quelque chose change.</summary>
    private readonly record struct Announcement(bool Visible, SKRect Bounds, GridSize Size, float TextScale, Theme Theme);

    /// <summary>Un abonné à l'aperçu. Les champs d'état ne se touchent que depuis le fil du <see cref="Tick"/>.</summary>
    private sealed class PreviewSubscriber(DisplayLoop owner, TimeSpan interval, Action<PreviewFrame> onFrame) : IDisposable
    {
        private int _disposed;

        public TimeSpan Interval { get; } = interval;
        public Action<PreviewFrame> OnFrame { get; } = onFrame;
        public PreviewFrame? Pending { get; set; }
        public bool HasPublished { get; set; }
        public long LastPublish { get; set; }
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.RemoveSubscriber(this);
        }
    }

    private readonly ModuleRegistry _registry;
    private readonly ScreenDevice _screen;
    private readonly FrameScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly RateLimitedErrorLog _renderErrors;
    private volatile Live _live = Live.Empty;
    private readonly DisplayedViewStore? _viewStore;
    private readonly Dictionary<IModule, Announcement> _announced = new(ReferenceEqualityComparer.Instance); // sous _lifecycleGate
    private FrameComposer _composer;
    private ThemeDefinition _theme;
    private IBackground _background;
    private double _configuredFps;          // le plafond de la disposition (MaxFps) ; le plafond réel peut être plus bas sans écran
    private long _backgroundStart;
    private TimeSpan? _backgroundDue;       // échéance absolue de l'image suivante du fond, sur l'axe du temps du fond (depuis _backgroundStart)
    private TimeSpan? _backgroundRetryAt;   // fond en échec : prochain essai d'avance, sur le même axe

    // Identifiants d'instance, lus depuis n'importe quel thread (OnStatusChanged) et modifiés à chaque changement de disposition.
    private readonly ConcurrentDictionary<IModule, Guid> _instanceIds = [];
    private readonly EventHandler _onInvalidated;
    private readonly EventHandler _onStatusChanged;

    // Démarrage des modules, sous verrou : Start et l'application d'une disposition ne se chevauchent pas.
    private readonly object _lifecycleGate = new();
    private bool _started;
    private CancellationToken _startToken;

    // Travaux à faire au début du Tick suivant (changements de disposition, redémarrages) ; chacun reçoit « annulé » à l'arrêt.
    private readonly object _queueGate = new();
    private readonly List<Action<bool>> _queue = [];
    private bool _queueClosed;

    private readonly object _subscribersGate = new();
    private volatile PreviewSubscriber[] _subscribers = [];

    private bool _hasTriedConnect;
    private long _lastConnectAttempt;
    private long _lastPresenceCheck;
    private long _lastSend;
    private bool _forceReconnect;
    private int _reconnectRequested;
    private volatile bool _disposed;

    // Durées de rendu (composition et encodage) et d'envoi des images envoyées depuis la dernière ligne de journal.
    private TimeSpan _renderTotal;
    private TimeSpan _sendTotal;
    private int _timedFrames;

    // Moyennes glissantes affichées par Status (millisecondes) ; négatif : pas encore de mesure.
    private double _renderMs = -1;
    private double _sendMs = -1;
    private int _framesSent;

    /// <param name="theme">Le thème complet ; Classique si absent.</param>
    /// <param name="background">
    /// Le fond de l'écran ; la boucle en devient propriétaire et le libère à son arrêt. Absent : fond uni de la couleur du thème.
    /// </param>
    /// <param name="viewStore">
    /// Où la vue affichée est gardée entre deux démarrages : lue ici, écrite à chaque changement de vue, vidée à l'arrêt.
    /// Absent : la boucle démarre sur la première vue et ne retient rien.
    /// </param>
    public DisplayLoop(
        DashboardLayout layout, ModuleRegistry registry, ScreenDevice screen, FrameScheduler scheduler, TimeProvider time,
        ThemeDefinition? theme = null, IBackground? background = null, DisplayedViewStore? viewStore = null)
    {
        _registry = registry;
        _viewStore = viewStore;
        _screen = screen;
        _scheduler = scheduler;
        _time = time;
        _onInvalidated = (sender, _) =>
        {
            // Une case cachée qui s'invalide ne réveille pas l'écran : sa vue sera de toute façon redessinée quand on l'affichera.
            if (sender is not IModule module || _live.DisplayedSet.Contains(module)) _scheduler.MarkDirty();
        };
        _onStatusChanged = OnStatusChanged;
        _renderErrors = new RateLimitedErrorLog(time);
        _composer = new FrameComposer(_renderErrors);
        _theme = theme ?? Themes.ClassiqueDefinition;
        _background = background ?? new ColorBackground(_theme.DefaultBackground.Color);
        _backgroundStart = time.GetTimestamp(); // la lecture d'un fond animé part du démarrage de la boucle

        // Une disposition mal formée ne doit jamais empêcher le service de démarrer : les refus sont journalisés.
        var stored = viewStore?.Load();
        var firstView = stored is { } storedId && layout.Views.Any(v => v.Id == storedId) ? storedId : layout.Views[0].Id;
        _live = Reconcile(layout, Live.Empty, firstView, [], out _);
        Announce(_live);
        if (layout.MaxFps is { } maxFps) _scheduler.SetMaxFps(maxFps);
        _configuredFps = FpsOf(_scheduler.MinInterval);
        _scheduler.MarkDirty(); // la première image part dès que l'écran est prêt
    }

    /// <summary>La disposition appliquée. Lisible depuis n'importe quel thread.</summary>
    public DashboardLayout Layout => _live.Layout;

    /// <summary>
    /// Le module qui reçoit une commande de raccourci : l'instance demandée si elle accepte les commandes, sinon le
    /// premier module qui les accepte, dans l'ordre de la vue affichée (colonne, puis rangée). Une instance qui est une case
    /// d'une vue cachée rend <c>null</c> (le raccourci n'agit pas) ; le repli ne sert que pour une instance absente ou qui
    /// n'accepte pas les commandes. <c>null</c> s'il n'y en a aucun.
    /// Appelable depuis le thread de l'interface, y compris pendant un changement de disposition : chaque appel lit
    /// un état cohérent (modules et disposition ensemble). L'appelant doit cesser d'appeler avant de libérer la boucle.
    /// </summary>
    public ICommandableModule? FindCommandTarget(Guid? instanceId)
    {
        var live = _live;
        if (instanceId is { } id && live.Modules.TryGetValue(id, out var requested) && requested is ICommandableModule target)
        {
            // Un raccourci visé sur une case d'une vue cachée n'agit pas : ni lui, ni un repli sur la vue affichée.
            return live.DisplayedSet.Contains(requested) ? target : null;
        }

        foreach (var placement in live.Placements.OrderBy(p => p.Column).ThenBy(p => p.Row))
        {
            if (live.Modules.TryGetValue(placement.InstanceId, out var module) && module is ICommandableModule commandable
                && live.DisplayedSet.Contains(module))
            {
                return commandable;
            }
        }

        return null;
    }

    /// <summary>
    /// L'état de la boucle : mode émulé, modules (état et remarques), images envoyées et durées récentes de rendu et d'envoi.
    /// Lisible depuis n'importe quel thread ; une lecture ne copie que la liste des modules.
    /// </summary>
    public LoopStatus Status
    {
        get
        {
            var live = _live;
            var modules = new LoopModule[live.Entries.Length];
            for (var i = 0; i < modules.Length; i++)
            {
                var (id, module) = (live.Entries[i].Key, live.Entries[i].Value);
                modules[i] = new LoopModule(id, module.Descriptor.TypeId, ReadStatus(module), ReadNotices(module));
            }

            return new LoopStatus(
                IsEmulated, modules, Volatile.Read(ref _framesSent),
                Math.Max(0, Volatile.Read(ref _renderMs)), Math.Max(0, Volatile.Read(ref _sendMs)));
        }
    }

    private bool IsEmulated => !_disposed && _screen.State != ScreenState.Ready && _subscribers.Length > 0;

    private static ModuleStatus ReadStatus(IModule module)
    {
        try
        {
            return module.Status;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec de lecture de l'état d'un module : {ex.Message}");
            return new ModuleStatus(ModuleHealth.Offline);
        }
    }

    private static IReadOnlyList<string> ReadNotices(IModule module)
    {
        if (module is not IModuleNotices withNotices) return [];
        try
        {
            return withNotices.Notices;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec de lecture des remarques d'un module : {ex.Message}");
            return [];
        }
    }

    /// <summary>Journalise le nouvel état d'un module et redemande une image. Peut être appelé depuis n'importe quel thread.</summary>
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (sender is IModule module)
        {
            try
            {
                var status = module.Status;
                var action = status.ActionMessage is { Length: > 0 } message ? $" — {message}" : "";
                var instance = _instanceIds.TryGetValue(module, out var id) ? $" {id.ToString("N")[..8]}" : "";
                Trace.TraceInformation($"[{module.Descriptor.TypeId}{instance}] État : {status.Health}{action}");
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Échec de lecture de l'état d'un module : {ex.Message}");
            }
        }

        if (sender is not IModule changed || _live.DisplayedSet.Contains(changed)) _scheduler.MarkDirty();
    }

    /// <summary>Nombre d'images envoyées et validées par l'écran depuis le démarrage.</summary>
    public int FramesSent
    {
        get => Volatile.Read(ref _framesSent);
        private set => Volatile.Write(ref _framesSent, value);
    }

    /// <summary>Nombre de renvois de la dernière image (écran maintenu allumé sans changement).</summary>
    public int KeepAlivesSent { get; private set; }

    /// <summary>Démarre les modules. Un module qui échoue est journalisé, les autres continuent.</summary>
    public void Start(CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            _startToken = ct;
            _started = true;
            foreach (var module in _live.List)
            {
                StartModule(module);
            }
        }
    }

    private void StartModule(IModule module)
    {
        try
        {
            module.StartAsync(_startToken).ContinueWith(
                t => Trace.TraceError($"Échec du démarrage du module « {module.Descriptor.TypeId} » : {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec du démarrage du module « {module.Descriptor.TypeId} » : {ex}");
        }
    }

    /// <summary>
    /// Demande d'abandonner la connexion et de se reconnecter tout de suite (reprise après veille). Peut être
    /// appelée depuis n'importe quel thread : le prochain <see cref="Tick"/> s'en charge.
    /// </summary>
    public void RequestReconnect()
    {
        Volatile.Write(ref _reconnectRequested, 1);
        _scheduler.Wake();
    }

    // ---- Vues ----

    /// <summary>Levé après chaque changement de vue affichée (raccourci, rechargement qui supprime la vue affichée, <see cref="ShowView"/>), jamais au démarrage. Peut l'être depuis le fil de l'interface ou celui de la boucle : un abonné ne doit pas bloquer.</summary>
    public event Action<Guid>? ViewChanged;

    /// <summary>La vue affichée. Lisible depuis n'importe quel thread.</summary>
    public Guid CurrentViewId => _live.ViewId;

    /// <summary>Le sens du dernier changement de vue (−1 : vers la précédente, +1 : vers la suivante, 0 : sans sens) ; sert au glissement.</summary>
    public int LastViewDirection { get; private set; }

    /// <summary>
    /// Affiche la vue <paramref name="viewId"/> tout de suite (une seule recomposition). Faux si cette vue n'existe pas ; vrai
    /// (sans rien faire) si elle est déjà affichée. <paramref name="direction"/> (−1, 0 ou +1) dit dans quel sens on y va, pour
    /// le glissement. Appelable depuis n'importe quel thread.
    /// </summary>
    public bool ShowView(Guid viewId, int direction)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return false;

            var old = _live;
            if (!old.Layout.Views.Any(v => v.Id == viewId)) return false;
            if (old.ViewId == viewId) return true;

            LastViewDirection = Math.Clamp(direction, -1, 1);
            _live = old.WithView(viewId);
            Announce(_live);
            _scheduler.MarkDirty();
        }

        OnViewChanged(viewId);
        return true;
    }

    /// <summary>
    /// Affiche la vue d'à côté, en boucle (après la dernière, la première). <paramref name="step"/> : +1 la suivante, −1 la précédente.
    /// Faux s'il n'y a pas d'autre vue où aller.
    /// </summary>
    public bool ShowAdjacentView(int step)
    {
        var live = _live;
        var views = live.Layout.Views;
        if (step == 0 || views.Count < 2) return false;

        var current = views.ToList().FindIndex(v => v.Id == live.ViewId);
        var target = (((current + step) % views.Count) + views.Count) % views.Count;
        return ShowView(views[target].Id, Math.Sign(step));
    }

    /// <summary>Affiche la vue de rang <paramref name="index"/> (à partir de 0). Faux s'il n'y a pas de vue de ce rang.</summary>
    public bool ShowViewAt(int index)
    {
        var live = _live;
        var views = live.Layout.Views;
        if (index < 0 || index >= views.Count) return false;

        var current = views.ToList().FindIndex(v => v.Id == live.ViewId);
        return ShowView(views[index].Id, Math.Sign(index - current));
    }

    /// <summary>Retient la vue affichée et prévient les abonnés ; un abonné qui échoue est journalisé, sans effet sur la boucle.</summary>
    private void OnViewChanged(Guid viewId)
    {
        _viewStore?.Save(viewId);
        try
        {
            ViewChanged?.Invoke(viewId);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Un abonné à ViewChanged a échoué : {ex}");
        }
    }

    /// <summary>
    /// Dit à chaque module cachable si sa case est visible, avec sa place, quand cela a changé depuis la dernière annonce (création,
    /// changement de vue, case déplacée ou thème changé). À appeler sous <c>_lifecycleGate</c>, une fois la nouvelle <see cref="Live"/> publiée.
    /// </summary>
    private void Announce(Live live)
    {
        var now = _time.GetLocalNow();
        foreach (var (id, module) in live.Entries)
        {
            if (module is not IHideableModule hideable || !live.Placed.TryGetValue(id, out var placement)) continue;

            var place = ModulePlace.Context(placement, _theme, now);
            var announcement = new Announcement(live.DisplayedSet.Contains(module), place.Bounds, place.Size, place.TextScale, place.Theme);
            if (_announced.TryGetValue(module, out var last) && last == announcement) continue;

            _announced[module] = announcement;
            try
            {
                hideable.SetVisible(announcement.Visible, place);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Échec de l'annonce de visibilité du module « {module.Descriptor.TypeId} » : {ex}");
            }
        }
    }

    // ---- Changement de disposition à chaud ----

    /// <summary>
    /// Applique une nouvelle disposition, un nouveau thème et un nouveau fond. Appelable depuis n'importe quel thread ;
    /// le changement se fait au début du <see cref="Tick"/> suivant, dans l'ordre des appels. La boucle devient propriétaire
    /// du fond. Le résultat donne les avertissements, en français (module refusé, type inconnu…) ; vide si tout va bien.
    /// <para>
    /// Un module en place est gardé (sans être redémarré) quand son identifiant d'instance, son type et ses réglages sont les
    /// mêmes ; sa place, sa taille et la taille de son texte se lisent dans la nouvelle disposition. Sinon l'ancien est arrêté,
    /// puis le nouveau est créé et démarré ; un module disparu est arrêté. Le compositeur est recréé quand le thème ou le fond
    /// change, et l'ancien fond est libéré après lui. Le plafond d'images vient de <see cref="DashboardLayout.MaxFps"/>
    /// (12 par défaut), et une image est redemandée.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<string>> ApplyAsync(LoopConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Layout);
        ArgumentNullException.ThrowIfNull(config.Theme);
        ArgumentNullException.ThrowIfNull(config.Background);

        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Enqueue(cancelled =>
        {
            if (cancelled)
            {
                DisposeUnlessCurrent(config.Background); // la boucle en était devenue propriétaire
                completion.TrySetResult([]);
                return;
            }

            try
            {
                completion.TrySetResult(ApplyNow(config));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued)
        {
            DisposeUnlessCurrent(config.Background);
            completion.TrySetResult([]);
        }

        return completion.Task;
    }

    /// <summary>
    /// Une demande annulée libère le fond qu'elle apportait, sauf si c'est celui que la boucle montre déjà (un rechargement qui
    /// garde le fond le rend tel quel) : <see cref="Dispose"/> le libère, une seule fois.
    /// </summary>
    private void DisposeUnlessCurrent(IBackground background)
    {
        if (!ReferenceEquals(background, _background)) DisposeQuietly(background);
    }

    /// <summary>
    /// Arrête l'instance placée dans la disposition courante puis en crée et démarre une neuve à partir du même emplacement ;
    /// appliqué au début du <see cref="Tick"/> suivant, comme <see cref="ApplyAsync"/>. Un module gardé d'un rechargement à
    /// l'autre ne relit jamais ses secrets et n'oublie rien de son état : on le redémarre après une action ou un changement de
    /// secret. Faux s'il n'y a pas d'instance de cet identifiant dans la disposition (ou si elle y est refusée), ou si le
    /// nouveau module n'a pas pu être créé. Appelable depuis n'importe quel thread.
    /// </summary>
    public Task<bool> RestartAsync(Guid instanceId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Enqueue(cancelled =>
        {
            if (cancelled)
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                completion.TrySetResult(RestartNow(instanceId));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued) completion.TrySetResult(false);
        return completion.Task;
    }

    private bool Enqueue(Action<bool> work)
    {
        lock (_queueGate)
        {
            if (_queueClosed) return false;
            _queue.Add(work);
        }

        _scheduler.Wake();
        return true;
    }

    /// <summary>Fait les travaux en attente, dans l'ordre.</summary>
    private void RunQueued()
    {
        Action<bool>[] work;
        lock (_queueGate)
        {
            if (_queue.Count == 0) return;
            work = [.. _queue];
            _queue.Clear();
        }

        foreach (var item in work)
        {
            item(false);
        }
    }

    private IReadOnlyList<string> ApplyNow(LoopConfig config)
    {
        var warnings = new List<string>();
        Guid? viewChanged;
        lock (_lifecycleGate)
        {
            var layout = config.Layout;
            var old = _live;

            // Le fond et le thème d'abord : le compositeur est recréé s'ils changent, l'ancien fond est libéré après lui.
            ReplaceTheme(config.Theme, config.Background);

            _live = Reconcile(layout, old, ViewAfterReload(old, layout), warnings, out var created);
            viewChanged = _live.ViewId != old.ViewId ? _live.ViewId : null;
            Announce(_live);
            if (_started)
            {
                foreach (var module in created) StartModule(module);
            }

            _configuredFps = layout.MaxFps ?? DefaultMaxFps;
            ApplyFpsCap();
            _scheduler.MarkDirty();
        }

        if (viewChanged is { } shown) OnViewChanged(shown);
        return warnings;
    }

    /// <summary>
    /// La vue affichée après un rechargement : la même si elle existe encore ; sinon la vue qui a pris sa place (la suivante, ou la
    /// précédente si c'était la dernière) ; à défaut la première.
    /// </summary>
    private static Guid ViewAfterReload(Live old, DashboardLayout layout)
    {
        if (layout.Views.Any(v => v.Id == old.ViewId)) return old.ViewId;

        var index = old.Layout.Views.ToList().FindIndex(v => v.Id == old.ViewId);
        return index < 0 ? layout.Views[0].Id : layout.Views[Math.Min(index, layout.Views.Count - 1)].Id;
    }

    private void ReplaceTheme(ThemeDefinition theme, IBackground background)
    {
        var backgroundChanged = !ReferenceEquals(background, _background);
        if (!backgroundChanged && Equals(theme, _theme)) return;

        var oldComposer = _composer;
        var oldBackground = _background;
        _theme = theme;
        _background = background;
        _composer = new FrameComposer(_renderErrors);

        // Le compositeur garde une copie floutée du fond : on le libère avant le fond.
        DisposeQuietly(oldComposer);
        if (backgroundChanged)
        {
            DisposeQuietly(oldBackground);
            _backgroundStart = _time.GetTimestamp(); // le fond animé repart de zéro
            _backgroundDue = null;
            _backgroundRetryAt = null;
        }
    }

    /// <summary>
    /// Fait correspondre les modules à <paramref name="layout"/> : garde ceux qui n'ont pas changé, arrête les autres,
    /// crée les nouveaux (<paramref name="created"/> : à démarrer par l'appelant, une fois le résultat publié). Les refus sont
    /// journalisés et ajoutés à <paramref name="warnings"/>.
    /// </summary>
    private Live Reconcile(DashboardLayout layout, Live old, Guid viewId, List<string> warnings, out List<IModule> created)
    {
        // 1. Ce qu'on garde, ce qu'on crée : les cases de toutes les vues (seule la vue affichée est dessinée).
        var plan = new List<(ModulePlacement Placement, IModule? Keep, bool Duplicate)>();
        var seen = new HashSet<Guid>();
        foreach (var (view, placement) in layout.Views.SelectMany(v => v.Placements.Select(p => (v, p))))
        {
            try
            {
                var error = LayoutValidator.Check(view.Placements, placement, _registry.Descriptors);
                if (error == PlacementError.UnknownType)
                {
                    Refuse(warnings, $"Module ignoré : type inconnu « {placement.TypeId} ».", $"({placement.InstanceId})");
                    continue;
                }

                if (error != PlacementError.None)
                {
                    Refuse(warnings, $"Module ignoré : « {placement.TypeId} » ({placement.InstanceId}) refusé : {error}.");
                    continue;
                }

                if (!seen.Add(placement.InstanceId))
                {
                    plan.Add((placement, null, true));
                    continue;
                }

                IModule? keep = null;
                if (old.Modules.TryGetValue(placement.InstanceId, out var inPlace)
                    && old.Placed.TryGetValue(placement.InstanceId, out var before)
                    && before.TypeId == placement.TypeId
                    && SettingsEqual(before.Settings, placement.Settings))
                {
                    keep = inPlace;
                }

                plan.Add((placement, keep, false));
            }
            catch (Exception ex)
            {
                // Une disposition mal formée ne doit jamais empêcher le service de démarrer.
                Refuse(warnings, $"Module ignoré : emplacement {placement?.InstanceId} refusé : {ex.Message}", ex.ToString());
            }
        }

        // 2. Les anciens modules qui ne restent pas sont arrêtés avant la création des nouveaux (une ressource exclusive est libérée).
        var kept = new HashSet<IModule>(plan.Where(p => p.Keep is not null).Select(p => p.Keep!));
        foreach (var module in old.List)
        {
            if (!kept.Contains(module)) Retire(module);
        }

        // 3. Les nouveaux sont créés, puis démarrés une fois la liste publiée par l'appelant.
        var modules = new Dictionary<Guid, IModule>();
        var placed = new Dictionary<Guid, ModulePlacement>();
        created = [];
        foreach (var (placement, keep, duplicate) in plan)
        {
            try
            {
                if (keep is not null)
                {
                    modules[placement.InstanceId] = keep;
                    placed[placement.InstanceId] = placement;
                    continue;
                }

                var module = _registry.Create(placement, _time);
                if (module is null)
                {
                    if (!duplicate) warnings.Add($"Module ignoré : « {placement.TypeId} » n'a pas pu être créé.");
                    continue;
                }

                if (duplicate)
                {
                    // Deux emplacements du même identifiant : le second ne doit pas écraser (ni laisser tourner) le premier.
                    Refuse(warnings, $"Module ignoré : identifiant d'instance en double ({placement.InstanceId}).");
                    DisposeModule(module);
                    continue;
                }

                Adopt(module, placement.InstanceId);
                modules[placement.InstanceId] = module;
                placed[placement.InstanceId] = placement;
                created.Add(module);
            }
            catch (Exception ex)
            {
                Refuse(warnings, $"Module ignoré : emplacement {placement.InstanceId} refusé : {ex.Message}", ex.ToString());
            }
        }

        return Live.Of(layout, modules, placed, viewId);
    }

    private static void Refuse(List<string> warnings, string warning, string? detail = null)
    {
        Trace.TraceWarning(detail is null ? warning : $"{warning} {detail}");
        warnings.Add(warning);
    }

    private static bool SettingsEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Undefined || b.ValueKind == JsonValueKind.Undefined)
        {
            return a.ValueKind == b.ValueKind;
        }

        return JsonElement.DeepEquals(a, b);
    }

    /// <summary>Branche un module neuf à la boucle.</summary>
    private void Adopt(IModule module, Guid instanceId)
    {
        _instanceIds[module] = instanceId;
        module.Invalidated += _onInvalidated;
        module.StatusChanged += _onStatusChanged;
    }

    /// <summary>Débranche un module de la boucle et le libère.</summary>
    private void Retire(IModule module)
    {
        module.Invalidated -= _onInvalidated;
        module.StatusChanged -= _onStatusChanged;
        _instanceIds.TryRemove(module, out _);
        _announced.Remove(module);
        DisposeModule(module);
    }

    private static void DisposeModule(IModule module)
    {
        try
        {
            module.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec de l'arrêt du module « {module.Descriptor.TypeId} » : {ex}");
        }
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec de la libération d'une ressource : {ex}");
        }
    }

    private bool RestartNow(Guid instanceId)
    {
        lock (_lifecycleGate)
        {
            var old = _live;
            var view = old.Layout.Views.FirstOrDefault(v => v.Placements.Any(p => p.InstanceId == instanceId));
            var placement = old.Placed.TryGetValue(instanceId, out var accepted)
                ? accepted
                : view?.Placements.FirstOrDefault(p => p.InstanceId == instanceId);
            if (placement is null) return false;

            if (!old.Placed.ContainsKey(instanceId)
                && (view is null || LayoutValidator.Check(view.Placements, placement, _registry.Descriptors) != PlacementError.None))
            {
                return false;
            }

            if (old.Modules.TryGetValue(instanceId, out var current)) Retire(current);

            var modules = new Dictionary<Guid, IModule>(old.Modules);
            var placed = new Dictionary<Guid, ModulePlacement>(old.Placed);
            modules.Remove(instanceId);
            placed.Remove(instanceId);

            var fresh = _registry.Create(placement, _time);
            if (fresh is not null)
            {
                Adopt(fresh, instanceId);
                modules[instanceId] = fresh;
                placed[instanceId] = placement;
            }

            _live = Live.Of(old.Layout, modules, placed, old.ViewId);
            Announce(_live);
            if (fresh is not null && _started) StartModule(fresh);
            _scheduler.MarkDirty();
            return fresh is not null;
        }
    }

    // ---- Aperçu ----

    /// <summary>
    /// Abonne <paramref name="onFrame"/> aux images de l'écran, au plus <paramref name="maxFps"/> par seconde (borné à [1, 12])
    /// et par abonné. Quand l'écran est prêt, chaque image envoyée est publiée (pas les renvois de maintien) ; sans écran
    /// mais avec au moins un abonné, la boucle rend et encode quand même, sans envoyer (mode émulé). Un abonné à qui son
    /// plafond fait sauter des images reçoit la plus récente dès que son délai est écoulé. Une image est redemandée à l'abonnement.
    /// <para>
    /// <paramref name="onFrame"/> est appelé sur le fil du <see cref="Tick"/> : il ne doit jamais bloquer ; une exception est
    /// journalisée et sans effet sur la boucle. Le tableau d'octets de l'image n'est plus modifié après l'appel.
    /// </para>
    /// </summary>
    public IDisposable SubscribePreview(int maxFps, Action<PreviewFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        var fps = Math.Clamp(maxFps, 1, DefaultMaxFps);
        var subscriber = new PreviewSubscriber(this, TimeSpan.FromTicks((long)Math.Round((double)TimeSpan.TicksPerSecond / fps)), onFrame);
        lock (_subscribersGate)
        {
            if (_disposed) return subscriber;
            _subscribers = [.. _subscribers, subscriber];
        }

        _scheduler.MarkDirty(); // une image fraîche pour le nouvel abonné ; elle réveille aussi la boucle sans écran
        return subscriber;
    }

    private void RemoveSubscriber(PreviewSubscriber subscriber)
    {
        lock (_subscribersGate)
        {
            _subscribers = [.. _subscribers.Where(s => !ReferenceEquals(s, subscriber))];
        }

        _scheduler.Wake();
    }

    private bool HasSubscribers => _subscribers.Length > 0;

    private static double FpsOf(TimeSpan interval) => (double)TimeSpan.TicksPerSecond / Math.Max(1, interval.Ticks);

    /// <summary>
    /// Le plafond d'images du moment. Sans écran, l'image ne part nulle part : elle ne sert qu'aux abonnés de l'aperçu, donc le
    /// plafond ne dépasse pas celui du plus rapide d'entre eux (4 i/s pour un aperçu normal : pas d'encodage perdu à 12 i/s).
    /// Avec l'écran, c'est celui de la disposition.
    /// </summary>
    private void ApplyFpsCap()
    {
        var cap = _configuredFps;
        if (_screen.State != ScreenState.Ready)
        {
            TimeSpan? shortest = null;
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.IsDisposed) continue;
                if (shortest is null || subscriber.Interval < shortest) shortest = subscriber.Interval;
            }

            if (shortest is { } interval) cap = Math.Min(cap, FpsOf(interval));
        }

        _scheduler.SetMaxFps(cap);
    }

    /// <summary>Remet <paramref name="frame"/> à chaque abonné (il remplace l'image en attente) puis livre ceux dont le délai est écoulé.</summary>
    private void Publish(PreviewFrame frame)
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.Pending = frame;
        }

        FlushPreview();
    }

    /// <summary>Livre l'image en attente de chaque abonné dont le plafond le permet.</summary>
    private void FlushPreview()
    {
        var subscribers = _subscribers;
        if (subscribers.Length == 0) return;

        var now = _time.GetTimestamp();
        foreach (var subscriber in subscribers)
        {
            if (subscriber.Pending is not { } frame || subscriber.IsDisposed) continue;
            if (subscriber.HasPublished && _time.GetElapsedTime(subscriber.LastPublish, now) < subscriber.Interval) continue;

            subscriber.Pending = null;
            subscriber.HasPublished = true;
            subscriber.LastPublish = now;
            try
            {
                subscriber.OnFrame(frame);
            }
            catch (Exception ex)
            {
                // Un abonné qui plante ne doit ni arrêter la boucle ni priver les autres.
                _renderErrors.Report($"Échec de la remise d'une image à l'aperçu : {ex}");
            }
        }
    }

    /// <summary>Le délai avant qu'un abonné puisse recevoir son image en attente ; <c>null</c> si aucune n'attend.</summary>
    private TimeSpan? UntilNextPreview()
    {
        TimeSpan? soonest = null;
        foreach (var subscriber in _subscribers)
        {
            if (subscriber.Pending is null || subscriber.IsDisposed) continue;
            var wait = subscriber.HasPublished ? subscriber.Interval - _time.GetElapsedTime(subscriber.LastPublish) : TimeSpan.Zero;
            if (soonest is null || wait < soonest) soonest = wait;
        }

        return soonest;
    }

    /// <summary>
    /// Une itération : fait les changements de disposition en attente, abandonne la connexion si on l'a demandé ou si l'écran
    /// a disparu (sondé au plus une fois par seconde), reconnecte si besoin (au plus toutes les 2 s), rend et envoie si
    /// l'ordonnanceur le demande, sinon renvoie la dernière image si rien n'est parti depuis 1 s. Sans écran mais avec un
    /// abonné à l'aperçu, rend et publie sans envoyer.
    /// </summary>
    /// <returns>Le délai avant la prochaine itération utile, borné à [0, 1 s] : le réveil par le signal de l'ordonnanceur peut venir avant.</returns>
    public TimeSpan Tick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RunQueued();

        if (Interlocked.Exchange(ref _reconnectRequested, 0) == 1)
        {
            _screen.MarkLost(ResumeReason);
            _forceReconnect = true;
        }
        else if (_screen.State == ScreenState.Ready && _time.GetElapsedTime(_lastPresenceCheck) >= PresenceInterval)
        {
            _lastPresenceCheck = _time.GetTimestamp();
            if (!_screen.IsPresent()) _screen.MarkLost(ScreenDevice.AbsentReason);
        }

        if (_screen.State != ScreenState.Ready && !TryReconnect() && !HasSubscribers)
        {
            return NextDelay();
        }

        ApplyFpsCap();
        AdvanceBackground();
        RenderAndSend();
        KeepAlive();
        FlushPreview();
        return NextDelay();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Les changements en attente ne restent pas suspendus : ils reçoivent un résultat vide.
        Action<bool>[] pending;
        lock (_queueGate)
        {
            _queueClosed = true;
            pending = [.. _queue];
            _queue.Clear();
        }

        foreach (var item in pending)
        {
            item(true);
        }

        lock (_subscribersGate)
        {
            _subscribers = [];
        }

        lock (_lifecycleGate)
        {
            foreach (var module in _live.List)
            {
                Retire(module);
            }
        }

        _viewStore?.Flush();

        // Le compositeur garde une copie floutée du fond : on le libère avant le fond.
        _composer.Dispose();
        _background.Dispose();
    }

    /// <summary>
    /// Le temps à dormir d'après l'état après cette itération : jusqu'au prochain essai de connexion si l'écran
    /// n'est pas prêt ; sinon jusqu'à la prochaine image possible (si un redessin est attendu ou si un module
    /// s'anime), au renvoi de l'image ou au sondage de présence. Sans écran mais avec un abonné à l'aperçu, la prochaine
    /// image possible compte aussi. Borné à [0, 1 s].
    /// </summary>
    private TimeSpan NextDelay()
    {
        var delay = MaxDelay;
        var ready = _screen.State == ScreenState.Ready;
        if (!ready)
        {
            if (!_forceReconnect && _hasTriedConnect)
            {
                delay = Min(delay, ReconnectInterval - _time.GetElapsedTime(_lastConnectAttempt));
            }
            else
            {
                delay = TimeSpan.Zero;
            }
        }
        else
        {
            delay = Min(delay, PresenceInterval - _time.GetElapsedTime(_lastPresenceCheck));
            if (_screen.LastFrame is not null)
            {
                delay = Min(delay, KeepAliveInterval - _time.GetElapsedTime(_lastSend));
            }
        }

        if (ready || HasSubscribers)
        {
            if (_scheduler.IsDirty || AnyAnimating())
            {
                delay = Min(delay, _scheduler.UntilNextSlot());
            }

            // Un fond animé réveille la boucle à sa prochaine image, jamais plus vite que le plafond d'images par seconde.
            // L'échéance est absolue : le temps passé à rendre et à envoyer l'image ne s'ajoute pas au délai.
            if (_background.IsAnimated)
            {
                var elapsed = _time.GetElapsedTime(_backgroundStart);
                if (_backgroundRetryAt is { } retry)
                {
                    delay = Min(delay, retry - elapsed); // fond en échec : un essai par seconde, pas un réveil par image
                }
                else if (_backgroundDue is { } due)
                {
                    delay = Min(delay, Max(due - elapsed, _scheduler.UntilNextSlot()));
                }
            }

            // Une image retenue par le plafond d'un abonné part dès que son délai est écoulé.
            if (UntilNextPreview() is { } preview)
            {
                delay = Min(delay, preview);
            }
        }

        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Avance le fond animé au temps écoulé depuis le démarrage (horloge monotone : un changement d'heure ne bloque pas la
    /// vidéo) et demande une image s'il a changé. Rien tant que personne ne verrait le résultat (écran pas prêt et aucun
    /// abonné à l'aperçu). Note l'échéance absolue de l'image suivante. Si l'avance lève, l'erreur est journalisée (une fois,
    /// puis au plus une ligne de synthèse par minute) et le fond n'est réessayé qu'une fois par seconde : un fond en échec
    /// ne réveille pas la boucle à chaque image.
    /// </summary>
    private void AdvanceBackground()
    {
        if (!_background.IsAnimated || (_screen.State != ScreenState.Ready && !HasSubscribers)) return;

        var elapsed = _time.GetElapsedTime(_backgroundStart);
        if (_backgroundRetryAt is { } retry && elapsed < retry) return;

        try
        {
            _backgroundRetryAt = null;
            if (_background.Advance(elapsed)) _scheduler.MarkDirty();
            _backgroundDue = _background.UntilNextFrame is { } until ? elapsed + until : null;
        }
        catch (Exception ex)
        {
            _renderErrors.Report($"Échec de l'avance du fond d'écran : {ex}");
            _backgroundDue = null;
            _backgroundRetryAt = elapsed + MaxDelay;
        }
    }

    private bool TryReconnect()
    {
        if (!_forceReconnect && _hasTriedConnect && _time.GetElapsedTime(_lastConnectAttempt) < ReconnectInterval)
        {
            return false;
        }

        _forceReconnect = false;
        _hasTriedConnect = true;
        _lastConnectAttempt = _time.GetTimestamp(); // début de la tentative : une toutes les 2 s exactement
        var connected = _screen.TryConnect();
        if (!connected) return false;

        _lastPresenceCheck = _time.GetTimestamp(); // le sondage de présence repart de la connexion
        _lastSend = _lastPresenceCheck; // TryConnect a renvoyé la dernière image, s'il y en avait une

        // ShouldRender consomme le drapeau : après une reconnexion, on redemande une image.
        _scheduler.MarkDirty();
        return true;
    }

    private void RenderAndSend()
    {
        var emulated = _screen.State != ScreenState.Ready;
        if (emulated && !HasSubscribers) return;
        if (!_scheduler.ShouldRender(AnyAnimating())) return;

        try
        {
            var live = _live;
            byte[] jpeg;
            var renderStart = _time.GetTimestamp();
            try
            {
                using var bitmap = _composer.Compose(live.Placements, live.Modules, _theme, _background, _time.GetLocalNow());
                jpeg = JpegEncoder.Encode(bitmap, live.Layout.Rotation);
            }
            catch (Exception ex)
            {
                _renderErrors.Report($"Échec du rendu de l'image : {ex}");
                _scheduler.MarkDirty();
                return;
            }

            var sendStart = _time.GetTimestamp();
            var renderTime = _time.GetElapsedTime(renderStart, sendStart);
            Smooth(ref _renderMs, renderTime);
            var frame = new PreviewFrame(jpeg, live.Layout.Rotation);

            if (emulated)
            {
                // Pas d'écran : l'image ne part nulle part, elle nourrit seulement l'aperçu.
                Publish(frame);
                return;
            }

            bool sent;
            try
            {
                sent = _screen.TrySendFrame(jpeg);
            }
            catch (ArgumentException ex)
            {
                // Au-delà de 450 000 octets le protocole refuse l'image : on l'ignore, sans réessayer en boucle.
                Trace.TraceError($"Image ignorée ({jpeg.Length} octets, trop grande) : {ex.Message}");
                return;
            }

            if (sent)
            {
                _lastSend = _time.GetTimestamp();
                FramesSent++;
                var sendTime = _time.GetElapsedTime(sendStart, _lastSend);
                Smooth(ref _sendMs, sendTime);
                _renderTotal += renderTime;
                _sendTotal += sendTime;
                _timedFrames++;
                if (FramesSent <= 10 || FramesSent % 100 == 0)
                {
                    Trace.TraceInformation($"Image {FramesSent} envoyée ({jpeg.Length} octets){TimingSummary()}");
                }

                Publish(frame); // le tableau envoyé n'est plus touché : on le passe tel quel aux abonnés
            }
            else
            {
                _scheduler.MarkDirty(); // l'écran a décroché : l'image sera renvoyée après la reconnexion
            }
        }
        finally
        {
            _scheduler.OnRendered();
        }
    }

    /// <summary>Moyenne glissante (poids 0,2 pour la dernière mesure) ; la première mesure sert de départ.</summary>
    private static void Smooth(ref double average, TimeSpan sample)
    {
        var ms = sample.TotalMilliseconds;
        var current = Volatile.Read(ref average);
        Volatile.Write(ref average, current < 0 ? ms : current + (ms - current) * 0.2);
    }

    /// <summary>
    /// " ; rendu 41 ms, envoi 58 ms" : les durées moyennes de rendu (composition et encodage) et d'envoi des images envoyées
    /// depuis la ligne de journal précédente ; remet les compteurs à zéro. Sert à mesurer ce que coûte un fond animé.
    /// </summary>
    private string TimingSummary()
    {
        int count = Math.Max(1, _timedFrames);
        var render = (long)Math.Round(_renderTotal.TotalMilliseconds / count);
        var send = (long)Math.Round(_sendTotal.TotalMilliseconds / count);
        var average = _timedFrames > 1 ? " en moyenne" : "";
        _renderTotal = _sendTotal = TimeSpan.Zero;
        _timedFrames = 0;
        return $" ; rendu {render} ms, envoi {send} ms{average}";
    }

    private void KeepAlive()
    {
        if (_screen.State != ScreenState.Ready || _time.GetElapsedTime(_lastSend) < KeepAliveInterval) return;

        var lastFrame = _screen.LastFrame;
        if (lastFrame is null) return;

        _lastSend = _time.GetTimestamp();
        if (_screen.TrySendFrame(lastFrame))
        {
            KeepAlivesSent++;
        }
        else
        {
            _scheduler.MarkDirty(); // l'écran a décroché : une image fraîche partira après la reconnexion
        }
    }

    private bool AnyAnimating()
    {
        foreach (var module in _live.Displayed)
        {
            if (module.IsAnimating) return true;
        }

        return false;
    }
}
