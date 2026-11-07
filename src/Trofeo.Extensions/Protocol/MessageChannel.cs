using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Trofeo.Extensions.Protocol;

/// <summary>
/// Le canal entre le service et l'hôte de module, sur deux flux (en pratique deux tubes anonymes). Une trame est une
/// longueur sur 4 octets (petit-boutiste) suivie d'autant d'octets de JSON UTF-8, 1 Mio au plus. L'envoi est sûr pour
/// plusieurs appelants à la fois ; la lecture se fait par un seul.
/// <para>
/// <b>Arrêt.</b> Les tubes anonymes sont des flux synchrones : ni l'annulation (<c>CancellationToken</c>) ni
/// <see cref="DisposeAsync"/> ne débloquent une lecture ou une écriture déjà en attente dessus. Une lecture ne se termine que
/// lorsque l'autre extrémité se ferme, c'est-à-dire quand le processus hôte est tué <i>et</i> que le service a fermé ses
/// propres copies des poignées client. Pour arrêter un hôte, on le tue (job object) et on ferme ces copies ; on ne compte
/// pas sur ce canal.
/// </para>
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
    /// <summary>La taille maximale d'une trame (le JSON, sans son en-tête).</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private const int HeaderBytes = 4;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    /// <param name="input">Ce que l'autre côté écrit (lu par <see cref="ReadAllAsync"/>).</param>
    /// <param name="output">Ce que ce côté écrit (<see cref="SendAsync"/>).</param>
    public MessageChannel(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// Envoie un message. L'en-tête et le JSON partent en un seul appel d'écriture, sous un verrou : deux envois
    /// simultanés ne s'entremêlent jamais. Un flux fermé lève une <see cref="IOException"/> ou <see cref="ObjectDisposedException"/>.
    /// <para>
    /// <b>Peut bloquer indéfiniment</b> si l'hôte ne lit plus son tube (plein) : l'écriture est synchrone, <paramref name="ct"/>
    /// ne l'interrompt pas, et les autres envois attendent derrière le verrou. Seule la mort de l'hôte la libère. Les appelants
    /// n'envoient donc jamais depuis la boucle de lecture (un hôte qui n'écoute plus figerait aussi sa propre lecture).
    /// </para>
    /// </summary>
    public async Task SendAsync(HostMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var json = JsonSerializer.SerializeToUtf8Bytes<HostMessage>(message, ProtocolJson.Options);
        if (json.Length > MaxFrameBytes)
        {
            throw new ArgumentException($"Message trop gros ({json.Length} octets, {MaxFrameBytes} au plus).", nameof(message));
        }

        var frame = new byte[HeaderBytes + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, HeaderBytes);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _output.WriteAsync(frame, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Lit les messages jusqu'à la fin du flux. Un flux qui s'arrête (ou se coupe) en pleine trame termine la lecture sans
    /// exception ; une trame de plus de 1 Mio, vide, ou qui n'est pas un message valide (type ou permission inconnus, champ
    /// obligatoire absent ou nul, énumération hors bornes) lève <see cref="InvalidDataException"/> et met fin à la lecture.
    /// Une lecture déjà bloquée sur un tube anonyme ne se termine que quand l'autre extrémité se ferme (voir la remarque de la classe).
    /// </summary>
    public async IAsyncEnumerable<HostMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        while (true)
        {
            if (!await TryReadExactlyAsync(header, ct).ConfigureAwait(false))
            {
                yield break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length > MaxFrameBytes)
            {
                throw new InvalidDataException($"Trame trop grosse ({length} octets, {MaxFrameBytes} au plus).");
            }

            if (length == 0)
            {
                throw new InvalidDataException("Trame vide.");
            }

            var payload = new byte[length];
            if (!await TryReadExactlyAsync(payload, ct).ConfigureAwait(false))
            {
                yield break;
            }

            HostMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<HostMessage>(payload, ProtocolJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // NotSupportedException : le discriminant « type » est absent (le type de base est abstrait).
                throw new InvalidDataException("Message illisible : " + ex.Message, ex);
            }

            if (message is null)
            {
                throw new InvalidDataException("Message vide.");
            }

            // Les éléments d'une liste ne sont pas couverts par les annotations de nullabilité : on les vérifie ici.
            if (message is LookupReply { Results: var results } && results.Any(result => result is null))
            {
                throw new InvalidDataException("Message illisible : une réponse de recherche contient un résultat nul.");
            }

            yield return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Sans attendre le verrou d'écriture. Ceci interdit les envois suivants (ObjectDisposedException) et ferme les flux,
        // mais NE débloque PAS une lecture ou une écriture déjà en cours sur un tube anonyme (voir la remarque de la classe) :
        // c'est la mort de l'hôte, ou la fermeture de l'autre extrémité, qui les libère.
        // Le verrou d'écriture n'est pas libéré avec Dispose : SemaphoreSlim ne tient aucune ressource native ici (on n'en
        // lit pas le WaitHandle), et le libérer ferait lever Release à l'envoi en cours et laisserait ses voisins en attente.
        try
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _input.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Remplit le tampon. Faux si le flux s'arrête avant (ou se coupe, ou est fermé).</summary>
    private async Task<bool> TryReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int count;
            try
            {
                count = await _input.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false; // tube coupé : l'autre bout est parti
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
