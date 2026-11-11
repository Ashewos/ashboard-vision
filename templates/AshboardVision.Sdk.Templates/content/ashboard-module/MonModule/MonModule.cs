using System.Globalization;
using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace ModuleNamespace;

/// <summary>
/// Ton premier module : il affiche un message (« Bonjour » tant que le réglage « Message » est vide) et l'heure, aux couleurs et
/// dans la police du thème du logiciel. Il ne demande aucune permission.
/// <para>
/// Le logiciel crée une instance par case posée sur le damier : <see cref="StartAsync"/> lit les réglages de cette case, puis
/// <see cref="Render"/> dessine chaque fois qu'une image est nécessaire.
/// </para>
/// </summary>
public sealed class ModuleClassName : CommunityModule
{
    private const string DefaultMessage = "Bonjour";
    private const float Padding = 16f;
    private const float MessageSize = 24f;
    private const float TimeSize = 52f;

    private string _message = DefaultMessage;
    private ITimer? _minuteTimer;

    /// <summary>Lit le réglage « Message » et demande une nouvelle image à chaque changement de minute.</summary>
    public override Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        if (settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            _message = message.GetString()!.Trim();
        }

        // L'heure change une fois par minute : un seul minuteur, réglé sur la prochaine minute pleine. Sans lui, l'image ne serait
        // redessinée que quand le logiciel le décide (changement de thème, de taille…).
        var now = Services.Time.GetUtcNow();
        var untilNextMinute = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromTicks(now.Ticks % TimeSpan.TicksPerSecond);
        _minuteTimer = Services.Time.CreateTimer(_ => Invalidate(), null, untilNextMinute, TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <summary>Dessine le message puis l'heure dans <c>ctx.Bounds</c>, en réduisant le texte pour qu'il tienne dans la case.</summary>
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        var time = TimeZoneInfo.ConvertTime(ctx.Now, Services.Time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

        var scale = ctx.TextScale;
        var area = SKRect.Inflate(ctx.Bounds, -Padding * scale, -Padding * scale);

        // Les deux lignes doivent tenir en hauteur : sinon on réduit les deux d'un même facteur.
        var fit = Math.Min(1f, area.Height / ((MessageSize + TimeSize) * scale * 1.2f));
        if (fit <= 0f || area.Width <= 0f)
        {
            return;
        }

        using var messageFont = ModuleDrawing.Font(ctx.Theme, MessageSize * scale * fit);
        using var timeFont = ModuleDrawing.Font(ctx.Theme, TimeSize * scale * fit);
        using var messagePaint = new SKPaint { Color = ctx.Theme.TextMuted, IsAntialias = true };
        using var timePaint = new SKPaint { Color = ctx.Theme.Highlight, IsAntialias = true };

        var messageBaseline = area.Top - messageFont.Metrics.Ascent;
        var text = ModuleDrawing.Ellipsize(_message, messageFont, area.Width);
        ModuleDrawing.DrawText(canvas, text, area.Left, messageBaseline, SKTextAlign.Left, messageFont, messagePaint);

        var timeBaseline = messageBaseline + messageFont.Metrics.Descent + (timeFont.Size * 0.15f) - timeFont.Metrics.Ascent;
        ModuleDrawing.DrawText(canvas, time, area.Left, timeBaseline, SKTextAlign.Left, timeFont, timePaint);
    }

    /// <summary>Arrête le minuteur quand la case se referme.</summary>
    public override ValueTask DisposeAsync()
    {
        _minuteTimer?.Dispose();
        _minuteTimer = null;
        return ValueTask.CompletedTask;
    }
}
