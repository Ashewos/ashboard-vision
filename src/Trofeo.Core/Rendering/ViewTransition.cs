using SkiaSharp;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Le glissement d'une vue à l'autre : la vue d'arrivée entre par le côté de <paramref name="direction"/> pendant que l'ancienne
/// sort de l'autre, avec une courbe douce et un flou de mouvement horizontal. L'ancienne image (<paramref name="from"/>) est
/// celle qui est encore à l'écran ; la nouvelle (<c>to</c> de <see cref="Compose"/>) est composée une fois par l'appelant, puis
/// seulement décalée et floutée à chaque image : aucun module n'est redessiné pendant le glissement. L'objet ne possède ni l'une
/// ni l'autre des images, et ne retient rien d'une image à l'autre.
/// </summary>
/// <param name="from">L'image de départ (celle de l'ancienne vue, ou la dernière image du glissement abandonné).</param>
/// <param name="direction">+1 : la vue d'arrivée entre par la droite ; −1 : par la gauche. 0 vaut +1.</param>
/// <param name="duration">La durée du glissement, <see cref="Duration"/> d'ordinaire.</param>
public sealed class ViewTransition(SKBitmap from, int direction, TimeSpan duration)
{
    /// <summary>La durée d'un glissement.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    /// <summary>L'écart-type du flou, en pixels, quand les vues vont le plus vite (au milieu du glissement) ; à régler sur l'écran.</summary>
    public const float MaxBlurSigma = 12f;

    /// <summary>En dessous, un flou ne se voit pas : on dessine net.</summary>
    private const float MinVisibleSigma = 0.5f;

    private readonly int _direction = direction >= 0 ? 1 : -1;

    /// <summary>Vrai quand le glissement a duré <see cref="Duration"/> : l'image suivante est celle de la vue d'arrivée, nette.</summary>
    public bool IsDone(TimeSpan elapsed) => elapsed >= duration;

    /// <summary>
    /// L'image au temps <paramref name="elapsed"/> (neuve, à libérer par l'appelant) : au départ l'ancienne vue nette, à l'arrivée la
    /// nouvelle vue nette. <paramref name="to"/> a la taille de <c>from</c>. Avec <paramref name="gpu"/>, le glissement et son flou
    /// se dessinent sur la carte, puis l'image est rapatriée ; si la carte est perdue (ou refuse une surface), l'image sort quand
    /// même, dessinée par le processeur : l'appelant voit <see cref="IGpuDevice.IsLost"/> et décide de la suite.
    /// </summary>
    public SKBitmap Compose(SKBitmap to, TimeSpan elapsed, IGpuDevice? gpu = null)
    {
        ArgumentNullException.ThrowIfNull(to);

        var progress = Progress(elapsed);
        var width = to.Width;
        var shift = (float)Math.Round(width * Ease(progress));
        var sigma = BlurSigma(progress);

        if (gpu is not null && !gpu.IsLost)
        {
            var onGpu = TryComposeOnGpu(to, shift, width, sigma, gpu);
            if (onGpu is not null) return onGpu;
        }

        var result = new SKBitmap(to.Width, to.Height);
        try
        {
            using var canvas = new SKCanvas(result);
            Draw(canvas, to, shift, width, sigma);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Le glissement dessiné sur une surface de la carte, puis relu. <c>null</c> si la carte est perdue ou refuse la surface.</summary>
    private SKBitmap? TryComposeOnGpu(SKBitmap to, float shift, int width, float sigma, IGpuDevice gpu)
    {
        SKBitmap? result = null;
        try
        {
            var info = new SKImageInfo(to.Width, to.Height);
            using var surface = gpu.CreateSurface(info);
            Draw(surface.Canvas, to, shift, width, sigma);
            gpu.Submit();
            if (gpu.IsLost) return null;

            result = new SKBitmap(info);
            if (surface.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0) && !gpu.IsLost) return result;

            result.Dispose();
            return null;
        }
        catch (GpuLostException)
        {
            result?.Dispose();
            return null;
        }
        catch (Exception) when (gpu.IsLost)
        {
            result?.Dispose();
            return null;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    private void Draw(SKCanvas canvas, SKBitmap to, float shift, int width, float sigma)
    {
        canvas.Clear(SKColors.Transparent);
        if (sigma < MinVisibleSigma)
        {
            DrawBoth(canvas, to, shift, width);
            return;
        }

        // Les deux vues sont dessinées ensemble puis floutées d'un bloc : un flou par vue laisserait une raie sombre à la jonction
        // (chacune s'effacerait sur son bord). Les bords de l'écran se prolongent (Clamp) au lieu de s'éteindre.
        using var filter = SKImageFilter.CreateBlur(sigma, 0f, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.SaveLayer(paint);
        DrawBoth(canvas, to, shift, width);
        canvas.Restore();
    }

    private void DrawBoth(SKCanvas canvas, SKBitmap to, float shift, int width)
    {
        canvas.DrawBitmap(from, -_direction * shift, 0f);
        canvas.DrawBitmap(to, _direction * (width - shift), 0f);
    }

    private double Progress(TimeSpan elapsed) =>
        duration <= TimeSpan.Zero ? 1.0 : Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);

    /// <summary>La courbe douce (lente au départ et à l'arrivée) : 0 au départ, 1 à l'arrivée.</summary>
    private static double Ease(double progress) => progress * progress * (3.0 - 2.0 * progress);

    /// <summary>
    /// L'écart-type du flou à l'avancement <paramref name="progress"/> (0 à 1) : proportionnel à la vitesse de la courbe douce,
    /// donc nul au départ et à l'arrivée, et au plus fort (<see cref="MaxBlurSigma"/>) au milieu.
    /// </summary>
    public static float BlurSigma(double progress)
    {
        var p = Math.Clamp(progress, 0.0, 1.0);
        // La vitesse de p²(3 − 2p) vaut 6p(1 − p), au plus 1,5 au milieu : rapportée à ce maximum, elle vaut 4p(1 − p).
        return (float)(MaxBlurSigma * 4.0 * p * (1.0 - p));
    }
}
