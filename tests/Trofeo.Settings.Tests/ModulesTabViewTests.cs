using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.ViewModels.Fields;
using Trofeo.Settings.Views;
using static Trofeo.Settings.Tests.ModulesFixture;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Modules dans la vraie fenêtre : clics sur les boutons, saisie, perte du focus, secret masqué.</summary>
public sealed class ModulesTabViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-modules-view-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeSecretAccess _secrets = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LayoutStore _store;
    private MainWindow? _window;

    public ModulesTabViewTests()
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

    private (MainViewModel Model, LayoutDocument Document) Open(Guid select, Func<DashboardLayout, DashboardLayout>? seed = null)
    {
        var layout = DashboardLayout.Default;
        _store.Save(seed?.Invoke(layout) ?? layout);
        var document = new LayoutDocument(_store, _link);
        document.Load();
        var model = new MainViewModel(_link, document, new EditorPreferences(FirstRunDone: true), null, ["Arial"], _secrets, _time);
        _window = new MainWindow { DataContext = model };
        _window.Show();
        model.SelectedTab = model.ModulesTab;
        model.ModulesTab.Select(select);
        Dispatcher.UIThread.RunJobs();
        return (model, document);
    }

    private IEnumerable<T> Find<T>() where T : Visual => _window!.GetVisualDescendants().OfType<T>();

    private T Named<T>(string name) where T : Avalonia.Controls.Control => Find<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);

    private Button ButtonLabelled(string text) =>
        Find<Button>().Single(b => b.IsEffectivelyVisible && b.Content is string label && label == text);

    private Point Center(Avalonia.Controls.Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _window!)!.Value;

    private void Click(Avalonia.Controls.Control control)
    {
        _window!.MouseDown(Center(control), MouseButton.Left);
        _window!.MouseUp(Center(control), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condition n'est pas devenue vraie à temps.");
    }

    [AvaloniaFact]
    public void ModuleWithoutSchema_ShowsNothingToSet()
    {
        Open(NowPlayingId);

        Assert.Equal("Ce module n'a rien à régler.", Named<TextBlock>("NothingToSet").Text);
        Assert.DoesNotContain(Find<TextBox>(), t => t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ListsShowPlacedThenShelved_AndTheHeaderShowsNameAndHealth()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with
        {
            Modules = [new ModuleState(ClockId, "clock", ModuleHealth.Online, null, [])],
        });
        Open(ClockId, layout => layout with
        {
            Shelf = [new ModulePlacement(CodexId, "codex", 0, 0, 8, 4, TextSize.Normal, Json("{}"))],
        });

        Assert.Equal(5, Named<ListBox>("PlacedList").ItemCount);
        Assert.Equal(1, Named<ListBox>("ShelvedList").ItemCount);
        Assert.Equal("Heure, date et météo", Named<TextBlock>("CaseTitle").Text);
        Assert.Equal("En ligne", Named<TextBlock>("Health").Text);
    }

    [AvaloniaFact]
    public async Task SegmentedChoice_Click_SavesAndMarksTheButton()
    {
        var (_, document) = Open(ClockId);

        Click(ButtonLabelled("12 h (AM/PM)"));

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("12h", document.Current.Views[0].Placements.Single(p => p.InstanceId == ClockId).Settings.GetProperty("format").GetString());
        await WaitUntil(() => ButtonLabelled("12 h (AM/PM)").Classes.Contains("primary"));
        Assert.DoesNotContain("primary", ButtonLabelled("24 h").Classes);
    }

    [AvaloniaFact]
    public async Task TextBox_SavesWhenFocusLeaves_NotWhileTyping()
    {
        var (_, document) = Open(MonitoringId);
        var box = Find<TextBox>().Single(t => t.IsEffectivelyVisible);
        box.Focus();
        Assert.True(box.IsFocused, "focus non pris");
        box.Text = "Salon";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, _link.ReloadCount);                              // la frappe n'écrit rien

        var other = Named<ListBox>("PlacedList").GetVisualDescendants().OfType<ListBoxItem>().First();
        other.Focus();                                                   // le focus part ailleurs
        Assert.False(box.IsFocused, "focus pas parti");
        Dispatcher.UIThread.RunJobs();

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Salon", document.Current.Views[0].Placements.Single(p => p.InstanceId == MonitoringId).Settings.GetProperty("title").GetString());
    }

    [AvaloniaFact]
    public async Task TextBox_Enter_Saves()
    {
        var (_, document) = Open(MonitoringId);
        var box = Find<TextBox>().Single(t => t.IsEffectivelyVisible);
        box.Focus();
        box.Text = "Bureau";

        _window!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Bureau", document.Current.Views[0].Placements.Single(p => p.InstanceId == MonitoringId).Settings.GetProperty("title").GetString());
    }

    [AvaloniaFact]
    public async Task Toggle_Click_Saves()
    {
        var (_, document) = Open(ClaudeId);
        var switches = Find<ToggleSwitch>().Where(s => s.IsEffectivelyVisible).ToList();
        Assert.Equal(2, switches.Count);

        Click(switches[1]);                                               // « Faire pâlir les lignes anciennes »

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.True(document.Current.Views[0].Placements.Single(p => p.InstanceId == ClaudeId).Settings.GetProperty("feedFade").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Secret_ShowsPresence_MasksTheInput_AndSavesToTheVault()
    {
        var (model, document) = Open(NetworkId, layout => layout.WithPlacements(
            layout.Views[0].Id,
            [.. layout.Views[0].Placements.Select(p => p.InstanceId == NetworkId
                ? p with { Settings = JsonDocument.Parse("""{"source":"unifi","unifi":{"host":"10.0.0.1"}}""").RootElement.Clone() }
                : p)]));
        Assert.Equal("Absente", Named<TextBlock>("Status").Text);

        Click(ButtonLabelled("Modifier"));
        var box = Find<TextBox>().Single(t => t.IsEffectivelyVisible && t.PasswordChar != default);
        Assert.Equal('●', box.PasswordChar);                             // masquée
        box.Text = "s3cr3t";
        Dispatcher.UIThread.RunJobs();
        Click(ButtonLabelled("Enregistrer"));

        await WaitUntil(() => _secrets.Has("network", "unifi.apiKey"));
        await WaitUntil(() => _link.Restarts.Count == 1);
        Assert.Equal("Enregistrée", Named<TextBlock>("Status").Text);
        Assert.Equal(0, _link.ReloadCount);
        Assert.DoesNotContain("s3cr3t", _store.Load().Views[0].Placements.Single(p => p.InstanceId == NetworkId).Settings.GetRawText());
        Assert.False(model.Session.CanUndo);                              // un secret n'entre pas dans l'historique de la disposition
        Assert.NotNull(document);
    }

    [AvaloniaFact]
    public async Task Action_Confirm_ThenRuns_AndShowsTheMessage()
    {
        _link.OnAction = (_, _) => Task.FromResult(new ActionResult(true, "Certificat oublié."));
        Open(NetworkId, layout => layout.WithPlacements(
            layout.Views[0].Id,
            [.. layout.Views[0].Placements.Select(p => p.InstanceId == NetworkId
                ? p with { Settings = JsonDocument.Parse("""{"source":"unifi","unifi":{"host":"10.0.0.1"}}""").RootElement.Clone() }
                : p)]));

        Click(ButtonLabelled("Oublier le certificat de la console"));
        Assert.Empty(_link.Actions);                                     // la question d'abord
        Assert.True(Named<Border>("Confirm").IsEffectivelyVisible);

        Click(Named<Button>("ConfirmButton"));

        await WaitUntil(() => _link.Actions.Count == 1);
        await WaitUntil(() => Named<TextBlock>("Result").Text == "Certificat oublié.");
        Assert.Contains("ok", Named<TextBlock>("Result").Classes);
    }

    [AvaloniaFact]
    public async Task Lookup_TypingShowsResults_AndAClickPicksOne()
    {
        _link.OnLookup = (_, _, _) => Task.FromResult<IReadOnlyList<LookupResult>>(
            [new LookupResult("Lyon, Rhône, France", Json("""{"name":"Lyon","latitude":45.76,"longitude":4.83}"""))]);
        var (model, document) = Open(ClockId);
        var city = model.ModulesTab.Form!.Field<LookupFieldViewModel>("location");
        var box = Find<TextBox>().Single(t => t.IsEffectivelyVisible);

        box.Focus();
        box.Text = "Lyon";
        Dispatcher.UIThread.RunJobs();
        _time.Advance(TimeSpan.FromMilliseconds(400));
        await city.SearchTask;
        Dispatcher.UIThread.RunJobs();

        Click(ButtonLabelled("Lyon, Rhône, France"));

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Lyon", document.Current.Views[0].Placements.Single(p => p.InstanceId == ClockId).Settings.GetProperty("location").GetProperty("name").GetString());
        await WaitUntil(() => Named<TextBlock>("Current").Text == "Lyon");
        Assert.False(city.IsOpen);
    }

    [AvaloniaFact]
    public void FieldsAreHiddenUntilTheirConditionHolds()
    {
        Open(NetworkId);                                                  // source « pc » : seul le choix de la source

        Assert.DoesNotContain(Find<TextBox>(), t => t.IsEffectivelyVisible);
        Assert.DoesNotContain(Find<Button>(), b => b.IsEffectivelyVisible && b.Content is string label && label.StartsWith("Oublier"));
    }

    [AvaloniaFact]
    public async Task ChangingTab_WritesThePendingText()
    {
        var (model, document) = Open(MonitoringId);
        var box = Find<TextBox>().Single(t => t.IsEffectivelyVisible);
        box.Text = "Salon";
        Dispatcher.UIThread.RunJobs();

        model.SelectedTab = model.LayoutTab;                             // on quitte l'onglet sans que le focus ait bougé

        await WaitUntil(() => _link.ReloadCount == 1);
        Assert.Equal("Salon", document.Current.Views[0].Placements.Single(p => p.InstanceId == MonitoringId).Settings.GetProperty("title").GetString());
    }
}
