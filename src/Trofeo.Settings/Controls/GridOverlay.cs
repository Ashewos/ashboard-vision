using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Trofeo.Core.Layout;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Controls;

/// <summary>
/// Le damier, posé sur la bande (même cellule de la grille, même taille) : les cases en pointillés <c>Line</c> avec les
/// écarts du thème, le module choisi cerné d'un trait <c>Accent</c> de 2 px et de huit poignées, et pendant un glissement
/// le cadre fantôme calé sur les cases (<c>Accent</c> si la place est libre, <c>Refuse</c> sinon), et, sur la case d'un module de la
/// communauté, un point d'exclamation jaune dans un angle. Il ne décide de rien :
/// il traduit la souris en pixels de l'écran pour <see cref="LayoutTabViewModel"/>, qui applique les règles de placement.
/// Échap abandonne le glissement.
/// </summary>
public sealed class GridOverlay : Avalonia.Controls.Control
{
    public static readonly StyledProperty<ScreenStrip?> StripProperty =
        AvaloniaProperty.Register<GridOverlay, ScreenStrip?>(nameof(Strip));

    public static readonly StyledProperty<LayoutTabViewModel?> ModelProperty =
        AvaloniaProperty.Register<GridOverlay, LayoutTabViewModel?>(nameof(Model));

    private const double HandleSize = 10;
    private const double HandleHit = 9;      // demi-côté de la zone qui attrape une poignée, un peu plus large que son dessin
    private const double CellRadius = 4;
    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = [];

    private IPointer? _pointer;

    static GridOverlay()
    {
        AffectsRender<GridOverlay>(StripProperty, ModelProperty);
        // Le damier disparaît (autre onglet, service arrêté) : un glissement en cours est abandonné.
        IsVisibleProperty.Changed.AddClassHandler<GridOverlay>((overlay, change) =>
        {
            if (change.NewValue is false)
                overlay.Model?.CancelDrag();
        });
        ModelProperty.Changed.AddClassHandler<GridOverlay>((overlay, change) =>
        {
            if (change.OldValue is LayoutTabViewModel old)
                old.PropertyChanged -= overlay.OnModelChanged;
            if (change.NewValue is LayoutTabViewModel model)
                model.PropertyChanged += overlay.OnModelChanged;
        });
    }

