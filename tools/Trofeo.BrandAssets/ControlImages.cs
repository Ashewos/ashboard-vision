using System.Buffers.Binary;
using SkiaSharp;

namespace Trofeo.BrandAssets;

/// <summary>
/// Des PNG de contrôle, à regarder avant de commiter : ce que contient chaque <c>.ico</c> (image par image, et une planche qui
/// agrandit les petites tailles sur fond clair et sombre), chaque <c>.svg</c> et chaque <c>.bmp</c> convertis en PNG. Rien de cela n'est commité.
/// </summary>
internal static class ControlImages
{
    private static readonly SKColor Light = new(0xF3, 0xF3, 0xF3);
    private static readonly SKColor Dark = new(0x20, 0x20, 0x20);

    public static void Write(string brandDir, string controlDir)
    {
        Directory.CreateDirectory(controlDir);

        foreach (var ico in Directory.GetFiles(brandDir, "*.ico").Order())
        {
            var name = Path.GetFileNameWithoutExtension(ico);
            var frames = ReadIco(File.ReadAllBytes(ico));
            foreach (var (size, png) in frames)
                File.WriteAllBytes(Path.Combine(controlDir, $"{name}-{size}.png"), png);
            File.WriteAllBytes(Path.Combine(controlDir, $"planche-{name}.png"), SheetPng(frames));
        }

        // Les logos horizontaux : le clair sur fond clair, le sombre sur fond sombre.
        foreach (var svg in Directory.GetFiles(brandDir, "*.svg").Order())
        {
            using var raster = new SvgRaster(File.ReadAllText(svg));
            using var bitmap = raster.RenderWidth(1400, svg.EndsWith("-clair.svg", StringComparison.Ordinal) ? Light : Dark);
            File.WriteAllBytes(Path.Combine(controlDir, Path.GetFileNameWithoutExtension(svg) + "-svg.png"), SvgRaster.Encode(bitmap));
        }

        foreach (var bmp in Directory.GetFiles(brandDir, "*.bmp").Order())
        {
            using var bitmap = SKBitmap.Decode(bmp) ?? throw new InvalidDataException($"BMP illisible : {bmp}");
            File.WriteAllBytes(Path.Combine(controlDir, Path.GetFileNameWithoutExtension(bmp) + "-bmp.png"), SvgRaster.Encode(bitmap));
        }
    }

    /// <summary>Les images d'une icône (taille, PNG), relues dans le fichier.</summary>
    internal static List<(int Size, byte[] Png)> ReadIco(byte[] ico)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        var frames = new List<(int, byte[])>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + i * 16, 16);
            var size = entry[0] == 0 ? 256 : entry[0];
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            frames.Add((size, ico.AsSpan(offset, length).ToArray()));
        }

        return frames;
    }

    /// <summary>Une planche : chaque taille au pixel près, agrandie 4 fois (au plus 256 px de côté), sur fond clair puis sombre.</summary>
    private static byte[] SheetPng(List<(int Size, byte[] Png)> frames)
    {
        const int gap = 12;
        var shown = frames.Select(f => (f.Size, Zoom: f.Size >= 128 ? 1 : 4)).ToList();
        var width = shown.Sum(s => s.Size * s.Zoom + gap) + gap;
        var height = shown.Max(s => s.Size * s.Zoom) + 2 * gap;
        using var sheet = new SKBitmap(width, height * 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(sheet))
        {
            using var paint = new SKPaint { IsAntialias = false };
            foreach (var (row, background) in new[] { (0, Light), (1, Dark) })
            {
                var x = gap;
                canvas.DrawRect(0, row * height, width, height, new SKPaint { Color = background });
                for (var i = 0; i < frames.Count; i++)
                {
                    using var frame = SKBitmap.Decode(frames[i].Png);
                    var size = shown[i].Size * shown[i].Zoom;
                    var target = new SKRect(x, row * height + gap, x + size, row * height + gap + size);
                    // Pixels nets : on veut voir chaque pixel de l'icône, pas un lissage.
                    canvas.DrawBitmap(frame, target, paint);
                    x += size + gap;
                }
            }
        }

        return SvgRaster.Encode(sheet);
    }
}
