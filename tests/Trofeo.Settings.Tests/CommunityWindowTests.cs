using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>
/// Ce que la fenêtre ajoute autour de la section des modules complémentaires : le bandeau « Mode développeur actif » (dans tous les onglets,
/// dès l'ouverture), et la consommation qui ne se mesure que si l'onglet Paramètres est vraiment sous les yeux.
/// </summary>
public sealed class CommunityWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-community-window-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeTimeProvider _time = new();
    private MainWindow? _window;

    public CommunityWindowTests()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose()
    {
        _window?.Close();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private MainViewModel NewModel(bool firstRunDone = true)
    {
        Directory.CreateDirectory(_dir);
        var document = new LayoutDocument(new LayoutStore(Path.Combine(_dir, "layout.json")), _link);
        return new MainViewModel(
            _link, document, new EditorPreferences(FirstRunDone: firstRunDone), time: _time, preferencesPath: Path.Combine(_dir, "settings-app.json"));
    }

    private int Reads => _link.ModuleCalls.Count(c => c == "consumption");

    // ---- Le bandeau ----

    [Fact]
    public async Task Window_DeveloperModeAlreadyOn_IsKnownBeforeTheSettingsTabIsOpened()
    {
        _link.Community = new CommunityOverview([], @"C:\Modules", DeveloperMode: true, [], false);

        using var model = NewModel();

        Assert.True(model.SettingsTab.Community.ShowsDeveloperBanner);   // une seule lecture à l'ouverture, pour le bandeau
        Assert.Equal(1, _link.ModuleCalls.Count(c => c == "community"));
        Assert.Equal(0, Reads);
        await Task.CompletedTask;
    }

    [Fact]
    public void Window_ServiceReachedLater_ReadsTheModeOnceItConnects()
    {
        _link.Set(LinkState.Connecting);
        _link.Community = new CommunityOverview([], @"C:\Modules", DeveloperMode: true, [], false);
        using var model = NewModel();
        Assert.False(model.SettingsTab.Community.ShowsDeveloperBanner);
        Assert.DoesNotContain("community", _link.ModuleCalls);

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());   // l'état revient chaque seconde : une seule lecture

        Assert.True(model.SettingsTab.Community.ShowsDeveloperBanner);
        Assert.Equal(1, _link.ModuleCalls.Count(c => c == "community"));
    }

    [AvaloniaFact]
    public void Window_Banner_IsShownInEveryTab_OnlyWhileTheModeIsOn()
    {
        _link.Community = new CommunityOverview([], @"C:\Modules", DeveloperMode: true, [], false);
        var model = NewModel();
        _window = new MainWindow { DataContext = model };
        _window.Show();

        Assert.Contains(_window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Mode développeur actif");

        model.SelectedTab = model.AppearanceTab;
        Assert.Contains(_window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Mode développeur actif");

        _link.Community = _link.Community with { DeveloperMode = false };
        _ = model.SettingsTab.Community.RefreshAsync();
        Assert.DoesNotContain(_window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Mode développeur actif");
    }

    // ---- La consommation ----

    [Fact]
    public void Consumption_FollowsTheSelectedTab()
    {
        using var model = NewModel();
        var consumption = model.SettingsTab.Community.Consumption;
        Assert.False(consumption.IsPolling);                 // l'onglet Disposition est affiché : rien n'est mesuré

        model.SelectedTab = model.SettingsTab;
        Assert.True(consumption.IsPolling);
        Assert.Equal(1, Reads);

        model.SelectedTab = model.ModulesTab;
        Assert.False(consumption.IsPolling);

        model.SelectedTab = model.SettingsTab;
        Assert.True(consumption.IsPolling);
    }

    [Fact]
    public void Consumption_StopsWhenTheWindowIsMinimized_AndResumesWhenItComesBack()
    {
        using var model = NewModel();
        model.SelectedTab = model.SettingsTab;
        Assert.True(model.SettingsTab.Community.Consumption.IsPolling);

        model.SetPreviewPaused(true);
        Assert.False(model.SettingsTab.Community.Consumption.IsPolling);
        var frozen = Reads;
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(frozen, Reads);

        model.SetPreviewPaused(false);
        Assert.True(model.SettingsTab.Community.Consumption.IsPolling);
        Assert.Equal(frozen + 1, Reads);
    }

    [Fact]
    public void Consumption_StopsWhenAPanelCoversTheTab()
    {
        using var model = NewModel();
        model.SelectedTab = model.SettingsTab;
        var consumption = model.SettingsTab.Community.Consumption;
        Assert.True(consumption.IsPolling);

        _link.Set(LinkState.ServiceStopped);                  // le panneau « service arrêté » remplace l'onglet
        Assert.False(consumption.IsPolling);

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        Assert.True(consumption.IsPolling);

        model.PreferencesStore.Update(p => p with { FirstRunDone = false }); // l'assistant le recouvre
        Assert.False(consumption.IsPolling);
    }

    [Fact]
    public void Consumption_WhileTheFirstRunAssistantIsShown_IsNotMeasured()
    {
        using var model = NewModel(firstRunDone: false);

        model.SelectedTab = model.SettingsTab;

        Assert.False(model.SettingsTab.Community.Consumption.IsPolling);
        Assert.Equal(0, Reads);
    }

    // ---- Le repère orange ----

    [AvaloniaFact]
    public void Consumption_HighMark_IsDrawnWithTheAmberToken_OnlyOnTheHighRow()
    {
        _link.Consumption.AddRange(
        [
            new ModuleConsumption("originaux", "Modules d'origine", false, 0.4, 44L * 1024 * 1024, false),
            new ModuleConsumption("alex.camera", "Caméra", true, 3.2, 180L * 1024 * 1024, true),
        ]);
        var model = NewModel();
        _window = new MainWindow { DataContext = model, Width = 1440, Height = 1800 };
        _window.Show();

        model.SelectedTab = model.SettingsTab;
        Dispatcher.UIThread.RunJobs();

        var marks = _window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "HighMark" && t.IsEffectivelyVisible).ToList();
        var mark = Assert.Single(marks);                      // la ligne des modules d'origine n'en a pas
        Assert.Equal("Consommation élevée", mark.Text);
        Assert.Equal(Color.Parse("#F2B84B"), Assert.IsAssignableFrom<ISolidColorBrush>(mark.Foreground).Color);
        Assert.Contains(_window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Caméra");
    }
}
