using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>La section « Verre » de l'onglet Personnalisation (thème « Liquid Glass avancé »), sans fenêtre.</summary>
public sealed class AppearanceGlassTests : IDisposable
{
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");
    private const string Advanced = "liquid-glass-avance";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-glass-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public AppearanceGlassTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppearanceTabViewModel Open(AppearanceSettings? appearance = null)
    {
        var clock = new ModulePlacement(ClockId, "clock", 12, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        _store.Save(DashboardLayout.SingleView(180, [clock], appearance: appearance));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new AppearanceTabViewModel(_session, null, ["Arial"]);
    }

    private AppearanceSettings OnDisk => _store.Load().Appearance ?? AppearanceSettings.Default;

    private static GlassSettings Clair => GlassPresets.Find(GlassPresets.Clair)!;

    private static GlassSliderItem Slider(AppearanceTabViewModel tab, string id) => tab.Glass.Sliders.Single(s => s.Id == id);

    [Fact]
    public async Task GlassSection_HiddenForOtherThemes_ShownForAdvanced()
    {
        var tab = Open();
        Assert.False(tab.ShowsGlass);

        await tab.SetThemeAsync("liquid-glass");
        Assert.False(tab.ShowsGlass);
        await tab.SetThemeAsync("pixel");
        Assert.False(tab.ShowsGlass);

        await tab.SetThemeAsync(Advanced);
        Assert.True(tab.ShowsGlass);
        Assert.Equal(Advanced, OnDisk.Theme);

        Assert.True(Open(new AppearanceSettings(Advanced)).ShowsGlass);
    }

    [Fact]
    public void ThemeCard_Advanced_IsTheThirdCard_AndWarnsAboutConsumption()
    {
        var tab = Open();

        Assert.Equal(["classique", "liquid-glass", Advanced, "pixel"], tab.Themes.Select(t => t.Id));
        var card = tab.Themes[2];
        Assert.Equal("Liquid Glass avancé", card.Name);
        // Une seule ligne, comme les autres cartes (demande d'Alex, 15/11/2026) ; elle prévient toujours de la consommation.
        Assert.Equal("Verre d'iOS, plus gourmand en processeur.", card.Description);
    }

    [Fact]
    public void Presets_AreFourButtons_ClairSelectedByDefault()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        Assert.Equal(["clair", "givre", "prisme", "discret"], tab.Glass.Presets.Select(p => p.Id));
        Assert.Equal(["Clair", "Givré", "Prisme", "Discret"], tab.Glass.Presets.Select(p => p.Label));
        Assert.Equal([true, false, false, false], tab.Glass.Presets.Select(p => p.IsSelected));
        Assert.False(tab.Glass.IsCustom);
        Assert.False(tab.Glass.ShowsDetails);   // le repli est fermé par défaut
    }

    [Fact]
    public async Task ChoosePreset_WritesPresetOnly()
    {
        var tab = Open(new AppearanceSettings(Advanced, 0.5f, "Manrope"));

        await tab.Glass.Presets.Single(p => p.Id == GlassPresets.Prisme).SelectCommand.ExecuteAsync(null);

        Assert.Equal(new AppearanceSettings(Advanced, 0.5f, "Manrope", Glass: new GlassAppearance(GlassPresets.Prisme)), OnDisk);   // le reste est gardé, et pas de valeurs
        Assert.Null(OnDisk.Glass!.Values);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal([false, false, true, false], tab.Glass.Presets.Select(p => p.IsSelected));
    }

    [Fact]
    public async Task ChoosePreset_ShowsItsValuesOnTheSliders()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        Assert.Equal(0.1, Slider(tab, nameof(GlassSettings.Blur)).Value, 6);

        await tab.Glass.SetPresetAsync(GlassPresets.Givre);

