using System.IO.Pipes;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>
/// Ce que l'hôte fait pour une instance de module : les signaux de la classe de base (redessiner, changer d'état) et les
/// demandes au service (capteurs, musique, relais vers ce PC). Les permissions sont déjà vérifiées par <see cref="ModuleServices"/>.
/// </summary>
internal sealed class HostBackend : IModuleBackend
{
    private static readonly TimeSpan DataPatience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(30);

    private readonly HostLink _link;
    private readonly Guid _instanceId;
    private readonly Action _invalidate;
    private readonly Action<ModuleHealth, string?> _setStatus;

    public HostBackend(HostLink link, Guid instanceId, Action invalidate, Action<ModuleHealth, string?> setStatus)
    {
        _link = link;
        _instanceId = instanceId;
        _invalidate = invalidate;
        _setStatus = setStatus;
    }

    /// <summary>Un fond sans signaux, pour les instances à part des recherches et des actions des réglages.</summary>
    public static HostBackend Detached(HostLink link, Guid instanceId) => new(link, instanceId, () => { }, (_, _) => { });

    public void Invalidate() => _invalidate();

    public void SetStatus(ModuleHealth health, string? message) => _setStatus(health, message);

    public async Task<DataResult<SensorReading>> GetSensorsAsync(CancellationToken ct)
    {
        var value = await AskAsync(HostPayloads.SensorsKind, ct).ConfigureAwait(false);
        if (value.Status != DataStatus.Ok)
        {
            return new DataResult<SensorReading>(value.Status, null);
        }

        try
        {
            return HostPayloads.ReadSensors(value.Json) is { } reading
                ? new DataResult<SensorReading>(DataStatus.Ok, reading)
                : new DataResult<SensorReading>(DataStatus.Unavailable, null);
        }
        catch (JsonException)
        {
            return new DataResult<SensorReading>(DataStatus.Unavailable, null);
        }
    }

    public async Task<DataResult<NowPlayingInfo>> GetNowPlayingAsync(CancellationToken ct)
    {
        var value = await AskAsync(HostPayloads.NowPlayingKind, ct).ConfigureAwait(false);
        if (value.Status != DataStatus.Ok)
        {
            return new DataResult<NowPlayingInfo>(value.Status, null);
        }

        try
        {
            return HostPayloads.ReadNowPlaying(value.Json) is { } info
                ? new DataResult<NowPlayingInfo>(DataStatus.Ok, info)
                : new DataResult<NowPlayingInfo>(DataStatus.Unavailable, null);
        }
        catch (JsonException)
        {
            return new DataResult<NowPlayingInfo>(DataStatus.Unavailable, null);
        }
    }

    public async Task<DataResult<Stream>> ConnectLocalAsync(int port, CancellationToken ct)
    {
        HostMessage reply;
        try
        {
            reply = await _link.RequestAsync(id => new LocalConnect(id, _instanceId, port), ConnectPatience, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new DataResult<Stream>(DataStatus.Unavailable, null);
        }

        switch (reply)
        {
            case LocalConnected connected:
                return OpenRelay(connected);
            case LocalRefused:
                return new DataResult<Stream>(DataStatus.Denied, null);
            default:
                return new DataResult<Stream>(DataStatus.Unavailable, null);
        }
    }

    /// <summary>Ferme des poignées que le service a dupliquées pour une connexion que plus personne n'attend.</summary>
    public static void CloseRelay(LocalConnected connected)
    {
        CloseHandle(connected.ReadHandle);
        CloseHandle(connected.WriteHandle);
    }

    private static DataResult<Stream> OpenRelay(LocalConnected connected)
    {
        var readHandle = new SafePipeHandle(new IntPtr(connected.ReadHandle), ownsHandle: true);
        var writeHandle = new SafePipeHandle(new IntPtr(connected.WriteHandle), ownsHandle: true);
        try
        {
            var reader = new AnonymousPipeClientStream(PipeDirection.In, readHandle);
            var writer = new AnonymousPipeClientStream(PipeDirection.Out, writeHandle);
            return new DataResult<Stream>(DataStatus.Ok, new DuplexPipeStream(reader, writer));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Une poignée qui n'est pas un tube : rien à relayer. Les deux sont fermées (fermer deux fois est sans danger).
            readHandle.Dispose();
            writeHandle.Dispose();
            return new DataResult<Stream>(DataStatus.Unavailable, null);
        }
    }

    private static void CloseHandle(long handle)
    {
        if (handle > 0)
        {
            new SafeFileHandle(new IntPtr(handle), ownsHandle: true).Dispose();
        }
    }

    private async Task<(DataStatus Status, JsonElement Json)> AskAsync(string kind, CancellationToken ct)
    {
        HostMessage reply;
        try
        {
            reply = await _link.RequestAsync(id => new DataGet(id, kind), DataPatience, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return (DataStatus.Unavailable, default);
        }

        if (reply is not DataValue value)
        {
            return (DataStatus.Unavailable, default);
        }

        var status = value.Status switch
        {
            "ok" => DataStatus.Ok,
            "denied" => DataStatus.Denied,
            _ => DataStatus.Unavailable,
        };
        return status == DataStatus.Ok && value.Value is { } json ? (status, json) : (status == DataStatus.Ok ? DataStatus.Unavailable : status, default);
    }
}
