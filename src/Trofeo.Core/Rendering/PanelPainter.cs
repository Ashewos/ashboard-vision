using SkiaSharp;
using Trofeo.Core.Layout;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Peint le panneau sous un module, selon le style du thème (mat, verre, pixel), garde de lisibilité comprise. Pour
/// « Liquid Glass avancé », il ne peint que la teinte : le verre et l'ombre viennent de <see cref="LiquidGlassLayers"/>.
/// </summary>
internal static class PanelPainter
{
    /// <summary>Luminance relative maximale du panneau posé sur le fond (WCAG, 0 à 1).</summary>
    public const float MaxLuminance = 0.35f;

    /// <summary>Ce que le reflet blanc du verre ajoute à la luminance du panneau.</summary>
    public const float GlassSheenLuminance = 0.05f;

    private static readonly SKSamplingOptions Upscale = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>
    /// Peint le panneau sous le module (avant son rendu).
    /// </summary>
    /// <param name="backdrop">Le fond flouté de <see cref="Backdrop.Get"/> ; null hors verre.</param>
    /// <param name="background">Le fond net de l'écran ; sert à mesurer la luminance quand elle n'est pas donnée.</param>
    /// <param name="backdropLuminance">Luminance de la zone la plus claire du fond sous <paramref name="rect"/> (<see cref="Backdrop.BrightestLuminance(IBackground, SKRect)"/>) ; sinon mesurée sur <paramref name="background"/>. Inutile quand la garde est coupée.</param>
    /// <param name="alpha">L'opacité à appliquer, quand l'appelant l'a lissée dans le temps ; sinon celle de <see cref="TargetAlpha"/>.</param>
    public static void Paint(
        SKCanvas canvas, SKRect rect, ThemeDefinition theme, SKImage? backdrop, SKImage background,
        float? backdropLuminance = null, float? alpha = null)
    {
        var look = theme.Panel;
        float applied = alpha
            ?? TargetAlpha(theme, theme.LegibilityGuard ? backdropLuminance ?? MeasureOnce(background, rect) : 0f);

        switch (look.Kind)
        {
            case PanelKind.Glass:
                PaintGlass(canvas, rect, theme, backdrop, applied);
                break;
            case PanelKind.Pixel:
                PaintPixel(canvas, rect, theme, applied);
                break;
            case PanelKind.LiquidGlass:
                PaintTint(canvas, rect, theme, applied);
                break;
            default:
                PaintMatte(canvas, rect, theme, applied);
                break;
        }
    }

    /// <summary>
    /// L'opacité voulue du panneau devant une zone du fond de luminance <paramref name="backdropLuminance"/> : celle de la garde
    /// de lisibilité (<see cref="LegibleAlpha"/>, avec le reflet du verre), ou exactement l'opacité choisie quand le thème a
    /// coupé la garde (<see cref="ThemeDefinition.LegibilityGuard"/>). Pour « Liquid Glass avancé », la luminance est celle de la
    /// couche de verre posée sur le fond (<see cref="LiquidGlassLayers.Luminance"/>) : elle contient déjà les reflets.
    /// </summary>
    public static float TargetAlpha(ThemeDefinition theme, float backdropLuminance)
    {
        if (!theme.LegibilityGuard) return theme.Theme.PanelOpacity;

        return LegibleAlpha(
            backdropLuminance,
            RelativeLuminance(theme.Theme.Panel),
            theme.Theme.PanelOpacity,
            theme.Panel.Kind == PanelKind.Glass ? GlassSheenLuminance : 0f);
    }

    /// <summary>Le rognage d'un module : le contour du panneau (arrondi, ou angles vifs en Pixel).</summary>
    public static void ClipToPanel(SKCanvas canvas, SKRect rect, ThemeDefinition theme)
    {
        if (theme.Panel.Kind == PanelKind.Pixel)
        {
            canvas.ClipRect(rect, SKClipOperation.Intersect, false);
            return;
        }

        using var clip = new SKRoundRect(rect, theme.Theme.CornerRadius);
        canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
    }

    // ---- Mat ----

    private static void PaintMatte(SKCanvas canvas, SKRect rect, ThemeDefinition def, float alpha)
    {
        float radius = def.Theme.CornerRadius;
        using (var fill = new SKPaint { IsAntialias = true, Color = Tint(def.Theme.Panel, alpha) })
        {
            canvas.DrawRoundRect(rect, radius, radius, fill);
        }

        DrawInnerOutline(canvas, rect, radius, def.Panel.Outline);
    }

