using System.Diagnostics;
using AshboardVision.Sdk;
using Trofeo.Extensions.Service;
using Trofeo.Modules.NowPlaying;
using Trofeo.Sensors;

namespace Trofeo.Display;

/// <summary>
/// Ce que le service lit pour les modules de la communauté qui ont la permission : les capteurs (le <see cref="ISensorFeed"/> partagé
/// des modules d'origine) et la musique en cours (une <see cref="IMediaSource"/> créée à la première demande). Rien n'est lu, créé ni
/// minuté tant qu'aucun module n'en demande ; après <see cref="IdleTimeout"/> sans demande, le ticket sur les capteurs est rendu et la
/// source de musique libérée. Les méthodes sont appelées depuis un fil du pool, jamais depuis la boucle d'affichage, et peuvent attendre
/// quelques secondes la première lecture.
/// </summary>
public sealed class ServiceDataBroker : IServiceDataBroker, IDisposable
{
    /// <summary>Le temps sans demande après lequel les sources sont libérées.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FirstReadingWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MediaStartWait = TimeSpan.FromSeconds(3);

    private readonly ISensorFeed _sensors;
    private readonly Func<IMediaSource> _createMedia;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly object _mediaGate = new(); // une seule création de la source de musique à la fois
    private IDisposable? _sensorLease;
    private IMediaSource? _media;
    private ITimer? _idleTimer;
    private DateTimeOffset _lastUse;
    private DateTimeOffset _mediaRetryAt;
    private bool _disposed;

    /// <param name="sensors">Le relevé partagé des capteurs.</param>
    /// <param name="createMedia">Crée la source de musique (les contrôles média de Windows en production).</param>
    /// <param name="time">La source de temps (position de lecture, délai d'inactivité).</param>
    public ServiceDataBroker(ISensorFeed sensors, Func<IMediaSource> createMedia, TimeProvider time)
    {
        _sensors = sensors ?? throw new ArgumentNullException(nameof(sensors));
        _createMedia = createMedia ?? throw new ArgumentNullException(nameof(createMedia));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public DataResult<SensorReading> Sensors()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new DataResult<SensorReading>(DataStatus.Unavailable, null);
            }

            _sensorLease ??= _sensors.Acquire();
            TouchLocked();
        }

        var snapshot = _sensors.Latest ?? WaitForFirstReading();
        return snapshot is null
            ? new DataResult<SensorReading>(DataStatus.Unavailable, null)
            : new DataResult<SensorReading>(DataStatus.Ok, new SensorReading(snapshot.CpuName, snapshot.GpuName, new Dictionary<string, double>(snapshot.Values)));
    }

    /// <inheritdoc />
    public DataResult<NowPlayingInfo> NowPlaying()
    {
        var source = MediaSource();
        if (source?.Current is not { } current)
        {
            return new DataResult<NowPlayingInfo>(DataStatus.Unavailable, null);
        }

        var position = current.Position;
        if (current.IsPlaying)
        {
            position += _time.GetUtcNow() - current.PositionAt;
        }

        if (current.Duration > TimeSpan.Zero && position > current.Duration)
        {
            position = current.Duration;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        return new DataResult<NowPlayingInfo>(DataStatus.Ok, new NowPlayingInfo(
            current.Title, current.Artist, current.AppName, position, current.Duration, current.IsPlaying, current.Artwork));
    }

    /// <summary>Libère les sources et arrête la minuterie d'inactivité.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseLocked();
        }
    }

    /// <summary>Attend (au plus quelques secondes) le premier relevé d'un hub qui vient d'être démarré par le premier ticket.</summary>
    private SensorSnapshot? WaitForFirstReading()
    {
        using var arrived = new ManualResetEventSlim();
        void OnUpdated(object? sender, EventArgs e) => arrived.Set();
        _sensors.Updated += OnUpdated;
        try
        {
            return _sensors.Latest ?? (arrived.Wait(FirstReadingWait) ? _sensors.Latest : null);
        }
        finally
        {
            _sensors.Updated -= OnUpdated;
        }
    }

    /// <summary>
    /// La source de musique, créée et démarrée à la première demande (une seule à la fois : les autres demandes attendent la même).
    /// <c>null</c> si elle ne démarre pas ; on ne réessaie alors qu'après <see cref="IdleTimeout"/>.
    /// </summary>
    private IMediaSource? MediaSource()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            TouchLocked();
            if (_media is not null || _time.GetUtcNow() < _mediaRetryAt)
            {
                return _media;
            }
        }

        lock (_mediaGate)
        {
            lock (_gate)
            {
                if (_disposed || _media is not null)
                {
                    return _media;
                }
            }

            var started = StartMedia(); // hors de _gate : les capteurs ne l'attendent pas
            lock (_gate)
            {
                if (started is null)
                {
                    _mediaRetryAt = _time.GetUtcNow() + IdleTimeout;
                    return null;
                }

                if (_disposed)
                {
                    started.Dispose();
                    return null;
                }

                _media = started;
                return started;
            }
        }
    }

    private IMediaSource? StartMedia()
    {
        IMediaSource? source = null;
        try
        {
            source = _createMedia();
            source.StartAsync(CancellationToken.None).WaitAsync(MediaStartWait).GetAwaiter().GetResult();
            return source;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"La musique en cours n'est pas lisible pour les modules de la communauté : {ex.Message}");
            source?.Dispose();
            return null;
        }
    }

    private void TouchLocked()
    {
        _lastUse = _time.GetUtcNow();
        _idleTimer ??= _time.CreateTimer(_ => OnIdleCheck(), null, IdleTimeout, IdleTimeout);
    }

    private void OnIdleCheck()
    {
        lock (_gate)
        {
            if (!_disposed && _time.GetUtcNow() - _lastUse >= IdleTimeout)
            {
                ReleaseLocked();
            }
        }
    }

    private void ReleaseLocked()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _sensorLease?.Dispose();
        _sensorLease = null;
        _media?.Dispose();
        _media = null;
    }
}
