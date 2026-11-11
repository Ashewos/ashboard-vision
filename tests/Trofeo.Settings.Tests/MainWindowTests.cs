using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using SkiaSharp;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-window-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private MainWindow? _window;

    public void Dispose()
    {
        _window?.Close();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private (MainWindow Window, MainViewModel Model) Open()
    {
        Directory.CreateDirectory(_dir);
        var document = new LayoutDocument(new LayoutStore(Path.Combine(_dir, "layout.json")), _link);
        var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        _window = new MainWindow { DataContext = model };
        _window.Show();
        return (_window, model);
    }

    private static IEnumerable<string> VisibleTexts(MainWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!);

    [AvaloniaFact]
    public void MainWindow_ShowsStoppedPanel_WhenServiceStopped()
    {
        _link.Set(LinkState.ServiceStopped);
        var (window, _) = Open();

        var texts = VisibleTexts(window).ToList();
        Assert.Contains(Texts.ServiceStoppedMessage, texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, Texts.StartService));
        Assert.DoesNotContain(Texts.ServiceNotFound, texts);
        // Le panneau remplace le contenu des onglets.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "TabContent" && c.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void MainWindow_VersionMismatch_ShowsItsOwnMessage_WithoutTheUselessStartButton()
    {
        _link.Set(LinkState.VersionMismatch);
        var (window, _) = Open();

        var texts = VisibleTexts(window).ToList();
        Assert.Contains(Texts.VersionMismatchMessage, texts);
        Assert.DoesNotContain(Texts.ServiceStoppedMessage, texts);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, Texts.StartService));
        Assert.Contains(Texts.LinkVersionMismatch, texts); // la pastille aussi le dit
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "TabContent" && c.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task MainWindow_StartFails_ShowsNotFoundMessage()
    {
        _link.StartResult = false;
        _link.Set(LinkState.ServiceStopped);
        var (window, model) = Open();

        await model.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, _link.StartCount);
        Assert.Contains(Texts.ServiceNotFound, VisibleTexts(window));
    }

    [AvaloniaFact]
    public void MainWindow_EmulatedPill_WhenStatusEmulated()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status(LinkScreenState.Disconnected, emulated: true));
        var (window, _) = Open();

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("Mode émulé", texts);
        Assert.DoesNotContain("Écran non branché", texts);
    }

    [AvaloniaTheory]
    [InlineData(LinkScreenState.Ready, "Écran branché")]
    [InlineData(LinkScreenState.Disconnected, "Écran non branché")]
    [InlineData(LinkScreenState.Recovering, "Reprise de l'écran…")]
    public void MainWindow_ScreenPill_FollowsScreenState(LinkScreenState state, string expected)
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status(state));
        var (window, _) = Open();

        Assert.Contains(expected, VisibleTexts(window));
    }

    [AvaloniaFact]
    public void MainWindow_HasFiveTabs_AndKeepsTheSelectedOneAcrossAStop()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, model) = Open();

        var texts = VisibleTexts(window).ToList();
        foreach (var tab in new[] { "Disposition", "Personnalisation", "Modules", "Raccourcis", "Paramètres" })
            Assert.Contains(tab, texts);
        Assert.Equal(5, model.Tabs.Count);

        model.SelectedTab = model.Tabs[2];
        _link.Set(LinkState.Connecting);
        _link.Set(LinkState.ServiceStopped);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());

        Assert.Same(model.Tabs[2], model.SelectedTab);
    }

    [AvaloniaFact]
    public void Preview_PausedWhileMinimized()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, _) = Open();
        Assert.False(_link.Paused);

        window.WindowState = WindowState.Minimized;
        Assert.True(_link.Paused);

        window.WindowState = WindowState.Normal;
        Assert.False(_link.Paused);
    }

    [AvaloniaFact]
    public void MainWindow_Defaults_AreTheSpecifiedSize()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, _) = Open();

        Assert.Equal("Réglages d'Ashboard Vision", window.Title);
        Assert.Equal(1440, window.Width);
        Assert.Equal(900, window.Height);
        Assert.Equal(1200, window.MinWidth);
        Assert.Equal(760, window.MinHeight);
    }

    [AvaloniaFact]
    public void MainWindow_PreviewFrame_ReachesTheStrip()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, model) = Open();
        Assert.Null(model.PreviewImage);
        Assert.Contains(Texts.StripWaiting, VisibleTexts(window));

        _link.Push(new PreviewFrame(SampleData.Jpeg(SKColors.Teal), 180));

        Assert.NotNull(model.PreviewImage);
        Assert.Equal(180, model.PreviewRotation);
        Assert.DoesNotContain(Texts.StripWaiting, VisibleTexts(window));
    }

    [AvaloniaFact]
    public void MainWindow_KeyboardFocus_DrawsATwoPixelAccentOutline()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, _) = Open();
        var tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("tabs"));
        var item = (ListBoxItem)tabs.ContainerFromIndex(1)!;

        item.Focus(Avalonia.Input.NavigationMethod.Tab);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame()!;

        var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
        var y = (int)(origin.Y + item.Bounds.Height / 2);
        var accent = Color("AccentColor");
        bool IsAccent(int x)
        {
            using var buffer = frame.Lock();
            var bytes = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);
            var (r, g, b) = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (bytes[2], bytes[1], bytes[0]) : (bytes[0], bytes[1], bytes[2]);
            return Math.Abs(r - accent.R) < 12 && Math.Abs(g - accent.G) < 12 && Math.Abs(b - accent.B) < 12;
        }

        var left = (int)origin.X;
        Assert.True(IsAccent(left) && IsAccent(left + 1), "contour de 2 px attendu au bord gauche de l'onglet");
        Assert.False(IsAccent(left + 2) || IsAccent(left + 3), "le contour ne fait que 2 px");
    }

    [AvaloniaFact]
    public void Tokens_MutedOnPanel_MeetsContrast()
    {
        _ = Open();
        double Luminance(Avalonia.Media.Color c)
        {
            static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double Ratio(string foreground, string background)
        {
            var a = Luminance(Color(foreground)) + 0.05;
            var b = Luminance(Color(background)) + 0.05;
            return Math.Max(a, b) / Math.Min(a, b);
        }

        Assert.True(Ratio("MutedColor", "PanelColor") >= 4.5, "Muted sur Panel");
        Assert.True(Ratio("MutedColor", "InkColor") >= 4.5, "Muted sur Ink");
        Assert.True(Ratio("TextColor", "PanelColor") >= 7, "Text sur Panel");
        Assert.True(Ratio("InkColor", "AccentColor") >= 7, "Ink sur Accent");
        Assert.True(Ratio("RefuseColor", "PanelColor") >= 4.5, "Refuse sur Panel");
        Assert.True(Ratio("AmberColor", "PanelColor") >= 4.5, "Amber sur Panel");
    }

    private static Avalonia.Media.Color Color(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Avalonia.Styling.ThemeVariant.Dark, out var value), key);
        return (Avalonia.Media.Color)value!;
    }

    [AvaloniaFact]
    public void Tokens_AreTheOnesOfThePlan()
    {
        _ = Open();
        Assert.Equal(Avalonia.Media.Color.Parse("#0E1116"), Color("InkColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#151920"), Color("PanelColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#1C212A"), Color("RaisedColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#262C37"), Color("LineColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#ECEEF0"), Color("TextColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#8C929C"), Color("MutedColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#7FD1C7"), Color("AccentColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#F2B84B"), Color("AmberColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#E5604F"), Color("RefuseColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#6FD08C"), Color("OnlineColor"));
        Assert.Equal(Avalonia.Media.Color.Parse("#5B606D"), Color("IdleColor"));
    }

    [AvaloniaFact]
    public void Fonts_AreEmbedded()
    {
        foreach (var name in new[] { "Manrope-Medium.ttf", "BarlowSemiCondensed-Regular.ttf", "BarlowSemiCondensed-SemiBold.ttf" })
            Assert.True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://Trofeo.Settings/Assets/Fonts/" + name)), name);
    }

    // ---- layout.json illisible ----

    private (MainWindow Window, MainViewModel Model, string File) OpenUnreadable()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "layout.json");
        File.WriteAllText(file, "{ ceci n'est pas du JSON");
        var document = new LayoutDocument(new LayoutStore(file), _link);
        document.Load();
        var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        _window = new MainWindow { DataContext = model };
        _window.Show();
        return (_window, model, file);
    }

    [AvaloniaFact]
    public async Task UnreadableLayout_ShowsTheReasonAndTheResetButton_ThenResets()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, model, file) = OpenUnreadable();

        var line = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SaveMessage");
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ResetLayout");
        Assert.True(line.IsEffectivelyVisible);
        Assert.StartsWith("layout.json est illisible : ", line.Text);
        Assert.Contains("warning", line.Classes);
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal("Repartir de la disposition par défaut", button.Content);

        await model.ResetLayoutCommand.ExecuteAsync(null);

        Assert.False(line.IsEffectivelyVisible);
        Assert.False(button.IsEffectivelyVisible);
        Assert.True(File.Exists(file + ".avant-reglages"));
        Assert.True(new LayoutStore(file).Load().Views[0].Placements.Count > 0);
    }

    [AvaloniaFact]
    public void ReadableLayout_HasNoResetButton()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (window, model) = Open();

        Assert.False(model.CanResetLayout);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "ResetLayout" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task UnreadableLayout_AnEditIsRefused_AndTheReasonIsShownOnce()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var (_, model, file) = OpenUnreadable();

        await model.SettingsTab.SetRotationAsync(0);

        Assert.Equal("{ ceci n'est pas du JSON", File.ReadAllText(file));
        Assert.Single(model.SaveMessage!.Split('\n')); // la ligne du motif, sans doublon avec le refus
        Assert.True(model.CanResetLayout);
    }
}
