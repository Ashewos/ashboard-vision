using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Trofeo.ModuleKit;
using Trofeo.Shared;

namespace AshboardVision.Sdk.Testing;

/// <summary>
/// Le faux « service » d'une instance en test : garde l'état que le module annonce, rend les capteurs et la musique que le test a posés, et
/// ouvre une vraie connexion vers <c>127.0.0.1</c> comme le relais du logiciel, avec la même règle : seul un port saisi dans un champ
/// <c>port</c> des réglages passe, sinon la réponse est <see cref="DataStatus.Denied"/> (la permission « Cet ordinateur » est déjà vérifiée
/// par <see cref="ModuleServices"/>).
/// </summary>
internal sealed class TestBackend(IReadOnlyList<PortField> portFields, JsonElement settings) : IModuleBackend
{
    /// <summary>Le délai de connexion du relais du logiciel.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private ModuleHealth _health = ModuleHealth.Online;
    private string? _message;
    private SensorReading? _sensors;
    private NowPlayingInfo? _nowPlaying;

    public ModuleHealth Health
    {
        get { lock (_gate) { return _health; } }
    }

    public string? StatusMessage
    {
        get { lock (_gate) { return _message; } }
    }

    public void SetSensors(SensorReading? reading)
    {
        lock (_gate)
        {
            _sensors = reading;
        }
    }

    public void SetNowPlaying(NowPlayingInfo? info)
    {
        lock (_gate)
        {
            _nowPlaying = info;
        }
    }

    public void Invalidate()
    {
        // Les tests dessinent à la demande (Render) : une demande de redessin n'a rien à déclencher.
    }

    public void SetStatus(ModuleHealth health, string? message)
    {
        lock (_gate)
        {
            _health = health;
            _message = message;
        }
    }

    public Task<DataResult<SensorReading>> GetSensorsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_sensors is null
                ? new DataResult<SensorReading>(DataStatus.Unavailable, null)
                : new DataResult<SensorReading>(DataStatus.Ok, _sensors));
        }
    }

    public Task<DataResult<NowPlayingInfo>> GetNowPlayingAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_nowPlaying is null
                ? new DataResult<NowPlayingInfo>(DataStatus.Unavailable, null)
                : new DataResult<NowPlayingInfo>(DataStatus.Ok, _nowPlaying));
        }
    }

    public async Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct)
    {
        // Comme le service : un port qui n'est pas celui d'un champ « port » des réglages est refusé, et un port où rien n'écoute aussi
        // (le relais répond « refusé », l'hôte en fait Denied).
        if (!PortRule.IsAllowed(portFields, settings, port))
        {
            return new DataResult<Stream>(DataStatus.Denied, null);
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(false);
            return new DataResult<Stream>(DataStatus.Ok, new NetworkStream(socket, ownsSocket: true));
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            socket.Dispose();
            return new DataResult<Stream>(DataStatus.Denied, null);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
