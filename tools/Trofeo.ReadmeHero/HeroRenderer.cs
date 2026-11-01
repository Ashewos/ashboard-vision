using SkiaSharp;
using Trofeo.BrandAssets;
using Trofeo.Core.Layout;

namespace Trofeo.ReadmeHero;

/// <summary>Un thème de l'écran : son nom affiché et le rendu complet de l'écran (1920 × 462).</summary>
internal sealed record HeroTheme(string Name, SKBitmap Screen);

/// <summary>
/// Dessine une image de l'accueil à un instant donné, d'après la maquette <c>readme-accueil-v4.html</c> : fond sombre, nom et
/// phrase en haut, écran incliné de 10° avec sa lueur Ambilight (une copie floue, saturée et agrandie de l'image), glissement
/// et flou à chaque changement, reflet qui balaie la vitre, nom du thème, barre de progression et légère oscillation.
/// </summary>
internal sealed class HeroRenderer : IDisposable
{
    public const int Width = 1280;
    public const int Height = 620;

    // La maquette est dessinée pour une page d'environ 910 px de large : toutes ses mesures (en px CSS) passent à l'échelle de l'image.
    private const float K = Width / 910f;

    private static readonly SKColor Background = SKColor.Parse("#05060A");

    // Le plan de l'écran (le « stage » de la maquette) : 84 % de la largeur, centré, sous le nom et la phrase.
    private const float StageWidth = Width * 0.84f;
    private const float StageLeft = (Width - StageWidth) / 2f;
    private const float StageTop = 218f;
    private const float Padding = 8 * K; // la bordure noire de l'appareil
    private const float ImageWidth = StageWidth - 2 * Padding;
    private static readonly float ImageHeight = MathF.Round(ImageWidth * GridGeometry.Height / GridGeometry.Width);
    private static readonly float StageHeight = ImageHeight + 2 * Padding;
    private static readonly float DeviceRadius = 10 * K;
    private static readonly float PerspectiveDistance = 1400 * K;

    // La lueur : flou de 38 px CSS (donc 38 × K à 1280 de large, comme toutes les mesures de la maquette), saturation 1,9, luminosité 1,15, opacité 0,85, agrandie de 1,18 × 1,9.
    private const float GlowBlur = 38 * K;
    private const float GlowScaleX = 1.18f, GlowScaleY = 1.9f;
    private const float GlowOpacity = 0.85f;

    private const int LogoWidth = 460;

    private readonly string _name;
    private readonly string _tagline;
    private readonly IReadOnlyList<HeroTheme> _themes;
    private readonly SKTypeface _typeface;
    private readonly SKBitmap? _logo;
    private readonly List<SKBitmap> _glows = [];
    private readonly List<SKBitmap> _screens = [];

    public HeroRenderer(string name, string tagline, string? logoSvg, IReadOnlyList<HeroTheme> themes, SKTypeface typeface)
    {
        _name = name;
        _tagline = tagline;
        _themes = themes;
        _typeface = typeface;

        if (logoSvg is not null)
        {
            using var svg = new SvgRaster(logoSvg);
            _logo = svg.RenderWidth(LogoWidth, SKColors.Transparent);
        }

        foreach (var theme in themes)
        {
            _screens.Add(theme.Screen.Resize(
                new SKImageInfo((int)ImageWidth, (int)ImageHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell)));
            _glows.Add(MakeGlow(theme.Screen));
        }
    }

