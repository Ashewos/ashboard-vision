using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>
/// La section « Modules complémentaires » sans fenêtre : un faux lien (qui connaît un module complémentaire de plus), une vraie
/// disposition dans un dossier temporaire, un faux sélecteur de fichier.
/// </summary>
public sealed class CommunityFixture : IDisposable
{
    public static readonly Guid DemoId = new("d3a10000-0000-4000-8000-000000000001");
    public static readonly Guid DemoShelfId = new("d3a10000-0000-4000-8000-000000000002");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-community-" + Guid.NewGuid().ToString("N"));

    public FakeServiceLink Link { get; } = new();
    public FakeFilePicker Picker { get; } = new();
    public LayoutStore Store { get; }
    public LayoutDocument Document { get; }
    public EditSession Session { get; }
    public CommunitySectionViewModel Section { get; }

    public CommunityFixture(DashboardLayout? layout = null, bool connected = true)
    {
        Directory.CreateDirectory(_dir);
        Store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        Store.Save(layout ?? WithDemo());
        Link.Package = Package();
        Link.Set(connected ? LinkState.Connected : LinkState.Connecting, connected ? SampleData.Welcome() : null, connected ? SampleData.Status() : null);
        if (connected)
            Link.SetTypes([.. SampleData.Types(), DemoType()]);
        Document = new LayoutDocument(Store, Link);
        Document.Load();
        Session = new EditSession(Document);
        Section = new CommunitySectionViewModel(Link, Session, () => Picker);
    }

    /// <summary>La dernière demande qui change quelque chose (les relectures de la liste ne comptent pas).</summary>
    public string LastChange => Link.ModuleCalls.Last(call => call != "community");

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>La disposition par défaut, avec une case « Démo » placée et une rangée.</summary>
    public static DashboardLayout WithDemo() => DashboardLayout.Default with
    {
        Placements = [.. DashboardLayout.Default.Placements.Where(p => p.TypeId != "clock"), new ModulePlacement(DemoId, "alex.demo", 12, 0, 4, 2, TextSize.Normal, Json("{}"))],
        Shelf = [new ModulePlacement(DemoShelfId, "alex.demo", 0, 0, 4, 2, TextSize.Normal, Json("{}"))],
    };

    /// <summary>Le type d'un module complémentaire, tel que le service l'annonce.</summary>
    public static ModuleTypeInfo DemoType(string id = "alex.demo", string name = "Démo", string author = "Alex") => new(
        id, name, new GridSize(2, 1), new Dictionary<SizePreset, GridSize> { [SizePreset.Ideal] = new(4, 2) }, false, SettingsSchema.Empty,
        new CommunityTypeInfo(author, "Un module pour essayer.", "1.2.0", [ModulePermission.Internet, ModulePermission.Sensors], [ModulePermission.Internet]));

    public static CommunityModuleInfo Module(
        string id = "alex.demo", LinkCommunityState state = LinkCommunityState.Ready, string version = "1.2.0",
        ModulePermission[]? declared = null, ModulePermission[]? granted = null, string? updateVersion = null, string? updateNotes = null,
        string? message = null, bool fromDevFolder = false, string name = "Démo", string author = "Alex", string description = "Un module pour essayer.") => new(
        id, name, author, description, version, state, message,
        declared ?? [ModulePermission.Internet, ModulePermission.Sensors], granted ?? [ModulePermission.Internet], fromDevFolder, updateVersion, updateNotes);

    public CommunityFixture WithModules(params CommunityModuleInfo[] modules)
    {
        Link.Community = new CommunityOverview(modules, @"C:\Modules", false, [], false);
        return this;
    }

    public static PackageInfo Package(
        string id = "alex.demo", string name = "Démo", string author = "Alex", string version = "1.0.0", string description = "Un module pour essayer.",
        ModulePermission[]? declared = null, string[]? errors = null, string? conflict = null, string? installedVersion = null) => new(
        id, name, author, version, description, declared ?? [ModulePermission.Internet, ModulePermission.Sensors], errors ?? [], conflict, installedVersion);

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
