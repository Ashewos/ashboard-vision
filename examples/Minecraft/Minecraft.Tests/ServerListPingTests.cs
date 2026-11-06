using System.Net;
using System.Net.Sockets;
using System.Text;
using SkiaSharp;

namespace Minecraft.Tests;

/// <summary>
/// Les tests du protocole « Server List Ping ». Ils parlent à un faux serveur TCP (<see cref="FakeMinecraftServer"/>) qui écoute sur <c>127.0.0.1</c> et se
/// ferme avec le test : aucun vrai serveur Minecraft, aucun accès à Internet. Une bonne moitié vérifie qu'un serveur cassé ou malveillant donne une
/// erreur claire, jamais une exception qui échapperait au module ni une réservation de mémoire géante.
/// </summary>
public sealed class ServerListPingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    /// <summary>Un PNG de 64 × 64 d'une seule couleur, comme l'icône d'un serveur.</summary>
    internal static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static string Favicon(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static Task<ServerStatus> QueryAsync(FakeMinecraftServer server, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ServerListPing.QueryAsync("127.0.0.1", server.Port, timeout ?? Timeout, ct);

    private static async Task<ServerStatus> QueryJsonAsync(string json)
    {
        await using var server = FakeMinecraftServer.Answering(json);
        return await QueryAsync(server);
    }

    private static string WithDescription(string description) => $$"""{ "description": {{description}} }""";

    [Fact]
    public async Task Query_ParsesStatusFromFakeServer()
    {
        var icon = Png(SKColors.Magenta);
        await using var server = FakeMinecraftServer.Answering($$"""
            {
              "version": { "name": "Paper 1.21.4", "protocol": 769 },
              "players": { "max": 20, "online": 3, "sample": [ { "name": "Alex", "id": "a" }, { "name": "Steve", "id": "b" }, { "name": "Notch", "id": "c" } ] },
              "description": "Bienvenue sur Ashboard",
              "favicon": "{{Favicon(icon)}}"
            }
            """);

        var status = await QueryAsync(server);

        Assert.Equal("Bienvenue sur Ashboard", status.Motd);
        Assert.Equal("Paper 1.21.4", status.Version);
        Assert.Equal(3, status.Online);
        Assert.Equal(20, status.Max);
        Assert.Equal(["Alex", "Steve", "Notch"], status.SamplePlayers);
        Assert.Equal(icon, status.FaviconPng);
        Assert.True(status.Latency >= TimeSpan.Zero && status.Latency < Timeout);
    }

    [Fact]
    public async Task Query_SendsHandshakeStatusRequestAndPing()
    {
        // Le protocole : poignée de main (version -1, adresse, port, état suivant 1), demande d'état, puis ping avec un jeton que le serveur renvoie.
        await using var server = FakeMinecraftServer.Answering("""{ "description": "x" }""");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        await ServerListPing.QueryAsync(client.GetStream(), "mc.example.org", 25570, Timeout, CancellationToken.None);

        // Le faux serveur a lu la poignée de main avant de répondre ; elle porte l'adresse et le port qu'on lui a donnés, pas ceux de la connexion.
        Assert.Equal([new Handshake(-1, "mc.example.org", 25570, 1)], server.Handshakes);
        Assert.Single(server.Pings);
    }

    [Fact]
    public async Task Query_DescriptionAsChatObject_ReturnsText()
    {
        var status = await QueryJsonAsync(WithDescription("""{ "text": "Hello ", "color": "gold", "extra": [ { "text": "Wor", "bold": true }, { "text": "ld", "extra": [ { "text": "!" } ] } ] }"""));

        Assert.Equal("Hello World!", status.Motd);
    }

    [Theory]
    [InlineData("\"Texte simple\"", "Texte simple")]
    [InlineData("""[ "Un ", { "text": "tableau" } ]""", "Un tableau")]
    [InlineData("""{ "text": "Deux\nlignes   et   espaces" }""", "Deux lignes et espaces")]
    [InlineData("42", "")] // pas du texte : le message du jour est vide, l'état reste lisible
    [InlineData("""{ "translate": "motd.ailleurs" }""", "")]
    public async Task Query_DescriptionInOtherShapes_ReturnsWhatIsText(string description, string expected)
    {
        var status = await QueryJsonAsync(WithDescription(description));

        Assert.Equal(expected, status.Motd);
    }

    [Fact]
    public async Task Query_DescriptionNestedWithoutEnd_StopsAtALimit()
    {
        // Un objet de chat qui s'imbrique sur 12 niveaux (24 profondeurs de JSON) : le texte au-delà de 8 niveaux est ignoré, sans erreur.
        var nested = new StringBuilder();
        for (var depth = 0; depth < 12; depth++)
        {
            nested.Append($$"""{ "text": "{{depth % 10}}", "extra": [""");
        }

        nested.Append("""{ "text": "fin" }""").Append(string.Concat(Enumerable.Repeat("]}", 12)));

        var status = await QueryJsonAsync(WithDescription(nested.ToString()));

        Assert.StartsWith("0123", status.Motd, StringComparison.Ordinal);
        Assert.DoesNotContain("fin", status.Motd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_StripsColorCodes()
    {
        // §a (vert), §l (gras), §r (retour), et la couleur hexadécimale de Minecraft 1.16 : « §x » puis six « §chiffre ».
        var status = await QueryJsonAsync(WithDescription(JsonString("§aVert §l§nGras§r §x§1§2§3§4§5§6Hexa §")));

        Assert.Equal("Vert Gras Hexa", status.Motd);
    }

    [Fact]
    public async Task Query_MissingFields_GivesEmptyValues()
    {
        var status = await QueryJsonAsync("{}");

        Assert.Equal("", status.Motd);
        Assert.Equal("", status.Version);
        Assert.Equal(0, status.Online);
        Assert.Equal(0, status.Max);
        Assert.Empty(status.SamplePlayers);
        Assert.Null(status.FaviconPng);
    }

    [Fact]
    public async Task Query_PlayerSample_IsCappedAndCleaned()
    {
        var names = string.Join(",", Enumerable.Range(0, 40).Select(i => $$"""{ "name": "§cJoueur{{i}}", "id": "{{i}}" }"""));
        var status = await QueryJsonAsync($$"""{ "players": { "online": 40, "max": 50, "sample": [ {{names}}, { "name": "" }, 7 ] } }""");

        Assert.Equal(12, status.SamplePlayers.Count);
        Assert.Equal("Joueur0", status.SamplePlayers[0]);
        Assert.Equal(40, status.Online);
    }

    [Fact]
    public async Task Query_PlayerCountsOutOfRange_AreZero()
    {
        var status = await QueryJsonAsync("""{ "players": { "online": -5, "max": 99999999999 } }""");

        Assert.Equal(0, status.Online);
        Assert.Equal(0, status.Max);
    }

    [Fact]
    public async Task Query_Timeout_Throws()
    {
        // Le serveur accepte la connexion, lit la demande et ne répond jamais.
        await using var server = FakeMinecraftServer.Silent();

        await Assert.ThrowsAsync<TimeoutException>(() => QueryAsync(server, Short));
    }

    [Fact]
    public async Task Query_Cancelled_ThrowsOperationCanceled()
    {
        // Une annulation de l'appelant (la case se ferme) n'est pas un délai : elle reste une annulation.
        await using var server = FakeMinecraftServer.Silent();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QueryAsync(server, Timeout, cancel.Token));
    }

    [Fact]
    public async Task Query_PingNeverAnswered_KeepsTheStatus()
    {
        // Un serveur qui ne renvoie pas le ping ne fait pas perdre l'état déjà reçu : la latence est celle de la demande d'état.
        await using var server = FakeMinecraftServer.IgnoringPings("""{ "players": { "online": 1, "max": 2 } }""");

        var status = await QueryAsync(server, Short);

        Assert.Equal(1, status.Online);
        Assert.True(status.Latency >= TimeSpan.Zero && status.Latency < Short);
    }

    [Fact]
    public async Task Query_ServerHangsUpAfterTheStatus_KeepsTheStatus()
    {
        await using var server = FakeMinecraftServer.Answering("""{ "players": { "online": 2, "max": 4 } }""", answerPing: false);

        var status = await QueryAsync(server);

        Assert.Equal(2, status.Online);
    }

    [Fact]
    public async Task Query_PingTokenEchoed_MeasuresThePing()
    {
        // Le serveur met 300 ms à renvoyer le ping : la latence est celle du ping, pas celle de la demande d'état (quasi immédiate).
        await using var server = FakeMinecraftServer.AnsweringPing("""{ "description": "x" }""", TimeSpan.FromMilliseconds(300), echoToken: true);

        var status = await QueryAsync(server);

        Assert.True(status.Latency >= TimeSpan.FromMilliseconds(250), $"Latence {status.Latency.TotalMilliseconds} ms : le ping n'a pas été mesuré.");
    }

    [Fact]
    public async Task Query_PingTokenNotEchoed_FallsBackOnTheStatusRoundTrip()
    {
        // Le serveur renvoie un autre jeton (donc pas la réponse à notre ping) : sa latence ne veut rien dire, on garde celle de la demande d'état.
        await using var server = FakeMinecraftServer.AnsweringPing("""{ "description": "x" }""", TimeSpan.FromMilliseconds(300), echoToken: false);

        var status = await QueryAsync(server);

        Assert.Equal("x", status.Motd);
        Assert.True(status.Latency < TimeSpan.FromMilliseconds(250), $"Latence {status.Latency.TotalMilliseconds} ms : le jeton qui ne correspond pas a été pris pour le ping.");
    }

    // ---- Un serveur cassé ou malveillant ----

    [Theory]
    [InlineData(int.MaxValue)] // 2 Gio annoncés
    [InlineData(ServerListPing.MaxPacketBytes + 1)] // juste au-dessus de la limite
    [InlineData(0)] // un paquet vide n'existe pas
    [InlineData(-1)] // négatif : un VarInt de 5 octets
    public async Task Query_AbsurdPacketLength_Throws(int announced)
    {
        await using var server = FakeMinecraftServer.Replying(Packets.VarInt(announced));
        var before = GC.GetTotalAllocatedBytes(precise: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));

        // Le client a refusé la longueur avant de réserver quoi que ce soit : bien loin des 2 Gio annoncés.
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - before < 64L * 1024 * 1024, "Le client a réservé une mémoire démesurée.");
    }

    [Fact]
    public async Task Query_VarIntOfSixBytes_Throws()
    {
        await using var server = FakeMinecraftServer.Replying([0x80, 0x80, 0x80, 0x80, 0x80, 0x01]);

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_JsonLengthLargerThanItsPacket_Throws()
    {
        // Le paquet fait 3 octets (l'identifiant et un VarInt de 2 octets) mais annonce un JSON de 300 000 octets.
        byte[] payload = [0x00, .. Packets.VarInt(300_000)];
        await using var server = FakeMinecraftServer.Replying(Packets.Frame(payload));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_NegativeJsonLength_Throws()
    {
        byte[] payload = [0x00, .. Packets.VarInt(-1), 0x7B, 0x7D];
        await using var server = FakeMinecraftServer.Replying(Packets.Frame(payload));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_WrongPacketId_Throws()
    {
        await using var server = FakeMinecraftServer.Replying(Packets.Frame([0x02, 0x00]));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Theory]
    [InlineData("pas du JSON")]
    [InlineData("[1, 2]")]
    [InlineData("\"texte\"")]
    [InlineData("{ \"players\": ")]
    public async Task Query_JsonThatIsNotAStatus_Throws(string json)
    {
        await using var server = FakeMinecraftServer.Replying(Packets.Frame(Packets.StatusResponse(json)));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_JsonNestedTooDeep_Throws()
    {
        var json = string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));
        await using var server = FakeMinecraftServer.Replying(Packets.Frame(Packets.StatusResponse(json)));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_ServerHangsUpWithoutAnswering_ThrowsIOException()
    {
        await using var server = FakeMinecraftServer.HangingUp();

        await Assert.ThrowsAnyAsync<IOException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_PacketCutShort_ThrowsIOException()
    {
        // Il annonce 100 octets, en envoie 10, puis ferme.
        await using var server = FakeMinecraftServer.SendingThenHangingUp([.. Packets.VarInt(100), .. new byte[10]]);

        await Assert.ThrowsAnyAsync<IOException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_ServerDrippingOneByteAtATime_StopsAtTheDeadline()
    {
        // Un octet toutes les 200 ms : la réponse mettrait plusieurs secondes. Le délai est global : l'interrogation s'arrête à l'échéance.
        await using var server = FakeMinecraftServer.Dripping("""{ "description": "lent" }""", TimeSpan.FromMilliseconds(200));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => QueryAsync(server, TimeSpan.FromMilliseconds(600)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"L'interrogation a duré {clock.Elapsed.TotalSeconds:0.0} s pour un délai de 0,6 s.");
    }

    [Fact]
    public async Task Query_InvalidUtf8InAString_Throws()
    {
        // Un octet 0xFF n'existe pas en UTF-8 : le JSON est bien formé pour l'analyseur, mais la chaîne ne se lit pas.
        byte[] json = [.. "{\"description\":\""u8, 0xFF, .. "\"}"u8];
        await using var server = FakeMinecraftServer.Replying(Packets.Frame([0x00, .. Packets.VarInt(json.Length), .. json]));

        await Assert.ThrowsAsync<InvalidDataException>(() => QueryAsync(server));
    }

    [Fact]
    public async Task Query_LoneSurrogateEscape_Throws()
    {
        // Un \ud800 seul (une moitié de paire) ne se lit pas comme du texte : une erreur claire, pas une exception qui s'échappe.
        await Assert.ThrowsAsync<InvalidDataException>(() => QueryJsonAsync(WithDescription(@"""Bonjour \ud800 !""")));
    }

    [Fact]
    public async Task Query_BidiAndControlCharacters_AreRemoved()
    {
        // U+202E (inverse le sens de lecture), U+2066 et U+2069 (isolats) et un caractère de contrôle (U+0001) : retirés du message et des pseudos.
        // Le JSON les écrit par leurs échappements, comme un serveur les envoie.
        var status = await QueryJsonAsync("{ \"description\": \"A" + @"\u202Eb\u0001c\u2066d\u2069e" + "\", \"players\": { \"online\": 1, \"max\": 2, \"sample\": [ { \"name\": \"Al" + @"\u202Eex" + "\" } ] } }");

        Assert.Equal("Abcde", status.Motd);
        Assert.Equal(["Alex"], status.SamplePlayers);
    }

    // ---- L'icône ----

    [Fact]
    public async Task Query_FaviconNotAPng_IsIgnoredButTheStatusIsKept()
    {
        var notPng = Convert.ToBase64String(Encoding.ASCII.GetBytes("ceci n'est pas un PNG, même s'il y est écrit png"));
        var status = await QueryJsonAsync($$"""{ "players": { "online": 4, "max": 8 }, "favicon": "data:image/png;base64,{{notPng}}" }""");

        Assert.Null(status.FaviconPng);
        Assert.Equal(4, status.Online);
    }

    [Theory]
    [InlineData("data:image/jpeg;base64,/9j/4AAQ")]
    [InlineData("data:image/png;base64,@@@pas du base64@@@")]
    [InlineData("pas une adresse de données")]
    [InlineData("")]
    public async Task Query_FaviconUnusable_IsIgnoredButTheStatusIsKept(string favicon)
    {
        var status = await QueryJsonAsync($$"""{ "players": { "online": 4, "max": 8 }, "favicon": {{JsonString(favicon)}} }""");

        Assert.Null(status.FaviconPng);
        Assert.Equal(4, status.Online);
    }

    [Fact]
    public async Task Query_FaviconLargerThanTheLimit_IsIgnoredButTheStatusIsKept()
    {
        // Un PNG valide au début, suivi de 300 Kio : plus que les 128 Kio que le module accepte pour une icône (la réponse reste sous la limite d'un paquet).
        byte[] big = [.. Png(SKColors.Red), .. new byte[300_000]];
        var status = await QueryJsonAsync($$"""{ "players": { "online": 4, "max": 8 }, "favicon": "{{Favicon(big)}}" }""");

        Assert.Null(status.FaviconPng);
        Assert.Equal(4, status.Online);
    }

    // ---- Les arguments et la connexion ----

    [Fact]
    public async Task Query_HostTooLong_Throws()
    {
        await using var server = FakeMinecraftServer.Answering("{}");

        await Assert.ThrowsAsync<ArgumentException>(() => ServerListPing.QueryAsync(new string('a', 256), server.Port, Timeout, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task Query_PortOutOfRange_Throws(int port)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ServerListPing.QueryAsync("127.0.0.1", port, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task Query_NothingListening_ThrowsSocketException()
    {
        // Un port libre qu'on vient de relâcher : rien n'y écoute.
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await Assert.ThrowsAsync<SocketException>(() => ServerListPing.QueryAsync("127.0.0.1", closed, Timeout, CancellationToken.None));
    }

    private static string JsonString(string text) => System.Text.Json.JsonSerializer.Serialize(text);
}
