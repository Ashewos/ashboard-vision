using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Paramètres sans fenêtre : faux lien, faux registre, faux lancement élevé, vrai fichier dans un dossier temporaire.</summary>
public sealed class SettingsTabTests : IDisposable
{
    private const string DisplayExe = @"C:\Trofeo\Trofeo.Display.exe";
    private const string HelperExe = @"C:\Trofeo\Trofeo.SensorHelper.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-parameters-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeFilePicker _picker = new();
    private readonly FakeStartupRegistration _startup = new();
    private readonly FakeElevatedLauncher _launcher = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;
    private PreferencesStore _preferences = null!;
    private string? _displayExe = DisplayExe;
    private string? _helperExe = HelperExe;
    private long _ownBytes = 100_663_296;

    public SettingsTabTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PreferencesPath => Path.Combine(_dir, "settings-app.json");

    private SettingsTabViewModel Open(DashboardLayout? layout = null, EditorPreferences? preferences = null)
    {
        _store.Save(layout ?? DashboardLayout.Default);
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        _preferences = new PreferencesStore(preferences ?? new EditorPreferences(), PreferencesPath);
        return new SettingsTabViewModel(_link, _session, _preferences,
            new SystemServices(_startup, _launcher, () => _displayExe, () => _helperExe, () => _ownBytes), _picker);
    }

    private static ServiceStatus Status(SensorInfo? sensors = null, ProcessInfo? process = null) =>
        SampleData.Status() with { Sensors = sensors ?? new SensorInfo(true, true, true), Process = process ?? new ProcessInfo(1_000_000, 2_000_000, 1.5) };

    private void SetStatus(ServiceStatus status) => _link.Set(LinkState.Connected, SampleData.Welcome(), status);

    // ---- Orientation ----

