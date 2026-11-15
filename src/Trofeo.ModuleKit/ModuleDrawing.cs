using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace Trofeo.ModuleKit;

/// <summary>
/// Primitives de dessin partagées par les modules : texte avec police de secours, coupure,
/// barres de progression et message d'action. Tout est sans état hormis le cache des polices
/// de secours (thread-safe) : appelable depuis le thread d'affichage.
/// </summary>
public static class ModuleDrawing
{
    private const string Ellipsis = "…";

    /// <summary><see cref="Theme.TypefaceScale"/>, ou 1 s'il n'est pas un nombre fini positif.</summary>
    public static float TypefaceScale(Theme theme) =>
        float.IsFinite(theme.TypefaceScale) && theme.TypefaceScale > 0f ? theme.TypefaceScale : 1f;

    /// <summary>
    /// Police du thème (<see cref="Theme.Typeface"/>) à <paramref name="size"/> × <see cref="Theme.TypefaceScale"/> : seuls
    /// les caractères grandissent, la mise en page (calculée d'après <paramref name="size"/>) ne bouge pas.
    /// Toute police de texte d'un module passe par ici ; le fil d'activité garde <see cref="Theme.FeedTypeface"/> et <see cref="Theme.FeedScale"/>.
    /// </summary>
    public static SKFont Font(Theme theme, float size, bool subpixel = false) =>
        new(theme.Typeface, size * TypefaceScale(theme)) { Subpixel = subpixel };

    /// <summary>Polices de secours déjà résolues, par (famille de la police principale, point de code). <c>null</c> = aucune trouvée.</summary>
    private static readonly ConcurrentDictionary<(string Family, int CodePoint), SKTypeface?> FallbackCache = new();

    /// <summary>Suite de caractères dessinée avec une même police (principale ou de secours).</summary>
    private readonly record struct Run(string Text, SKTypeface Typeface);

