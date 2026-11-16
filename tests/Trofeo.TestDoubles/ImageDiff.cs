using SkiaSharp;

namespace Trofeo.TestDoubles;

/// <summary>
/// Compare deux images pixel par pixel, pour vérifier qu'un rendu par la carte graphique reste proche du rendu par le processeur.
/// Les écarts se comptent par canal (rouge, vert, bleu et alpha), sur 255.
/// </summary>
public static class ImageDiff
{
    /// <summary>
    /// <c>Mean</c> : l'écart moyen sur tous les canaux de tous les pixels. <c>ShareWithin4</c> : la part (de 0 à 1) des pixels dont
    /// aucun canal ne s'écarte de plus de 4. <c>Max</c> : le plus grand écart d'un canal.
    /// Les deux images ont la même taille ; <paramref name="actual"/> est converti au format de <paramref name="expected"/> si besoin.
    /// </summary>
    public static (double Mean, double ShareWithin4, int Max) Compare(SKBitmap expected, SKBitmap actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            throw new ArgumentException(
                $"Les images n'ont pas la même taille : {expected.Width} × {expected.Height} contre {actual.Width} × {actual.Height}.");
        }

        using var converted = actual.Info.ColorType == expected.Info.ColorType && actual.Info.AlphaType == expected.Info.AlphaType
            ? null
            : Convert(actual, expected.Info);
        var other = converted ?? actual;

        int width = expected.Width, height = expected.Height, bytesPerPixel = expected.BytesPerPixel;
        var a = expected.GetPixelSpan();
        var b = other.GetPixelSpan();
        long sum = 0;
        int max = 0, within = 0;
        for (var y = 0; y < height; y++)
        {
            var rowA = a.Slice(y * expected.RowBytes, width * bytesPerPixel);
            var rowB = b.Slice(y * other.RowBytes, width * bytesPerPixel);
            for (var x = 0; x < width; x++)
            {
                var pixelMax = 0;
                for (var c = 0; c < bytesPerPixel; c++)
                {
                    var d = Math.Abs(rowA[x * bytesPerPixel + c] - rowB[x * bytesPerPixel + c]);
                    sum += d;
                    if (d > pixelMax) pixelMax = d;
                }
                if (pixelMax <= 4) within++;
                if (pixelMax > max) max = pixelMax;
            }
        }

        double pixels = (double)width * height;
        return (sum / (pixels * bytesPerPixel), within / pixels, max);
    }

    private static SKBitmap Convert(SKBitmap source, SKImageInfo target)
    {
        var copy = new SKBitmap(target);
        using var from = source.PeekPixels();
        using var to = copy.PeekPixels();
        if (!from.ReadPixels(to))
        {
            copy.Dispose();
            throw new InvalidOperationException("Impossible de convertir l'image au format de l'image de référence.");
        }
        return copy;
    }
}
