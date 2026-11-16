using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;

namespace Trofeo.Display;

/// <summary>
/// Le choix de la carte graphique : Automatique prend la première carte de la liste, une carte enregistrée se retrouve par ses
/// identifiants puis par son nom.
/// </summary>
public static class GpuAdapterChoice
{
    /// <summary>
    /// La carte à utiliser, ou <c>null</c> s'il n'y en a aucune. Une carte enregistrée qui n'existe plus (carte retirée,
    /// disposition copiée d'un autre PC) donne la carte automatique, avec un avertissement.
    /// </summary>
    public static GpuAdapterInfo? Resolve(AccelerationSettings settings, IReadOnlyList<GpuAdapterInfo> found, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(warn);

        if (found.Count == 0) return null;
        if (settings.Adapter is not { } saved) return found[0];

        var match = found.FirstOrDefault(a => a.Id.VendorId == saved.VendorId && a.Id.DeviceId == saved.DeviceId && a.Id.SubSysId == saved.SubSysId)
            ?? found.FirstOrDefault(a => string.Equals(a.Id.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        warn($"La carte {saved.Name} n'est plus là : la carte automatique est utilisée.");
        return found[0];
    }
}

/// <summary>
/// Le chef de l'accélération matérielle : choisit la carte d'après les réglages, garde l'appareil de dessin, se replie sur le
/// processeur quand la carte tombe en panne ou n'est pas assez rapide, retente la carte plus tard, et dit où on en est.
/// <para>
/// Les méthodes qui changent l'état se servent d'un verrou : elles peuvent venir de la boucle d'affichage (qui seule dessine sur
/// l'appareil) comme du fil des rechargements. <see cref="Status"/>, <see cref="DrawingDevice"/> et <see cref="VideoAdapter"/> se
/// lisent de n'importe quel fil. <see cref="EffectiveChoiceChanged"/> est levé hors du verrou, depuis le fil de l'appelant : un
/// abonné ne doit ni bloquer ni rappeler le contrôleur de façon bloquante.
/// </para>
/// <para>
/// Un appareil remplacé ou en panne n'est pas libéré tout de suite : des compositeurs s'en servent peut-être encore. Il attend
/// <see cref="ReleaseRetired"/>, que la boucle appelle une fois ses compositeurs refaits (jamais avant : un compositeur libère ses
/// surfaces en mourant, et elles appartiennent à l'appareil).
/// </para>
/// </summary>
public sealed class AccelerationController : IDisposable
{
    /// <summary>Le délai entre une panne (ou un essai raté) et le nouvel essai.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Combien de nouveaux essais après une panne, au plus, tant que les réglages ne changent pas.</summary>
    private const int MaxRetries = 3;

    /// <summary>La durée sur laquelle on moyenne le temps de dessin d'une image.</summary>
    private static readonly TimeSpan SlowWindow = TimeSpan.FromSeconds(10);

    /// <summary>Au-delà (en moyenne), la carte ne tient pas le rythme de 12 images par seconde.</summary>
    private const double SlowMilliseconds = 83;

    /// <summary>Sous ce nombre d'images dans la fenêtre, l'écran est presque immobile : pas de verdict (une image de démarrage lente ne condamne pas la carte).</summary>
    private const int MinSamples = 3;

    private const string CpuDrawing = "dessin sur le processeur";

    private readonly object _gate = new();
    private readonly IGpuDeviceFactory? _factory;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    // Tout ce qui suit est sous _gate, sauf _device, _video et _status (publiés pour les lecteurs d'autres fils).
    private AccelerationSettings? _settings;
    private IReadOnlyList<GpuAdapterInfo> _adapters = [];
    private GpuAdapterInfo? _candidate;             // la carte voulue : le choix, ou la carte suivante après une lenteur
    private GpuAdapterInfo? _deviceAdapter;         // la carte de _device
    private readonly List<IGpuDevice> _retired = [];
    private bool _autoPick;                         // la carte vient du choix automatique (ou d'une carte enregistrée absente)
    private bool _suspended;                        // en panne : tout sur le processeur en attendant un nouvel essai
    private string? _failure;
    private string? _slow;
    private string? _notice;
    private int _retries;
    private long? _retryFrom;                       // l'instant de la panne : le nouvel essai a lieu 5 min plus tard
    private long? _windowStart;
    private double _windowMilliseconds;
    private int _windowSamples;
    private bool _disposed;

    private volatile IGpuDevice? _device;
    private volatile GpuAdapterInfo? _video;
    private volatile AccelerationStatus _status = new(false, false, null, false, false, null, []);

    /// <param name="factory">Les cartes et leurs appareils ; <c>null</c> : jamais de carte, tout reste sur le processeur.</param>
    /// <param name="time">L'horloge des nouveaux essais et de la mesure de vitesse (factice dans les tests).</param>
    /// <param name="log">Reçoit chaque ligne de journal (panne, nouvel essai, carte trop lente…).</param>
    public AccelerationController(IGpuDeviceFactory? factory, TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _factory = factory;
        _time = time;
        _log = log;
    }

    /// <summary>L'appareil sur lequel dessiner ; <c>null</c> : le processeur dessine.</summary>
    public IGpuDevice? DrawingDevice => _device;

    /// <summary>La carte qui décode les vidéos ; <c>null</c> : le processeur les décode.</summary>
    public GpuAdapterInfo? VideoAdapter => _video;

    /// <summary>L'état à montrer, remplacé d'un bloc à chaque changement.</summary>
    public AccelerationStatus Status => _status;

    /// <summary>
    /// La carte des vidéos a changé (réglages, panne, nouvel essai, carte trop lente) : le service relit la disposition pour rouvrir
    /// la vidéo. Levé hors du verrou, depuis le fil qui a provoqué le changement.
    /// </summary>
    public event Action? EffectiveChoiceChanged;

    /// <summary>
    /// Applique les réglages (<c>null</c> : ceux par défaut). Des réglages identiques à ceux en place ne changent rien : un
    /// rechargement qui ne touche pas à l'accélération garde son appareil, sa panne éventuelle et ses essais. D'autres réglages
    /// repartent de zéro (carte relue, pannes et essais oubliés).
    /// </summary>
    public void Apply(AccelerationSettings? settings)
    {
        settings ??= AccelerationSettings.Default;
        Mutate(() =>
        {
            if (_settings is not null && _settings.Equals(settings)) return;

            _settings = settings;
            _suspended = false;
            _failure = _slow = _notice = null;
            _retries = 0;
            _retryFrom = null;
            ResetWindow();
            _adapters = ListAdapters();
            _candidate = settings.Enabled ? GpuAdapterChoice.Resolve(settings, _adapters, WarnAbsent) : null;
            _autoPick = settings.Adapter is null || _notice is not null;
            Settle();
        });
    }

    /// <summary>
    /// La carte est tombée en panne (perdue, réinitialisée, pilote planté) : tout passe sur le processeur, un nouvel essai viendra.
    /// L'appareil n'est pas libéré ici (voir <see cref="ReleaseRetired"/>). Sans effet si on est déjà en panne.
    /// </summary>
    public void ReportFailure(string reason) => Mutate(() =>
    {
        if (_settings is null || _suspended || (_device is null && _video is null)) return;

        Suspend($"La carte graphique a eu un problème ({reason}) : {CpuDrawing}.");
    });

    /// <summary>Le temps de dessin d'une image composée sur la carte (la composition seule, sans l'encodage ni l'envoi).</summary>
    public void ReportFrame(TimeSpan gpuRenderTime)
    {
        lock (_gate)
        {
            if (_device is null || _disposed) return;

            _windowStart ??= _time.GetTimestamp();
            _windowMilliseconds += gpuRenderTime.TotalMilliseconds;
            _windowSamples++;
        }
    }

    /// <summary>
    /// Fait ce que le temps qui passe demande : un nouvel essai de la carte après une panne (toutes les 5 minutes, trois fois au
    /// plus), puis le verdict de vitesse. En choix automatique, une carte trop lente laisse la place à la suivante, puis au
    /// processeur ; une carte choisie à la main reste, avec une mention.
    /// </summary>
    /// <returns>Vrai si <see cref="DrawingDevice"/> a changé (nouvel essai, carte suivante, repli).</returns>
    public bool Tick() => Mutate(() =>
    {
        if (_settings is null) return;

        if (_suspended && _retryFrom is { } since && _time.GetElapsedTime(since) >= RetryInterval)
        {
            _retries++;
            _retryFrom = null;
            _suspended = false;
            _failure = null;
            _log($"Accélération matérielle : nouvel essai de la carte graphique ({_retries}/{MaxRetries}).");
            Settle();
        }

        JudgeSpeed();
    });

    /// <summary>
    /// Libère les appareils remplacés ou en panne. À appeler une fois que plus aucun compositeur ne s'en sert : la boucle l'appelle
    /// après avoir refait les siens.
    /// </summary>
    public void ReleaseRetired()
    {
        IGpuDevice[] devices;
        lock (_gate)
        {
            if (_retired.Count == 0) return;

            devices = [.. _retired];
            _retired.Clear();
        }

        DisposeAll(devices);
    }

    public void Dispose()
    {
        IGpuDevice[] devices;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            RetireDevice();
            Publish();
            devices = [.. _retired];
            _retired.Clear();
        }

        DisposeAll(devices);
    }

    /// <summary>
    /// Change l'état sous le verrou, publie le nouvel état, puis lève <see cref="EffectiveChoiceChanged"/> (hors du verrou) si la carte
    /// des vidéos a changé. Rend vrai si l'appareil de dessin a changé.
    /// </summary>
    private bool Mutate(Action change)
    {
        bool deviceChanged, videoChanged;
        lock (_gate)
        {
            if (_disposed) return false;

            var (device, video) = (_device, _video);
            change();
            Publish();
            deviceChanged = !ReferenceEquals(device, _device);
            videoChanged = !Equals(video, _video);
        }

        if (videoChanged) RaiseEffectiveChoiceChanged();
        return deviceChanged;
    }

    private void RaiseEffectiveChoiceChanged()
    {
        try
        {
            EffectiveChoiceChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"Accélération matérielle : un abonné au changement de carte a échoué : {ex.Message}");
        }
    }

