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

    /// <summary>Combien de nouveaux essais de suite après une panne, au plus, tant que les réglages ne changent pas.</summary>
    private const int MaxRetries = 3;

    /// <summary>Un appareil qui tourne sans panne pendant ce temps remet le compte des essais à zéro : une panne plus tardive retrouve ses trois essais.</summary>
    private static readonly TimeSpan HealthyRunReset = TimeSpan.FromHours(1);

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
    private bool _suspended;                        // le dessin est en panne : il passe sur le processeur en attendant un nouvel essai (pas les vidéos)
    private bool _noFastCard;                       // aucune carte n'est assez rapide pour le dessin : il reste sur le processeur (pas les vidéos)
    private bool _videoOpen;                        // une vidéo est ouverte en fond (rapporté par la boucle)
    private long? _videoDecoderLuid;                // la carte qui la décode vraiment ; null : le processeur
    private string? _failure;
    private string? _slow;
    private string? _notice;
    private DateTimeOffset _failureAt;              // l'heure où chaque phrase a été posée (UTC) ; vaut tant que la phrase existe
    private DateTimeOffset _slowAt;
    private DateTimeOffset _noticeAt;
    private int _retries;
    private long? _retryFrom;                       // l'instant de la panne : le nouvel essai a lieu 5 min plus tard
    private long? _healthySince;                    // l'instant où l'appareil en place a été ouvert
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

    /// <summary>
    /// La carte qui décoderait les vidéos avec <paramref name="settings"/> (<c>null</c> : le processeur), sans rien changer : aucun
    /// appareil n'est ouvert, aucun événement n'est levé. Avec les réglages en place, c'est <see cref="VideoAdapter"/> (qui tient compte
    /// d'une panne ou d'une carte trop lente) ; avec d'autres, la carte que <see cref="Apply"/> prendrait, pannes oubliées. Le service
    /// s'en sert pour ouvrir (ou rouvrir) la vidéo avant que la boucle applique les réglages. Sûr depuis n'importe quel fil, y compris
    /// celui des rechargements : ne prend le verrou que pour lire.
    /// </summary>
    public GpuAdapterInfo? VideoAdapterFor(AccelerationSettings? settings)
    {
        settings ??= AccelerationSettings.Default;
        if (!settings.Enabled || !settings.VideoDecoding) return null;

        lock (_gate)
        {
            if (_disposed || _factory is null) return null;
            if (_settings is not null && _settings.Equals(settings)) return _video;
        }

        return GpuAdapterChoice.Resolve(settings, ListAdapters(), _ => { }); // l'avertissement d'une carte absente viendra avec Apply
    }

    /// <summary>L'état à montrer, remplacé d'un bloc à chaque changement.</summary>
    public AccelerationStatus Status => _status;

    /// <summary>
    /// La carte des vidéos a changé (réglages, carte retrouvée par un nouvel essai, carte suivante après une lenteur) : le service relit la disposition pour rouvrir
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
            _noFastCard = false;
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
    /// La carte est tombée en panne pendant le dessin (perdue, réinitialisée, pilote planté) : le dessin passe sur le processeur,
    /// un nouvel essai viendra. Les vidéos gardent leur carte : si le décodage lui-même échoue, c'est la vidéo qui se replie (voir
    /// <see cref="ReportVideoDecoder"/>). L'appareil n'est pas libéré ici (voir <see cref="ReleaseRetired"/>). Sans effet si le
    /// dessin n'est pas sur la carte (déjà en panne, coupé).
    /// </summary>
    public void ReportFailure(string reason) => Mutate(() =>
    {
        if (_settings is null || _suspended || _device is null) return;

        Suspend($"La carte graphique a eu un problème ({reason}) : {CpuDrawing}.");
    });

    /// <summary>
    /// Où la vidéo de fond se décode vraiment : <paramref name="videoOpen"/> dit si une vidéo est ouverte, <paramref name="decoderLuid"/>
    /// la carte qui la décode (<c>null</c> : le processeur, par choix ou après un repli). La fenêtre dit ainsi où les vidéos tournent
    /// réellement : sans vidéo ouverte, elle montre ce qui est voulu ; avec une vidéo qui se décode sur le processeur alors que la carte
    /// est voulue (fichier refusé, repli en cours de lecture), elle ne parle plus de vidéos sur la carte.
    /// </summary>
    public void ReportVideoDecoder(bool videoOpen, long? decoderLuid) => Mutate(() =>
    {
        _videoOpen = videoOpen;
        _videoDecoderLuid = videoOpen ? decoderLuid : null;
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
            var failedAt = _failureAt;
            _log($"Accélération matérielle : nouvel essai de la carte graphique ({_retries}/{MaxRetries}).");
            RetryCard();
            if (_failure is not null)
            {
                _failureAt = failedAt; // l'essai échoue avant que l'appareil ne s'ouvre : c'est la même carte morte, pas une nouvelle panne
            }
        }

        if (_device is not null && _retries > 0 && _healthySince is { } opened && _time.GetElapsedTime(opened) >= HealthyRunReset)
        {
            _retries = 0; // une heure sans panne : un nouvel échec aura de nouveau tous ses essais
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
        if (_notice != warning) _noticeAt = _time.GetUtcNow();
        _notice = warning;
        _log(warning);
    }

    /// <summary>
    /// Un nouvel essai après une panne : les cartes sont relues d'abord. Le LUID d'une carte ne vaut que pour une séance Windows
    /// (il change après une mise à jour du pilote ou un arrêt et un redémarrage de la carte), et la fabrique retrouve la carte par
    /// lui : la carte gardée en mémoire ne se retrouverait plus. On cherche donc la même carte (mêmes identifiants, puis même nom)
    /// dans la liste fraîche ; absente, la carte automatique la remplace, avec la mention habituelle.
    /// </summary>
    private void RetryCard()
    {
        _adapters = ListAdapters();
        var wanted = _candidate is null ? _settings! : new AccelerationSettings(Adapter: _candidate.Id);
        _candidate = GpuAdapterChoice.Resolve(wanted, _adapters, WarnAbsent);
        _autoPick = _settings!.Adapter is null || _notice is not null;
        if (_candidate is null)
        {
            Suspend($"La carte graphique n'a pas pu démarrer (aucune carte graphique trouvée) : {CpuDrawing}.");
            return;
        }

        Settle();
    }

    /// <summary>Met l'appareil de dessin en accord avec les réglages et la carte voulue : en ouvre un, garde celui qui convient, retire les autres.</summary>
    private void Settle()
    {
        var settings = _settings!;
        var wantDevice = settings is { Enabled: true, Drawing: true } && !_suspended && !_noFastCard && _candidate is not null;
        if (_device is not null && (!wantDevice || !Equals(_deviceAdapter, _candidate))) RetireDevice();
        if (!wantDevice || _device is not null) return;

        try
        {
            _device = _factory!.Create(_candidate!);
            _deviceAdapter = _candidate;
            _healthySince = _time.GetTimestamp();
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
        _failureAt = _time.GetUtcNow(); // une nouvelle panne (même au même texte) est un nouvel événement ; un essai raté garde l'heure (voir Tick)
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
            SetSlow($"{name} est trop lente pour tenir le rythme.");
            return;
        }

        var index = _adapters.ToList().FindIndex(a => Equals(a, _candidate));
        var next = index >= 0 && index + 1 < _adapters.Count ? _adapters[index + 1] : null;
        _log(
            $"Accélération matérielle : « {name} » met {average:0} ms par image : trop lente, "
            + (next is null ? "dessin sur le processeur." : $"essai de « {next.Id.Name} »."));
        if (next is null)
        {
            // Le dessin se replie sur le processeur ; la carte reste celle des vidéos, que sa lenteur à dessiner ne concerne pas.
            _noFastCard = true;
            RetireDevice();
            SetSlow($"Aucune carte n'est assez rapide : {CpuDrawing}.");
        }
        else
        {
            _candidate = next;
            Settle();
        }
    }

    /// <summary>Pose la phrase de lenteur ; la même phrase reposée à chaque verdict garde son heure.</summary>
    private void SetSlow(string sentence)
    {
        if (_slow != sentence) _slowAt = _time.GetUtcNow();
        _slow = sentence;
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
        _healthySince = null;
    }

    /// <summary>Calcule la carte des vidéos et l'état à montrer d'après tout ce qui précède.</summary>
    private void Publish()
    {
        var settings = _settings;
        // Les vidéos ne suivent pas le dessin : une panne ou une lenteur du dessin ne leur retire pas la carte (un décodage qui
        // échoue se replie vidéo par vidéo, voir ReportVideoDecoder).
        _video = settings is { Enabled: true, VideoDecoding: true } ? _candidate : null;
        var drawing = _device is not null;

        // Une vidéo ouverte qui ne se décode pas sur la carte voulue : la fenêtre ne dit pas qu'elle tourne dessus.
        var videoOnCard = _video is not null && (!_videoOpen || _videoDecoderLuid == _video.Luid);
        // Le genre et l'heure suivent la phrase publiée (_failure ?? _slow ?? _notice).
        var (kind, at) = _failure is not null ? (AccelerationProblemKind.Failure, _failureAt)
            : _slow is not null ? (AccelerationProblemKind.Slow, _slowAt)
            : _notice is not null ? (AccelerationProblemKind.Gone, _noticeAt)
            : ((AccelerationProblemKind?)null, default(DateTimeOffset));
        _status = new AccelerationStatus(
            Available: _factory is not null && _adapters.Count > 0,
            Enabled: settings?.Enabled ?? false,
            Adapter: drawing || videoOnCard ? _candidate : null,
            Drawing: drawing,
            VideoDecoding: videoOnCard,
            Problem: _failure ?? _slow ?? _notice,
            Adapters: _adapters,
            ProblemKind: kind,
            ProblemAt: kind is null ? null : at);
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
