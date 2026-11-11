using System.Text.Json;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Une case d'un module de la communauté, vue par la boucle d'affichage comme un module ordinaire. Le module tourne dans un autre
/// processus (l'hôte, voir <see cref="ModuleHostSupervisor"/>) : ici on ne fait que poser la <b>dernière image reçue</b>, sans
/// jamais attendre l'hôte. Avant la première image, rien n'est dessiné ; si l'hôte meurt ou se tait, la dernière image reste
/// (assombrie) avec un message, et l'état de la case passe en <see cref="ModuleHealth.Offline"/>.
/// <para>
/// La taille de la case n'est connue qu'au premier <see cref="Render"/> : c'est lui qui fait ouvrir l'instance dans l'hôte (et
/// la rouvrir quand la taille ou la taille du texte change). <see cref="Render"/>, <see cref="Dispose"/> et les événements
/// sont sûrs entre fils.
/// </para>
/// </summary>
public sealed class RemoteModule : IModule
{
    /// <summary>Le message d'une case dont l'hôte n'a envoyé aucune image à temps.</summary>
    public const string NotRespondingText = "Le module ne répond pas";

    /// <summary>Le délai sans image, après l'ouverture (ou une demande de dessin), au-delà duquel la case l'affiche.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Combien de changements d'état annoncés par l'hôte sont pris par fenêtre de <see cref="StatusWindow"/> ; les suivants attendent la fin de la fenêtre (le dernier gagne).</summary>
    internal const int MaxStatusChangesPerWindow = 4;

    internal static readonly TimeSpan StatusWindow = TimeSpan.FromSeconds(1);

    private readonly ModuleHostSupervisor _owner;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private SKBitmap? _front;
    private bool _animating;
    private ModuleStatus _hostStatus = new(ModuleHealth.Online);
    private string? _supervisorMessage;
    private bool _awaiting;
    private bool _notResponding;
    private ITimer? _awaitTimer;
    private InstanceSlot? _slot;
    private FrameGeometry? _geometry;
    private ModuleStatus _status = new(ModuleHealth.Online);
    private bool _disposed;
    private long _messageVersion = -1;
    private long _framesReceived;
    private DateTimeOffset _statusWindowStart;
    private int _statusChanges;
    private ModuleStatus? _pendingHostStatus;
    private ITimer? _statusTimer;

    internal RemoteModule(ModuleHostSupervisor owner, ModuleDescriptor descriptor, ModuleContext context, TimeProvider time)
    {
        _owner = owner;
        Descriptor = descriptor;
        Context = context;
        _time = time;
    }

