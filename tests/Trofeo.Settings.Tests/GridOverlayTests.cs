using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Controls;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>Le damier posé sur la bande : calage à l'échelle de l'aperçu, souris, clavier, couleurs.</summary>
public sealed class GridOverlayTests : IDisposable
{
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private Window? _window;

    public GridOverlayTests()
    {
        Directory.CreateDirectory(_dir);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose()
    {
        _window?.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private static ModulePlacement Placement(Guid id, string typeId, int column, int row, int columns, int rows) =>
        new(id, typeId, column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private LayoutDocument Document(params ModulePlacement[] placements)
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        store.Save(DashboardLayout.SingleView(180, placements));
        var document = new LayoutDocument(store, _link);
        document.Load();
        return document;
    }

    /// <summary>Une bande de 1152 px de large (marge de 24 px de chaque côté dans une fenêtre de 1200 px) et son damier.</summary>
    private (ScreenStrip Strip, GridOverlay Overlay, LayoutTabViewModel Tab) Show(LayoutDocument document)
    {
        var tab = new LayoutTabViewModel(_link, new EditSession(document));
        var strip = new ScreenStrip();
        var overlay = new GridOverlay { Strip = strip, Model = tab };
        var grid = new Grid { Margin = new Thickness(24), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        grid.Children.Add(strip);
        grid.Children.Add(overlay);
        _window = new Window { Width = 1200, Height = 500, Content = grid };
        _window.Show();
        return (strip, overlay, tab);
    }

    private static Point Center(int column, int row)
    {
        var rect = GridGeometry.ModuleRect(column, row, 1, 1);
        return new Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0);
    }

    /// <summary>Un point de l'écran (pixels 1920 × 462) en coordonnées de la fenêtre.</summary>
    private Point InWindow(ScreenStrip strip, Point screen) => strip.TranslatePoint(strip.ScreenToStrip(screen), _window!)!.Value;

    [AvaloniaFact]
    public void Ghost_SnapsToCells_AtPreviewScale()
    {
        var (strip, overlay, _) = Show(Document(Placement(ClockId, "clock", 12, 0, 4, 2)));

        Assert.Equal(1152, strip.Bounds.Width);
        Assert.InRange(strip.ContentRect.Width, 1149, 1151);

        // Le centre de chaque case, vu à l'échelle de la bande, retombe sur sa case.
        for (var column = 0; column < GridGeometry.Columns; column++)
            for (var row = 0; row < GridGeometry.Rows; row++)
                Assert.Equal((column, row), overlay.CellAt(strip.ScreenToStrip(Center(column, row))));

        // Quelques pixels de part et d'autre du bord entre les colonnes 7 et 8 (au milieu de l'écart).
        var edge = GridGeometry.ModuleRect(7, 0, 1, 1).Right + GridGeometry.Gap / 2.0;
        Assert.Equal(7, overlay.CellAt(strip.ScreenToStrip(new Point(edge - 4, 100))).Column);
        Assert.Equal(8, overlay.CellAt(strip.ScreenToStrip(new Point(edge + 4, 100))).Column);
    }

    [AvaloniaFact]
    public void Pointer_DragBody_MovesTheGhostCellByCell_AndSavesOnRelease()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        Assert.True(tab.IsDragging);
        Assert.Equal(ClockId, tab.SelectedId);

        _window!.MouseMove(InWindow(strip, Center(13, 2)));
        Assert.Equal((12, 2), (tab.Ghost!.Column, tab.Ghost.Row));
        Assert.True(tab.Ghost.Ok);

        _window!.MouseUp(InWindow(strip, Center(13, 2)), MouseButton.Left);

        Assert.False(tab.IsDragging);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(2, tab.Placed.Single(p => p.Id == ClockId).Row);
    }

    [AvaloniaFact]
    public void Pointer_DragHandle_ResizesTheModule()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 8, 0, 4, 2)));
        tab.Select(ClockId);

        // Le coin bas-droite du module (bord droit 12, bord bas 2), puis le bord 14 / 3.
        var corner = GridGeometry.ModuleRect(8, 0, 4, 2);
        var start = new Point(corner.Right, corner.Bottom);
        var edgeX = GridGeometry.ModuleRect(13, 0, 1, 1).Right + GridGeometry.Gap / 2.0;
        var edgeY = GridGeometry.ModuleRect(0, 2, 1, 1).Bottom + GridGeometry.Gap / 2.0;
        _window!.MouseDown(InWindow(strip, start), MouseButton.Left);
        _window!.MouseMove(InWindow(strip, new Point(edgeX, edgeY)));

        Assert.Equal((8, 0, 6, 3), (tab.Ghost!.Column, tab.Ghost.Row, tab.Ghost.Columns, tab.Ghost.Rows));

        _window!.MouseUp(InWindow(strip, new Point(edgeX, edgeY)), MouseButton.Left);
        Assert.Equal(6, tab.Placed.Single(p => p.Id == ClockId).Columns);
    }

    [AvaloniaFact]
    public void Pointer_ClickOnEmptyCell_Deselects()
    {
        var (strip, _, tab) = Show(Document(Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.Select(ClockId);

        _window!.MouseDown(InWindow(strip, Center(2, 2)), MouseButton.Left);
        _window!.MouseUp(InWindow(strip, Center(2, 2)), MouseButton.Left);

        Assert.Null(tab.SelectedId);
        Assert.False(tab.IsDragging);
        Assert.Equal(0, _link.ReloadCount);
    }

    [AvaloniaFact]
    public void Pointer_ClickOnModule_SelectsIt_WithoutSaving()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        _window!.MouseUp(InWindow(strip, Center(13, 0)), MouseButton.Left);

        Assert.Equal(ClockId, tab.SelectedId);
        Assert.Equal(0, _link.ReloadCount);
    }

    [AvaloniaFact]
    public void Escape_CancelsDrag()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        _window!.MouseMove(InWindow(strip, Center(13, 2)));
        Assert.NotNull(tab.Ghost);

        _window!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);

        // Le lâcher qui suit n'enregistre rien.
        _window!.MouseUp(InWindow(strip, Center(13, 2)), MouseButton.Left);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal(0, tab.Placed.Single(p => p.Id == ClockId).Row);
    }

    [AvaloniaFact]
    public void Escape_InTheMainWindow_CancelsDrag()
    {
        var document = Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2));
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model };
        _window = window;
        window.Show();
        var tab = (LayoutTabViewModel)model.Tabs[0];
        var strip = window.GetVisualDescendants().OfType<ScreenStrip>().Single();

        window.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        window.MouseMove(InWindow(strip, Center(13, 2)));
        Assert.True(tab.IsDragging);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(tab.IsDragging);
    }

    [AvaloniaFact]
    public void CtrlZ_AndCtrlY_UndoAndRedoInTheMainWindow()
    {
        var document = Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2));
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model };
        _window = window;
        window.Show();
        var tab = (LayoutTabViewModel)model.Tabs[0];
        var strip = window.GetVisualDescendants().OfType<ScreenStrip>().Single();

        window.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        window.MouseMove(InWindow(strip, Center(13, 2)));
        window.MouseUp(InWindow(strip, Center(13, 2)), MouseButton.Left);
        Assert.Equal(2, tab.Placed.Single(p => p.Id == ClockId).Row);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Assert.Equal(0, tab.Placed.Single(p => p.Id == ClockId).Row);
        Assert.Equal(2, _link.ReloadCount);  // l'annulation est enregistrée

        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
        Assert.Equal(2, tab.Placed.Single(p => p.Id == ClockId).Row);
        Assert.Equal(3, _link.ReloadCount);
    }

    [AvaloniaFact]
    public void Pointer_RightButton_DoesNotStartADrag()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Right);

        Assert.False(tab.IsDragging);
        Assert.Null(tab.SelectedId);
        _window!.MouseUp(InWindow(strip, Center(13, 0)), MouseButton.Right);
    }

    [AvaloniaFact]
    public void Pointer_SecondButtonDuringDrag_IsIgnored()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        _window!.MouseMove(InWindow(strip, Center(13, 2)));
        var ghost = tab.Ghost;

        // Un appui du bouton droit pendant le glissement ne démarre rien et ne change pas le cadre.
        _window!.MouseDown(InWindow(strip, Center(2, 3)), MouseButton.Right);

        Assert.True(tab.IsDragging);
        Assert.Equal(ghost, tab.Ghost);
        Assert.Equal(ClockId, tab.SelectedId);
        Assert.Equal(0, _link.ReloadCount);
    }

    [AvaloniaFact]
    public void HidingTheGrid_DropsTheDrag()
    {
        var (strip, overlay, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        _window!.MouseDown(InWindow(strip, Center(13, 0)), MouseButton.Left);
        _window!.MouseMove(InWindow(strip, Center(13, 2)));
        Assert.True(tab.IsDragging);

        overlay.IsVisible = false;

        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);
        Assert.Equal(0, _link.ReloadCount);
    }

    [AvaloniaFact]
    public void CtrlZ_WorksOnEveryTab_AndUndoesTheLastEditWhicheverTabMadeIt()
    {
        var document = Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2));
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model };
        _window = window;
        window.Show();

        model.SelectedTab = model.Tabs[1];  // un autre onglet que Disposition
        _ = model.Session.ApplyAsync(layout => layout with { Rotation = 0 });
        Assert.Equal(0, document.Current.Rotation);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Assert.Equal(180, document.Current.Rotation);

        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
        Assert.Equal(0, document.Current.Rotation);
    }

    private static (byte R, byte G, byte B) Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (bytes[2], bytes[1], bytes[0]) : (bytes[0], bytes[1], bytes[2]);
    }

    private static bool Is(Color color, (byte R, byte G, byte B) pixel) =>
        Math.Abs(pixel.R - color.R) < 14 && Math.Abs(pixel.G - color.G) < 14 && Math.Abs(pixel.B - color.B) < 14;

    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Avalonia.Styling.ThemeVariant.Dark, out var value), key);
        return (Color)value!;
    }

    [AvaloniaFact]
    public void Overlay_SelectedModule_HasATwoPixelAccentOutline()
    {
        var (strip, _, tab) = Show(Document(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.Select(ClockId);
        var frame = _window!.CaptureRenderedFrame()!;

        // Le milieu du bord haut du module, à l'échelle de la bande : un trait de 2 px d'Accent.
        var module = GridGeometry.ModuleRect(12, 0, 4, 2);
        var top = InWindow(strip, new Point((module.Left + module.Right) / 2.0 + 30, module.Top));
        var accent = Token("AccentColor");
        var x = (int)Math.Round(top.X);
        var y = (int)Math.Round(top.Y);
        Assert.True(Enumerable.Range(y - 1, 3).Any(row => Is(accent, Pixel(frame, x, row))), "contour Accent attendu au bord haut");
        Assert.False(Is(accent, Pixel(frame, x, y - 5)));
        Assert.False(Is(accent, Pixel(frame, x, y + 5)));
    }

    [AvaloniaFact]
    public void Overlay_Ghost_IsAccentWhenFree_AndRefuseWhenTaken()
    {
        var (strip, _, tab) = Show(Document(
            Placement(MonitoringId, "monitoring", 8, 0, 4, 2), Placement(ClockId, "clock", 12, 0, 4, 2)));

        bool GhostEdgeIs(Color color)
        {
            var frame = _window!.CaptureRenderedFrame()!;
            var g = tab.Ghost!;
            var rect = GridGeometry.ModuleRect(g.Column, g.Row, g.Columns, g.Rows);
            // Le bord bas du fantôme, à l'échelle de la bande.
            var point = InWindow(strip, new Point((rect.Left + rect.Right) / 2.0 + 40, rect.Bottom));
            var x = (int)Math.Round(point.X);
            var y = (int)Math.Round(point.Y);
            return Enumerable.Range(y - 2, 5).Any(row => Is(color, Pixel(frame, x, row)));
        }

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));
        Assert.True(GhostEdgeIs(Token("AccentColor")), "fantôme libre en Accent");

        tab.UpdateDrag(Center(10, 0));  // sur Monitoring PC
        Assert.False(tab.Ghost!.Ok);
        Assert.True(GhostEdgeIs(Token("RefuseColor")), "fantôme refusé en Refuse");
    }
}