    /// <summary>La lueur d'un thème : l'écran étiré dans le plan, agrandi, flou, saturé et plus clair, en marge assez large pour le flou.</summary>
    private static SKBitmap MakeGlow(SKBitmap screen)
    {
        var margin = (int)MathF.Ceiling(GlowBlur * 3);
        var width = (int)MathF.Round(StageWidth * GlowScaleX);
        var height = (int)MathF.Round(StageHeight * GlowScaleY);
        var glow = new SKBitmap(width + 2 * margin, height + 2 * margin, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(glow);
        canvas.Clear(SKColors.Transparent);

        // CSS : blur(), puis saturate(1.9), puis brightness(1.15). Les deux derniers font une seule matrice.
        const float s = 1.9f, b = 1.15f;
        float[] matrix =
        [
            b * (0.213f + 0.787f * s), b * (0.715f - 0.715f * s), b * (0.072f - 0.072f * s), 0, 0,
            b * (0.213f - 0.213f * s), b * (0.715f + 0.285f * s), b * (0.072f - 0.072f * s), 0, 0,
            b * (0.213f - 0.213f * s), b * (0.715f - 0.715f * s), b * (0.072f + 0.928f * s), 0, 0,
            0, 0, 0, 1, 0,
        ];
        using var blur = SKImageFilter.CreateBlur(GlowBlur, GlowBlur);
        using var color = SKColorFilter.CreateColorMatrix(matrix);
        using var filter = SKImageFilter.CreateColorFilter(color, blur);
        using var paint = new SKPaint { ImageFilter = filter, IsAntialias = true };
        using var image = SKImage.FromBitmap(screen);
        canvas.DrawImage(image, new SKRect(margin, margin, margin + width, margin + height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return glow;
    }

    /// <summary>L'image à cet instant (en secondes depuis le début du cycle).</summary>
    public SKBitmap Render(double seconds)
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Background);

        // L'écran d'abord, le texte par-dessus : la lueur passe derrière le nom et la phrase.
        DrawStage(canvas, seconds);
        DrawHeader(canvas);
        DrawLabelAndBar(canvas, seconds);
        return bitmap;
    }

    // --- L'écran incliné ---

    private void DrawStage(SKCanvas canvas, double seconds)
    {
        var matrix = Perspective.Plane(
            StageLeft, StageTop, StageWidth, StageHeight, 10, HeroTimeline.Drift(seconds), PerspectiveDistance);
        var sheen = HeroTimeline.At(seconds).Sheen;

        for (var theme = 0; theme < _themes.Count; theme++)
        {
            var state = HeroTimeline.Layer(theme, seconds);
            if (state.Opacity <= 0.001)
            {
                continue;
            }

            canvas.Save();
            canvas.SetMatrix(matrix);

            // Le glissement et l'échelle se font autour du centre du plan, avant la perspective.
            canvas.Translate((float)state.Offset * StageWidth, 0);
            canvas.Translate(StageWidth / 2, StageHeight / 2);
            canvas.Scale((float)state.Scale);
            canvas.Translate(-StageWidth / 2, -StageHeight / 2);

            var filtered = state.Opacity < 0.999 || state.Blur > 0.01;
            using var layerBlur = state.Blur > 0.01 ? SKImageFilter.CreateBlur((float)state.Blur * K, (float)state.Blur * K) : null;
            using var layerPaint = filtered
                ? new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(state.Opacity * 255)), ImageFilter = layerBlur }
                : null;
            if (layerPaint is not null)
            {
                canvas.SaveLayer(layerPaint);
            }

            DrawGlow(canvas, theme);
            DrawDevice(canvas, theme, sheen);

            if (layerPaint is not null)
            {
                canvas.Restore();
            }

            canvas.Restore();
        }
    }

    private void DrawGlow(SKCanvas canvas, int theme)
    {
        var glow = _glows[theme];
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(GlowOpacity * 255)) };
        using var image = SKImage.FromBitmap(glow);
        canvas.DrawImage(image, (StageWidth - glow.Width) / 2f, (StageHeight - glow.Height) / 2f,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private void DrawDevice(SKCanvas canvas, int theme, double sheen)
    {
        var device = new SKRect(0, 0, StageWidth, StageHeight);
        var rounded = new SKRoundRect(device, DeviceRadius);

        // box-shadow: 0 0 0 1px #2a2f36, 0 20px 50px #000c.
        using (var shadow = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(0xCC),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 25 * K),
        })
        {
            canvas.Save();
            canvas.Translate(0, 20 * K);
            canvas.DrawRoundRect(rounded, shadow);
            canvas.Restore();
        }

        using (var ring = new SKPaint { Color = SKColor.Parse("#2A2F36"), IsAntialias = true })
        {
            var outer = new SKRoundRect(SKRect.Inflate(device, K, K), DeviceRadius + K);
            canvas.DrawRoundRect(outer, ring);
        }

        using (var body = new SKPaint { Color = SKColor.Parse("#0B0B0D"), IsAntialias = true })
        {
            canvas.DrawRoundRect(rounded, body);
        }

        canvas.Save();
        canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, true);

        using (var image = SKImage.FromBitmap(_screens[theme]))
        {
            var target = new SKRect(Padding, Padding, Padding + ImageWidth, Padding + ImageHeight);
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(target, 3 * K), SKClipOperation.Intersect, true);
            canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Restore();
        }

        DrawSheen(canvas, device, sheen);
        canvas.Restore();
    }

    /// <summary>Le reflet : une bande de 30 % de la largeur, qui va de −40 % à 120 % ; dégradé à 100°, transparent, blanc à 13 %, transparent.</summary>
    private static void DrawSheen(SKCanvas canvas, SKRect device, double sheen)
    {
        if (sheen <= 0 || sheen >= 1)
        {
            return;
        }

        var bandWidth = device.Width * 0.30f;
        var left = device.Left + device.Width * (float)(-0.4 + 1.6 * sheen);
        var band = new SKRect(left, device.Top, left + bandWidth, device.Bottom);

        var angle = 100 * MathF.PI / 180;
        var direction = new SKPoint(MathF.Sin(angle), -MathF.Cos(angle));
        var length = MathF.Abs(band.Width * MathF.Sin(angle)) + MathF.Abs(band.Height * MathF.Cos(angle));
        var center = new SKPoint(band.MidX, band.MidY);
        var from = new SKPoint(center.X - direction.X * length / 2, center.Y - direction.Y * length / 2);
        var to = new SKPoint(center.X + direction.X * length / 2, center.Y + direction.Y * length / 2);

        var clear = new SKColor(255, 255, 255, 0);
        using var shader = SKShader.CreateLinearGradient(
            from, to, [clear, new SKColor(255, 255, 255, 0x22), clear], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(band, paint);
    }

    // --- Le nom et la phrase ---

    private void DrawHeader(SKCanvas canvas)
    {
        float tagBaseline;
        if (_logo is { } logo)
        {
            // Le logo horizontal contient déjà le nom en tracés ; un symbole seul reçoit le nom à sa droite.
            if (logo.Width >= 2 * logo.Height)
            {
                canvas.DrawBitmap(logo, (Width - logo.Width) / 2f, 14);
                tagBaseline = 14 + logo.Height * 1.08f;
            }
            else
            {
                using var nameFont = new SKFont(_typeface, 40 * K) { Subpixel = true };
                var total = logo.Width + 18 + nameFont.MeasureText(_name);
                var left = (Width - total) / 2f;
                canvas.DrawBitmap(logo, left, 20);
                DrawName(canvas, nameFont, left + logo.Width + 18, 20 + logo.Height * 0.62f);
                tagBaseline = 20 + logo.Height + 24;
            }
        }
        else
        {
            using var nameFont = new SKFont(_typeface, 40 * K) { Subpixel = true };
            DrawName(canvas, nameFont, (Width - nameFont.MeasureText(_name)) / 2f, 88);
            tagBaseline = 88 + 44;
        }

        using var font = new SKFont(_typeface, 17 * K) { Subpixel = true };
        using var paint = new SKPaint { Color = SKColor.Parse("#9AA7B4"), IsAntialias = true };
        canvas.DrawText(_tagline, Width / 2f, tagBaseline, SKTextAlign.Center, font, paint);
    }

    /// <summary>Le nom en dégradé blanc vers gris-bleu, comme le titre de la maquette (sans logo, ou à côté d'un symbole).</summary>
    private void DrawName(SKCanvas canvas, SKFont font, float x, float baseline)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(x, 0), new SKPoint(x + font.MeasureText(_name), 0),
            [SKColors.White, SKColor.Parse("#AAB8C6")], null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawText(_name, x, baseline, SKTextAlign.Left, font, paint);
    }

    // --- Nom du thème et barre de progression ---

    private void DrawLabelAndBar(SKCanvas canvas, double seconds)
    {
        var labelY = StageTop + StageHeight + 22 * K + 18 * K;
        using var font = new SKFont(_typeface, 14 * K) { Subpixel = true };
        for (var theme = 0; theme < _themes.Count; theme++)
        {
            var (opacity, rise) = HeroTimeline.Label(theme, seconds);
            if (opacity <= 0.001)
            {
                continue;
            }

            // Capitales espacées de 5 px : le texte est dessiné lettre à lettre.
            var text = _themes[theme].Name.ToUpperInvariant();
            var spacing = 5 * K;
            var advances = text.Select(c => font.MeasureText(c.ToString())).ToArray();
            var x = (Width - (advances.Sum() + spacing * (text.Length - 1))) / 2f;
            using var paint = new SKPaint
            {
                Color = SKColor.Parse("#D6DDE4").WithAlpha((byte)Math.Round(opacity * 255)),
                IsAntialias = true,
            };
            for (var i = 0; i < text.Length; i++)
            {
                canvas.DrawText(text[i].ToString(), x, labelY + (float)rise * 8 * K, SKTextAlign.Left, font, paint);
                x += advances[i] + spacing;
            }
        }

        // Trois traits de 22 × 3 px : celui du thème en cours se remplit, les autres restent vides.
        var (current, progress, _) = HeroTimeline.At(seconds);
        float barWidth = 22 * K, barHeight = 3 * K, gap = 8 * K;
        var left = (Width - (3 * barWidth + 2 * gap)) / 2f;
        var top = labelY + 12 * K + 10 * K;
        using var track = new SKPaint { Color = new SKColor(255, 255, 255, 0x26), IsAntialias = true };
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < 3; i++)
        {
            var bar = new SKRect(left + i * (barWidth + gap), top, left + i * (barWidth + gap) + barWidth, top + barHeight);
            canvas.DrawRoundRect(bar, 2 * K, 2 * K, track);
            if (i == current)
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(bar.Left, bar.Top, bar.Left + barWidth * (float)progress, bar.Bottom));
                canvas.DrawRoundRect(bar, 2 * K, 2 * K, fill);
                canvas.Restore();
            }
        }
    }

    public void Dispose()
    {
        _logo?.Dispose();
        foreach (var bitmap in _glows.Concat(_screens))
        {
            bitmap.Dispose();
        }
    }
}