    [Fact]
    public async Task Orientation_Change_Saves180()
    {
        var tab = Open(DashboardLayout.Default with { Rotation = 0 });
        Assert.True(tab.IsUpright);
        Assert.False(tab.IsFlipped);

        await tab.SetRotationAsync(180);

        Assert.Equal(180, _document.Current.Rotation);
        Assert.Equal(180, _store.Load().Rotation);
        Assert.True(tab.IsFlipped);
        Assert.False(tab.IsUpright);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Orientation_SameChoice_WritesNothing()
    {
        var tab = Open(); // 180° par défaut

        await tab.SetRotationAsync(180);

        Assert.Equal(0, _link.ReloadCount);
        Assert.True(tab.IsFlipped);
    }

    [Fact]
    public async Task Orientation_BackTo0_IsUndoable()
    {
        var tab = Open();
        await tab.SetRotationAsync(0);
        Assert.True(tab.IsUpright);

        await _session.UndoAsync();

        Assert.True(tab.IsFlipped);
        Assert.Equal(180, _document.Current.Rotation);
    }

    [Fact]
    public void Orientation_Texts_AreTheOnesOfTheSpec()
    {
        Assert.Equal("À l'endroit (0°)", Texts.SettingsUpright);
        Assert.Equal("Retourné (180°)", Texts.SettingsFlipped);
    }

    // ---- Démarrer avec Windows ----

    [Fact]
    public void Startup_Toggle_UsesRegistrationWithQuotedPath()
    {
        var tab = Open();
        Assert.False(tab.StartupEnabled);
        Assert.True(tab.CanToggleStartup);

        tab.StartupEnabled = true;

        Assert.Equal(DisplayExe, _startup.EnabledWith);
        Assert.True(_startup.IsEnabled);
        Assert.True(tab.StartupEnabled);
        // La valeur du registre porte le chemin entre guillemets (un chemin avec des espaces ne doit pas être coupé).
        Assert.Equal("\"C:\\Program Files\\Trofeo\\Trofeo.Display.exe\"", RunKeyStartup.Command(@"C:\Program Files\Trofeo\Trofeo.Display.exe"));

        tab.StartupEnabled = false;

        Assert.Equal(1, _startup.DisableCalls);
        Assert.False(_startup.IsEnabled);
        Assert.False(tab.StartupEnabled);
    }

    [Fact]
    public void Startup_AlreadyEnabled_IsShownOn()
    {
        _startup.IsEnabled = true;

        var tab = Open();

        Assert.True(tab.StartupEnabled);
    }

    [Fact]
    public void Startup_DisplayExeNotFound_DisablesTheSwitchAndSays()
    {
        _displayExe = null;
        var tab = Open();

        Assert.False(tab.CanToggleStartup);
        Assert.Equal("Le service d'affichage est introuvable : démarrage avec Windows impossible.", tab.StartupMessage);
        tab.StartupEnabled = true;
        Assert.Equal(0, _startup.EnableCalls);
        Assert.False(tab.StartupEnabled);
    }

    [Fact]
    public void Startup_DisplayExeNotFound_ButAlreadyEnabled_CanStillBeTurnedOff()
    {
        _displayExe = null;
        _startup.IsEnabled = true;
        var tab = Open();

        Assert.True(tab.CanToggleStartup);   // on peut toujours retirer ce qui est inscrit
        tab.StartupEnabled = false;
        Assert.Equal(1, _startup.DisableCalls);
    }

    [Fact]
    public void Startup_WindowsRefuses_SaysAndKeepsTheSwitchOff()
    {
        _startup.Failure = "Accès refusé.";
        var tab = Open();

        tab.StartupEnabled = true;

        Assert.False(tab.StartupEnabled);
        Assert.Equal("Windows n'a pas permis de changer le démarrage : Accès refusé.", tab.StartupMessage);
    }

    [Fact]
    public void RunKeyStartup_Command_QuotesThePath() =>
        Assert.Equal("\"D:\\a b\\Trofeo.Display.exe\"", RunKeyStartup.Command(@"D:\a b\Trofeo.Display.exe"));

    // ---- Images par seconde ----

    [Fact]
    public async Task MaxFps_Slider_Saves()
    {
        var tab = Open();
        Assert.Equal(12, tab.MaxFpsValue);
        Assert.Equal("12 (le maximum de l'écran)", tab.MaxFpsText);

        tab.MaxFpsValue = 8;
        Assert.Equal(0, _link.ReloadCount);      // le curseur tiré n'enregistre rien
        Assert.Equal("8", tab.MaxFpsText);
        await tab.CommitMaxFpsAsync();

        Assert.Equal(8, _document.Current.MaxFps);
        Assert.Equal(8, _store.Load().MaxFps);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task MaxFps_Released_WithoutMoving_WritesNothing()
    {
        var tab = Open();

        await tab.CommitMaxFpsAsync();

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task MaxFps_Back_To12_IsTheServiceDefault()
    {
        var tab = Open(DashboardLayout.Default with { MaxFps = 6 });
        Assert.Equal(6, tab.MaxFpsValue);

        tab.MaxFpsValue = 12;
        await tab.CommitMaxFpsAsync();

        Assert.Null(_document.Current.MaxFps);
    }

    [Fact]
    public async Task MaxFps_IsClampedTo1To12_AndRounded()
    {
        var tab = Open();
        tab.MaxFpsValue = 0.2;
        Assert.Equal(1, tab.MaxFpsValue);
        tab.MaxFpsValue = 99;
        Assert.Equal(12, tab.MaxFpsValue);
        tab.MaxFpsValue = 4.6;
        await tab.CommitMaxFpsAsync();
        Assert.Equal(5, _document.Current.MaxFps);
    }

    [Fact]
    public async Task MaxFps_OneUndoStep()
    {
        var tab = Open();
        tab.MaxFpsValue = 3;
        tab.MaxFpsValue = 5;
        tab.MaxFpsValue = 8;
        await tab.CommitMaxFpsAsync();

        await _session.UndoAsync();

        Assert.Null(_document.Current.MaxFps);
        Assert.Equal(12, tab.MaxFpsValue);
    }

    // ---- Aperçu ----

    [Fact]
    public void RealTime_Toggle_SavesPreferencesAndTellsTheLink()
    {
        var tab = Open();
        Assert.False(tab.RealTimePreview);

        tab.RealTimePreview = true;

        Assert.True(_link.RealTime);
        Assert.True(_preferences.Current.RealTimePreview);
        Assert.True(EditorPreferences.Load(PreferencesPath).RealTimePreview);

        tab.RealTimePreview = false;

        Assert.False(_link.RealTime);
        Assert.False(EditorPreferences.Load(PreferencesPath).RealTimePreview);
    }

    [Fact]
    public void RealTime_StartsFromThePreference()
    {
        var tab = Open(preferences: new EditorPreferences(RealTimePreview: true));

        Assert.True(tab.RealTimePreview);
    }

    [Fact]
    public void RealTime_KeepsTheOtherPreferences()
    {
        var tab = Open(preferences: new EditorPreferences(FirstRunDone: true));

        tab.RealTimePreview = true;

        Assert.True(EditorPreferences.Load(PreferencesPath).FirstRunDone);
    }

    // ---- Températures du processeur ----

    [Theory]
    [InlineData(false, false, false, "Le pilote PawnIO n'est pas installé.", true)]
    [InlineData(false, true, true, "Le pilote PawnIO n'est pas installé.", true)]
    [InlineData(true, false, false, "Le pilote est installé, mais l'assistant des capteurs ne tourne pas.", true)]
    [InlineData(true, true, false, "Le pilote est installé, mais l'assistant des capteurs ne tourne pas.", true)]
    [InlineData(true, false, true, "Le pilote est installé, mais l'assistant des capteurs ne tourne pas.", true)]
    [InlineData(true, true, true, "Actives.", false)]
    public void Sensors_StatusInOneSentence(bool pawnIo, bool helperInstalled, bool helperConnected, string expected, bool canInstall)
    {
        SetStatus(Status(new SensorInfo(pawnIo, helperInstalled, helperConnected)));
        var tab = Open();

        Assert.Equal(expected, tab.SensorsText);
        Assert.Equal(canInstall, tab.CanInstallSensors);
    }

    [Fact]
    public void Sensors_StatusFollowsTheService()
    {
        SetStatus(Status(new SensorInfo(false, false, false)));
        var tab = Open();
        Assert.Equal("Le pilote PawnIO n'est pas installé.", tab.SensorsText);

        SetStatus(Status(new SensorInfo(true, true, true)));

        Assert.Equal("Actives.", tab.SensorsText);
        Assert.False(tab.CanInstallSensors);
    }

    [Fact]
    public async Task Sensors_InstallWithoutPawnIo_AsksForInstaller()
    {
        SetStatus(Status(new SensorInfo(false, false, false)));
        var tab = Open();
        _picker.Result = @"C:\Users\Alex\Downloads\PawnIO_setup.exe";

        await tab.InstallSensorsAsync();

        Assert.Equal(1, _picker.Calls);
        Assert.Equal("Choisis PawnIO_setup.exe", _picker.Title);
        Assert.Equal(["exe"], _picker.Extensions);
        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal(HelperExe, launched.Exe);
        Assert.Equal("install --pawnio \"C:\\Users\\Alex\\Downloads\\PawnIO_setup.exe\"", launched.Arguments);
    }

    [Fact]
    public async Task Sensors_InstallWithPawnIo_DoesNotAsk()
    {
        SetStatus(Status(new SensorInfo(true, false, false)));
        var tab = Open();

        await tab.InstallSensorsAsync();

        Assert.Equal(0, _picker.Calls);
        Assert.Equal("install", Assert.Single(_launcher.Launched).Arguments);
    }

    [Fact]
    public async Task Sensors_InstallerPickCancelled_LaunchesNothing()
    {
        SetStatus(Status(new SensorInfo(false, false, false)));
        var tab = Open();
        _picker.Result = null;

        await tab.InstallSensorsAsync();

        Assert.Empty(_launcher.Launched);
        Assert.Null(tab.SensorsMessage);
    }

    [Fact]
    public async Task Sensors_PickerFails_SaysWhy()
    {
        SetStatus(Status(new SensorInfo(false, false, false)));
        var tab = Open();
        _picker.Failure = "Le sélecteur de fichier n'a pas pu s'ouvrir : x";

        await tab.InstallSensorsAsync();

        Assert.Equal("Le sélecteur de fichier n'a pas pu s'ouvrir : x", tab.SensorsMessage);
        Assert.True(tab.SensorsMessageIsError);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Sensors_ElevationRefused_Says()
    {
        SetStatus(Status(new SensorInfo(true, false, false)));
        var tab = Open();
        _launcher.Accept = false;

        await tab.InstallSensorsAsync();

        Assert.Equal("Installation annulée : Windows n'a pas reçu l'autorisation.", tab.SensorsMessage);
        Assert.True(tab.SensorsMessageIsError);
    }

    [Fact]
    public async Task Sensors_InstallStarted_Says_AndIsClearedWhenSensorsWork()
    {
        SetStatus(Status(new SensorInfo(true, false, false)));
        var tab = Open();

        await tab.InstallSensorsAsync();

        Assert.Equal("Installation lancée : l'état se met à jour dans un instant.", tab.SensorsMessage);
        Assert.False(tab.SensorsMessageIsError);

        SetStatus(Status(new SensorInfo(true, true, true)));

        Assert.Null(tab.SensorsMessage);
    }

    [Fact]
    public async Task Sensors_HelperNotFound_Says()
    {
        SetStatus(Status(new SensorInfo(true, false, false)));
        _helperExe = null;
        var tab = Open();

        await tab.InstallSensorsAsync();

        Assert.Equal("L'assistant des capteurs (Trofeo.SensorHelper.exe) est introuvable.", tab.SensorsMessage);
        Assert.Empty(_launcher.Launched);
    }

    // ---- Consommation ----

    [Fact]
    public void Consumption_FormatsMegabytesAndPercent()
    {
        SetStatus(Status(process: new ProcessInfo(46_137_344, 60_000_000, 0.83)));
        var tab = Open();

        Assert.Equal("Service d'affichage : 44 Mo, 0,8 % du processeur", tab.ServiceUsageText);
        Assert.Equal("Réglages : 96 Mo, libérés à la fermeture", tab.OwnUsageText);
    }

    [Theory]
    [InlineData(46_137_344, 1.0, "Service d'affichage : 44 Mo, 1,0 % du processeur")]
    [InlineData(104_857_600, 12.34, "Service d'affichage : 100 Mo, 12,3 % du processeur")]
    [InlineData(1_572_864, 0.0, "Service d'affichage : 2 Mo, 0,0 % du processeur")]
    public void Consumption_RoundsMegabytes_AndKeepsOneDecimal(long bytes, double cpu, string expected)
    {
        SetStatus(Status(process: new ProcessInfo(bytes, 0, cpu)));

        Assert.Equal(expected, Open().ServiceUsageText);
    }

    [Fact]
    public void Consumption_FollowsTheStatus_EverySecond()
    {
        SetStatus(Status(process: new ProcessInfo(46_137_344, 0, 0.83)));
        var tab = Open();
        _ownBytes = 52_428_800;

        SetStatus(Status(process: new ProcessInfo(94_371_840, 0, 2.5)));

        Assert.Equal("Service d'affichage : 90 Mo, 2,5 % du processeur", tab.ServiceUsageText);
        Assert.Equal("Réglages : 50 Mo, libérés à la fermeture", tab.OwnUsageText);
    }

    [Fact]
    public void Consumption_Goal_IsWritten() =>
        Assert.Equal("Objectif : moins de 100 Mo et environ 1 % en fond.", Texts.UsageGoal);

    // ---- Partager ----

    private static string ExportedJson(DashboardLayout layout) => LayoutTransfer.Export(layout);

    [Fact]
    public async Task Export_WritesTheLayoutInTheChosenFile()
    {
        var tab = Open();
        var target = Path.Combine(_dir, "ma-disposition.trofeo.json");
        _picker.SaveResult = target;

        await tab.ExportAsync();

        Assert.Equal(1, _picker.SaveCalls);
        Assert.Equal("disposition.trofeo.json", _picker.SuggestedName);
        var imported = LayoutTransfer.Import(File.ReadAllText(target));
        Assert.NotNull(imported.Layout);
        Assert.Equal(_document.Current.Placements.Count, imported.Layout!.Placements.Count);
        Assert.Equal("Disposition exportée : ma-disposition.trofeo.json", tab.ShareMessage);
        Assert.False(tab.ShareMessageIsError);
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing()
    {
        var tab = Open();
        _picker.SaveResult = null;

        await tab.ExportAsync();

        Assert.Null(tab.ShareMessage);
        Assert.Empty(Directory.GetFiles(_dir, "*.trofeo.json"));
    }

    [Fact]
    public async Task Export_WithoutExtension_AddsTheTrofeoOne()
    {
        var tab = Open();
        _picker.SaveResult = Path.Combine(_dir, "partage");

        await tab.ExportAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "partage.trofeo.json")));
    }

    [Fact]
    public async Task Export_WriteFails_SaysWhy()
    {
        var tab = Open();
        _picker.SaveResult = Path.Combine(_dir, "dossier-absent", "x.trofeo.json");

        await tab.ExportAsync();

        Assert.True(tab.ShareMessageIsError);
        Assert.StartsWith("Le fichier n'a pas pu être écrit : ", tab.ShareMessage);
    }

    [Fact]
    public async Task Import_Invalid_ShowsReason_AndKeepsCurrent()
    {
        var tab = Open();
        var before = File.ReadAllText(Path.Combine(_dir, "layout.json"));
        var bad = Path.Combine(_dir, "pas-une-disposition.json");
        File.WriteAllText(bad, """{"hello":"world"}""");
        _picker.Result = bad;

        await tab.ImportAsync();

        Assert.Equal("Ce fichier n'est pas une disposition Ashboard Vision.", tab.ShareMessage);
        Assert.True(tab.ShareMessageIsError);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "layout.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "layout.json.avant-import")));
        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal(DashboardLayout.Default.Placements.Count, _document.Current.Placements.Count);
    }

    [Fact]
    public async Task Import_InvalidLayout_ShowsTheValidatorReason()
    {
        var tab = Open();
        var bad = Path.Combine(_dir, "chevauche.trofeo.json");
        File.WriteAllText(bad, "{\"Format\":\"" + LayoutTransfer.Format + "\",\"Layout\":{\"Rotation\":90,\"Placements\":[]}}");
        _picker.Result = bad;

        await tab.ImportAsync();

        Assert.True(tab.ShareMessageIsError);
        Assert.False(string.IsNullOrWhiteSpace(tab.ShareMessage));
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Import_Unreadable_SaysWhy()
    {
        var tab = Open();
        _picker.Result = Path.Combine(_dir, "absent.trofeo.json");

        await tab.ImportAsync();

        Assert.True(tab.ShareMessageIsError);
        Assert.StartsWith("Le fichier n'a pas pu être lu : ", tab.ShareMessage);
    }

    [Fact]
    public async Task Import_Cancelled_DoesNothing()
    {
        var tab = Open();
        _picker.Result = null;

        await tab.ImportAsync();

        Assert.Null(tab.ShareMessage);
        Assert.Equal(0, _link.ReloadCount);
    }

    private static DashboardLayout Shared() => new(
        0,
        [new ModulePlacement(Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Large, JsonDocument.Parse("""{"format":"12h"}""").RootElement.Clone())])
    {
        MaxFps = 6,
    };

    [Fact]
    public async Task Import_Valid_BacksUpThenSaves()
    {
        var tab = Open();
        var layoutPath = Path.Combine(_dir, "layout.json");
        var before = File.ReadAllText(layoutPath);
        var shared = Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(shared, ExportedJson(Shared()));
        _picker.Result = shared;

        await tab.ImportAsync();

        Assert.Equal(before, File.ReadAllText(layoutPath + ".avant-import"));   // l'ancienne disposition est gardée
        var now = _store.Load();
        Assert.Equal(180, now.Rotation);   // l'orientation est celle de CE PC (le fichier partagé disait 0), voir Import_KeepsThisPcsOrientation
        Assert.Equal("clock", Assert.Single(now.Placements).TypeId);
        Assert.Equal(6, now.MaxFps);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal("Disposition importée. Ctrl+Z la défait.", tab.ShareMessage);
        Assert.False(tab.ShareMessageIsError);
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(180, 0)]
    public async Task Import_KeepsThisPcsOrientation(int mine, int shared)
    {
        // Le sens de l'écran dépend de la façon dont CE PC l'a monté, pas de celui de l'ami qui a exporté sa disposition.
        var tab = Open(DashboardLayout.Default with { Rotation = mine });
        var file = Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(file, ExportedJson(Shared() with { Rotation = shared }));
        _picker.Result = file;

        await tab.ImportAsync();

        Assert.Equal("clock", Assert.Single(_document.Current.Placements).TypeId);   // la disposition, elle, est bien importée
        Assert.Equal(mine, _document.Current.Rotation);
        Assert.Equal(mine, _store.Load().Rotation);
    }

    // ---- Import : les modules qui touchent une clé de ce PC gardent les réglages de ce PC ----

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

    private static ModulePlacement Network(Guid id, string settings, int column = 12, int row = 2) =>
        new(id, "network", column, row, 4, 2, TextSize.Normal, Json(settings));

    private static readonly Guid LocalNetwork = new("a2002423-c807-4403-9c5a-2a983fb6a371");

    private const string Hostile = """{"source":"unifi","unifi":{"host":"attaquant.example"}}""";

    private async Task ImportAsync(SettingsTabViewModel tab, DashboardLayout shared)
    {
        var file = Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(file, ExportedJson(shared));
        _picker.Result = file;
        await tab.ImportAsync();
    }

    [Fact]
    public async Task Import_ModuleWithASecretField_KeepsThisPcsSettings()
    {
        var tab = Open();
        var mine = Compact(_document.Current.Placements.Single(p => p.TypeId == "network").Settings);

        await ImportAsync(tab, new DashboardLayout(180, [Network(Guid.NewGuid(), Hostile)]));

        var network = Assert.Single(_document.Current.Placements);
        Assert.Equal(mine, Compact(network.Settings));
        // L'hôte du fichier reçu n'atteint jamais layout.json : la clé UniFi de ce PC ne peut pas partir vers lui.
        Assert.DoesNotContain("attaquant.example", File.ReadAllText(_store.FilePath));
        Assert.Equal("Disposition importée. Ctrl+Z la défait. Réglages gardés pour Réseau : ils touchent une clé de ce PC.", tab.ShareMessage);
        Assert.False(tab.ShareMessageIsError);
    }

    [Fact]
    public async Task Import_ModuleWithASecretField_TakesTheLocalInstanceOfTheSameTypeEvenWithAnotherId()
    {
        var tab = Open();

        await ImportAsync(tab, new DashboardLayout(180, [Network(new Guid("11111111-0000-0000-0000-000000000001"), Hostile)]));

        var network = Assert.Single(_document.Current.Placements);
        Assert.Equal(new Guid("11111111-0000-0000-0000-000000000001"), network.InstanceId); // l'emplacement importé est gardé
        Assert.Equal("""{"source":"pc"}""", Compact(network.Settings));
    }

    [Fact]
    public async Task Import_ModuleWithASecretFieldAndNoLocalCounterpart_ClearsItsSettings()
    {
        var clock = new ModulePlacement(Guid.NewGuid(), "clock", 0, 0, 4, 2, TextSize.Normal, Json("{}"));
        var tab = Open(new DashboardLayout(180, [clock])); // aucun module Réseau sur ce PC

        await ImportAsync(tab, new DashboardLayout(180, [Network(Guid.NewGuid(), Hostile)]));

        var network = Assert.Single(_document.Current.Placements);
        Assert.Equal("{}", Compact(network.Settings));
        Assert.DoesNotContain("attaquant.example", File.ReadAllText(_store.FilePath));
        Assert.Contains("Réglages gardés pour Réseau", tab.ShareMessage);
    }

    [Fact]
    public async Task Import_ShelvedModuleWithASecretField_IsProtectedToo()
    {
        var tab = Open();
        var shared = new DashboardLayout(180, [])
        {
            Shelf = [Network(Guid.NewGuid(), Hostile)],
        };

        await ImportAsync(tab, shared);

        var shelved = Assert.Single(_document.Current.Shelf!);
        Assert.Equal("""{"source":"pc"}""", Compact(shelved.Settings));
        Assert.DoesNotContain("attaquant.example", File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public async Task Import_TwoInstancesOfASecretType_TakeLocalOnesInOrder_ThenClear()
    {
        var second = new Guid("a2002423-c807-4403-9c5a-2a983fb6a372");
        var tab = Open(new DashboardLayout(180,
        [
            Network(LocalNetwork, """{"source":"freebox"}""", column: 0, row: 0),
            Network(second, """{"source":"pc"}""", column: 4, row: 0),
        ]));
        var shared = new DashboardLayout(180,
        [
            Network(Guid.NewGuid(), Hostile, column: 0, row: 0),
            Network(Guid.NewGuid(), Hostile, column: 4, row: 0),
            Network(Guid.NewGuid(), Hostile, column: 8, row: 0),
        ]);

        await ImportAsync(tab, shared);

        Assert.Equal(
            ["""{"source":"freebox"}""", """{"source":"pc"}""", "{}"],
            _document.Current.Placements.Select(p => Compact(p.Settings)));
        Assert.DoesNotContain("attaquant.example", File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public async Task Import_ModulesWithoutSecretField_KeepTheImportedSettings_AndSayNothingMore()
    {
        var tab = Open();

        await ImportAsync(tab, Shared());

        Assert.Equal("""{"format":"12h"}""", Compact(Assert.Single(_document.Current.Placements).Settings));
        Assert.Equal("Disposition importée. Ctrl+Z la défait.", tab.ShareMessage);
    }

    [Fact]
    public async Task Import_Valid_IsOneUndoableEdit()
    {
        var tab = Open();
        var shared = Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(shared, ExportedJson(Shared()));
        _picker.Result = shared;
        await tab.ImportAsync();
        Assert.Single(_document.Current.Placements);

        await _session.UndoAsync();

        Assert.Equal(DashboardLayout.Default.Placements.Count, _document.Current.Placements.Count);
        Assert.Equal(180, _document.Current.Rotation);
    }

    [Fact]
    public async Task Import_UnknownModuleType_IsRefused()
    {
        var tab = Open();
        var shared = Path.Combine(_dir, "futur.trofeo.json");
        var layout = new DashboardLayout(
            180, [new ModulePlacement(Guid.NewGuid(), "hologramme", 0, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone())]);
        File.WriteAllText(shared, ExportedJson(layout));
        _picker.Result = shared;

        await tab.ImportAsync();

        Assert.Equal("Ce service ne connaît pas le module « hologramme ».", tab.ShareMessage);
        Assert.True(tab.ShareMessageIsError);
        Assert.Equal(0, _link.ReloadCount);
        Assert.False(File.Exists(Path.Combine(_dir, "layout.json.avant-import")));
    }

    [Fact]
    public async Task Import_ModuleSmallerThanItsMinimum_IsRefused()
    {
        var tab = Open();
        var shared = Path.Combine(_dir, "petit.trofeo.json");
        var layout = new DashboardLayout(
            180, [new ModulePlacement(Guid.NewGuid(), "claudecode", 0, 0, 2, 1, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone())]);
        File.WriteAllText(shared, ExportedJson(layout));
        _picker.Result = shared;

        await tab.ImportAsync();

        Assert.Equal("« Claude Code » est plus petit que son minimum (6 × 3).", tab.ShareMessage);
        Assert.Equal(0, _link.ReloadCount);
    }

    // ---- Version ----

    [Fact]
    public void Version_ShowsTheServiceVersion()
    {
        var tab = Open();

        Assert.Equal("Version 0.5.0", tab.VersionText);
    }

    // ---- Dans la vraie fenêtre ----

    [AvaloniaFact]
    public void SettingsTab_ShowsEverySection_WithTheTextsOfTheSpec()
    {
        SetStatus(Status(new SensorInfo(false, false, false), new ProcessInfo(46_137_344, 0, 0.83)));
        Directory.CreateDirectory(_dir);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true));
        var window = new MainWindow { DataContext = model, Width = 1440, Height = 900 };
        window.Show();
        model.SelectedTab = model.SettingsTab;
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        foreach (var expected in new[]
                 {
                     "Orientation", "Démarrer avec Windows", "Images par seconde", "12 (le maximum de l'écran)", "Aperçu",
                     "Rendu réel 12 images/s",
                     "L'aperçu suit l'écran image par image. Il coûte un peu plus de processeur pendant que les réglages sont ouverts.",
                     "Températures du processeur", "Le pilote PawnIO n'est pas installé.", "Consommation",
                     "Service d'affichage : 44 Mo, 0,8 % du processeur", "Objectif : moins de 100 Mo et environ 1 % en fond.",
                     "Partager", "Version 0.5.0",
                 })
            Assert.Contains(expected, texts);
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        foreach (var expected in new[]
                 {
                     "À l'endroit (0°)", "Retourné (180°)", "Installer le pilote des températures",
                     "Exporter la disposition…", "Importer une disposition…",
                 })
            Assert.Contains(expected, buttons);
        window.Close();
    }
}
