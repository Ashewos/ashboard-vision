using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Personnalisation dans la vraie fenêtre : clics, curseur, liste, champ de couleur, vignettes.</summary>
public sealed class AppearanceTabViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-appearance-view-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private MainWindow? _window;

    public AppearanceTabViewTests()
    {
        Directory.CreateDirectory(_dir);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
    }

    public void Dispose()
    {
        _window?.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private (MainViewModel Model, LayoutDocument Document) Open(AppearanceSettings? appearance = null, IFilePicker? picker = null)
    {
        var clock = new ModulePlacement(new Guid("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01"), "clock", 12, 0, 4, 2, TextSize.Normal,
            JsonDocument.Parse("{}").RootElement.Clone());
        _store.Save(DashboardLayout.SingleView(180, [clock], appearance: appearance));
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true), picker, ["Arial", "Verdana"]);
        _window = new MainWindow { DataContext = model };
        _window.Show();
        model.SelectedTab = model.AppearanceTab;
        Dispatcher.UIThread.RunJobs();
        return (model, document);
    }

    private IEnumerable<T> Find<T>() where T : Visual => _window!.GetVisualDescendants().OfType<T>();

    private T Named<T>(string name) where T : Avalonia.Controls.Control => Find<T>().Single(c => c.Name == name);

    private Point Center(Avalonia.Controls.Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _window!)!.Value;

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condition n'est pas devenue vraie à temps.");
    }

    private void Click(Avalonia.Controls.Control control)
    {
        _window!.MouseDown(Center(control), MouseButton.Left);
        _window!.MouseUp(Center(control), MouseButton.Left);
    }

    [AvaloniaFact]
    public void ThemeCards_ShowTheirEmbeddedThumbnail()
    {
        Open();

        var images = Named<ItemsControl>("ThemeCards").GetVisualDescendants().OfType<Image>().ToList();

        Assert.Equal(3, images.Count);
        Assert.All(images, image => Assert.NotNull(image.Source));   // les trois PNG de Assets/Themes sont dans l'assembly
    }

    [AvaloniaFact]
    public async Task ThemeCard_Click_SavesTheTheme_AndMarksTheCard()
    {
        var (model, document) = Open();
        var cards = Find<Button>().Where(b => b.Classes.Contains("card")).ToList();
        Assert.Equal(3, cards.Count);

        Click(cards[2]);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("pixel", document.Current.Appearance!.Theme);
        await WaitUntil(() => Find<Button>().Where(b => b.Classes.Contains("card")).ElementAt(2).Classes.Contains("selected"));
        Assert.True(model.AppearanceTab.Themes[2].IsSelected);
    }

    [AvaloniaFact]
    public async Task OpacitySlider_SavesOnRelease_NotWhileDragging()
    {
        var (_, document) = Open();
        var slider = Named<Slider>("OpacitySlider");
        var left = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.25, slider.Bounds.Height / 2), _window!)!.Value;
        var right = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.5, slider.Bounds.Height / 2), _window!)!.Value;

        _window!.MouseDown(left, MouseButton.Left);
        _window!.MouseMove(new Point((left.X + right.X) / 2, left.Y));
        _window!.MouseMove(right);
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(slider.Value, 40, 60);      // le curseur a suivi la souris
        Assert.Equal(0, _link.ReloadCount);        // rien n'est enregistré pendant le glissement
        Assert.Null(document.Current.Appearance);

        _window!.MouseUp(right, MouseButton.Left);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal((float)(Math.Round(slider.Value) / 100.0), document.Current.Appearance!.PanelOpacity);
    }

    [AvaloniaFact]
    public async Task OpacitySlider_Keyboard_SavesWhenTheKeyIsReleased()
    {
        var (_, document) = Open(new AppearanceSettings("classique", 0.5f));
        var slider = Named<Slider>("OpacitySlider");
        slider.Focus();

        _window!.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, _link.ReloadCount);
        _window!.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal(0.51f, document.Current.Appearance!.PanelOpacity);
    }

    private ListBoxItem FontRow(string name) =>
        Named<ListBox>("FontList").GetVisualDescendants().OfType<ListBoxItem>().Single(i => (i.DataContext as FontItem)?.Name == name);

    [AvaloniaFact]
    public async Task FontList_ClickingARow_SavesTheFont()
    {
        var (model, document) = Open();
        var list = Named<ListBox>("FontList");
        Assert.Equal(model.AppearanceTab.Fonts[0], list.SelectedItem);   // « Celle du thème »

        Click(FontRow("Manrope"));

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Manrope", document.Current.Appearance!.Font);
    }

    [AvaloniaFact]
    public async Task FontList_Keyboard_ArrowsOnlyMoveTheHighlight_EnterSaves()
    {
        var (model, document) = Open();
        var list = Named<ListBox>("FontList");
        FontRow(model.AppearanceTab.Fonts[0].Name).Focus(NavigationMethod.Tab);

        (Key Key, PhysicalKey Physical)[] keys =
        [
            (Key.Down, PhysicalKey.ArrowDown), (Key.Down, PhysicalKey.ArrowDown), (Key.Down, PhysicalKey.ArrowDown),
            (Key.Up, PhysicalKey.ArrowUp), (Key.End, PhysicalKey.End), (Key.Home, PhysicalKey.Home),
            (Key.Down, PhysicalKey.ArrowDown), (Key.Down, PhysicalKey.ArrowDown),
        ];
        foreach (var (key, physical) in keys)
        {
            _window!.KeyPress(key, RawInputModifiers.None, physical, null);
            _window!.KeyRelease(key, RawInputModifiers.None, physical, null);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, _link.ReloadCount);                      // se promener dans la liste n'écrit rien
        Assert.False(model.Session.CanUndo);
        Assert.Null(document.Current.Appearance);
        var highlighted = (FontItem)list.SelectedItem!;
        Assert.Equal(model.AppearanceTab.Fonts[2], highlighted);   // Home puis deux fois Bas : Manrope

        _window!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Manrope", document.Current.Appearance!.Font);
    }

    [AvaloniaFact]
    public void FontList_SelectsTheSavedFont_WithoutSavingAnything()
    {
        Open(new AppearanceSettings("classique", Font: "Verdana"));

        var list = Named<ListBox>("FontList");

        Assert.Equal("Verdana", ((FontItem)list.SelectedItem!).Name);
        Assert.Equal(0, _link.ReloadCount);   // remplir la liste ne choisit rien
    }

    [AvaloniaFact]
    public async Task ColorField_Enter_SavesTheColor_AndABadOneShowsTheError()
    {
        var (model, document) = Open();
        await model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Color);
        Dispatcher.UIThread.RunJobs();
        var box = Named<TextBox>("HexBox");
        Assert.True(box.IsEffectivelyVisible);

        box.Focus();
        box.Text = "#12345";
        _window!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => model.AppearanceTab.HasColorError);
        Assert.True(Named<TextBlock>("ColorError").IsEffectivelyVisible);
        Assert.Equal(0, _link.ReloadCount);

        box.Text = "#1a2b3c";
        _window!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("#1A2B3C", document.Current.Appearance!.Background!.Color);
    }

    [AvaloniaFact]
    public async Task Swatch_Click_SavesThatColor()
    {
        var (model, document) = Open();
        await model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Color);
        Dispatcher.UIThread.RunJobs();

        var swatches = Find<Button>().Where(b => b.Classes.Contains("swatch")).ToList();
        Assert.Equal(8, swatches.Count);
        Click(swatches[4]);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal(model.AppearanceTab.Palette[4].Hex, document.Current.Appearance!.Background!.Color);
    }

    [AvaloniaFact]
    public async Task ChooseFile_UsesThePickerOfTheWindow_AndShowsVideoAdviceAndFileName()
    {
        var picker = new FakeFilePicker { Result = Path.Combine(_dir, "boucle.mp4") };
        var (model, document) = Open(picker: picker);
        Assert.Same(picker, model.AppearanceTab.Picker);   // la vue ne remplace pas celui qu'on a donné
        Assert.False(Named<StackPanel>("FileSection").IsEffectivelyVisible);

        await model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Video);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>("VideoAdvice").IsEffectivelyVisible);
        Assert.Equal(Texts.AppearanceNoFile, Named<TextBlock>("FileName").Text);

        Click(Named<Button>("ChooseFile"));

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal(new BackgroundSettings(BackgroundKind.Video, Path: Path.Combine(_dir, "boucle.mp4")), document.Current.Appearance!.Background);
        await WaitUntil(() => Named<TextBlock>("FileName").Text == "boucle.mp4");
    }

    [AvaloniaFact]
    public void Picker_WithoutOne_IsTheStorageProviderOfTheWindow()
    {
        var (model, _) = Open();

        Assert.IsType<StorageFilePicker>(model.AppearanceTab.Picker);
        Assert.True(Named<Button>("ChooseFile").IsEnabled || !Named<Button>("ChooseFile").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task LegibilitySwitch_Click_WritesFalse_ThenTrue()
    {
        var (_, document) = Open();
        var toggle = Named<ToggleSwitch>("LegibilitySwitch");
        Assert.True(toggle.IsChecked);

        Click(toggle);
        await WaitUntil(() => document.Current.Appearance?.Legibility == false);
        Assert.False(toggle.IsChecked);

        Click(toggle);
        await WaitUntil(() => document.Current.Appearance?.Legibility == true);
    }

    [AvaloniaFact]
    public async Task AnimateViewSwitchSwitch_Click_WritesFalse_ThenTrue()
    {
        var (_, document) = Open();
        var toggle = Named<ToggleSwitch>("AnimateViewSwitchSwitch");
        Assert.True(toggle.IsChecked);

        Click(toggle);
        await WaitUntil(() => document.Current.Appearance?.AnimateViewSwitch == false);
        Assert.False(toggle.IsChecked);

        Click(toggle);
        await WaitUntil(() => document.Current.Appearance is null || document.Current.Appearance.AnimateViewSwitch);
    }

    [AvaloniaFact]
    public async Task BackgroundWarning_IsShownUnderTheBackgroundChoice_NotInTheWindowLine()
    {
        var (model, _) = Open(picker: new FakeFilePicker { Result = @"C:\fonds\photo.png" });
        const string Missing = "Fond d'écran introuvable : C:\\fonds\\photo.png. Fond du thème utilisé.";
        _link.OnReload = () => Task.FromResult(new Trofeo.Control.Reloaded(true, null, [Missing]));
        await model.AppearanceTab.SetBackgroundKindAsync(BackgroundKind.Image);
        await model.AppearanceTab.ChooseFileAsync();
        Dispatcher.UIThread.RunJobs();

        var warning = Named<TextBlock>("BackgroundWarning");
        Assert.True(warning.IsEffectivelyVisible);
        Assert.Equal(Missing, warning.Text);
        Assert.Contains("warning", warning.Classes);
        Assert.False(Named<TextBlock>("SaveMessage").IsEffectivelyVisible);
    }
}
