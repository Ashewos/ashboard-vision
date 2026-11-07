using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// L'état d'un serveur Minecraft Java, tel que le serveur le décrit.
/// </summary>
/// <param name="Motd">Le message du jour (le « nom » du serveur), sans codes de couleur, sur une ligne.</param>
/// <param name="Version">Le nom de la version du serveur (« 1.21.4 », « Paper 1.21.4 »…).</param>
/// <param name="Online">Le nombre de joueurs connectés.</param>
/// <param name="Max">Le nombre de joueurs que le serveur accepte.</param>
/// <param name="SamplePlayers">Quelques pseudos de joueurs connectés (le serveur choisit combien, souvent aucun).</param>
/// <param name="Latency">Le temps d'un aller-retour avec le serveur.</param>
/// <param name="FaviconPng">L'icône du serveur (un PNG de 64 × 64 en général), ou <c>null</c> s'il n'en a pas ou si elle est inutilisable.</param>
public sealed record ServerStatus(
    string Motd, string Version, int Online, int Max, IReadOnlyList<string> SamplePlayers, TimeSpan Latency, byte[]? FaviconPng);

/// <summary>
/// Le protocole « Server List Ping » de Minecraft Java : celui que le jeu utilise pour remplir sa liste de serveurs. Une poignée de main, une
/// demande d'état (le serveur répond par un JSON), puis un ping pour mesurer la latence. Tous les entiers de longueur sont des « VarInt » (1 à 5
/// octets, 7 bits utiles par octet).
/// <para>
/// <b>Le serveur est un inconnu.</b> Ce qu'il envoie n'est jamais cru sur parole : un VarInt fait 5 octets au plus, un paquet ou un JSON ne dépasse pas
/// <see cref="MaxPacketBytes"/>, et la mémoire n'est réservée qu'après avoir vérifié la longueur annoncée. Un serveur cassé ou malveillant donne une
/// <see cref="InvalidDataException"/>, une <see cref="IOException"/> (connexion fermée) ou une <see cref="TimeoutException"/> : jamais une réservation géante.
/// </para>
/// </summary>
public static class ServerListPing
{
    /// <summary>La taille maximale d'un paquet reçu : une icône fait 64 Kio au plus (en base64, un tiers de plus), le reste est bien plus petit.</summary>
    public const int MaxPacketBytes = 512 * 1024;

    /// <summary>La taille maximale d'une icône décodée ; une plus grosse est ignorée (le reste de l'état est gardé).</summary>
    private const int MaxFaviconBytes = 128 * 1024;

    /// <summary>Le nombre maximal de pseudos gardés : la case en montre trois, le reste serait de la place perdue.</summary>
    private const int MaxSamplePlayers = 12;