    /// <summary>Largeur du texte en pixels, caractères absents de la police principale compris (police de secours).</summary>
    public static float MeasureText(string text, SKFont font)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        var width = 0f;
        foreach (var run in Split(text, font))
        {
            if (ReferenceEquals(run.Typeface, font.Typeface))
            {
                width += font.MeasureText(run.Text);
            }
            else
            {
                using var fallback = CloneFont(font, run.Typeface);
                width += fallback.MeasureText(run.Text);
            }
        }
        return width;
    }

    /// <summary>
    /// Dessine le texte sur sa ligne de base. Les caractères absents de la police principale sont
    /// dessinés avec une police de secours du système ; l'alignement porte sur la largeur totale.
    /// </summary>
    public static void DrawText(SKCanvas canvas, string text, float x, float baseline, SKTextAlign align, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;

        var runs = Split(text, font);
        var start = x;
        if (align != SKTextAlign.Left)
        {
            var total = MeasureText(text, font);
            start = align == SKTextAlign.Center ? x - total / 2f : x - total;
        }

        foreach (var run in runs)
        {
            if (ReferenceEquals(run.Typeface, font.Typeface))
            {
                canvas.DrawText(run.Text, start, baseline, SKTextAlign.Left, font, paint);
                start += font.MeasureText(run.Text);
            }
            else
            {
                using var fallback = CloneFont(font, run.Typeface);
                canvas.DrawText(run.Text, start, baseline, SKTextAlign.Left, fallback, paint);
                start += fallback.MeasureText(run.Text);
            }
        }
    }

    /// <summary>
    /// Raccourcit le texte pour qu'il tienne dans <paramref name="maxWidth"/> : le plus long préfixe
    /// suivi de « … » qui tient (sans jamais couper une paire de substitution), ou « … » seul.
    /// </summary>
    public static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (MeasureText(text, font) <= maxWidth) return text;

        var runes = text.EnumerateRunes().ToArray();
        // Recherche dichotomique du plus grand nombre de caractères conservés.
        int low = 0, high = runes.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (MeasureText(Prefix(runes, mid) + Ellipsis, font) <= maxWidth) low = mid;
            else high = mid - 1;
        }
        return low == 0 ? Ellipsis : Prefix(runes, low) + Ellipsis;
    }

    /// <summary>
    /// Coupe le texte sur les espaces en lignes d'au plus <paramref name="maxWidth"/>, sur au plus
    /// <paramref name="maxLines"/> lignes. Un mot trop long est ellipsé sur sa ligne ; s'il reste du texte
    /// après la dernière ligne autorisée, celle-ci est ellipsée.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, SKFont font, float maxWidth, int maxLines)
        => WrapCore(text, font, maxWidth, maxLines).Lines;

    /// <summary>Remplit <paramref name="rect"/> d'une piste arrondie, puis de la fraction (bornée à [0, 1], NaN = 0) en <paramref name="fill"/>.</summary>
    public static void DrawBar(SKCanvas canvas, SKRect rect, float fraction, SKColor fill, SKColor track)
    {
        if (rect.Width <= 0f || rect.Height <= 0f) return;

        var clamped = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        var radius = rect.Height / 2f;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        paint.Color = track;
        canvas.DrawRoundRect(rect, radius, radius, paint);

        if (clamped <= 0f) return;

        // Le remplissage est rogné par la piste : extrémités arrondies même pour une fraction faible.
        canvas.Save();
        using (var rounded = new SKRoundRect(rect, radius, radius))
        {
            canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, antialias: true);
        }
        paint.Color = fill;
        canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * clamped, rect.Bottom), paint);
        canvas.Restore();
    }

    /// <summary>
    /// Rectangle de la barre dans le style du thème. <see cref="ThemeShape.Rounded"/> : <paramref name="rect"/> tel quel.
    /// <see cref="ThemeShape.Pixel"/> : 1,5 × sa hauteur arrondie à l'entier (8 → 12), centrée sur la même ligne.
    /// </summary>
    public static SKRect BarRect(SKRect rect, Theme theme)
    {
        if (theme.Shape != ThemeShape.Pixel || rect.Height <= 0f) return rect;

        var height = MathF.Round(rect.Height * 1.5f, MidpointRounding.AwayFromZero);
        return new SKRect(rect.Left, rect.MidY - height / 2f, rect.Right, rect.MidY + height / 2f);
    }

    /// <summary>
    /// Barre dans le style du thème, <see cref="Theme.Track"/> pour la piste. <see cref="ThemeShape.Rounded"/> : <see cref="DrawBar(SKCanvas, SKRect, float, SKColor, SKColor)"/>.
    /// <see cref="ThemeShape.Pixel"/> : angles vifs, segments pleins de 0,75 × hauteur séparés de 0,25 × hauteur
    /// (autant de segments entiers que la largeur en contient, le reste à droite est laissé vide),
    /// <c>round(fraction × nombre)</c> segments remplis (arrondi loin de zéro), sans anticrénelage, coordonnées arrondies au pixel.
    /// </summary>
    public static void DrawBar(SKCanvas canvas, SKRect rect, float fraction, SKColor fill, Theme theme)
    {
        if (theme.Shape != ThemeShape.Pixel)
        {
            DrawBar(canvas, rect, fraction, fill, theme.Track);
            return;
        }

        if (rect.Width <= 0f || rect.Height <= 0f) return;

        var segment = rect.Height * 0.75f;
        var gap = rect.Height * 0.25f;
        var pitch = segment + gap;
        var count = (int)MathF.Floor((rect.Width + gap) / pitch);
        if (count <= 0) return;

        var clamped = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        var filled = (int)MathF.Round(clamped * count, MidpointRounding.AwayFromZero);
        var top = MathF.Round(rect.Top);
        var bottom = MathF.Round(rect.Bottom);

        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        for (var i = 0; i < count; i++)
        {
            var left = MathF.Round(rect.Left + i * pitch);
            var right = MathF.Round(rect.Left + i * pitch + segment);
            if (right <= left || bottom <= top) continue;

            paint.Color = i < filled ? fill : theme.Track;
            canvas.DrawRect(new SKRect(left, top, right, bottom), paint);
        }
    }

    /// <summary>
    /// Marque l'élément choisi d'une liste (un onglet, une ligne) : à appeler pour le rectangle <paramref name="rect"/> de cet
    /// élément, en coordonnées de la toile comme tout dessin du module, avant d'y dessiner son texte.
    /// <list type="bullet">
    /// <item>Thèmes Classique, Liquid Glass et Pixel : un aplat <see cref="Theme.Surface"/>, et à gauche un trait de
    /// 3 × <see cref="ModuleRenderContext.TextScale"/> px en <see cref="Theme.Accent"/> (Pixel : 4 × l'échelle, en
    /// <see cref="Theme.Highlight"/>).</item>
    /// <item>Thème « Liquid Glass avancé » : une pastille de verre, que le logiciel dessine lui-même sous tout ce que le
    /// module dessine, que ce soit avant ou après cet appel. Hors du logiciel (un banc d'essai sans service), une pilule
    /// claire la remplace.</item>
    /// </list>
    /// </summary>
    public static void DrawSelection(SKCanvas canvas, SKRect rect, ModuleRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(ctx);

        var theme = ctx.Theme;
        if (!theme.ServiceDrawsSelection)
        {
            // L'onglet choisi tel qu'il a toujours été dessiné : sans anticrénelage, au pixel près.
            var pixel = theme.Shape == ThemeShape.Pixel;
            using var paint = new SKPaint { Color = theme.Surface, Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, paint);

            paint.Color = pixel ? theme.Highlight : theme.Accent;
            canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + (pixel ? 4f : 3f) * ctx.TextScale, rect.Bottom), paint);
            return;
        }

        if (ctx.Selections is { } selections)
        {
            rect.Offset(-ctx.Bounds.Left, -ctx.Bounds.Top);
            selections.Add(rect);
            return;
        }

        DrawLocalSelection(canvas, rect, theme);
    }

    /// <summary>
    /// La pastille sans verre, quand personne ne peut dessiner la vraie : une pilule <see cref="Theme.Text"/> à 12 %, avec un
    /// trait intérieur de 1,5 px, à 45 % en haut et à 15 % en bas. Rien n'est peint hors de <paramref name="rect"/>.
    /// </summary>
    internal static void DrawLocalSelection(SKCanvas canvas, SKRect rect, Theme theme)
    {
        if (!(rect.Width > 0f) || !(rect.Height > 0f)) return; // vide, à l'envers ou pas un nombre

        const float Stroke = 1.5f;
        var radius = rect.Height / 2f;
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Text.WithAlpha(31) })
        {
            canvas.DrawRoundRect(rect, radius, radius, fill);
        }

        var inner = SKRect.Inflate(rect, -Stroke / 2f, -Stroke / 2f);
        if (inner.Width <= 0f || inner.Height <= 0f) return;

        using var shade = SKShader.CreateLinearGradient(
            new SKPoint(0f, rect.Top), new SKPoint(0f, rect.Bottom),
            [theme.Text.WithAlpha(115), theme.Text.WithAlpha(38)], SKShaderTileMode.Clamp);
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Stroke, Shader = shade };
        var innerRadius = Math.Max(0f, radius - Stroke / 2f);
        canvas.DrawRoundRect(inner, innerRadius, innerRadius, edge);
    }

    /// <summary>
    /// Message d'action (ex. « Réglages requis ») en <see cref="Theme.Warning"/>, centré dans
    /// <paramref name="area"/> sur au plus 3 lignes. Taille 24 × <paramref name="textScale"/>, réduite
    /// par pas de 2 jusqu'à 16 × <paramref name="textScale"/> si le texte ne tient pas.
    /// </summary>
    public static void DrawActionMessage(SKCanvas canvas, SKRect area, Theme theme, float textScale, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || area.Width <= 0f || area.Height <= 0f) return;

        var (size, lines) = LayoutActionMessage(theme, textScale, area.Width, message);
        using var font = Font(theme, size, subpixel: true);
        DrawCentered(canvas, area, theme, font, lines);
    }

    /// <summary>
    /// Choisit la taille et les lignes du message d'action : on part de 24 × <paramref name="textScale"/>
    /// et on réduit par pas de 2 (le dernier pas tombe exactement sur 16 × <paramref name="textScale"/>)
    /// tant que le texte doit être ellipsé sur 3 lignes.
    /// </summary>
    internal static (float Size, IReadOnlyList<string> Lines) LayoutActionMessage(Theme theme, float textScale, float width, string message)
    {
        const int maxLines = 3;
        var minSize = 16f * textScale;
        var size = 24f * textScale;

        while (true)
        {
            using var font = Font(theme, size, subpixel: true);
            var (lines, truncated) = WrapCore(message, font, width, maxLines);
            if (!truncated || size <= minSize) return (size, lines);
            size = Math.Max(size - 2f, minSize);
        }
    }

    private static void DrawCentered(SKCanvas canvas, SKRect area, Theme theme, SKFont font, IReadOnlyList<string> lines)
    {
        var metrics = font.Metrics;
        var lineHeight = font.Spacing;
        var top = area.MidY - lineHeight * lines.Count / 2f;

        using var paint = new SKPaint { Color = theme.Warning, IsAntialias = true };
        canvas.Save();
        canvas.ClipRect(area);
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = top + i * lineHeight - metrics.Ascent;
            DrawText(canvas, lines[i], area.MidX, baseline, SKTextAlign.Center, font, paint);
        }
        canvas.Restore();
    }

    private static (IReadOnlyList<string> Lines, bool Truncated) WrapCore(string text, SKFont font, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        if (maxLines <= 0 || string.IsNullOrWhiteSpace(text)) return (lines, false);

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var truncated = false;
        var current = string.Empty;

        for (var i = 0; i < words.Length; i++)
        {
            var candidate = current.Length == 0 ? words[i] : current + " " + words[i];
            if (MeasureText(candidate, font) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                if (lines.Count == maxLines - 1)
                {
                    // Dernière ligne autorisée : elle absorbe tout le reste du texte et est ellipsée.
                    lines.Add(Ellipsize(string.Join(' ', words[(i - CountWords(current))..]), font, maxWidth));
                    return (lines, true);
                }
                lines.Add(current);
            }

            // Un mot plus large que la ligne est ellipsé sur sa propre ligne.
            if (MeasureText(words[i], font) > maxWidth)
            {
                truncated = true;
                if (lines.Count == maxLines - 1)
                {
                    lines.Add(Ellipsize(string.Join(' ', words[i..]), font, maxWidth));
                    return (lines, true);
                }
                lines.Add(Ellipsize(words[i], font, maxWidth));
                current = string.Empty;
            }
            else
            {
                current = words[i];
            }
        }

        if (current.Length > 0) lines.Add(current);
        return (lines, truncated);
    }

    private static int CountWords(string line)
    {
        var count = 1;
        foreach (var c in line)
        {
            if (c == ' ') count++;
        }
        return count;
    }

    private static string Prefix(Rune[] runes, int count)
    {
        var sb = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++) sb.Append(runes[i].ToString());
        return sb.ToString();
    }

    /// <summary>Découpe le texte en suites de caractères résolus vers la même police.</summary>
    private static List<Run> Split(string text, SKFont font)
    {
        var runs = new List<Run>(1);
        var primary = font.Typeface;
        var sb = new StringBuilder(text.Length);
        SKTypeface? current = null;

        foreach (var rune in text.EnumerateRunes())
        {
            var typeface = Resolve(rune.Value, primary);
            if (current is not null && !ReferenceEquals(typeface, current))
            {
                runs.Add(new Run(sb.ToString(), current));
                sb.Clear();
            }
            current = typeface;
            sb.Append(rune.ToString());
        }

        if (current is not null) runs.Add(new Run(sb.ToString(), current));
        return runs;
    }

    private static SKTypeface Resolve(int codePoint, SKTypeface primary)
    {
        if (primary.ContainsGlyph(codePoint)) return primary;

        var fallback = FallbackCache.GetOrAdd(
            (primary.FamilyName, codePoint),
            static key => SKFontManager.Default.MatchCharacter(key.CodePoint));
        return fallback ?? primary;
    }

    private static SKFont CloneFont(SKFont source, SKTypeface typeface) => new(typeface, source.Size, source.ScaleX, source.SkewX)
    {
        Edging = source.Edging,
        Subpixel = source.Subpixel,
        Hinting = source.Hinting,
        LinearMetrics = source.LinearMetrics,
        Embolden = source.Embolden,
    };
}
