using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>
/// L'onglet Modules sans fenêtre : un faux lien, un vrai fichier dans un dossier temporaire, un faux coffre et un faux
/// temps. La disposition est celle par défaut (Claude Code, Monitoring, En écoute, Heure, Réseau) avec Codex retiré.
/// </summary>
public sealed class ModulesFixture : IDisposable
{
    public static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    public static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    public static readonly Guid NowPlayingId = new("8ebcc647-ab4b-401d-b2b3-f7a0ebd97b4b");
    public static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");
    public static readonly Guid NetworkId = new("a2002423-c807-4403-9c5a-2a983fb6a371");
    public static readonly Guid CodexId = new("c0de0000-0000-4000-8000-000000000001");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-modules-" + Guid.NewGuid().ToString("N"));

    public FakeServiceLink Link { get; } = new();
    public FakeSecretAccess Secrets { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public LayoutStore Store { get; }
    public LayoutDocument Document { get; }
    public EditSession Session { get; }
    public ModulesTabViewModel Tab { get; }

    /// <param name="seed">Change la disposition de départ (les réglages d'un module, par exemple).</param>
    /// <param name="modules">Ce que le service annonce de chaque instance ; vide : rien (instances « absentes »).</param>
    public ModulesFixture(Func<DashboardLayout, DashboardLayout>? seed = null, params ModuleState[] modules)
    {
        Directory.CreateDirectory(_dir);
        Store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        var layout = DashboardLayout.Default with
        {
            Shelf =
            [
                new ModulePlacement(CodexId, "codex", 0, 0, 8, 4, TextSize.Normal, Json("{}")),
            ],
        };
        Store.Save(seed?.Invoke(layout) ?? layout);
        Link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { Modules = modules });
        Document = new LayoutDocument(Store, Link);
        Document.Load();
        Session = new EditSession(Document);
        Tab = new ModulesTabViewModel(Link, Session, Secrets, Time);
    }

    public string LayoutPath => Path.Combine(_dir, "layout.json");

    public string FileText => File.ReadAllText(LayoutPath);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Change les réglages d'un module (placé ou retiré) avant l'ouverture.</summary>
    public static Func<DashboardLayout, DashboardLayout> WithSettings(Guid id, string json) => layout => layout.WithPlacements(
        layout.Views[0].Id, [.. layout.Views[0].Placements.Select(p => p.InstanceId == id ? p with { Settings = Json(json) } : p)]) with
    {
        Shelf = [.. (layout.Shelf ?? []).Select(p => p.InstanceId == id ? p with { Settings = Json(json) } : p)],
    };

    /// <summary>Les réglages de l'instance dans la disposition en mémoire.</summary>
    public JsonElement SettingsOf(Guid id) =>
        Document.Current.Views[0].Placements.Concat(Document.Current.Shelf ?? []).Single(p => p.InstanceId == id).Settings;

    /// <summary>Les réglages de l'instance relus dans le fichier.</summary>
    public JsonElement SettingsOnDisk(Guid id) =>
        Store.Load().Views[0].Placements.Concat(Store.Load().Shelf ?? []).Single(p => p.InstanceId == id).Settings;

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
