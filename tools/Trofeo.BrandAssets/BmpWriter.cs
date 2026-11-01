using System.Buffers.Binary;
using SkiaSharp;

namespace Trofeo.BrandAssets;

/// <summary>
/// Écrit un BMP 24 bits sans compression et sans transparence, comme Inno Setup les attend pour les images de l'assistant.
/// Skia ne sait pas encoder le BMP : l'en-tête (14 + 40 octets) est écrit ici.
/// </summary>
internal static class BmpWriter
{
    private const int HeadersSize = 54;

    /// <summary>
    /// Le BMP de cette image. Une image qui a de la transparence est d'abord posée sur <paramref name="background"/>, car un BMP 24 bits
    /// n'a pas d'alpha.
    /// </summary>
    public static byte[] Write(SKBitmap bitmap, SKColor background)
    {
        using var flat = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(background);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        var width = flat.Width;
        var height = flat.Height;
        var stride = (width * 3 + 3) & ~3; // chaque ligne est complétée à un multiple de 4 octets
        var bmp = new byte[HeadersSize + stride * height];
        var span = bmp.AsSpan();

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[10..], HeadersSize);   // début des pixels
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], 40);            // taille de l'en-tête d'image
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], height);         // positif : lignes de bas en haut
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 1);             // plans
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 24);            // bits par pixel
        BinaryPrimitives.WriteUInt32LittleEndian(span[34..], (uint)(stride * height));

        var pixels = flat.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = span.Slice(HeadersSize + (height - 1 - y) * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var color = pixels[y * width + x];
                row[x * 3] = color.Blue;
                row[x * 3 + 1] = color.Green;
                row[x * 3 + 2] = color.Red;
            }
        }

        return bmp;
    }
}
