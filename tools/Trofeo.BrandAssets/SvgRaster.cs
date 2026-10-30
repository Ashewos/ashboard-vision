using SkiaSharp;
using Svg.Skia;

namespace Trofeo.BrandAssets;

/// <summary>Un SVG du kit, chargé une fois, qu'on dessine à la taille voulue (le vecteur est redessiné à chaque taille, jamais rétréci).</summary>
internal sealed class SvgRaster : IDisposable
{
    private readonly SKSvg _svg = new();
    private readonly SKPicture _picture;

    public SvgRaster(string svgText)
    {
        _picture = _svg.FromSvg(svgText) ?? throw new InvalidDataException("SVG illisible.");
    }

    /// <summary>Dessine le SVG dans le carré qui commence en (<paramref name="x"/>, <paramref name="y"/>) et fait <paramref name="size"/> de côté.</summary>
    public void Draw(SKCanvas canvas, float x, float y, float size)
    {
        var bounds = _picture.CullRect;
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size / bounds.Width, size / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(_picture);
        canvas.Restore();
    }

    /// <summary>Un bitmap carré, transparent autour du dessin.</summary>
    public SKBitmap Render(int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        Draw(canvas, 0, 0, size);
        return bitmap;
    }

    /// <summary>Un bitmap de cette largeur, à la hauteur qui garde les proportions du SVG, posé sur ce fond.</summary>
    public SKBitmap RenderWidth(int width, SKColor background)
    {
        var bounds = _picture.CullRect;
        var scale = width / bounds.Width;
        var bitmap = new SKBitmap(width, (int)Math.Ceiling(bounds.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(_picture);
        return bitmap;
    }

    /// <summary>Le PNG carré de cette taille.</summary>
    public byte[] RenderPng(int size)
    {
        using var bitmap = Render(size);
        return Encode(bitmap);
    }

    public static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        _svg.Dispose();
    }
}