    private IReadOnlyList<GpuAdapterInfo> ListAdapters()
    {
        if (_factory is null) return [];

        try
        {
            return [.. _factory.ListAdapters()];
        }
        catch (Exception ex)
        {
            _log($"Accélération matérielle : les cartes graphiques n'ont pas pu être listées : {ex.Message}");
            return [];
        }
    }

    private void WarnAbsent(string warning)
    {
        _notice = warning;
        _log(warning);
    }

    /// <summary>Met l'appareil de dessin en accord avec les réglages et la carte voulue : en ouvre un, garde celui qui convient, retire les autres.</summary>
    private void Settle()
    {
        var settings = _settings!;
        var wantDevice = settings is { Enabled: true, Drawing: true } && !_suspended && _candidate is not null;
        if (_device is not null && (!wantDevice || !Equals(_deviceAdapter, _candidate))) RetireDevice();
        if (!wantDevice || _device is not null) return;

        try
        {
            _device = _factory!.Create(_candidate!);
            _deviceAdapter = _candidate;
            ResetWindow();
        }
        catch (Exception ex)
        {
            _log($"Accélération matérielle : « {_candidate!.Id.Name} » n'a pas démarré : {ex}");
            Suspend($"La carte graphique n'a pas pu démarrer ({ex.Message}) : {CpuDrawing}.");
        }
    }

