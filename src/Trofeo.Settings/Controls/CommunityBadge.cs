using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Trofeo.Settings.Controls;

/// <summary>
/// Le point d'exclamation jaune des modules complémentaires : un disque <c>Amber</c> et un point d'exclamation <c>Ink</c> dessiné
/// en formes (pas de police : il se lit net à toute taille). Il est dans la liste des modules à ajouter, dans les listes de modules,
/// dans l'en-tête des réglages d'un module, et — par <see cref="Draw"/> — dans un angle de sa case dans l'éditeur de disposition.
/// </summary>
public sealed class CommunityBadge : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<CommunityBadge, double>(nameof(Size), 16);

    private static readonly Color AmberFallback = Color.Parse("#F2B84B");
    private static readonly Color InkFallback = Color.Parse("#0E1116");

    static CommunityBadge()
    {
        AffectsMeasure<CommunityBadge>(SizeProperty);
        AffectsRender<CommunityBadge>(SizeProperty);
    }

    public CommunityBadge()
    {
        Focusable = false;
        // Le lecteur d'écran et l'infobulle disent ce que le point d'exclamation veut dire.
        AutomationProperties.SetName(this, Texts.CommunityNotice);
        ToolTip.SetTip(this, Texts.CommunityNotice);
    }

    /// <summary>Le diamètre du disque, en pixels.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    private IBrush Resource(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);

    public override void Render(DrawingContext context) =>
        Draw(context, new Point(Size / 2, Size / 2), Size, Resource("Amber", AmberFallback), Resource("Ink", InkFallback));

    /// <summary>Dessine le point d'exclamation jaune : un disque de <paramref name="diameter"/> centré en <paramref name="center"/>.</summary>
    public static void Draw(DrawingContext context, Point center, double diameter, IBrush fill, IBrush ink)
    {
        var radius = diameter / 2;
        context.DrawEllipse(fill, null, center, radius, radius);

        // Le trait du point d'exclamation, puis son point.
        var stemWidth = Math.Max(1.5, diameter * 0.15);
        var stemHeight = diameter * 0.40;
        var top = center.Y - radius + diameter * 0.20;
        context.DrawRectangle(ink, null, new Rect(center.X - stemWidth / 2, top, stemWidth, stemHeight), stemWidth / 2, stemWidth / 2);
        var dot = Math.Max(1.2, diameter * 0.085);
        context.DrawEllipse(ink, null, new Point(center.X, center.Y - radius + diameter * 0.76), dot, dot);
    }
}