        var givre = GlassPresets.Find(GlassPresets.Givre)!;
        Assert.Equal(givre.Blur, Slider(tab, nameof(GlassSettings.Blur)).Value, 6);
        Assert.Equal(givre.Tint, Slider(tab, nameof(GlassSettings.Tint)).Value, 6);
        Assert.Equal(givre.Saturation, Slider(tab, nameof(GlassSettings.Saturation)).Value, 6);
    }

    [Fact]
    public async Task ChoosePreset_Clair_OnTheDefault_WritesNothing()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        await tab.Glass.SetPresetAsync(GlassPresets.Clair);

        Assert.Equal(0, _link.ReloadCount);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task ChoosePreset_Unknown_IsRefused()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        await tab.Glass.SetPresetAsync("neon");
        await tab.Glass.SetPresetAsync(GlassPresets.Custom);

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task MoveSlider_DoesNotSaveUntilCommit_ThenWritesCustom()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        var blur = Slider(tab, nameof(GlassSettings.Blur));
        var refraction = Slider(tab, nameof(GlassSettings.Refraction));

        blur.Value = 0.3;
        blur.Value = 0.5;
        refraction.Value = 1.2;

        Assert.Equal(0, _link.ReloadCount);            // pendant le glissement : rien
        Assert.Null(OnDisk.Glass);
        Assert.Equal("0.50", blur.ValueText.Replace(',', '.'));

        await tab.Glass.CommitAsync();

        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(GlassPresets.Custom, OnDisk.Glass!.Preset);
        Assert.Equal(Clair with { Blur = 0.5f, Refraction = 1.2f }, OnDisk.Glass.Values);   // les autres valeurs sont celles du préréglage de départ
        Assert.False(blur.IsDirty);
    }

    [Fact]
    public async Task MoveSlider_Commit_WithoutMovement_SavesNothing()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        await tab.Glass.CommitAsync();

        Assert.Equal(0, _link.ReloadCount);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task MoveSlider_BackToTheSameValue_SavesNothing()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        var blur = Slider(tab, nameof(GlassSettings.Blur));

        blur.Value = 0.7;
        blur.Value = Clair.Blur;
        await tab.Glass.CommitAsync();

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task MoveSlider_ValueIsBoundedAndRoundedToTheStep()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        var blur = Slider(tab, nameof(GlassSettings.Blur));
        var offset = Slider(tab, nameof(GlassSettings.ShadowOffsetY));

        blur.Value = 7.0;                  // au-delà du maximum
        offset.Value = -3.4;               // entre deux pas de 1
        await tab.Glass.CommitAsync();

        Assert.Equal(1f, OnDisk.Glass!.Values!.Blur);
        Assert.Equal(-3f, OnDisk.Glass.Values.ShadowOffsetY);
        blur.Value = double.NaN;           // une valeur absurde est ignorée
        Assert.Equal(1.0, blur.Value);
    }

    [Fact]
    public async Task MoveSlider_WrittenValuesAreCleanFloats()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        Slider(tab, nameof(GlassSettings.ChromaticAberration)).Value = 0.155;
        Slider(tab, nameof(GlassSettings.Refraction)).Value = 0.69000000001;
        await tab.Glass.CommitAsync();

        var json = File.ReadAllText(Path.Combine(_dir, "layout.json"));
        Assert.Equal(0.155f, OnDisk.Glass!.Values!.ChromaticAberration);
        Assert.DoesNotContain("00000", json);   // pas de 0.15500000119…
    }

    [Fact]
    public async Task Undo_RestoresPreviousGlass()
    {
        var tab = Open(new AppearanceSettings(Advanced, Glass: new GlassAppearance(GlassPresets.Givre)));
        var blur = Slider(tab, nameof(GlassSettings.Blur));
        var givreBlur = GlassPresets.Find(GlassPresets.Givre)!.Blur;

        blur.Value = 0.9;
        await tab.Glass.CommitAsync();
        Assert.True(tab.Glass.IsCustom);
        Assert.Equal(0.9, blur.Value, 6);

        await _session.UndoAsync();                       // un seul pas d'annulation

        Assert.Equal(new GlassAppearance(GlassPresets.Givre), OnDisk.Glass);
        Assert.False(tab.Glass.IsCustom);
        Assert.Equal(givreBlur, blur.Value, 6);           // le curseur revient avec le fichier
        Assert.True(tab.Glass.Presets.Single(p => p.Id == GlassPresets.Givre).IsSelected);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task Sliders_HaveParameterBounds()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        Assert.Equal(["Verre", "Lumière", "Couleur", "Ombre"], tab.Glass.Groups.Select(g => g.Title));
        Assert.Equal([5, 4, 4, 3], tab.Glass.Groups.Select(g => g.Sliders.Count));
        var sliders = tab.Glass.Groups.SelectMany(g => g.Sliders).ToList();
        Assert.Equal(16, sliders.Count);
        Assert.Equal(GlassSettings.Parameters.Select(p => p.Name), sliders.Select(s => s.Id));
        foreach (var (parameter, slider) in GlassSettings.Parameters.Zip(sliders))
        {
            Assert.Equal(parameter.Min, slider.Min, 6);
            Assert.Equal(parameter.Max, slider.Max, 6);
            Assert.Equal(parameter.Step, slider.Step, 6);
            Assert.False(string.IsNullOrWhiteSpace(slider.Label));
        }

        Assert.Equal(
            ["Flou", "Réfraction", "Profondeur du biseau", "Rayon des angles", "Micro-déformation",
             "Lumière des bords", "Reflets", "Fresnel", "Franges de couleur",
             "Opacité du verre", "Saturation", "Teinte froide", "Luminosité",
             "Opacité de l'ombre", "Étendue de l'ombre", "Décalage de l'ombre"],
            sliders.Select(s => s.Label));

        // Les valeurs de départ sont celles de Clair.
        Assert.Equal(Clair.Refraction, Slider(tab, nameof(GlassSettings.Refraction)).Value, 6);
        Assert.Equal(Clair.CornerRadius, Slider(tab, nameof(GlassSettings.CornerRadius)).Value, 6);
        await Task.CompletedTask;
    }

    [Fact]
    public void Sliders_ValueText_FollowsTheStep()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            Slider(tab, nameof(GlassSettings.ChromaticAberration)).Value = 0.15;
            Slider(tab, nameof(GlassSettings.ZRadius)).Value = 55;
            Assert.Equal("0,150", Slider(tab, nameof(GlassSettings.ChromaticAberration)).ValueText);   // pas de 0,005 : trois décimales
            Assert.Equal("55", Slider(tab, nameof(GlassSettings.ZRadius)).ValueText);                  // pas de 1 : aucune
            Assert.Equal("0,69", Slider(tab, nameof(GlassSettings.Refraction)).ValueText);             // pas de 0,01 : deux
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public async Task CustomState_ShowsAfterSlider_ClearedByPreset()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        Assert.False(tab.Glass.IsCustom);

        Slider(tab, nameof(GlassSettings.Fresnel)).Value = 0.5;
        Assert.False(tab.Glass.IsCustom);                       // un brouillon n'est pas encore un réglage
        await tab.Glass.CommitAsync();
        Assert.True(tab.Glass.IsCustom);
        Assert.All(tab.Glass.Presets, p => Assert.False(p.IsSelected));

        await tab.Glass.SetPresetAsync(GlassPresets.Discret);
        Assert.False(tab.Glass.IsCustom);
        Assert.True(tab.Glass.Presets.Single(p => p.Id == GlassPresets.Discret).IsSelected);
    }

    [Fact]
    public async Task Bevel_ToggleWritesCustomDome()
    {
        var tab = Open(new AppearanceSettings(Advanced, Glass: new GlassAppearance(GlassPresets.Prisme)));
        Assert.False(tab.Glass.IsDome);
        Assert.True(tab.Glass.IsPill);

        await tab.Glass.SetBevelAsync(GlassBevel.Dome);

        Assert.Equal(new GlassAppearance(GlassPresets.Custom, GlassPresets.Find(GlassPresets.Prisme)! with { Bevel = GlassBevel.Dome }), OnDisk.Glass);
        Assert.True(tab.Glass.IsDome);
        Assert.False(tab.Glass.IsPill);
        Assert.True(tab.Glass.IsCustom);

        await tab.Glass.SetBevelAsync(GlassBevel.Pill);
        Assert.Equal(GlassBevel.Pill, OnDisk.Glass!.Values!.Bevel);
        Assert.True(tab.Glass.IsPill);
    }

    [Fact]
    public async Task Bevel_SameShape_WritesNothing()
    {
        var tab = Open(new AppearanceSettings(Advanced));

        await tab.Glass.SetBevelAsync(GlassBevel.Pill);

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Draft_SurvivesAnUnrelatedChange_ButNotAGlassChange()
    {
        var tab = Open(new AppearanceSettings(Advanced));
        var blur = Slider(tab, nameof(GlassSettings.Blur));

        blur.Value = 0.8;
        await _session.ApplyAsync(l => l with { Rotation = 0 });          // une autre modification passe
        Assert.Equal(0.8, blur.Value, 6);                                  // le curseur n'est pas ramené

        await _session.ApplyAsync(l => l with { Appearance = new AppearanceSettings(Advanced, Glass: new GlassAppearance(GlassPresets.Givre)) });
        Assert.Equal(GlassPresets.Find(GlassPresets.Givre)!.Blur, blur.Value, 6);   // le verre enregistré a changé : le curseur suit
    }

    [Fact]
    public void BrokenFile_ShowsClair_WithoutThrowing()
    {
        var tab = Open(new AppearanceSettings(Advanced, Glass: new GlassAppearance("neon")));

        Assert.True(tab.Glass.Presets.Single(p => p.Id == GlassPresets.Clair).IsSelected);   // un préréglage inconnu : le verre est Clair
        Assert.Equal(Clair.Blur, Slider(tab, nameof(GlassSettings.Blur)).Value, 6);

        var custom = Open(new AppearanceSettings(Advanced, Glass: new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: 9f, Refraction: float.NaN))));
        Assert.True(custom.Glass.IsCustom);
        Assert.Equal(1.0, Slider(custom, nameof(GlassSettings.Blur)).Value, 6);                // borné
        Assert.Equal(Clair.Refraction, Slider(custom, nameof(GlassSettings.Refraction)).Value, 6);   // NaN : la valeur de Clair
    }

    [Fact]
    public async Task OtherThemes_KeepTheirGlassChoice()
    {
        var tab = Open(new AppearanceSettings(Advanced, Glass: new GlassAppearance(GlassPresets.Prisme)));

        await tab.SetThemeAsync("classique");

        Assert.Equal(new GlassAppearance(GlassPresets.Prisme), OnDisk.Glass);   // le choix du verre est gardé
        Assert.False(tab.ShowsGlass);
    }
}