    /// <summary>Passe sur le processeur en attendant un nouvel essai (s'il en reste) ; <paramref name="problem"/> est la phrase à montrer.</summary>
    private void Suspend(string problem)
    {
        RetireDevice();
        _suspended = true;
        _failure = problem;
        _slow = null;
        _retryFrom = _retries < MaxRetries ? _time.GetTimestamp() : null;
        ResetWindow();
        _log(problem);
    }

    /// <summary>Le verdict de vitesse, une fois la fenêtre de mesure écoulée.</summary>
    private void JudgeSpeed()
    {
        if (_device is null || _windowStart is not { } start || _time.GetElapsedTime(start) < SlowWindow) return;

        var samples = _windowSamples;
        var average = _windowMilliseconds / Math.Max(1, samples);
        ResetWindow();
        if (samples < MinSamples) return;

        if (average <= SlowMilliseconds)
        {
            _slow = null;
            return;
        }

        var name = _candidate!.Id.Name;
        if (!_autoPick)
        {
            // Une carte choisie à la main reste : c'est le choix de l'utilisateur, la fenêtre le prévient.
            if (_slow is null) _log($"Accélération matérielle : « {name} » met {average:0} ms par image : trop lente.");
            _slow = $"{name} est trop lente pour tenir le rythme.";
            return;
        }

        var index = _adapters.ToList().FindIndex(a => Equals(a, _candidate));
        var next = index >= 0 && index + 1 < _adapters.Count ? _adapters[index + 1] : null;
        _log(
            $"Accélération matérielle : « {name} » met {average:0} ms par image : trop lente, "
            + (next is null ? "dessin sur le processeur." : $"essai de « {next.Id.Name} »."));
        _candidate = next;
        if (next is null)
        {
            RetireDevice();
            _slow = $"Aucune carte n'est assez rapide : {CpuDrawing}.";
        }
        else
        {
            Settle();
        }
    }

    private void ResetWindow()
    {
        _windowStart = null;
        _windowMilliseconds = 0;
        _windowSamples = 0;
    }

    /// <summary>Retire l'appareil de dessin : plus personne ne le reçoit, mais il n'est libéré qu'à <see cref="ReleaseRetired"/>.</summary>
    private void RetireDevice()
    {
        if (_device is not { } device) return;

        _retired.Add(device);
        _device = null;
        _deviceAdapter = null;
    }

    /// <summary>Calcule la carte des vidéos et l'état à montrer d'après tout ce qui précède.</summary>
    private void Publish()
    {
        var settings = _settings;
        _video = settings is { Enabled: true, VideoDecoding: true } && !_suspended ? _candidate : null;
        var drawing = _device is not null;
        _status = new AccelerationStatus(
            Available: _factory is not null && _adapters.Count > 0,
            Enabled: settings?.Enabled ?? false,
            Adapter: drawing || _video is not null ? _candidate : null,
            Drawing: drawing,
            VideoDecoding: _video is not null,
            Problem: _failure ?? _slow ?? _notice,
            Adapters: _adapters);
    }

    private void DisposeAll(IEnumerable<IGpuDevice> devices)
    {
        foreach (var device in devices)
        {
            try
            {
                device.Dispose();
            }
            catch (Exception ex)
            {
                _log($"Accélération matérielle : la carte graphique n'a pas pu être libérée : {ex.Message}");
            }
        }
    }
}
