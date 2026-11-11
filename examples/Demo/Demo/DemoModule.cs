using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Demo;

/// <summary>
/// Un module sans réseau ni permission, qui s'anime : une rangée de cases dont une s'allume et avance de 12 pas par seconde, aux couleurs et à
/// la forme du thème (arrondie ou carrée). Il sert à essayer <see cref="IsAnimating"/>, le plafond d'images par seconde de l'écran et la consommation.
/// <para>
/// Le module ne choisit pas la cadence : tant que <see cref="IsAnimating"/> est vrai, le logiciel redemande une image dès qu'il le peut, dans la
/// limite du plafond. L'animation, elle, dépend de l'heure (<see cref="ModuleRenderContext.Now"/>), pas du nombre d'images dessinées : elle
/// avance de 12 pas par seconde que le plafond soit de 5 ou de 30 images.
/// </para>
/// </summary>
public sealed class DemoModule : CommunityModule
{
    private const int StepsPerSecond = 12;
    private const int Cells = 12;

    private bool _animate = true;

    /// <summary>
    /// Vrai tant que le réglage « Animer » est coché. Décoché, le module n'est plus redessiné que lorsque le logiciel en a besoin
    /// (changement de thème, de taille…) : c'est ce qui permet de voir ce que l'animation coûte.
    /// </summary>
    public override bool IsAnimating => _animate;

    /// <summary>Lit le réglage « Animer » (coché par défaut).</summary>
    public override Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        if (settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("animate", out var animate)
            && animate.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _animate = animate.GetBoolean();
        }

        return Task.CompletedTask;
    }

    /// <summary>Dessine une phrase puis la rangée de cases : la case allumée et sa traînée avancent d'un cran à chaque douzième de seconde.</summary>
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        var theme = ctx.Theme;
        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -16f * scale, -12f * scale);

        using var font = ModuleDrawing.Font(theme, 18f * scale);
        using var textPaint = new SKPaint { Color = theme.TextMuted, IsAntialias = true };
        var baseline = area.Top - font.Metrics.Ascent;
        var sentence = _animate ? $"Animation à {StepsPerSecond} images par seconde" : "Animation arrêtée";
        ModuleDrawing.DrawText(canvas, ModuleDrawing.Ellipsize(sentence, font, area.Width), area.Left, baseline, SKTextAlign.Left, font, textPaint);

        // Le reste de la case reçoit la rangée, centrée en hauteur.
        var track = new SKRect(area.Left, baseline + font.Metrics.Descent + 8f * scale, area.Right, area.Bottom);
        var gap = 6f * scale;
        var cellWidth = (track.Width - gap * (Cells - 1)) / Cells;
        var cellHeight = Math.Min(track.Height, cellWidth * 1.5f);
        if (cellWidth <= 0f || cellHeight <= 0f)
        {
            return;
        }

        // Le pas ne dépend que de l'heure : deux images dessinées dans le même douzième de seconde sont identiques. Sans animation, la tête reste en place.
        var step = ctx.Now.ToUnixTimeMilliseconds() * StepsPerSecond / 1000;
        var head = _animate ? (int)(step % Cells) : 0;
        var top = track.MidY - cellHeight / 2f;
        var radius = theme.Shape == ThemeShape.Pixel ? 0f : cellWidth * 0.3f;

        // La tête a la couleur d'accent du thème, suivie d'une traînée qui s'éteint ; les autres cases ont la couleur de piste.
        byte[] trail = [255, 150, 80, 40];
        using var paint = new SKPaint { IsAntialias = true };
        for (var cell = 0; cell < Cells; cell++)
        {
            var behind = (head - cell + Cells) % Cells;
            paint.Color = behind < trail.Length ? theme.Accent.WithAlpha(trail[behind]) : theme.Track;
            var left = track.Left + cell * (cellWidth + gap);
            canvas.DrawRoundRect(new SKRect(left, top, left + cellWidth, top + cellHeight), radius, radius, paint);
        }
    }
}