    // ---- Verre ----

    private static void PaintGlass(SKCanvas canvas, SKRect rect, ThemeDefinition def, SKImage? backdrop, float alpha)
    {
        var look = def.Panel;
        float radius = def.Theme.CornerRadius;
        using var round = new SKRoundRect(rect, radius);

        // 1. Ombre portée, hors du panneau seulement (comme le box-shadow CSS : on ne la voit pas à travers le verre).
        if (look.DropShadow.Alpha > 0)
        {
            canvas.Save();
            canvas.ClipRoundRect(round, SKClipOperation.Difference, true);
            using var blur = SKImageFilter.CreateBlur(15f, 15f);
            using var shadow = new SKPaint { IsAntialias = true, Color = look.DropShadow, ImageFilter = blur };
            canvas.DrawRoundRect(SKRect.Create(rect.Left, rect.Top + 10f, rect.Width, rect.Height), radius, radius, shadow);
            canvas.Restore();
        }

        canvas.Save();
        canvas.ClipRoundRect(round, SKClipOperation.Intersect, true);

        // 2. Le fond flouté, ramené à la taille de l'écran.
        if (backdrop is not null)
        {
            canvas.DrawImage(backdrop, SKRect.Create(0, 0, GridGeometry.Width, GridGeometry.Height), Upscale);
        }

        // 3. La teinte.
        using (var tint = new SKPaint { IsAntialias = true, Color = Tint(def.Theme.Panel, alpha) })
        {
            canvas.DrawRect(rect, tint);
        }

        // 4. Le reflet, dégradé à 155°.
        var (start, end) = CssGradientLine(155, rect);
        using (var shader = SKShader.CreateLinearGradient(
                   start, end, [look.SheenTop, look.SheenMid, look.SheenBottom], [0f, .55f, 1f], SKShaderTileMode.Clamp))
        using (var sheen = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(rect, sheen);
        }
        canvas.Restore();

        // 5. Le contour intérieur.
        DrawInnerOutline(canvas, rect, radius, look.Outline);

        // 6. Le trait de lumière du bord haut : le croissant que laisse le panneau, une fois décalé de 1,5 px vers le bas
        //    (CSS « inset 0 1.5px 0 »). Il épouse le haut et les angles, et s'amincit jusqu'à rien le long des côtés.
        if (look.TopLight.Alpha > 0)
        {
            canvas.Save();
            canvas.ClipRoundRect(round, SKClipOperation.Intersect, true);
            using var lowered = new SKRoundRect(SKRect.Create(rect.Left, rect.Top + 1.5f, rect.Width, rect.Height), radius);
            canvas.ClipRoundRect(lowered, SKClipOperation.Difference, true);
            using var light = new SKPaint { Color = look.TopLight };
            canvas.DrawRect(rect, light);
            canvas.Restore();
        }
    }

    // ---- Liquid Glass avancé ----

    /// <summary>
    /// La teinte seule, sur la forme du verre. Le masque du verre passe par la moitié à un demi-pixel à l'intérieur du bord :
    /// la teinte est rentrée d'autant, pour ne pas dépasser du verre.
    /// </summary>
    private static void PaintTint(SKCanvas canvas, SKRect rect, ThemeDefinition def, float alpha)
    {
        var color = Tint(def.Theme.Panel, alpha);
        if (color.Alpha == 0)
        {
            return;
        }

        float radius = Math.Min(def.Panel.Glass?.CornerRadius ?? def.Theme.CornerRadius, Math.Min(rect.Width, rect.Height) / 2f);
        var inner = rect;
        inner.Inflate(-0.5f, -0.5f);
        if (inner.Width <= 0f || inner.Height <= 0f)
        {
            return;
        }

        float r = Math.Max(0f, radius - 0.5f);
        using var tint = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawRoundRect(inner, r, r, tint);
    }

    // ---- Pixel ----

