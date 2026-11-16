using SkiaSharp;
using Trofeo.Core.Layout;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Copies du fond au quart de la taille (480 × 116), gardées tant que le fond ne change pas :
/// <list type="bullet">
/// <item>une copie nette, qui sert à mesurer la luminance sous un panneau (garde de lisibilité, tous les styles) ;</item>
/// <item>une copie floutée et saturée pour le verre, recalculée seulement quand l'instance du fond, sa
/// <see cref="IBackground.Version"/> ou le <see cref="PanelLook"/> changent.</item>
/// </list>
/// Rien ne garde l'image courante du fond d'une image à l'autre : celle d'un fond animé n'est valable que jusqu'au
/// prochain <see cref="IBackground.Advance"/>. Les copies vivent dans leurs propres pixels. Un seul thread.
/// </summary>
internal sealed class Backdrop : IDisposable
{
    /// <summary>Largeur et hauteur des copies : 1920 / 4 et 462 / 4, arrondi au-dessus.</summary>
    public const int Width = 480, Height = 116;

    private const float ScaleX = (float)Width / GridGeometry.Width;
    private const float ScaleY = (float)Height / GridGeometry.Height;

    private static readonly SKSamplingOptions Shrink = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static readonly SKImageInfo SmallInfo = new(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

    // Luminance linéaire de chaque valeur sRGB d'un octet.
    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(v => PanelPainter.Linearize(v / 255f)).ToArray();

    private readonly SKBitmap _small = new(SmallInfo);
    private readonly Dictionary<SKRect, float> _luminances = [];
    private IBackground? _smallFor;
    private int _smallVersion;

    private SKSurface? _surface;
    private SKImage? _blurred;
    private IBackground? _blurFor;
    private int _blurVersion;
    private PanelLook? _blurLook;

    private IGpuDevice? _device;

    /// <summary>Nombre de fois que la copie floutée a été recalculée.</summary>
    internal int Computations { get; private set; }

    /// <summary>
    /// La carte qui calcule la copie floutée, ou <c>null</c> pour le processeur. Changer d'appareil jette la copie floutée et sa
    /// surface : elle est recalculée sur le nouvel appareil à la prochaine demande (une image de la carte ne se dessine pas sur
    /// le processeur, ni l'inverse, sans passer par la mémoire). La copie nette et les luminances restent celles du processeur.
    /// </summary>
    public IGpuDevice? Device
    {
        get => _device;
        set
        {
            if (ReferenceEquals(_device, value)) return;

            _device = value;
            DropBlur();
        }
    }

    /// <summary>La copie floutée et saturée du fond, au quart de la taille ; valable jusqu'au prochain appel avec un autre fond.</summary>
    public SKImage Get(IBackground background, PanelLook look)
    {
        EnsureSmall(background);
        if (_blurred is not null && ReferenceEquals(_blurFor, background) && _blurVersion == background.Version && look.Equals(_blurLook))
        {
            return _blurred;
        }

        _surface ??= _device?.CreateSurface(SmallInfo) ?? SKSurface.Create(SmallInfo)
            ?? throw new InvalidOperationException("Impossible de créer la surface du flou.");
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint();
        SKImageFilter? blur = null, filter = null;
        SKColorFilter? saturation = null;
        try
        {
            float sigma = look.BlurSigma / 4f;
            if (sigma > 0f)
            {
                // Clamp : les bords du fond sont prolongés, le flou ne s'assombrit pas vers l'extérieur.
                blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
            }
            if (look.Saturation != 1f)
            {
                saturation = SKColorFilter.CreateColorMatrix(PanelPainter.SaturationMatrix(look.Saturation));
                filter = SKImageFilter.CreateColorFilter(saturation, blur);
            }
            paint.ImageFilter = filter ?? blur;
            canvas.DrawBitmap(_small, 0, 0, paint);
        }
        finally
        {
            filter?.Dispose();
            saturation?.Dispose();
            blur?.Dispose();
        }

        _blurred?.Dispose();
        _blurred = _surface.Snapshot();
        _blurFor = background;
        _blurVersion = background.Version;
        _blurLook = look;
        Computations++;
        return _blurred;
    }

    /// <summary>
    /// Luminance relative (WCAG, 0 à 1) de la zone la plus claire du fond sous <paramref name="rect"/> (pixels de
    /// l'écran) : le maximum des moyennes de cellules d'environ <see cref="CellSize"/> px de la copie réduite. Le texte
    /// se pose sur une partie du panneau, pas sur sa moyenne. Calculée une fois par version du fond et par rectangle.
    /// </summary>
    public float BrightestLuminance(IBackground background, SKRect rect)
    {
        EnsureSmall(background);
        if (_luminances.TryGetValue(rect, out float cached))
        {
            return cached;
        }

        float value = BrightestLuminance(_small, rect);
        _luminances[rect] = value;
        return value;
    }

    /// <summary>Côté d'une cellule de mesure, en pixels de la copie réduite (24 × 4 = 96 px de l'écran).</summary>
    internal const int CellSize = 24;

    /// <summary>
    /// Plus grande luminance relative moyenne d'une cellule (environ <see cref="CellSize"/> × <see cref="CellSize"/>
    /// px, au moins une) sous <paramref name="rect"/>, sur une copie réduite à 480 × 116.
    /// </summary>
    internal static float BrightestLuminance(SKBitmap small, SKRect rect)
    {
        int x0 = Math.Clamp((int)Math.Floor(rect.Left * ScaleX), 0, Width - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(rect.Right * ScaleX), x0 + 1, Width);
        int y0 = Math.Clamp((int)Math.Floor(rect.Top * ScaleY), 0, Height - 1);
        int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom * ScaleY), y0 + 1, Height);
        return BrightestLuminance(small, x0, y0, x1, y1);
    }

    /// <summary>
    /// Plus grande luminance relative moyenne d'une cellule (environ <see cref="CellSize"/> × <see cref="CellSize"/> px, au
    /// moins une) de la zone [x0, x1[ × [y0, y1[ de <paramref name="small"/> (Bgra8888, opaque).
    /// </summary>
    internal static float BrightestLuminance(SKBitmap small, int x0, int y0, int x1, int y1)
    {
        int columns = Math.Max(1, (int)Math.Round((x1 - x0) / (double)CellSize));
        int rows = Math.Max(1, (int)Math.Round((y1 - y0) / (double)CellSize));

        var pixels = small.GetPixelSpan();
        int stride = small.RowBytes;
        float brightest = 0f;
        for (int row = 0; row < rows; row++)
        {
            int cy0 = y0 + (y1 - y0) * row / rows, cy1 = y0 + (y1 - y0) * (row + 1) / rows;
            for (int column = 0; column < columns; column++)
            {
                int cx0 = x0 + (x1 - x0) * column / columns, cx1 = x0 + (x1 - x0) * (column + 1) / columns;
                double sum = 0;
                for (int y = cy0; y < cy1; y++)
                {
                    int index = y * stride + cx0 * 4;
                    for (int x = cx0; x < cx1; x++, index += 4)
                    {
                        // Bgra8888 : B, G, R, A. Le fond est opaque.
                        sum += 0.0722f * Linear[pixels[index]] + 0.7152f * Linear[pixels[index + 1]] + 0.2126f * Linear[pixels[index + 2]];
                    }
                }
                brightest = Math.Max(brightest, (float)(sum / ((cx1 - cx0) * (cy1 - cy0))));
            }
        }
        return brightest;
    }

    /// <summary>Réduit au quart l'image de <paramref name="background"/> dans <paramref name="target"/> (480 × 116, Bgra).</summary>
    internal static void Shrunk(SKImage image, SKBitmap target)
    {
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKRect(0, 0, Width, Height), Shrink, paint);
    }

    private void EnsureSmall(IBackground background)
    {
        if (ReferenceEquals(_smallFor, background) && _smallVersion == background.Version)
        {
            return;
        }

        Shrunk(background.Current, _small);
        _luminances.Clear();
        _smallFor = background;
        _smallVersion = background.Version;
    }

    /// <summary>Jette la copie floutée et sa surface ; elles se refont à la prochaine demande.</summary>
    private void DropBlur()
    {
        _blurred?.Dispose();
        _surface?.Dispose();
        _blurred = null;
        _surface = null;
        _blurFor = null;
        _blurLook = null;
    }

    public void Dispose()
    {
        DropBlur();
        _small.Dispose();
        _smallFor = null;
    }
}
