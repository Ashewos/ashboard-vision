using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Controls;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.ViewModels.Fields;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>L'assistant de première ouverture : ses étapes, ce qu'elles appliquent, et la fenêtre qui le montre à la place des onglets.</summary>
public sealed class FirstRunTests : IDisposable
{
    private const string DisplayExe = @"C:\Trofeo\Trofeo.Display.exe";
    private const string HelperExe = @"C:\Trofeo\Trofeo.SensorHelper.exe";
    private const string TrccBusy = "Écran utilisé par un autre programme (ferme TRCC)";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-firstrun-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeStartupRegistration _startup = new();
    private readonly FakeElevatedLauncher _launcher = new();
    private readonly FakeSecretAccess _secrets = new();
    private readonly List<MainViewModel> _models = [];
    private MainWindow? _window;

    public FirstRunTests()
    {
        Directory.CreateDirectory(_dir);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose()
    {
        _window?.Close();
        foreach (var model in _models)
            model.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string PreferencesPath => Path.Combine(_dir, "settings-app.json");
    private string LayoutPath => Path.Combine(_dir, "layout.json");

    private MainViewModel Open(DashboardLayout? layout = null, bool firstRunDone = false)
    {
        var store = new LayoutStore(LayoutPath);
        if (layout is not null)
            store.Save(layout);
        var document = new LayoutDocument(store, _link);
        document.Load();
        var system = new SystemServices(_startup, _launcher, () => DisplayExe, () => HelperExe, () => 100_000_000);
        var model = new MainViewModel(
            _link, document, new EditorPreferences(FirstRunDone: firstRunDone), null, ["Arial"], _secrets, null, PreferencesPath, system);
        _models.Add(model);
        return model;
    }

    /// <summary>La disposition par défaut, réduite aux modules de ces types.</summary>
    private static DashboardLayout Layout(params string[] typeIds) => DashboardLayout.Default.WithPlacements(
        DashboardLayout.DefaultViewId, [.. DashboardLayout.Default.Views[0].Placements.Where(p => typeIds.Contains(p.TypeId))]);

    private static DashboardLayout WithoutClaude() => Layout("monitoring", "nowplaying", "clock", "network");

    private static DashboardLayout WithSettings(DashboardLayout layout, string typeId, string json) => layout.WithPlacements(
        layout.Views[0].Id, [.. layout.Views[0].Placements.Select(p => p.TypeId == typeId ? p with { Settings = Json(json) } : p)]);

    /// <summary>Passe les étapes (« Passer ») jusqu'à celle-ci.</summary>
    private static void GoTo(FirstRunViewModel wizard, FirstRunStep step)
    {
        for (var guard = 0; wizard.Step != step && guard < 20; guard++)
            wizard.Skip();
        Assert.Equal(step, wizard.Step);
    }

    private static ModulePlacement Placed(MainViewModel model, string typeId) =>
        model.Document.Current.Views[0].Placements.Single(p => p.TypeId == typeId);

    // ---- Quand il s'ouvre ----

    [Fact]
    public void FirstRun_ShownUntilDone()
    {
        var model = Open(firstRunDone: false);
        Assert.True(model.IsFirstRunVisible);
        Assert.False(model.ShowsTabs);
        Assert.Equal(FirstRunStep.Screen, model.FirstRun.Step);

        var done = Open(firstRunDone: true);
        Assert.False(done.IsFirstRunVisible);
        Assert.True(done.ShowsTabs);
    }

    [Fact]
    public void FirstRun_HidesTheGridOverlay_AndKeepsTheTabsInTheHeader()
    {
        var model = Open(firstRunDone: false);
        Assert.Same(model.LayoutTab, model.SelectedTab);

        Assert.False(model.IsGridVisible);                      // le damier n'a rien à faire sous l'assistant
        Assert.Equal(5, model.Tabs.Count);
    }

    [Fact]
    public void Closing_MidWizard_LeavesFirstRunNotDone_SoItReopens()
    {
        var model = Open();
        model.FirstRun.Skip();                                  // une étape faite, pas toutes
        model.FirstRun.Skip();
        Assert.False(model.Preferences.FirstRunDone);

        model.Dispose();
        _models.Remove(model);
        var reopened = new MainViewModel(
            _link, new LayoutDocument(new LayoutStore(LayoutPath), _link), EditorPreferences.Load(PreferencesPath));
        _models.Add(reopened);

        Assert.True(reopened.IsFirstRunVisible);
        Assert.Equal(FirstRunStep.Screen, reopened.FirstRun.Step);
    }

    // ---- 1. L'écran ----

    [Fact]
    public void ScreenStep_BusyWithTrcc_SaysToCloseIt()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(),
            SampleData.Status(LinkScreenState.Disconnected) with { Screen = new ScreenInfo(LinkScreenState.Disconnected, TrccBusy) });
        var model = Open();

        Assert.Equal("Le logiciel Thermalright utilise l'écran : ferme-le, puis attends quelques secondes.", model.FirstRun.ScreenMessage);
        Assert.True(model.FirstRun.ScreenMessageIsProblem);
        Assert.Equal("Écran non branché", model.FirstRun.Pill.Text);   // la pastille d'état est celle de la fenêtre
    }