    private const int MaxTextChars = 200;
    private const int MaxChatDepth = 8;
    private const string FaviconPrefix = "data:image/png;base64,";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Un code de mise en forme de Minecraft : « § » et le caractère qui suit (« §a » vert, « §l » gras, « §x§1§2§3§4§5§6 » couleur hexadécimale).
    private static readonly Regex FormattingCode = new("§.?", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Ouvre une connexion TCP vers le serveur, puis l'interroge (voir l'autre surcharge). Pour un serveur de ce PC, un module enfermé n'a pas le
    /// droit de se connecter lui-même : il ouvre la connexion avec <c>Services.ConnectLocalAsync</c> et appelle l'autre surcharge.
    /// </summary>
    /// <param name="timeout">Le délai de la connexion, puis celui de l'interrogation.</param>
    /// <exception cref="TimeoutException">Le serveur n'a pas répondu à temps.</exception>
    /// <exception cref="SocketException">Le serveur est introuvable ou refuse la connexion.</exception>
    public static async Task<ServerStatus> QueryAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        await using var connection = await ConnectAsync(host, port, timeout, ct).ConfigureAwait(false);
        return await QueryAsync(connection, host, port, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ouvre une connexion TCP vers <paramref name="host"/>, en une durée de <paramref name="timeout"/> au plus. À fermer par l'appelant.
    /// </summary>
    /// <exception cref="TimeoutException">Le serveur n'a pas répondu à temps.</exception>
    /// <exception cref="SocketException">Le serveur est introuvable ou refuse la connexion.</exception>
    public static async Task<Stream> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        CheckArguments(host, port, timeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await socket.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Le serveur {host}:{port} n'a pas répondu à la connexion en {timeout.TotalSeconds:0.#} s.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Interroge le serveur au bout de <paramref name="connection"/> (qui reste ouverte : c'est l'appelant qui la ferme). <paramref name="host"/> et
    /// <paramref name="port"/> sont ceux que la poignée de main annonce au serveur ; certains serveurs s'en servent pour choisir quoi répondre.
    /// </summary>
    /// <param name="timeout">Le délai de toute l'interrogation (poignée de main, état et ping).</param>
    /// <exception cref="TimeoutException">Le serveur n'a pas répondu à temps.</exception>
    /// <exception cref="InvalidDataException">Le serveur a répondu autrement que le protocole le veut (ou trop gros).</exception>
    /// <exception cref="IOException">Le serveur a fermé la connexion.</exception>
    public static async Task<ServerStatus> QueryAsync(Stream connection, string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        CheckArguments(host, port, timeout);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await ExchangeAsync(connection, host, port, limit.Token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && limit.IsCancellationRequested
                                   && ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Le délai s'est écoulé : selon le flux, l'annulation sort en OperationCanceledException ou en IOException.
            throw new TimeoutException($"Le serveur {host}:{port} n'a pas répondu en {timeout.TotalSeconds:0.#} s.");
        }
    }

    private static void CheckArguments(string host, int port, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Length > 255)
        {
            throw new ArgumentException("Le protocole limite l'adresse à 255 caractères.", nameof(host));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
    }

    /// <param name="ct">Annulé par l'appelant ou par le délai ; <paramref name="caller"/> est annulé par l'appelant seulement.</param>
    private static async Task<ServerStatus> ExchangeAsync(Stream connection, string host, int port, CancellationToken ct, CancellationToken caller)
    {
        // La poignée de main (identifiant 0x00) : version du protocole (-1 : « je demande seulement l'état »), adresse, port, état suivant (1 : l'état).
        using var handshake = new MemoryStream();
        handshake.WriteByte(0x00);
        WriteVarInt(handshake, -1);
        WriteString(handshake, host);
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        handshake.Write(portBytes);
        WriteVarInt(handshake, 1);

        // La demande d'état (identifiant 0x00, sans contenu) part avec la poignée de main.
        using var request = new MemoryStream();
        WritePacket(request, handshake.ToArray());
        WritePacket(request, [0x00]);
        var started = Stopwatch.GetTimestamp();
        await connection.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);
        await connection.FlushAsync(ct).ConfigureAwait(false);

        var response = await ReadPacketAsync(connection, ct).ConfigureAwait(false);
        var roundTrip = Stopwatch.GetElapsedTime(started);
        var status = ParseStatus(response);

        return status with { Latency = await PingAsync(connection, roundTrip, ct, caller).ConfigureAwait(false) };
    }

    /// <summary>
    /// Le ping (identifiant 0x01, un entier de 8 octets que le serveur renvoie tel quel) mesure la latence. Un serveur qui ne le renvoie pas, ou
    /// mal, ne fait pas perdre l'état déjà reçu : la latence est alors celle de la demande d'état (<paramref name="fallback"/>).
    /// </summary>
    private static async Task<TimeSpan> PingAsync(Stream connection, TimeSpan fallback, CancellationToken ct, CancellationToken caller)
    {
        try
        {
            using var ping = new MemoryStream();
            var token = new byte[8];
            var sent = Environment.TickCount64;
            BinaryPrimitives.WriteInt64BigEndian(token, sent);
            ping.WriteByte(0x01);
            ping.Write(token);
            using var framed = new MemoryStream();
            WritePacket(framed, ping.ToArray());

            var started = Stopwatch.GetTimestamp();
            await connection.WriteAsync(framed.ToArray(), ct).ConfigureAwait(false);
            await connection.FlushAsync(ct).ConfigureAwait(false);
            var reply = await ReadPacketAsync(connection, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            // Le serveur doit renvoyer le jeton envoyé, tel quel : sinon ce n'est pas la réponse à notre ping, et la latence mesurée ne veut rien dire.
            var offset = 0;
            var echoed = ReadVarInt(reply, ref offset) == 0x01 && reply.Length - offset == 8
                         && BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(offset)) == sent;
            return echoed ? elapsed : fallback;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            // Le délai de toute l'interrogation s'est écoulé pendant le ping, ou le serveur ne le renvoie pas : l'état est gardé. Une annulation de
            // l'appelant, elle, reste une annulation.
            caller.ThrowIfCancellationRequested();
            return fallback;
        }
    }

    // ---- Lire et écrire des paquets ----

    private static void WritePacket(Stream destination, ReadOnlySpan<byte> payload)
    {
        WriteVarInt(destination, payload.Length);
        destination.Write(payload);
    }

    private static void WriteVarInt(Stream destination, int value)
    {
        var rest = (uint)value;
        do
        {
            var next = (byte)(rest & 0x7F);
            rest >>= 7;
            destination.WriteByte(rest == 0 ? next : (byte)(next | 0x80));
        }
        while (rest != 0);
    }

    private static void WriteString(Stream destination, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteVarInt(destination, bytes.Length);
        destination.Write(bytes);
    }

    /// <summary>
    /// Lit un paquet entier : sa longueur (un VarInt), puis son contenu. La longueur annoncée est vérifiée <b>avant</b> de réserver la mémoire.
    /// </summary>
    private static async Task<byte[]> ReadPacketAsync(Stream connection, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(connection, ct).ConfigureAwait(false);
        if (length is < 1 or > MaxPacketBytes)
        {
            throw new InvalidDataException($"Le serveur annonce un paquet de {length} octets : le protocole n'en attend pas plus de {MaxPacketBytes}.");
        }

        var packet = new byte[length];
        await connection.ReadExactlyAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    private static async Task<int> ReadVarIntAsync(Stream connection, CancellationToken ct)
    {
        var one = new byte[1];
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            await connection.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            value |= (one[0] & 0x7F) << (7 * index);
            if ((one[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Le serveur envoie un entier de plus de 5 octets : ce n'est pas un VarInt.");
    }

    /// <summary>Lit un VarInt dans un paquet déjà reçu, sans jamais lire au-delà de la fin.</summary>
    private static int ReadVarInt(ReadOnlySpan<byte> packet, ref int offset)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= packet.Length)
            {
                throw new InvalidDataException("Le paquet du serveur s'arrête au milieu d'un entier.");
            }

            var current = packet[offset++];
            value |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Le serveur envoie un entier de plus de 5 octets : ce n'est pas un VarInt.");
    }

    // ---- Lire la réponse d'état ----

    /// <summary>Lit le paquet de réponse d'état (identifiant 0x00 puis une chaîne JSON). La latence est mise à zéro : c'est l'appelant qui la mesure.</summary>
    internal static ServerStatus ParseStatus(byte[] packet)
    {
        var offset = 0;
        var id = ReadVarInt(packet, ref offset);
        if (id != 0x00)
        {
            throw new InvalidDataException($"Le serveur répond par le paquet {id:X2} au lieu de la réponse d'état (00).");
        }

        var length = ReadVarInt(packet, ref offset);
        if (length < 0 || length > packet.Length - offset)
        {
            throw new InvalidDataException($"Le serveur annonce un JSON de {length} octets dans un paquet qui en contient {packet.Length - offset}.");
        }

        try
        {
            using var json = JsonDocument.Parse(packet.AsMemory(offset, length), new JsonDocumentOptions { MaxDepth = 32 });
            return ReadStatus(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("La réponse d'état du serveur n'est pas un JSON lisible.", ex);
        }
    }

    private static ServerStatus ReadStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("La réponse d'état du serveur n'est pas un objet JSON.");
        }

        var version = root.TryGetProperty("version", out var versionNode) && versionNode.ValueKind == JsonValueKind.Object ? Text(versionNode, "name") : "";

        var online = 0;
        var max = 0;
        var sample = new List<string>();
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
        {
            online = Number(players, "online");
            max = Number(players, "max");
            if (players.TryGetProperty("sample", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in names.EnumerateArray())
                {
                    var name = entry.ValueKind == JsonValueKind.Object ? Clean(Text(entry, "name"), 32) : "";
                    if (name.Length > 0 && sample.Count < MaxSamplePlayers)
                    {
                        sample.Add(name);
                    }
                }
            }
        }

        var motd = "";
        if (root.TryGetProperty("description", out var description))
        {
            var text = new StringBuilder();
            AppendChat(description, text, depth: 0);
            motd = Clean(text.ToString(), MaxTextChars);
        }

        var favicon = root.TryGetProperty("favicon", out var faviconNode) && faviconNode.ValueKind == JsonValueKind.String
            ? DecodeFavicon(faviconNode.GetString()!)
            : null;

        return new ServerStatus(motd, Clean(version, 40), online, max, sample, TimeSpan.Zero, favicon);
    }

    private static string Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static int Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? Math.Max(0, number) : 0;

    /// <summary>
    /// Le message du jour est un texte simple ou un « objet de chat » : un <c>text</c> suivi d'<c>extra</c>, une liste d'autres objets de chat (ou
    /// une liste directement). On garde le texte, dans l'ordre ; la couleur et la mise en forme sont ignorées. Profondeur et longueur sont bornées.
    /// </summary>
    private static void AppendChat(JsonElement node, StringBuilder text, int depth)
    {
        if (depth > MaxChatDepth || text.Length >= MaxTextChars * 2)
        {
            return;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                text.Append(node.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    AppendChat(item, text, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                if (node.TryGetProperty("text", out var own) && own.ValueKind == JsonValueKind.String)
                {
                    text.Append(own.GetString());
                }

                if (node.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                {
                    AppendChat(extra, text, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Retire les codes de mise en forme (<c>§a</c>…), les caractères de contrôle et ceux qui changent le sens de lecture (un serveur peut en glisser
    /// dans son message pour retourner le texte qui suit), ramène les espaces et retours à la ligne à un seul espace, et coupe à
    /// <paramref name="maxChars"/>.
    /// </summary>
    internal static string Clean(string text, int maxChars)
    {
        var plain = FormattingCode.Replace(text, "");
        plain = string.Concat(plain.Where(c => char.IsWhiteSpace(c) || !(char.IsControl(c) || IsBidiControl(c))));
        plain = Whitespace.Replace(plain, " ").Trim();
        return plain.Length > maxChars ? plain[..maxChars].TrimEnd() : plain;
    }

    /// <summary>U+202A à U+202E et U+2066 à U+2069 (embeddings, overrides et isolats), plus les marques U+200E, U+200F et U+061C.</summary>
    private static bool IsBidiControl(char c) => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C';

    /// <summary>L'icône : <c>data:image/png;base64,…</c>. Trop grosse, mal codée ou autre chose qu'un PNG : on l'ignore, sans faire échouer l'état.</summary>
    private static byte[]? DecodeFavicon(string value)
    {
        if (!value.StartsWith(FaviconPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var base64 = value.AsSpan(FaviconPrefix.Length);
        if (base64.Length > MaxFaviconBytes / 3 * 4 + 8)
        {
            return null;
        }

        var buffer = new byte[base64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(base64, buffer, out var written)
            || written < PngSignature.Length
            || !buffer.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return null;
        }

        return buffer.AsSpan(0, written).ToArray();
    }
}
