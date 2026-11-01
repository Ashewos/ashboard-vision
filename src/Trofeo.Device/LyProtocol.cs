using System.Buffers.Binary;

namespace Trofeo.Device;

/// <summary>Protocole USB « LY » de l'écran (annexe A de la spec) : poignée de main et découpage d'une image JPEG.</summary>
public static class LyProtocol
{
    public const int PacketSize = 512;
    public const int HeaderSize = 16;
    public const int PayloadSize = PacketSize - HeaderSize;
    public const int HandshakeSize = 2048;
    public const int MaxJpegSize = 450_000;

    /// <summary>Octet de la réponse à la poignée de main qui donne le code du modèle.</summary>
    private const int ModelByteIndex = 20;

    /// <summary>Le seul modèle pris en charge : Trofeo Vision 9,16 pouces, 1920 × 462 (<c>64 + octet 20</c>, avec 0 à 3 comptés pour 1).</summary>
    public const int SupportedModel = 65;

    /// <summary>2048 octets : [0]=0x02, [1]=0xFF, [8]=0x01, le reste à zéro.</summary>
    public static byte[] BuildHandshake()
    {
        var buffer = new byte[HandshakeSize];
        buffer[0] = 0x02;
        buffer[1] = 0xFF;
        buffer[8] = 0x01;
        return buffer;
    }

    /// <summary>Vrai si la réponse commence par 0x03 0xFF, que l'octet 8 vaut 0x01 et qu'elle est assez longue pour donner le modèle.</summary>
    public static bool IsValidHandshakeReply(ReadOnlySpan<byte> reply) =>
        reply.Length > ModelByteIndex && reply[0] == 0x03 && reply[1] == 0xFF && reply[8] == 0x01;

    /// <summary>
    /// Code du modèle d'écran : <c>64 + octet 20</c>, comme le logiciel officiel, qui remplace d'abord
    /// les valeurs 0 à 3 par 1.
    /// </summary>
    /// <param name="reply">Une réponse valide (voir <see cref="IsValidHandshakeReply"/>).</param>
    public static int ReadModel(ReadOnlySpan<byte> reply)
    {
        if (reply.Length <= ModelByteIndex)
            throw new ArgumentException("La réponse est trop courte pour donner le modèle.", nameof(reply));

        var code = reply[ModelByteIndex];
        return 64 + (code <= 3 ? 1 : code);
    }

    /// <summary>
    /// Découpe un JPEG en paquets de 512 octets (en-tête de 16 + charge de 496), complétés par des
    /// paquets à zéro jusqu'à un multiple de 4.
    /// </summary>
    /// <exception cref="ArgumentException">Le JPEG dépasse <see cref="MaxJpegSize"/>.</exception>
    public static byte[] Packetize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length > MaxJpegSize)
            throw new ArgumentException($"Le JPEG dépasse {MaxJpegSize} octets ({jpeg.Length}).", nameof(jpeg));

        var count = jpeg.Length / PayloadSize + 1;
        var paddedCount = (count + 3) / 4 * 4;
        var buffer = new byte[paddedCount * PacketSize];

        for (var i = 0; i < count; i++)
        {
            var packet = buffer.AsSpan(i * PacketSize, PacketSize);
            var chunk = jpeg.Slice(i * PayloadSize, Math.Min(PayloadSize, jpeg.Length - i * PayloadSize));

            packet[0] = 0x01;
            packet[1] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(packet[2..], (uint)jpeg.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], (ushort)chunk.Length);
            packet[8] = 0x01;
            BinaryPrimitives.WriteUInt16LittleEndian(packet[9..], (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(packet[11..], (ushort)i);
            chunk.CopyTo(packet[HeaderSize..]);
        }

        return buffer;
    }
}
