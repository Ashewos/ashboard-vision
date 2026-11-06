using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft.Tests;

/// <summary>Ce que le client a annoncé dans sa poignée de main.</summary>
internal sealed record Handshake(int Protocol, string Host, int Port, int NextState);

/// <summary>
/// Un faux serveur Minecraft pour les tests : il écoute sur <c>127.0.0.1</c> avec un port que le système choisit, répond comme le test le décide
/// (<see cref="Behavior"/>) et se ferme avec le test. Aucun test ne joint un vrai serveur Minecraft ni Internet. Il se souvient des poignées de main
/// reçues et du jeton du ping.
/// </summary>
internal sealed class FakeMinecraftServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Handshake> _handshakes = [];
    private readonly List<long> _pings = [];
    private readonly Task _accepting;
    private readonly List<Task> _connections = [];

    public FakeMinecraftServer(Func<ServerConnection, CancellationToken, Task> behavior)
    {
        Behavior = behavior;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>Le port que le système a donné au serveur.</summary>
    public int Port { get; }

    /// <summary>Comment le serveur répond à chaque connexion ; remplaçable en cours de test.</summary>
    public Func<ServerConnection, CancellationToken, Task> Behavior { get; set; }

    /// <summary>Les poignées de main reçues, dans l'ordre.</summary>
    public IReadOnlyList<Handshake> Handshakes
    {
        get { lock (_gate) { return [.. _handshakes]; } }
    }

    /// <summary>Les jetons de ping reçus, dans l'ordre.</summary>
    public IReadOnlyList<long> Pings
    {
        get { lock (_gate) { return [.. _pings]; } }
    }

    // ---- Les comportements (chacun existe en serveur prêt à l'emploi et en comportement, pour en changer en cours de test) ----

    /// <summary>Un serveur qui va bien : il répond à la demande d'état par ce JSON, puis renvoie le ping (si <paramref name="answerPing"/>), puis ferme.</summary>
    public static FakeMinecraftServer Answering(string json, bool answerPing = true) => new(Behaviors.Answering(json, answerPing));

    /// <summary>Un serveur qui répond ces octets-là, tels quels, à la demande d'état, puis attend que le client parte.</summary>
    public static FakeMinecraftServer Replying(byte[] bytes) => new(Behaviors.Replying(bytes));

    /// <summary>Un serveur qui envoie ces octets-là à la demande d'état, puis raccroche.</summary>
    public static FakeMinecraftServer SendingThenHangingUp(byte[] bytes) => new(Behaviors.SendingThenHangingUp(bytes));

    /// <summary>Un serveur qui accepte la connexion, lit la demande et ne répond jamais.</summary>
    public static FakeMinecraftServer Silent() => new(Behaviors.Silent);

    /// <summary>Un serveur qui lit la demande et raccroche sans répondre.</summary>
    public static FakeMinecraftServer HangingUp() => new(Behaviors.HangingUp);

    /// <summary>Un serveur qui répond à la demande d'état mais ne renvoie jamais le ping.</summary>
    public static FakeMinecraftServer IgnoringPings(string json) => new(Behaviors.IgnoringPings(json));

    internal void RecordHandshake(Handshake handshake)
    {
        lock (_gate)
        {
            _handshakes.Add(handshake);
        }
    }

    internal void RecordPing(long token)
    {
        lock (_gate)
        {
            _pings.Add(token);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var task = Task.Run(() => ServeAsync(client));
                lock (_gate)
                {
                    _connections.Add(task);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Le serveur se ferme.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await Behavior(new ServerConnection(this, client.GetStream()), _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
                // Le client est parti ou le serveur se ferme : un faux serveur n'a rien à en dire.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections);
        _stop.Dispose();
    }
}

/// <summary>Le côté serveur d'une connexion : il lit les paquets du client et en écrit.</summary>
internal sealed class ServerConnection(FakeMinecraftServer server, Stream stream)
{
    /// <summary>Lit la poignée de main puis la demande d'état, et note la poignée de main.</summary>
    public async Task ReadHandshakeAndStatusRequestAsync(CancellationToken ct)
    {
        var packet = await ReadPacketAsync(ct);
        var offset = 0;
        Assert.Equal(0x00, Packets.ReadVarInt(packet, ref offset));
        var protocol = Packets.ReadVarInt(packet, ref offset);
        var length = Packets.ReadVarInt(packet, ref offset);
        var host = Encoding.UTF8.GetString(packet, offset, length);
        offset += length;
        var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
        offset += 2;
        var next = Packets.ReadVarInt(packet, ref offset);
        server.RecordHandshake(new Handshake(protocol, host, port, next));

        Assert.Equal([0x00], await ReadPacketAsync(ct));
    }

    /// <summary>Lit le ping du client et le lui renvoie tel quel.</summary>
    public async Task AnswerPingAsync(CancellationToken ct)
    {
        var ping = await ReadPacketAsync(ct);
        Assert.Equal(9, ping.Length);
        Assert.Equal(0x01, ping[0]);
        server.RecordPing(BinaryPrimitives.ReadInt64BigEndian(ping.AsSpan(1)));
        await WritePacketAsync(ping, ct);
    }

    public async Task<byte[]> ReadPacketAsync(CancellationToken ct)
    {
        var length = 0;
        for (var index = 0; ; index++)
        {
            var one = new byte[1];
            await stream.ReadExactlyAsync(one, ct);
            length |= (one[0] & 0x7F) << (7 * index);
            if ((one[0] & 0x80) == 0)
            {
                break;
            }
        }

        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, ct);
        return packet;
    }

    /// <summary>Écrit un paquet : sa longueur (un VarInt) puis son contenu.</summary>
    public Task WritePacketAsync(byte[] payload, CancellationToken ct) => WriteRawAsync(Packets.Frame(payload), ct);

    public async Task WriteRawAsync(byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Attend que le client ferme la connexion (ou que le serveur se ferme).</summary>
    public async Task WaitForTheClientAsync(CancellationToken ct)
    {
        var buffer = new byte[64];
        while (await stream.ReadAsync(buffer, ct) > 0)
        {
        }
    }
}

/// <summary>Les paquets du protocole, écrits à la main pour les tests.</summary>
internal static class Packets
{
    public static byte[] VarInt(int value)
    {
        var bytes = new List<byte>();
        var rest = (uint)value;
        do
        {
            var next = (byte)(rest & 0x7F);
            rest >>= 7;
            bytes.Add(rest == 0 ? next : (byte)(next | 0x80));
        }
        while (rest != 0);
        return [.. bytes];
    }

    public static int ReadVarInt(byte[] data, ref int offset)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            var current = data[offset++];
            value |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt trop long.");
    }

    /// <summary>La longueur du paquet (un VarInt) suivie de son contenu.</summary>
    public static byte[] Frame(byte[] payload) => [.. VarInt(payload.Length), .. payload];

    /// <summary>Le contenu d'une réponse d'état : l'identifiant 0x00, la longueur du JSON (un VarInt) et le JSON.</summary>
    public static byte[] StatusResponse(string json)
    {
        var text = Encoding.UTF8.GetBytes(json);
        return [0x00, .. VarInt(text.Length), .. text];
    }
}

/// <summary>Les façons dont un faux serveur répond à une connexion.</summary>
internal static class Behaviors
{
    public static Func<ServerConnection, CancellationToken, Task> Answering(string json, bool answerPing = true) => async (connection, ct) =>
    {
        await connection.ReadHandshakeAndStatusRequestAsync(ct);
        await connection.WritePacketAsync(Packets.StatusResponse(json), ct);
        if (answerPing)
        {
            await connection.AnswerPingAsync(ct);
        }
    };

    public static Func<ServerConnection, CancellationToken, Task> Replying(byte[] bytes) => async (connection, ct) =>
    {
        await connection.ReadHandshakeAndStatusRequestAsync(ct);
        await connection.WriteRawAsync(bytes, ct);
        await connection.WaitForTheClientAsync(ct);
    };

    public static Func<ServerConnection, CancellationToken, Task> SendingThenHangingUp(byte[] bytes) => async (connection, ct) =>
    {
        await connection.ReadHandshakeAndStatusRequestAsync(ct);
        await connection.WriteRawAsync(bytes, ct);
    };

    public static async Task Silent(ServerConnection connection, CancellationToken ct)
    {
        await connection.ReadHandshakeAndStatusRequestAsync(ct);
        await connection.WaitForTheClientAsync(ct);
    }

    public static Task HangingUp(ServerConnection connection, CancellationToken ct) => connection.ReadHandshakeAndStatusRequestAsync(ct);

    public static Func<ServerConnection, CancellationToken, Task> IgnoringPings(string json) => async (connection, ct) =>
    {
        await connection.ReadHandshakeAndStatusRequestAsync(ct);
        await connection.WritePacketAsync(Packets.StatusResponse(json), ct);
        await connection.WaitForTheClientAsync(ct);
    };
}