    /// <inheritdoc />
    public ModuleDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Vrai tant que l'hôte dit que le module veut être redessiné en continu (et que la case n'est pas en panne).</summary>
    public bool IsAnimating
    {
        get
        {
            lock (_gate)
            {
                return _animating && _supervisorMessage is null;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Invalidated;

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Le nombre d'images que l'hôte a livrées à cette case depuis son ouverture (une image de l'ancienne taille, jetée, ne compte pas).
    /// Le banc d'essai (<c>ashboard-module check</c>) s'en sert pour savoir qu'une image neuve est arrivée.
    /// </summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Ce que la fabrique a reçu pour créer la case (identifiant d'instance, réglages, journal…).</summary>
    internal ModuleContext Context { get; }

    /// <summary>La case ouverte dans l'hôte pour la taille courante (posée par le superviseur) ; les images des autres sont jetées.</summary>
    internal InstanceSlot? Slot
    {
        get => Volatile.Read(ref _slot);
        set => Volatile.Write(ref _slot, value);
    }

    /// <summary>Les réglages de la case, tels que l'hôte les reçoit.</summary>
    internal JsonElement Settings => Context.Settings.ValueKind == JsonValueKind.Object ? Context.Settings : ModuleHostSupervisor.EmptySettings;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Pose la dernière image reçue à la place de la case. N'attend jamais l'hôte : au plus, elle prévient le superviseur que la
    /// taille a changé (il rouvre l'instance, sans bloquer).
    /// </summary>
    public void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        var geometry = new FrameGeometry(
            (int)Math.Round(ctx.Bounds.Width), (int)Math.Round(ctx.Bounds.Height), ctx.Size.Columns, ctx.Size.Rows, ctx.TextScale);
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changed = _geometry != geometry;
            if (changed)
            {
                _geometry = geometry;
            }
        }

        if (changed && geometry.Width > 0 && geometry.Height > 0)
        {
            _owner.GeometryChanged(this, geometry);
        }

        ModuleStatus status;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            status = _status;
            if (_front is { } image)
            {
                // En panne, la dernière image reste mais s'efface derrière le message.
                using var paint = status.Health == ModuleHealth.Offline ? new SKPaint { Color = SKColors.White.WithAlpha(90) } : null;
                canvas.DrawBitmap(image, ctx.Bounds.Left, ctx.Bounds.Top, paint);
            }
        }

        if (status.Health != ModuleHealth.Online && status.ActionMessage is { Length: > 0 } message)
        {
            ModuleDrawing.DrawActionMessage(canvas, ctx.Bounds, ctx.Theme, ctx.TextScale, message);
        }
    }

    /// <summary>Ferme la case : l'instance est refermée dans l'hôte, qui s'arrête avec sa dernière case.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _awaitTimer?.Dispose();
            _awaitTimer = null;
            _statusTimer?.Dispose();
            _statusTimer = null;
            _front?.Dispose();
            _front = null;
        }

        _owner.Release(this);
    }

    // --- Ce que le superviseur fait à la case ----------------------------------------------------------------------------------

    /// <summary>
    /// Pose une image copiée dans la case (la précédente revient à <paramref name="slot"/> pour la copie suivante) et redemande un dessin.
    /// Faux si la case est refermée ou si l'image n'est plus celle de sa taille actuelle.
    /// </summary>
    internal bool Publish(InstanceSlot slot, SKBitmap image, bool animating)
    {
        SKBitmap? replaced;
        bool statusChanged;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(Slot, slot))
            {
                image.Dispose();
                return false;
            }

            replaced = _front;
            _front = image;
            Interlocked.Increment(ref _framesReceived);
            _animating = animating;
            _awaiting = false;
            _notResponding = false;
            _awaitTimer?.Dispose();
            _awaitTimer = null;
            statusChanged = Recompute();
        }

        slot.Recycle(replaced);
        Raise(statusChanged, newImage: true);
        return true;
    }

    /// <summary>Une image n'a pas été copiée (débit dépassé), mais l'hôte dit toujours si le module est animé.</summary>
    internal void NoteAnimating(bool animating)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _animating = animating;
            }
        }
    }

    /// <summary>
    /// L'état que l'hôte annonce pour la case. Un hôte qui change d'état sans cesse n'agite pas la boucle d'affichage : au plus
    /// <see cref="MaxStatusChangesPerWindow"/> changements par <see cref="StatusWindow"/>, le dernier état en attente est pris à la fin de la fenêtre.
    /// </summary>
    internal void SetHostStatus(ModuleHealth health, string? message)
    {
        bool statusChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = new ModuleStatus(health, message);
            if (next == _hostStatus && _pendingHostStatus is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (now - _statusWindowStart >= StatusWindow)
            {
                _statusWindowStart = now;
                _statusChanges = 0;
            }

            if (_statusChanges >= MaxStatusChangesPerWindow)
            {
                _pendingHostStatus = next;
                _statusTimer ??= _time.CreateTimer(_ => ApplyPendingStatus(), null, _statusWindowStart + StatusWindow - now, Timeout.InfiniteTimeSpan);
                return;
            }

            _statusChanges++;
            _pendingHostStatus = null;
            _hostStatus = next;
            statusChanged = Recompute();
        }

        Raise(statusChanged);
    }

    private void ApplyPendingStatus()
    {
        bool statusChanged;
        lock (_gate)
        {
            _statusTimer?.Dispose();
            _statusTimer = null;
            if (_disposed || _pendingHostStatus is not { } pending)
            {
                return;
            }

            _pendingHostStatus = null;
            _statusWindowStart = _time.GetUtcNow();
            _statusChanges = 1;
            _hostStatus = pending;
            statusChanged = Recompute();
        }

        Raise(statusChanged);
    }

    /// <summary>
    /// Le message du superviseur (hôte arrêté, mis de côté, fichiers modifiés…) ; <c>null</c> le retire. Il l'emporte sur l'état de
    /// l'hôte et fait passer la case en <see cref="ModuleHealth.Offline"/>. <paramref name="version"/> numérote les décisions du superviseur (prise sous
    /// son verrou) : une décision périmée, arrivée en retard, est ignorée.
    /// </summary>
    internal void SetSupervisorMessage(string? message, long version = -1)
    {
        bool statusChanged;
        lock (_gate)
        {
            if (_disposed || (version >= 0 && version < _messageVersion))
            {
                return; // une décision plus récente du superviseur est déjà passée (les décisions s'appliquent hors de son verrou)
            }

            if (version >= 0)
            {
                _messageVersion = version;
            }

            _supervisorMessage = message;
            if (message is not null)
            {
                _animating = false;
            }

            statusChanged = Recompute();
        }

        Raise(statusChanged);
    }

    /// <summary>
    /// On attend une image (ouverture, nouvelle taille, nouveau thème, hôte relancé) : sans elle au bout de
    /// <see cref="ResponseTimeout"/>, la case dit que le module ne répond pas. Un seul minuteur, tant que l'image n'est pas là.
    /// </summary>
    internal void BeginAwaitingFrame()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _awaiting = true;
            _notResponding = false;
            _awaitTimer?.Dispose();
            _awaitTimer = _time.CreateTimer(_ => OnResponseTimeout(), null, ResponseTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnResponseTimeout()
    {
        bool statusChanged;
        lock (_gate)
        {
            if (_disposed || !_awaiting)
            {
                return;
            }

            _notResponding = true;
            statusChanged = Recompute();
        }

        Raise(statusChanged);
    }

    /// <summary>Recalcule l'état publié d'après ses trois sources. À appeler sous le verrou ; vrai s'il a changé.</summary>
    private bool Recompute()
    {
        var status = _supervisorMessage is { } message ? new ModuleStatus(ModuleHealth.Offline, message)
            : _notResponding ? new ModuleStatus(ModuleHealth.Offline, NotRespondingText)
            : _hostStatus;
        if (status == _status)
        {
            return false;
        }

        _status = status;
        return true;
    }

    /// <summary>Lève les événements, hors verrou : l'état a changé (<paramref name="statusChanged"/>) ou une image neuve est là (<paramref name="newImage"/>).</summary>
    private void Raise(bool statusChanged, bool newImage = false)
    {
        if (statusChanged)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        if (statusChanged || newImage)
        {
            Invalidated?.Invoke(this, EventArgs.Empty);
        }
    }
}