/// <summary>La section « Verre » dans la vraie fenêtre : la carte du thème, le repli, les curseurs, le lâcher.</summary>
public sealed class AppearanceGlassViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-glass-view-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private MainWindow? _window;

    public AppearanceGlassViewTests()
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

    private (MainViewModel Model, LayoutDocument Document) Open(AppearanceSettings? appearance = null)
    {
        var clock = new ModulePlacement(new Guid("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01"), "clock", 12, 0, 4, 2, TextSize.Normal,
            JsonDocument.Parse("{}").RootElement.Clone());
        _store.Save(DashboardLayout.SingleView(180, [clock], appearance: appearance));
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true), null, ["Arial", "Verdana"]);
        _window = new MainWindow { DataContext = model, Width = 1440, Height = 2200 };   // assez haut pour que toute la section soit à l'écran
        _window.Show();
        model.SelectedTab = model.AppearanceTab;
        Dispatcher.UIThread.RunJobs();
        return (model, document);
    }

    private IEnumerable<T> Find<T>() where T : Visual => _window!.GetVisualDescendants().OfType<T>();

    private T Named<T>(string name) where T : Avalonia.Controls.Control => Find<T>().Single(c => c.Name == name);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condition n'est pas devenue vraie à temps.");
    }

    private Point Center(Avalonia.Controls.Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _window!)!.Value;

    private void Click(Avalonia.Controls.Control control)
    {
        _window!.MouseDown(Center(control), MouseButton.Left);
        _window!.MouseUp(Center(control), MouseButton.Left);
    }

    [AvaloniaFact]
    public void GlassSection_Absent_ForTheClassicTheme_ButTheAdvancedCardIsThere()
    {
        Open();
        Assert.False(Named<Border>("GlassSection").IsVisible);

        var cards = Find<Button>().Where(b => b.Classes.Contains("card")).ToList();
        Assert.Equal(4, cards.Count);
        Assert.Equal("Liquid Glass avancé", AutomationProperties.GetName(cards[2]));
        var images = Named<ItemsControl>("ThemeCards").GetVisualDescendants().OfType<Image>().ToList();
        Assert.Equal(4, images.Count);
        Assert.All(images, image => Assert.NotNull(image.Source));   // la vignette du nouveau thème est embarquée
    }

    [AvaloniaFact]
    public void GlassSection_Present_ForTheAdvancedTheme_AndItsCardIsSelected()
    {
        Open(new AppearanceSettings("liquid-glass-avance"));

        Assert.True(Named<Border>("GlassSection").IsVisible);
        Assert.Contains("selected", Find<Button>().Where(b => b.Classes.Contains("card")).ElementAt(2).Classes);
    }

    [AvaloniaFact]
    public async Task GlassSection_PresetButtons_AndTheFoldOpens()
    {
        var (model, document) = Open(new AppearanceSettings("liquid-glass-avance"));

        var presets = Named<ItemsControl>("GlassPresets").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["Clair", "Givré", "Prisme", "Discret"], presets.Select(p => AutomationProperties.GetName(p)));
        Assert.Contains("primary", presets[0].Classes);

        Assert.False(Named<StackPanel>("GlassDetails").IsVisible);           // le repli est fermé
        Click(Named<Button>("GlassDetailsToggle"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<StackPanel>("GlassDetails").IsVisible);
        var sliders = Named<StackPanel>("GlassDetails").GetVisualDescendants().OfType<Slider>().ToList();
        Assert.Equal(16, sliders.Count);
        Assert.All(sliders, s => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(s))));
        Assert.All(sliders, s => Assert.True(s.Focusable));                    // atteignables au clavier

        Click(presets[1]);
        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal(new GlassAppearance(GlassPresets.Givre), document.Current.Appearance!.Glass);
        await WaitUntil(() => Named<ItemsControl>("GlassPresets").GetVisualDescendants().OfType<Button>().ElementAt(1).Classes.Contains("primary"));
        Assert.True(model.AppearanceTab.Glass.Presets[1].IsSelected);
    }

    [AvaloniaFact]
    public async Task GlassSlider_SavesOnRelease_AsCustom_NotWhileDragging()
    {
        var (_, document) = Open(new AppearanceSettings("liquid-glass-avance"));
        Click(Named<Button>("GlassDetailsToggle"));
        Dispatcher.UIThread.RunJobs();
        var slider = Named<StackPanel>("GlassDetails").GetVisualDescendants().OfType<Slider>().First();   // Flou
        var from = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.2, slider.Bounds.Height / 2), _window!)!.Value;
        var to = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.8, slider.Bounds.Height / 2), _window!)!.Value;

        _window!.MouseDown(from, MouseButton.Left);
        _window!.MouseMove(new Point((from.X + to.X) / 2, from.Y));
        _window!.MouseMove(to);
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(slider.Value, 0.6, 0.95);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Null(document.Current.Appearance!.Glass);

        _window!.MouseUp(to, MouseButton.Left);

        await WaitUntil(() => _link.ReloadCount == 1);
        var glass = document.Current.Appearance!.Glass!;
        Assert.Equal(GlassPresets.Custom, glass.Preset);
        Assert.InRange(glass.Values!.Blur, 0.6f, 0.95f);
        await WaitUntil(() => Named<TextBlock>("GlassCustom").IsVisible);
    }

    [AvaloniaFact]
    public async Task GlassBevel_Buttons_WriteDome()
    {
        var (model, document) = Open(new AppearanceSettings("liquid-glass-avance"));
        Click(Named<Button>("GlassDetailsToggle"));
        Dispatcher.UIThread.RunJobs();

        Click(Named<Button>("GlassBevelDome"));

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal(GlassBevel.Dome, document.Current.Appearance!.Glass!.Values!.Bevel);
        await WaitUntil(() => Named<Button>("GlassBevelDome").Classes.Contains("primary"));
        Assert.True(model.AppearanceTab.Glass.IsDome);
    }
}
