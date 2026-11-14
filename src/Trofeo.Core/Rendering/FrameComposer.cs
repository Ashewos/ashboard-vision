using System.Diagnostics;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Rendering;

/// <summary>Compose l'image complète de l'écran à partir de la disposition, des modules, du thème et du fond.</summary>
public sealed class FrameComposer : IDisposable
{
    private const string ErrorText = "Erreur du module";

    private readonly RateLimitedErrorLog _errors;
    private readonly Backdrop _backdrop = new();
    private readonly PanelAlphaSmoother _alpha = new(); // opacité des panneaux sur un fond animé, par rectangle

    // Fond uni de la surcharge sans fond, gardé tant que la couleur du thème ne change pas.
    private ColorBackground? _legacyBackground;
    private SKColor _legacyColor;

    /// <param name="errors">Destination des échecs de rendu ; par défaut le <see cref="Trace"/>, une fois par minute et par message.</param>
    public FrameComposer(RateLimitedErrorLog? errors = null) =>
        _errors = errors ?? new RateLimitedErrorLog(TimeProvider.System);

    /// <summary>Combien de fois le fond flouté du verre a été recalculé (tests).</summary>
    internal int BackdropComputations => _backdrop.Computations;

    /// <summary>
    /// Comme <see cref="Compose(IReadOnlyList{ModulePlacement}, IReadOnlyDictionary{Guid, IModule}, ThemeDefinition, IBackground, DateTimeOffset)"/>
    /// avec le dessin de Classique (panneau mat) pour <paramref name="theme"/> et un fond uni de sa couleur de fond.
    /// </summary>
    public SKBitmap Compose(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        Theme theme,
        DateTimeOffset now)
    {
        if (_legacyBackground is null || _legacyColor != theme.Background)
        {
            _legacyBackground?.Dispose();
            _legacyBackground = new ColorBackground(theme.Background);
            _legacyColor = theme.Background;
        }

        return Compose(placements, modules, Themes.ClassiqueDefinition with { Theme = theme }, _legacyBackground, now);
    }

    /// <summary>
    /// Peint le fond, puis pour chaque case de <paramref name="placements"/> (les cases d'une vue) son panneau (mat, verre ou pixel, selon le thème) et le rendu du
    /// module, rogné à la forme du panneau (1920 × 462).
    /// </summary>
    public SKBitmap Compose(
        IReadOnlyList<ModulePlacement> placements,
        IReadOnlyDictionary<Guid, IModule> modules,
        ThemeDefinition theme,
        IBackground background,
        DateTimeOffset now)
    {
        var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            DrawBackground(canvas, background);

            foreach (var placement in placements)
            {
                var rect = ModulePlace.Rect(placement, theme);
                PaintPanel(canvas, rect, theme, background, now);

                if (!modules.TryGetValue(placement.InstanceId, out var module))
                {
                    continue;
                }

                int saved = canvas.Save();
                try
                {
                    PanelPainter.ClipToPanel(canvas, rect, theme); // même forme que le panneau
                    module.Render(canvas, ModulePlace.Context(placement, theme, now));
                }
                catch (Exception ex)
                {
                    _errors.Report($"Échec du rendu du module {placement.TypeId} ({placement.InstanceId}) : {ex}");
                    canvas.RestoreToCount(saved);
                    PaintErrorPanel(canvas, rect, theme, background, now);
                }
                finally
                {
                    canvas.RestoreToCount(saved);
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose(); // pas de bitmap orphelin de 3,5 Mo si la composition échoue
            throw;
        }
    }

    public void Dispose()
    {
        _backdrop.Dispose();
        _legacyBackground?.Dispose();
        _legacyBackground = null;
    }

    // L'image courante du fond n'est jamais gardée : elle n'est valable que pour cette image de l'écran.
    private static void DrawBackground(SKCanvas canvas, IBackground background)
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(background.Current, 0, 0, paint);
    }

    /// <summary>
    /// Peint le panneau. Sur un fond fixe, l'opacité est celle que la garde de lisibilité veut tout de suite. Sur un fond animé,
    /// elle est lissée dans le temps (<see cref="PanelAlphaSmoother"/>) : une tache claire qui passe sous le panneau ne le fait
    /// pas clignoter. Garde coupée : l'opacité choisie, sans mesure ni lissage.
    /// </summary>
    private void PaintPanel(SKCanvas canvas, SKRect rect, ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        var blurred = theme.Panel.Kind == PanelKind.Glass ? _backdrop.Get(background, theme.Panel) : null;
        if (!theme.LegibilityGuard)
        {
            PanelPainter.Paint(canvas, rect, theme, blurred, background.Current);
            return;
        }

        float target = PanelPainter.TargetAlpha(theme, _backdrop.BrightestLuminance(background, rect));
        float alpha = background.IsAnimated ? _alpha.Smooth(rect, target, now) : target;
        PanelPainter.Paint(canvas, rect, theme, blurred, background.Current, alpha: alpha);
    }

    /// <summary>Efface ce que le module a pu peindre avant d'échouer, puis affiche le message d'erreur.</summary>
    private void PaintErrorPanel(SKCanvas canvas, SKRect rect, ThemeDefinition theme, IBackground background, DateTimeOffset now)
    {
        canvas.Save();
        PanelPainter.ClipToPanel(canvas, rect, theme); // le module n'a rien pu peindre au-delà : l'ombre et la bordure restent
        DrawBackground(canvas, background);
        PaintPanel(canvas, rect, theme, background, now);

        using var font = ModuleDrawing.Font(theme.Theme, 24f);
        float maxWidth = rect.Width - 16f;
        float width = font.MeasureText(ErrorText);
        if (width > maxWidth && maxWidth > 0)
        {
            font.Size = font.Size * maxWidth / width;
        }

        using var paint = new SKPaint { IsAntialias = true, Color = theme.Theme.Warning };
        float baseline = rect.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2f;
        canvas.DrawText(ErrorText, rect.MidX, baseline, SKTextAlign.Center, font, paint);
        canvas.Restore();
    }
}
