using System.Buffers.Binary;

namespace Trofeo.BrandAssets;

/// <summary>
/// Écrit un fichier <c>.ico</c> dont chaque image est un PNG (Windows le lit depuis Vista) : un en-tête de 6 octets, une entrée de
/// 16 octets par image, puis les PNG les uns à la suite des autres.
/// </summary>
internal static class IcoWriter
{
    private const int HeaderSize = 6;
    private const int EntrySize = 16;

    /// <summary>Le fichier <c>.ico</c> de ces images (taille en pixels, carrée, et PNG), dans l'ordre donné.</summary>
    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        if (images.Count == 0)
            throw new ArgumentException("Une icône a besoin d'au moins une image.", nameof(images));
        foreach (var (size, _) in images)
            if (size is < 1 or > 256)
                throw new ArgumentOutOfRangeException(nameof(images), size, "Une icône va de 1 à 256 pixels de côté.");

        var total = HeaderSize + EntrySize * images.Count + images.Sum(image => image.Png.Length);
        var ico = new byte[total];
        var span = ico.AsSpan();

        // Réservé (0), type (1 = icône), nombre d'images.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Count);

        var offset = HeaderSize + EntrySize * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var (size, png) = images[i];
            var entry = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            entry[0] = (byte)(size == 256 ? 0 : size); // largeur : 256 s'écrit 0
            entry[1] = (byte)(size == 256 ? 0 : size); // hauteur
            // Octets 2 (couleurs) et 3 (réservé) : 0.
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);   // plans
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);  // bits par pixel
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            png.CopyTo(span[offset..]);
            offset += png.Length;
        }

        return ico;
    }
}