    [Fact]
    public void ScreenStep_NotPlugged_SaysToPlugOrGoEmulated()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status(LinkScreenState.Disconnected));
        var model = Open();

        Assert.Equal("Branche l'écran ou continue en mode émulé.", model.FirstRun.ScreenMessage);
        Assert.True(model.FirstRun.ScreenMessageIsProblem);
    }

    [Fact]
    public void ScreenStep_FollowsTheServiceLive()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status(LinkScreenState.Disconnected));
        var model = Open();
        Assert.True(model.FirstRun.ScreenMessageIsProblem);

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status(LinkScreenState.Ready));   // il vient de brancher l'écran

        Assert.Equal(Texts.FirstRunScreenReady, model.FirstRun.ScreenMessage);
        Assert.False(model.FirstRun.ScreenMessageIsProblem);
        Assert.Equal("Écran branché", model.FirstRun.Pill.Text);
        Assert.True(model.FirstRun.Pill.Online);
    }

    // ---- 2. Le sens ----

    [Fact]
    public async Task Orientation_No_FlipsAndApplies()
    {
        var model = Open(Layout("clock"));                      // la rotation par défaut est 180°
        var wizard = model.FirstRun;
        GoTo(wizard, FirstRunStep.Orientation);
        Assert.Equal(180, model.Document.Current.Rotation);

        await wizard.AnswerNoAsync();

        Assert.Equal(0, model.Document.Current.Rotation);
        Assert.Equal(0, new LayoutStore(LayoutPath).Load().Rotation);   // appliqué tout de suite
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(FirstRunStep.Orientation, wizard.Step);            // on reste : on regarde si c'est mieux

        await wizard.AnswerNoAsync();                                   // toujours à l'envers : on le retourne de nouveau
        Assert.Equal(180, model.Document.Current.Rotation);
    }

    [Fact]
    public void Orientation_Yes_ContinuesWithoutChangingAnything()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Orientation);

        model.FirstRun.AnswerYes();

        Assert.Equal(FirstRunStep.Modules, model.FirstRun.Step);
        Assert.Equal(180, model.Document.Current.Rotation);
        Assert.Equal(0, _link.ReloadCount);
    }

    // ---- 3. Les modules ----

    [Fact]
    public void Modules_ListsTheSixTypes_CheckedFromTheCurrentLayout()
    {
        var model = Open(Layout("monitoring", "clock"));
        GoTo(model.FirstRun, FirstRunStep.Modules);

        Assert.Equal(["Claude Code", "Codex", "Monitoring PC", "En écoute", "Heure, date et météo", "Réseau"],
            model.FirstRun.Modules.Select(m => m.Name));
        Assert.Equal([false, false, true, false, true, false], model.FirstRun.Modules.Select(m => m.IsChecked));
    }

    [Fact]
    public async Task Modules_Uncheck_RemovesToShelf()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Modules);

        await model.FirstRun.SetModuleAsync("nowplaying", false);

        Assert.DoesNotContain(model.Document.Current.Views[0].Placements, p => p.TypeId == "nowplaying");
        Assert.Contains(model.Document.Current.Shelf!, p => p.TypeId == "nowplaying");     // rangé, avec ses réglages, pas détruit
        Assert.Contains(new LayoutStore(LayoutPath).Load().Shelf!, p => p.TypeId == "nowplaying");
        Assert.False(model.FirstRun.Modules.Single(m => m.TypeId == "nowplaying").IsChecked);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Modules_UncheckThroughTheItem_IsTheSameAsTheMethod()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Modules);

        model.FirstRun.Modules.Single(m => m.TypeId == "network").IsChecked = false;   // ce que fait la case dans la fenêtre
        await model.FirstRun.PendingModuleChange;

        Assert.DoesNotContain(model.Document.Current.Views[0].Placements, p => p.TypeId == "network");
    }

    [Fact]
    public async Task Modules_Check_PlacesItAndTakesBackItsShelvedSettings()
    {
        var layout = Layout("claudecode", "monitoring") with
        {
            Shelf = [new ModulePlacement(Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Normal, Json("""{"format":"12h"}"""))],
        };
        var model = Open(layout);
        GoTo(model.FirstRun, FirstRunStep.Modules);

        await model.FirstRun.SetModuleAsync("clock", true);

        Assert.Equal("12h", Placed(model, "clock").Settings.GetProperty("format").GetString());
        Assert.Empty(model.Document.Current.Shelf ?? []);
        Assert.True(model.FirstRun.Modules.Single(m => m.TypeId == "clock").IsChecked);
    }

    [Fact]
    public async Task Modules_CheckWithoutRoom_ShowsTheReason_AndStaysUnchecked()
    {
        var full = DashboardLayout.Default.WithPlacements(
            DashboardLayout.DefaultViewId, [DashboardLayout.Default.Views[0].Placements[0] with { Columns = 16, Rows = 4 }]);
        var model = Open(full);
        GoTo(model.FirstRun, FirstRunStep.Modules);
        var codex = model.FirstRun.Modules.Single(m => m.TypeId == "codex");

        await model.FirstRun.SetModuleAsync("codex", true);

        Assert.Equal("Plus de place libre : retire ou réduis un module.", codex.Reason);
        Assert.True(codex.HasReason);
        Assert.False(codex.IsChecked);
        Assert.Single(model.Document.Current.Views[0].Placements);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Modules_Change_IsOneUndoStepOnTheSharedHistory()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Modules);
        await model.FirstRun.SetModuleAsync("network", false);
        Assert.DoesNotContain(model.Document.Current.Views[0].Placements, p => p.TypeId == "network");

        await model.Session.UndoAsync();

        Assert.Contains(model.Document.Current.Views[0].Placements, p => p.TypeId == "network");
        Assert.True(model.FirstRun.Modules.Single(m => m.TypeId == "network").IsChecked);   // la case suit l'annulation
    }

    // ---- 4. Claude Code ----

    [Fact]
    public void ClaudeStep_SkippedWithoutClaudeModule()
    {
        var model = Open(WithoutClaude());
        var wizard = model.FirstRun;

        Assert.DoesNotContain(FirstRunStep.ClaudeCode, wizard.VisibleSteps);
        GoTo(wizard, FirstRunStep.Modules);
        wizard.Skip();

        Assert.Equal(FirstRunStep.Network, wizard.Step);        // Réseau, pas Claude Code
    }

    [Fact]
    public async Task ClaudeStep_OffersTheQuotasModAction()
    {
        var model = Open();
        var wizard = model.FirstRun;
        GoTo(wizard, FirstRunStep.ClaudeCode);

        var action = Assert.IsType<ActionFieldViewModel>(Assert.Single(wizard.ClaudeForm!.VisibleFields));   // que l'action, pas les réglages de la vue
        Assert.Equal("install-quotas", action.Key);
        Assert.Equal("Installer le mod des quotas", action.Label);

        await action.RequestAsync();                            // la question de confirmation du schéma…
        Assert.True(action.IsConfirming);
        Assert.Empty(_link.Actions);
        await action.ConfirmAsync();                            // …puis l'action du module

        Assert.Equal([(Placed(model, "claudecode").InstanceId, "install-quotas")], _link.Actions);
    }

    [Fact]
    public async Task ClaudeStep_UncheckingClaudeInTheModulesStep_RemovesTheStep_AndCheckingRestoresIt()
    {
        var model = Open();
        var wizard = model.FirstRun;
        GoTo(wizard, FirstRunStep.Modules);
        Assert.Equal("Étape 3 sur 8", wizard.ProgressText);

        await wizard.SetModuleAsync("claudecode", false);

        Assert.DoesNotContain(FirstRunStep.ClaudeCode, wizard.VisibleSteps);
        Assert.Equal("Étape 3 sur 7", wizard.ProgressText);
        wizard.Skip();
        Assert.Equal(FirstRunStep.Network, wizard.Step);

        await wizard.SetModuleAsync("claudecode", true);        // et la case la rend
        Assert.Contains(FirstRunStep.ClaudeCode, wizard.VisibleSteps);
        Assert.Equal(8, wizard.StepCount);
        Assert.Equal("Étape 5 sur 8", wizard.ProgressText);     // on est toujours sur Réseau, qui est maintenant la cinquième
    }

    // ---- 5 et 6. Réseau et Météo ----

    [Fact]
    public void NetworkAndWeatherSteps_SkippedWhenTheirModuleIsNotPlaced()
    {
        var model = Open(Layout("claudecode", "monitoring"));
        var wizard = model.FirstRun;

        Assert.Equal(
            [FirstRunStep.Screen, FirstRunStep.Orientation, FirstRunStep.Modules, FirstRunStep.ClaudeCode, FirstRunStep.Sensors, FirstRunStep.Startup],
            wizard.VisibleSteps);
        GoTo(wizard, FirstRunStep.ClaudeCode);
        wizard.Skip();
        Assert.Equal(FirstRunStep.Sensors, wizard.Step);
    }

    [Fact]
    public void NetworkStep_ShowsTheUsefulFieldsOnly()
    {
        var model = Open(WithSettings(Layout("claudecode", "network"), "network", """{"source":"unifi"}"""));
        GoTo(model.FirstRun, FirstRunStep.Network);

        var keys = model.FirstRun.NetworkForm!.VisibleFields.Select(f => f.Key).ToList();

        Assert.Equal(["source", "unifi.host", "unifi.apiKey"], keys);     // sans « Oublier le certificat », qui n'a rien à faire ici
    }

    [Fact]
    public async Task NetworkStep_Continue_WritesTheTypedText()
    {
        var model = Open(WithSettings(Layout("network"), "network", """{"source":"unifi"}"""));
        GoTo(model.FirstRun, FirstRunStep.Network);
        model.FirstRun.NetworkForm!.Field<TextFieldViewModel>("unifi.host").Text = "192.168.1.1";   // tapé, pas validé

        await model.FirstRun.ContinueAsync();

        Assert.Equal("192.168.1.1", Placed(model, "network").Settings.GetProperty("unifi").GetProperty("host").GetString());
        Assert.Equal(FirstRunStep.Sensors, model.FirstRun.Step);          // pas de module Heure : Météo est sautée
    }

    [Fact]
    public void WeatherStep_ShowsTheUnitAndTheCity()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Weather);

        Assert.Equal(["unit", "location"], model.FirstRun.WeatherForm!.VisibleFields.Select(f => f.Key));
    }

    [Fact]
    public async Task Skip_AppliesNothing()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Network);
        var source = model.FirstRun.NetworkForm!.Field<ChoiceFieldViewModel>("source");
        await source.SelectAsync(source.Options.Single(o => o.Value == "freebox"));   // un réglage déjà appliqué en direct
        var reloads = _link.ReloadCount;

        model.FirstRun.Skip();

        Assert.Equal(FirstRunStep.Weather, model.FirstRun.Step);
        Assert.Equal(reloads, _link.ReloadCount);               // « Passer » n'a rien écrit de plus
        Assert.Equal("freebox", Placed(model, "network").Settings.GetProperty("source").GetString());
    }

    // ---- Le compteur ----

    [Fact]
    public void Progress_CountsVisibleSteps()
    {
        var model = Open(WithoutClaude());
        var wizard = model.FirstRun;

        GoTo(wizard, FirstRunStep.Network);

        Assert.Equal(7, wizard.StepCount);
        Assert.Equal(4, wizard.StepNumber);
        Assert.Equal("Étape 4 sur 7", wizard.ProgressText);
    }

    [Fact]
    public void Progress_WithEveryModule_Counts8Steps()
    {
        var model = Open();
        var wizard = model.FirstRun;
        Assert.Equal("Étape 1 sur 8", wizard.ProgressText);

        GoTo(wizard, FirstRunStep.Modules);
        Assert.Equal("Étape 3 sur 8", wizard.ProgressText);

        GoTo(wizard, FirstRunStep.Startup);
        Assert.Equal("Étape 8 sur 8", wizard.ProgressText);
        Assert.True(wizard.IsLastStep);
        Assert.Equal("Terminer", wizard.ContinueText);
    }

    [Fact]
    public void Progress_ContinueText_IsContinuerUntilTheLastStep()
    {
        var model = Open();
        Assert.Equal("Continuer", model.FirstRun.ContinueText);
        Assert.False(model.FirstRun.IsLastStep);
        GoTo(model.FirstRun, FirstRunStep.Sensors);
        Assert.Equal("Continuer", model.FirstRun.ContinueText);
    }

    // ---- 7. Les températures du processeur ----

    [Fact]
    public void SensorsStep_ReusesTheSettingsBlock()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { Sensors = new SensorInfo(false, false, false) });
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Sensors);

        Assert.Same(model.SettingsTab, model.FirstRun.Settings);       // le même bloc que Paramètres, pas une copie
        Assert.Equal("Le pilote PawnIO n'est pas installé.", model.FirstRun.Settings.SensorsText);
        Assert.True(model.FirstRun.Settings.CanInstallSensors);
    }

    // ---- 8. Le démarrage avec Windows ----

    [Fact]
    public async Task StartupStep_DefaultsToOn_ButRegistersNothingUntilContinue()
    {
        var model = Open();
        var wizard = model.FirstRun;

        GoTo(wizard, FirstRunStep.Startup);

        Assert.True(wizard.StartupWanted);                      // l'interrupteur est sur « oui »…
        Assert.Equal(0, _startup.EnableCalls);                  // …mais rien n'est inscrit tant qu'on n'a pas continué
        Assert.False(_startup.IsEnabled);

        await wizard.ContinueAsync();

        Assert.Equal(1, _startup.EnableCalls);
        Assert.Equal(DisplayExe, _startup.EnabledWith);
        Assert.True(model.Preferences.FirstRunDone);            // dernière étape : « Terminer »
    }

    [Fact]
    public void StartupStep_Skip_LeavesItOff()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Startup);

        model.FirstRun.Skip();

        Assert.Equal(0, _startup.EnableCalls);
        Assert.False(_startup.IsEnabled);
        Assert.True(model.Preferences.FirstRunDone);            // « Passer » sur la dernière étape termine aussi
    }

    [Fact]
    public async Task StartupStep_SwitchedOff_ThenContinue_RegistersNothing()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Startup);
        model.FirstRun.StartupWanted = false;

        await model.FirstRun.ContinueAsync();

        Assert.Equal(0, _startup.EnableCalls);
        Assert.False(_startup.IsEnabled);
        Assert.True(model.Preferences.FirstRunDone);
    }

    [Fact]
    public async Task StartupStep_WindowsRefuses_StaysOnTheStepAndSaysWhy()
    {
        _startup.Failure = "Accès refusé";
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Startup);

        await model.FirstRun.ContinueAsync();

        Assert.Equal(FirstRunStep.Startup, model.FirstRun.Step);
        Assert.False(model.Preferences.FirstRunDone);
        Assert.Equal("Windows n'a pas permis de changer le démarrage : Accès refusé", model.SettingsTab.StartupMessage);
    }

    // ---- Terminer ----

    [Fact]
    public async Task Finish_SavesPreference()
    {
        var model = Open();
        GoTo(model.FirstRun, FirstRunStep.Startup);
        Assert.False(model.Preferences.FirstRunDone);

        await model.FirstRun.ContinueAsync();

        Assert.True(model.Preferences.FirstRunDone);
        Assert.True(EditorPreferences.Load(PreferencesPath).FirstRunDone);   // écrit dans settings-app.json
        Assert.False(model.IsFirstRunVisible);
        Assert.True(model.ShowsTabs);
        Assert.Same(model.LayoutTab, model.SelectedTab);
    }

    [Fact]
    public async Task Finish_KeepsTheOtherPreferences()
    {
        var model = Open();
        model.SettingsTab.RealTimePreview = true;
        GoTo(model.FirstRun, FirstRunStep.Startup);

        await model.FirstRun.ContinueAsync();

        Assert.True(model.Preferences.RealTimePreview);
        Assert.True(model.Preferences.FirstRunDone);
    }

    // ---- Relancer l'assistant ----

    [Fact]
    public async Task Restart_FromSettings_ShowsTheWizardAgainFromTheFirstStep()
    {
        var model = Open(firstRunDone: true);
        model.SelectedTab = model.SettingsTab;
        model.FirstRun.StartupWanted = false;
        Assert.False(model.IsFirstRunVisible);

        model.SettingsTab.RestartFirstRunCommand.Execute(null);

        Assert.False(model.Preferences.FirstRunDone);
        Assert.False(EditorPreferences.Load(PreferencesPath).FirstRunDone);   // et elle se rouvrirait au prochain lancement
        Assert.True(model.IsFirstRunVisible);
        Assert.Equal(FirstRunStep.Screen, model.FirstRun.Step);
        Assert.True(model.FirstRun.StartupWanted);
        Assert.Same(model.SettingsTab, model.SelectedTab);                    // l'onglet choisi n'est pas perdu
        GoTo(model.FirstRun, FirstRunStep.Startup);
        await model.FirstRun.ContinueAsync();
        Assert.False(model.IsFirstRunVisible);
    }

    // ---- Dans la vraie fenêtre ----

    private MainWindow Show(MainViewModel model)
    {
        _window = new MainWindow { DataContext = model, Width = 1440, Height = 900 };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        return _window;
    }

    private static IEnumerable<string> VisibleTexts(MainWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!);

    private static IEnumerable<string> VisibleButtons(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string).Select(b => (string)b.Content!);

    private static void Click(MainWindow window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, content));
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Window_ShowsTheWizardInPlaceOfTheTabs_WithHeaderAndStrip()
    {
        var window = Show(Open());

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("Étape 1 sur 8", texts);
        Assert.Contains("L'écran", texts);
        Assert.Contains(Texts.FirstRunScreenReady, texts);
        Assert.Contains("Écran branché", texts);
        foreach (var tab in new[] { "Disposition", "Personnalisation", "Modules", "Raccourcis", "Paramètres" })
            Assert.Contains(tab, texts);                                          // l'en-tête reste
        Assert.Contains(window.GetVisualDescendants().OfType<ScreenStrip>(), s => s.IsEffectivelyVisible);   // la bande reste
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "TabContent" && c.IsEffectivelyVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<GridOverlay>(), g => g.IsEffectivelyVisible);
        Assert.Contains("Continuer", VisibleButtons(window));
        Assert.Contains("Passer", VisibleButtons(window));
    }

    [AvaloniaFact]
    public void Window_WithFirstRunDone_ShowsTheTabs()
    {
        var window = Show(Open(firstRunDone: true));

        Assert.DoesNotContain(VisibleTexts(window), t => t.StartsWith("Étape "));
        Assert.Contains(window.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "TabContent" && c.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Window_EveryStep_ShowsItsTitleAndItsOwnContent()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { Sensors = new SensorInfo(false, false, false) });
        var model = Open();
        var window = Show(model);
        var expected = new (string Title, string Marker)[]
        {
            ("L'écran", Texts.FirstRunScreenReady),
            ("Le sens", "Sur ton écran, le texte est-il à l'endroit ?"),
            ("Les modules", "Claude Code"),
            ("Claude Code", "Installer le mod des quotas"),
            ("Réseau", "Source du débit"),
            ("Météo", "Ville de la météo"),
            ("Températures du processeur", "Installer le pilote des températures"),
            ("Démarrage avec Windows", "Démarrer avec Windows"),
        };

        for (var i = 0; i < expected.Length; i++)
        {
            var texts = VisibleTexts(window).Concat(VisibleButtons(window)).ToList();
            Assert.Contains($"Étape {i + 1} sur 8", texts);
            Assert.Contains(expected[i].Title, texts);
            Assert.Contains(expected[i].Marker, texts);
            if (i < expected.Length - 1)
                Click(window, "Passer");
        }

        Assert.Contains("Terminer", VisibleButtons(window));
    }

    [AvaloniaFact]
    public void Window_OrientationStep_HasYesAndNoButtons_InPlaceOfContinue()
    {
        var model = Open();
        var window = Show(model);
        Click(window, "Passer");

        Assert.Contains("Sur ton écran, le texte est-il à l'endroit ?", VisibleTexts(window));
        Assert.DoesNotContain("Continuer", VisibleButtons(window));               // « Oui » est la suite
        Click(window, "Non, retourner");
        Assert.Equal(FirstRunStep.Orientation, model.FirstRun.Step);
        Click(window, "Oui");
        Assert.Equal(FirstRunStep.Modules, model.FirstRun.Step);
    }

    [AvaloniaFact]
    public async Task Window_ModulesStep_HasSixCheckBoxes_ThatEditTheLayout()
    {
        var model = Open();
        var window = Show(model);
        Click(window, "Passer");
        Click(window, "Passer");

        var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(b => b.IsEffectivelyVisible).ToList();
        Assert.Equal(6, boxes.Count);
        Assert.Equal(5, boxes.Count(b => b.IsChecked == true));

        boxes.Single(b => Equals(b.Content, "Réseau")).IsChecked = false;
        await model.FirstRun.PendingModuleChange;

        Assert.DoesNotContain(model.Document.Current.Views[0].Placements, p => p.TypeId == "network");
    }

    [AvaloniaFact]
    public void Window_Finish_BringsTheTabsBack()
    {
        var model = Open(Layout("clock"));
        var window = Show(model);
        for (var i = 0; i < model.FirstRun.StepCount - 1; i++)
            Click(window, "Passer");

        Click(window, "Terminer");

        Assert.True(model.Preferences.FirstRunDone);
        Assert.Contains(window.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "TabContent" && c.IsEffectivelyVisible);
        Assert.DoesNotContain(VisibleTexts(window), t => t.StartsWith("Étape "));
    }

    [AvaloniaFact]
    public void Window_SettingsTab_HasRestartButton()
    {
        var model = Open(firstRunDone: true);
        model.SelectedTab = model.SettingsTab;
        var window = Show(model);

        Click(window, "Relancer l'assistant");

        Assert.True(model.IsFirstRunVisible);
        Assert.Contains("Étape 1 sur 8", VisibleTexts(window));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