    private static void PaintPixel(SKCanvas canvas, SKRect rect, ThemeDefinition def, float alpha)
    {
        var look = def.Panel;

        // Ombre décalée de (8, 8), agrandie de 4, puis bordure : hors du panneau seulement, sans anticrénelage.
        canvas.Save();
        canvas.ClipRect(rect, SKClipOperation.Difference, false);
        using (var shadow = new SKPaint { IsAntialias = false, Color = look.PixelShadow })
        {
            var offset = SKRect.Create(rect.Left + 8f, rect.Top + 8f, rect.Width, rect.Height);
            offset.Inflate(4f, 4f);
            canvas.DrawRect(offset, shadow);
        }
        using (var border = new SKPaint { IsAntialias = false, Color = look.Border })
        {
            var ring = rect;
            ring.Inflate(look.BorderWidth, look.BorderWidth);
            canvas.DrawRect(ring, border);
        }
        canvas.Restore();

        using var fill = new SKPaint { IsAntialias = false, Color = Tint(def.Theme.Panel, alpha) };
        canvas.DrawRect(rect, fill);
    }

    // ---- Aides ----

    /// <summary>Contour de 1 px à l'intérieur du rect (CSS « inset 0 0 0 1px »).</summary>
    private static void DrawInnerOutline(SKCanvas canvas, SKRect rect, float radius, SKColor color)
    {
        if (color.Alpha == 0)
        {
            return;
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = color };
        var inner = rect;
        inner.Inflate(-0.5f, -0.5f);
        float r = Math.Max(0f, radius - 0.5f);
        canvas.DrawRoundRect(inner, r, r, paint);
    }

    private static SKColor Tint(SKColor color, float alpha) => color.WithAlpha((byte)Math.Round(alpha * 255f));

    private static float MeasureOnce(SKImage background, SKRect rect)
    {
        using var small = new SKBitmap(new SKImageInfo(Backdrop.Width, Backdrop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Backdrop.Shrunk(background, small);
        return Backdrop.BrightestLuminance(small, rect);
    }

    /// <summary>
    /// Garde de lisibilité (<c>L_fond</c> = la zone la plus claire sous le panneau, pas sa moyenne) : si <c>L_fond × (1 − a) + L_panneau × a + reflet</c> dépasse <see cref="MaxLuminance"/>, la
    /// plus petite opacité qui ramène ce total à la limite ; sinon <paramref name="alpha"/>, inchangée.
    /// </summary>
    public static float LegibleAlpha(float backdropLuminance, float panelLuminance, float alpha, float sheen)
    {
        float total = backdropLuminance * (1f - alpha) + panelLuminance * alpha + sheen;
        if (total <= MaxLuminance || backdropLuminance <= panelLuminance)
        {
            return alpha; // dans la limite, ou un panneau plus clair que le fond ne peut rien y changer
        }

        float needed = (backdropLuminance + sheen - MaxLuminance) / (backdropLuminance - panelLuminance);
        return Math.Clamp(needed, alpha, 1f);
    }

    /// <summary>Luminance relative WCAG (sRGB linéarisé, coefficients 0,2126 / 0,7152 / 0,0722), de 0 à 1.</summary>
    public static float RelativeLuminance(SKColor color) =>
        0.2126f * Linearize(color.Red / 255f) + 0.7152f * Linearize(color.Green / 255f) + 0.0722f * Linearize(color.Blue / 255f);

    internal static float Linearize(float s) => s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);

    /// <summary>Matrice 4 × 5 de la saturation CSS <c>saturate(s)</c>, coefficients de luminance 0,2126 / 0,7152 / 0,0722.</summary>
    public static float[] SaturationMatrix(float s)
    {
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        return
        [
            r + (1 - r) * s, g - g * s, b - b * s, 0, 0,
            r - r * s, g + (1 - g) * s, b - b * s, 0, 0,
            r - r * s, g - g * s, b + (1 - b) * s, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>
    /// Ligne d'un dégradé CSS <c>linear-gradient(angle, …)</c> sur <paramref name="box"/> : l'angle part du haut et
    /// tourne dans le sens horaire ; la ligne passe par le centre et mesure |l·sin| + |h·cos|.
    /// </summary>
    public static (SKPoint Start, SKPoint End) CssGradientLine(double angleDegrees, SKRect box)
    {
        double angle = angleDegrees * Math.PI / 180;
        float dx = (float)Math.Sin(angle), dy = (float)-Math.Cos(angle);
        float half = (Math.Abs(box.Width * dx) + Math.Abs(box.Height * dy)) / 2f;
        return (
            new SKPoint(box.MidX - dx * half, box.MidY - dy * half),
            new SKPoint(box.MidX + dx * half, box.MidY + dy * half));
    }
}