    public GridOverlay()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>La bande sous le damier : elle donne l'échelle entre l'écran (1920 × 462) et ce qu'on voit.</summary>
    public ScreenStrip? Strip
    {
        get => GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    public LayoutTabViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutTabViewModel.CanEdit) && Model is { CanEdit: false } model)
            model.CancelDrag();
        InvalidateVisual();
    }

    /// <summary>La case sous un point de la bande (coordonnées du contrôle), selon les écarts du thème.</summary>
    public (int Column, int Row) CellAt(Point stripPoint)
    {
        var screen = Strip?.StripToScreen(stripPoint) ?? stripPoint;
        return GridGeometry.CellAt((float)screen.X, (float)screen.Y, Model?.Spacing ?? GridSpacing.Default);
    }

    // ---- Dessin ----

    private IBrush Resource(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);

    public override void Render(DrawingContext context)
    {
        // Transparent mais présent : sans fond, la souris traverserait le damier.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Strip is not { } strip || Model is not { } model || strip.ContentRect.Width <= 0)
            return;

        var spacing = model.Spacing;
        var line = Resource("Line", Color.Parse("#262C37"));
        var accent = Resource("Accent", Color.Parse("#7FD1C7"));
        var refuse = Resource("Refuse", Color.Parse("#E5604F"));
        var ink = Resource("Ink", Color.Parse("#0E1116"));
        var radius = CellRadius;

        // Les cases : des pointillés Line, aux écarts du thème.
        var dashed = new Pen(line, 1, new DashStyle([3, 3], 0));
        for (var column = 0; column < GridGeometry.Columns; column++)
            for (var row = 0; row < GridGeometry.Rows; row++)
                context.DrawRectangle(null, dashed, CellRect(strip, column, row, 1, 1, spacing).Deflate(0.5), radius, radius);

        // Le module choisi : un contour Accent de 2 px et huit poignées.
        if (model.SelectedId is { } id && model.Placed.FirstOrDefault(p => p.Id == id) is { } selected)
        {
            var rect = CellRect(strip, selected.Column, selected.Row, selected.Columns, selected.Rows, spacing);
            context.DrawRectangle(null, new Pen(accent, 2), rect, radius, radius);
            var handleBorder = new Pen(ink, 1);
            foreach (var (_, at) in Handles(rect))
                context.DrawRectangle(accent, handleBorder, new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize), 2, 2);
        }

        // Un module de la communauté : juste un point d'exclamation jaune dans l'angle de sa case, rien d'autre.
        var amberBrush = Resource("Amber", Color.Parse("#F2B84B"));
        foreach (var community in model.Placed.Where(p => p.IsCommunity))
        {
            var cell = CellRect(strip, community.Column, community.Row, community.Columns, community.Rows, spacing);
            CommunityBadge.Draw(context, BadgeCenter(cell), BadgeDiameter, amberBrush, ink);
        }

        // Le cadre fantôme du glissement.
        if (model.Ghost is { } ghost)
        {
            var rect = CellRect(strip, ghost.Column, ghost.Row, ghost.Columns, ghost.Rows, spacing);
            var color = ghost.Ok ? accent : refuse;
            var fill = color is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.16) : null;
            context.DrawRectangle(fill, new Pen(color, 2), rect, radius, radius);
        }

    }

    /// <summary>Le diamètre du point d'exclamation d'une case, en pixels de la bande.</summary>
    internal const double BadgeDiameter = 16;

    /// <summary>Le centre du point d'exclamation d'une case : dans son angle en haut à droite.</summary>
    internal static Point BadgeCenter(Rect cell) => new(cell.Right - BadgeDiameter / 2 - 6, cell.Top + BadgeDiameter / 2 + 6);

    private static Rect CellRect(ScreenStrip strip, int column, int row, int columns, int rows, GridSpacing spacing)
    {
        var px = GridGeometry.ModuleRect(column, row, columns, rows, spacing);
        return strip.ScreenToStrip(new Rect(px.Left, px.Top, px.Width, px.Height));
    }

    private static IEnumerable<(ResizeEdge Edge, Point At)> Handles(Rect rect)
    {
        var cx = rect.Center.X;
        var cy = rect.Center.Y;
        yield return (ResizeEdge.TopLeft, new Point(rect.Left, rect.Top));
        yield return (ResizeEdge.Top, new Point(cx, rect.Top));
        yield return (ResizeEdge.TopRight, new Point(rect.Right, rect.Top));
        yield return (ResizeEdge.Right, new Point(rect.Right, cy));
        yield return (ResizeEdge.BottomRight, new Point(rect.Right, rect.Bottom));
        yield return (ResizeEdge.Bottom, new Point(cx, rect.Bottom));
        yield return (ResizeEdge.BottomLeft, new Point(rect.Left, rect.Bottom));
        yield return (ResizeEdge.Left, new Point(rect.Left, cy));
    }

    // ---- Souris et clavier ----

    private ResizeEdge? HandleAt(ScreenStrip strip, LayoutTabViewModel model, Point stripPoint)
    {
        if (model.SelectedId is not { } id || model.Placed.FirstOrDefault(p => p.Id == id) is not { } selected)
            return null;
        var rect = CellRect(strip, selected.Column, selected.Row, selected.Columns, selected.Rows, model.Spacing);
        foreach (var (edge, at) in Handles(rect))
            if (Math.Abs(stripPoint.X - at.X) <= HandleHit && Math.Abs(stripPoint.Y - at.Y) <= HandleHit)
                return edge;
        return null;
    }

    private PlacedModuleItem? ModuleAt(LayoutTabViewModel model, Point screen)
    {
        var spacing = model.Spacing;
        foreach (var item in model.Placed)
        {
            var rect = GridGeometry.ModuleRect(item.Column, item.Row, item.Columns, item.Rows, spacing);
            if (screen.X >= rect.Left && screen.X < rect.Right && screen.Y >= rect.Top && screen.Y < rect.Bottom)
                return item;
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Seul le bouton gauche commence un glissement, et pas pendant un autre.
        if (Strip is not { } strip || Model is not { } model || model.IsDragging
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        Focus();
        var point = e.GetPosition(strip);
        if (HandleAt(strip, model, point) is { } edge)
        {
            model.BeginResize(model.SelectedId!.Value, edge);
        }
        else if (ModuleAt(model, strip.StripToScreen(point)) is { } module)
        {
            model.Select(module.Id);
            model.BeginMove(module.Id, strip.StripToScreen(point));
        }
        else
        {
            model.Select(null); // un clic sur une case vide
        }

        if (model.IsDragging)
        {
            _pointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Strip is not { } strip || Model is not { } model)
            return;

        var point = e.GetPosition(strip);
        if (model.IsDragging)
        {
            model.UpdateDrag(strip.StripToScreen(point));
            return;
        }

        SetCursor(HandleAt(strip, model, point) is { } edge ? CursorOf(edge)
            : ModuleAt(model, strip.StripToScreen(point)) is not null ? StandardCursorType.SizeAll
            : StandardCursorType.Arrow);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased
            || Model is not { IsDragging: true } model)
            return;

        _ = model.EndDragAsync(); // enregistre si le cadre est libre ; EndDragAsync ne laisse plus de glissement dès son premier pas
        ReleasePointer();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointer = null;
        Model?.CancelDrag(); // la souris a été reprise (autre fenêtre, ...) : le glissement est abandonné
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && Model is { IsDragging: true } model)
        {
            model.CancelDrag();
            ReleasePointer();
            e.Handled = true;
        }
    }

    private void ReleasePointer()
    {
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
    }

    private void SetCursor(StandardCursorType type)
    {
        if (!Cursors.TryGetValue(type, out var cursor))
            Cursors[type] = cursor = new Cursor(type);
        Cursor = cursor;
    }

    private static StandardCursorType CursorOf(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => StandardCursorType.SizeWestEast,
        ResizeEdge.Top or ResizeEdge.Bottom => StandardCursorType.SizeNorthSouth,
        ResizeEdge.TopLeft => StandardCursorType.TopLeftCorner,
        ResizeEdge.TopRight => StandardCursorType.TopRightCorner,
        ResizeEdge.BottomLeft => StandardCursorType.BottomLeftCorner,
        _ => StandardCursorType.BottomRightCorner,
    };
}
