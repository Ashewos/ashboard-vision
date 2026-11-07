using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>Ce que les modules complémentaires changent à l'onglet Paramètres hors de leur section : l'import d'une disposition, et la fenêtre qui les ouvre.</summary>
public sealed class SettingsTabCommunityTests : IDisposable
{
    private static readonly Guid MineId = new("aaaaaaaa-0000-4000-8000-000000000001");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-community-import-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeFilePicker _picker = new();

    public SettingsTabCommunityTests()
    {
        Directory.CreateDirectory(_dir);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModuleTypeInfo MinecraftType(SettingField[]? fields = null) => new(
        "alex.minecraft", "Serveur Minecraft", new GridSize(3, 1), new Dictionary<SizePreset, GridSize>(), false,
        new SettingsSchema(fields ?? [new PortField("port", "Port du serveur", 25565)]),
        new CommunityTypeInfo("Alex", "Un serveur à surveiller.", "1.0.0", [ModulePermission.ThisComputer], [ModulePermission.ThisComputer]));

    private SettingsTabViewModel Open(DashboardLayout layout, out LayoutDocument document)
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        store.Save(layout);
        document = new LayoutDocument(store, _link);
        document.Load();
        var session = new EditSession(document);
        return new SettingsTabViewModel(_link, session, new PreferencesStore(new EditorPreferences(), Path.Combine(_dir, "settings-app.json")), SystemServices.None, _picker);
    }

    private async Task ImportAsync(SettingsTabViewModel tab, DashboardLayout shared)
    {
        var file = Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(file, LayoutTransfer.Export(shared));
        _picker.Result = file;
        await tab.ImportAsync();
    }

    [Fact]
    public async Task Import_ModuleWithAPortField_KeepsThisPcsPort_NotTheOneOfTheFile()
    {
        // Le port est celui que le service relaie à un module qui a la permission « Cet ordinateur » : un fichier reçu ne le choisit pas.
        _link.SetTypes([.. SampleData.Types(), MinecraftType()]);
        var local = new ModulePlacement(MineId, "alex.minecraft", 0, 0, 4, 2, TextSize.Normal, Json("""{"port":25565}"""));
        var tab = Open(new DashboardLayout(180, [local]), out var document);

        await ImportAsync(tab, new DashboardLayout(180, [local with { Settings = Json("""{"port":3389}""") }]));

        Assert.Equal(25565, document.Current.Placements.Single().Settings.GetProperty("port").GetInt32());
        Assert.DoesNotContain("3389", File.ReadAllText(document.FilePath));
        Assert.Contains("Réglages gardés pour Serveur Minecraft", tab.ShareMessage);
    }

    [Fact]
    public async Task Import_ModuleWithoutPortNorSecret_KeepsTheImportedSettings()
    {
        _link.SetTypes([.. SampleData.Types(), MinecraftType([new NumberField("count", "Nombre", 1, 10, 3)])]);
        var local = new ModulePlacement(MineId, "alex.minecraft", 0, 0, 4, 2, TextSize.Normal, Json("""{"count":3}"""));
        var tab = Open(new DashboardLayout(180, [local]), out var document);

        await ImportAsync(tab, new DashboardLayout(180, [local with { Settings = Json("""{"count":9}""") }]));

        Assert.Equal(9, document.Current.Placements.Single().Settings.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Import_ModuleInstalledAfterTheConnection_IsKnown()
    {
        // La poignée de main ne connaissait pas ce module ; il a été installé depuis : le service l'annonce dans les types relus.
        var tab = Open(DashboardLayout.Default, out var document);
        _link.SetTypes([.. SampleData.Types(), MinecraftType([new NumberField("count", "Nombre", 1, 10, 3)])]);

        await ImportAsync(tab, new DashboardLayout(180, [new ModulePlacement(MineId, "alex.minecraft", 0, 0, 4, 2, TextSize.Normal, Json("{}"))]));

        Assert.Contains(document.Current.Placements, p => p.TypeId == "alex.minecraft");
        Assert.False(tab.ShareMessageIsError);
    }

    [Fact]
    public void Dispose_TheSection_StopsListeningToTheLink()
    {
        var tab = Open(DashboardLayout.Default, out _);
        tab.Community.Activate();
        var calls = _link.ModuleCalls.Count;

        tab.Dispose();
        _link.SetTypes([.. SampleData.Types(), MinecraftType()]);

        Assert.Equal(calls, _link.ModuleCalls.Count);
    }

    [Fact]
    public void MainViewModel_OpensTheSettingsTab_AndInspectsThePackageOnceConnected()
    {
        _link.Set(LinkState.Connecting);
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(store, _link);
        document.Load();
        _link.Package = new PackageInfo("alex.demo", "Démo", "Alex", "1.0.0", "Un module.", [ModulePermission.Internet], [], null, null);
        using var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true), _picker, ["Arial"]);

        model.OpenPackage(@"C:\x\alex.demo-1.0.0.avmod");

        Assert.Same(model.SettingsTab, model.SelectedTab);
        Assert.Null(model.SettingsTab.Community.Dialog);
        Assert.DoesNotContain(_link.ModuleCalls, c => c.StartsWith("inspect"));

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());

        Assert.NotNull(model.SettingsTab.Community.Dialog);
        Assert.Equal(@"C:\x\alex.demo-1.0.0.avmod", _link.ModuleCalls.Single(c => c.StartsWith("inspect"))["inspect ".Length..]);
        Assert.DoesNotContain(_link.ModuleCalls, c => c.StartsWith("install"));
    }
}
