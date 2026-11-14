using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using Trofeo.Settings.Views;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Disposition et ses vues : les onglets, le point vert, l'aperçu, la liste de la vue choisie, les noms de cases.</summary>
public sealed class LayoutViewsTabTests : IDisposable
{
    private static readonly Guid ViewA = new("a0000000-0000-4000-8000-00000000000a");
    private static readonly Guid ViewB = new("b0000000-0000-4000-8000-00000000000b");
    private static readonly Guid ViewC = new("c0000000-0000-4000-8000-00000000000c");

    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");
    private static readonly Guid NetworkId = new("a2002423-c807-4403-9c5a-2a983fb6a371");
    private static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    private static readonly Guid ShelvedId = new("5e1f0000-0000-4000-8000-000000000001");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-views-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private readonly List<IDisposable> _disposables = [];
    private LayoutDocument _document = null!;
    private EditSession _session = null!;
    private Window? _window;

    public LayoutViewsTabTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose()
    {
        _window?.Close();
        foreach (var disposable in _disposables)
            disposable.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModulePlacement Placement(Guid id, string typeId, int column, int row, int columns, int rows, string? name = null) =>
        new(id, typeId, column, row, columns, rows, TextSize.Normal, Json("{}"), name);

    /// <summary>Trois vues : « Un » (Claude Code, Heure), « Deux » (Réseau nommé « Débit du haut », Monitoring) et « Trois » (vide).</summary>
    private static DashboardLayout ThreeViews() => new(
        180,
        [
            new LayoutView(ViewA, "Un", [Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)]),
            new LayoutView(ViewB, "Deux", [Placement(NetworkId, "network", 0, 0, 4, 2, "Débit du haut"), Placement(MonitoringId, "monitoring", 4, 0, 4, 2)]),
            new LayoutView(ViewC, "Trois", []),
        ]);

    private LayoutTabViewModel Open(DashboardLayout layout, bool followView = true)
    {
        _store.Save(layout);
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        var tab = new LayoutTabViewModel(_link, _session, new PreferencesStore(new EditorPreferences(FollowView: followView)));
        _disposables.Add(tab);
        return tab;
    }

    // ---- Choisir un onglet ----

    [Fact]
    public async Task SelectingATab_WithFollowOn_AsksTheServiceToShowIt()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews());
        Assert.True(tab.FollowView);

        await tab.SelectViewAsync(tab.ViewTabs[1]);

        Assert.Equal([ViewB], _link.ShowRequests);   // le service affiche la vue…
        Assert.Equal(ViewB, _link.PreviewViewId);    // … et l'aperçu la montre
        Assert.Equal(ViewB, tab.SelectedViewId);
        Assert.Same(tab.ViewTabs[1], tab.SelectedView);
    }

    [Fact]
    public async Task SelectingATab_WithFollowOff_OnlyChangesThePreviewView()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);

        await tab.SelectViewAsync(tab.ViewTabs[1]);

        Assert.Empty(_link.ShowRequests);            // l'écran reste où il est
        Assert.Equal(ViewB, _link.PreviewViewId);
        Assert.True(tab.ViewTabs[0].IsOnScreen);
        Assert.False(tab.ViewTabs[1].IsOnScreen);
        Assert.Equal(ViewB, tab.SelectedViewId);
    }

    [Fact]
    public void SelectedViewSetter_IsHowTheTabRowChoosesATab()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews());

        tab.SelectedView = tab.ViewTabs[2];

        Assert.Equal([ViewC], _link.ShowRequests);
        Assert.Equal(ViewC, tab.SelectedViewId);
    }

    [Fact]
    public async Task FollowView_IsKeptWithThePreferences_AndTurningItOnShowsTheChosenView()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);
        await tab.SelectViewAsync(tab.ViewTabs[1]);
        Assert.Empty(_link.ShowRequests);

        tab.FollowView = true;

        Assert.Equal([ViewB], _link.ShowRequests);
        Assert.True(tab.FollowView);
    }

    [Fact]
    public void FollowView_ChangeIsWrittenToThePreferencesFile()
    {
        var file = Path.Combine(_dir, "settings-app.json");
        _store.Save(ThreeViews());
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        using var tab = new LayoutTabViewModel(_link, _session, new PreferencesStore(new EditorPreferences(FirstRunDone: true), file));

        tab.FollowView = false;

        Assert.False(EditorPreferences.Load(file).FollowView);
        Assert.True(EditorPreferences.Load(file).FirstRunDone);
    }

    [Fact]
    public void FollowView_IsOnByDefault_AndAnOldPreferencesFileKeepsItOn()
    {
        Assert.True(new EditorPreferences().FollowView);
        var file = Path.Combine(_dir, "settings-app.json");
        File.WriteAllText(file, """{"firstRunDone": true, "realTimePreview": true}""");

        Assert.True(EditorPreferences.Load(file).FollowView);
    }

    [Fact]
    public void FirstDisplay_ChoosesTheViewOnScreen_AndPreviewsIt()
    {
        _link.ShownViewId = ViewB;

        var tab = Open(ThreeViews());

        Assert.Equal(ViewB, tab.SelectedViewId);
        Assert.Equal([ViewB], _link.PreviewViews);
        Assert.Empty(_link.ShowRequests); // simplement la vue de l'écran : rien à demander
    }

    [Fact]
    public void FirstDisplay_ServiceNotAnswering_StartsOnTheFirstView_ThenFollowsTheScreenUntilYouChoose()
    {
        var tab = Open(ThreeViews());
        Assert.Equal(ViewA, tab.SelectedViewId);

        _link.PushViewShown(ViewC); // la liaison s'établit : le service dit quelle vue est sur l'écran

        Assert.Equal(ViewC, tab.SelectedViewId);
        Assert.True(tab.ViewTabs[2].IsOnScreen);
    }

    [Fact]
    public async Task PreviewView_IsSubscribedOncePerView()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);

        await tab.SelectViewAsync(tab.ViewTabs[1]);
        await tab.SelectViewAsync(tab.ViewTabs[1]);        // déjà choisie
        await tab.AddAsync(tab.AddTypes.Single(t => t.TypeId == "clock")); // une modification ne redemande pas l'aperçu
        await tab.SelectViewAsync(tab.ViewTabs[2]);

        Assert.Equal([ViewA, ViewB, ViewC], _link.PreviewViews);
    }

    [Fact]
    public async Task ReClickingTheSelectedTab_WithFollowOn_PutsTheScreenBackOnIt()
    {
        _link.ShownViewId = ViewB;
        var tab = Open(ThreeViews());
        Assert.Equal(ViewB, tab.SelectedViewId);
        _link.PushViewShown(ViewC); // un raccourci clavier emmène l'écran ailleurs
        Assert.Empty(_link.ShowRequests);

        await tab.SelectViewAsync(tab.ViewTabs[1]);

        Assert.Equal([ViewB], _link.ShowRequests);
        Assert.True(tab.ViewTabs[1].IsOnScreen);

        await tab.SelectViewAsync(tab.ViewTabs[1]); // déjà sur l'écran : rien à demander
        Assert.Single(_link.ShowRequests);
    }

    [Fact]
    public async Task ReClickingTheSelectedTab_WithFollowOff_DoesNothing()
    {
        _link.ShownViewId = ViewB;
        var tab = Open(ThreeViews(), followView: false);
        _link.PushViewShown(ViewC);

        await tab.SelectViewAsync(tab.ViewTabs[1]);

        Assert.Empty(_link.ShowRequests);
    }

    [Fact]
    public async Task ShowView_UnknownToTheService_ReloadsItThenAsksAgain()
    {
        _link.ShownViewId = ViewA;
        _link.ShowViewResult = false;
        var tab = Open(ThreeViews());

        await tab.SelectViewAsync(tab.ViewTabs[1]);

        Assert.Equal([ViewB, ViewB], _link.ShowRequests); // refusée, la disposition est rechargée, la demande refaite une fois
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(ViewB, tab.SelectedViewId);
    }

    // ---- L'aperçu hors de l'onglet Disposition ----

    [Fact]
    public async Task Preview_OutsideDisposition_ShowsTheViewOnScreen_AndComesBackOnTheChosenView()
    {
        _link.ShownViewId = ViewA;
        _store.Save(ThreeViews());
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        using var model = new MainViewModel(_link, _document, new EditorPreferences(FirstRunDone: true, FollowView: false));
        await model.LayoutTab.SelectViewAsync(model.LayoutTab.ViewTabs[1]);
        Assert.Equal(ViewB, _link.PreviewViewId);

        model.SelectedTab = model.AppearanceTab; // la bande redevient « L'écran en direct » : la vue cachée est relâchée
        Assert.Null(_link.PreviewViewId);

        model.SelectedTab = model.LayoutTab;
        Assert.Equal(ViewB, _link.PreviewViewId);

        model.SelectedTab = model.ShortcutsTab;
        Assert.Null(_link.PreviewViewId);
        Assert.Equal(ViewB, model.LayoutTab.SelectedViewId); // le choix, lui, est gardé
    }

    [Fact]
    public void Preview_InactiveTab_ChoosesTheScreenView_EvenWhenAnotherViewIsChosen()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);
        tab.IsActive = false;
        Assert.Null(_link.PreviewViewId);

        _ = tab.SelectViewAsync(tab.ViewTabs[2]);
        Assert.Null(_link.PreviewViewId);   // choisie, mais l'onglet n'est pas regardé

        tab.IsActive = true;
        Assert.Equal(ViewC, _link.PreviewViewId);
    }

    // ---- Sans service relié ----

    [Fact]
    public async Task DeleteView_WithoutService_DoesNotAskForAConfirmationThatWouldDoNothing()
    {
        var tab = Open(ThreeViews());
        Assert.True(tab.ViewTabs[1].CanDelete);

        _link.Set(LinkState.Connecting); // la liaison est coupée : les types ne sont plus connus
        Assert.False(tab.CanEdit);
        Assert.False(tab.ViewTabs[1].CanDelete);
        Assert.False(tab.ViewTabs[1].CanEdit);

        await tab.DeleteViewAsync(tab.ViewTabs[1]);

        Assert.False(tab.IsConfirmingViewDelete);
        Assert.Equal(3, _document.Current.Views.Count);
    }

    [Fact]
    public async Task DeleteConfirmation_IsDroppedWhenTheServiceGoesAway()
    {
        var tab = Open(ThreeViews());
        await tab.DeleteViewAsync(tab.ViewTabs[1]);
        Assert.True(tab.IsConfirmingViewDelete);

        _link.Set(LinkState.Connecting);

        Assert.False(tab.IsConfirmingViewDelete);
    }

    // ---- Le point vert ----

    [Fact]
    public async Task GreenDot_FollowsViewShown()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);
        await tab.SelectViewAsync(tab.ViewTabs[1]);
        Assert.True(tab.ViewTabs[0].IsOnScreen);

        _link.PushViewShown(ViewC); // un raccourci clavier change la vue de l'écran

        Assert.Equal([false, false, true], tab.ViewTabs.Select(t => t.IsOnScreen));
        Assert.Equal(ViewB, tab.SelectedViewId);                 // la sélection ne bouge pas
        Assert.True(tab.ViewTabs[1].IsSelected);
        Assert.Equal(ViewB, _link.PreviewViewId);                // ni l'aperçu
    }

    // ---- Les opérations sur les vues ----

    [Fact]
    public async Task AddView_AppendsAndChoosesTheNewView()
    {
        var tab = Open(ThreeViews());

        await tab.AddViewAsync();

        Assert.Equal(["Un", "Deux", "Trois", "Vue 4"], tab.ViewTabs.Select(t => t.Name));
        Assert.Equal(_document.Current.Views[3].Id, tab.SelectedViewId);
        Assert.Equal(_document.Current.Views[3].Id, _link.ShowRequests.Single());
        Assert.Empty(tab.Placed);
    }

    [Fact]
    public async Task AddView_TenthIsRefusedWithAMessage()
    {
        var tab = Open(DashboardLayout.SingleView(180, []));

        for (var i = 0; i < 8; i++)
            await tab.AddViewAsync();
        Assert.Equal(9, tab.ViewTabs.Count);
        Assert.False(tab.HasRefusal);

        await tab.AddViewAsync();

        Assert.Equal(9, tab.ViewTabs.Count);
        Assert.Equal(9, _document.Current.Views.Count);
        Assert.True(tab.HasRefusal);
        Assert.Equal("9 vues au plus.", tab.RefusalText);
    }

    [Fact]
    public async Task DuplicateView_TenthIsRefusedWithTheSameMessage()
    {
        var tab = Open(DashboardLayout.SingleView(180, []));
        for (var i = 0; i < 8; i++)
            await tab.AddViewAsync();

        await tab.DuplicateViewAsync(tab.ViewTabs[0]);

        Assert.Equal(9, tab.ViewTabs.Count);
        Assert.Equal("9 vues au plus.", tab.RefusalText);
    }

    [Fact]
    public async Task DeleteView_WithCases_AsksForConfirmation()
    {
        var tab = Open(ThreeViews());
        var two = tab.ViewTabs[1];

        await tab.DeleteViewAsync(two);

        Assert.Equal(3, _document.Current.Views.Count);   // rien n'est supprimé avant la réponse
        Assert.True(tab.IsConfirmingViewDelete);
        Assert.Equal("Supprimer la vue « Deux » ? Ses modules vont dans « Retirés ».", tab.ViewDeleteQuestion);

        tab.CancelDeleteView();
        Assert.False(tab.IsConfirmingViewDelete);
        Assert.Equal(3, _document.Current.Views.Count);

        await tab.DeleteViewAsync(two);
        await tab.ConfirmDeleteViewAsync();

        Assert.Equal(["Un", "Trois"], tab.ViewTabs.Select(t => t.Name));
        Assert.False(tab.IsConfirmingViewDelete);
        Assert.Equal(["Débit du haut", "Monitoring PC"], tab.Shelved.Select(s => s.Name));
        Assert.Equal(2, _document.Current.Shelf!.Count);
    }

    [Fact]
    public async Task DeleteView_Empty_DoesNotAsk()
    {
        var tab = Open(ThreeViews());

        await tab.DeleteViewAsync(tab.ViewTabs[2]);

        Assert.False(tab.IsConfirmingViewDelete);
        Assert.Equal(["Un", "Deux"], tab.ViewTabs.Select(t => t.Name));
    }

    [Fact]
    public async Task DeleteView_LastOne_IsNotPossible()
    {
        var tab = Open(DashboardLayout.SingleView(180, []));

        Assert.False(tab.CanDeleteView);
        await tab.DeleteViewAsync(tab.ViewTabs[0]);

        Assert.Single(tab.ViewTabs);
    }

    [Fact]
    public async Task DeleteView_ChosenOne_ChoosesItsNeighbour()
    {
        var tab = Open(ThreeViews());
        await tab.SelectViewAsync(tab.ViewTabs[2]);

        await tab.DeleteViewAsync(tab.ViewTabs[2]); // « Trois », vide

        Assert.Equal(ViewB, tab.SelectedViewId);
        Assert.Equal(ViewB, _link.PreviewViewId);
    }

    [Fact]
    public async Task DeleteView_IsUndoneByCtrlZ()
    {
        var tab = Open(ThreeViews());
        await tab.DeleteViewAsync(tab.ViewTabs[1]);
        await tab.ConfirmDeleteViewAsync();
        Assert.Equal(2, tab.ViewTabs.Count);
        Assert.True(tab.CanUndo);

        await tab.UndoAsync();

        Assert.Equal(["Un", "Deux", "Trois"], tab.ViewTabs.Select(t => t.Name));
        Assert.Empty(tab.Shelved);
        Assert.Equal(2, _document.Current.Views[1].Placements.Count);
        Assert.Equal("Débit du haut", _document.Current.Views[1].Placements[0].Name);

        await tab.RedoAsync();
        Assert.Equal(2, tab.ViewTabs.Count);
    }

    [Fact]
    public async Task RenameView_CleansTheNameAndIsUndoable()
    {
        var tab = Open(ThreeViews());

        await tab.RenameViewAsync(tab.ViewTabs[0], "  Jeux  ");
        Assert.Equal("Jeux", tab.ViewTabs[0].Name);
        Assert.Equal("Jeux", _document.Current.Views[0].Name);

        await tab.RenameViewAsync(tab.ViewTabs[0], "   "); // un nom vide est ignoré
        Assert.Equal("Jeux", tab.ViewTabs[0].Name);

        await tab.UndoAsync();
        Assert.Equal("Un", tab.ViewTabs[0].Name);
    }

    [Fact]
    public async Task DuplicateView_AddsACopyAfterTheOriginal_WithNewCaseIds()
    {
        var tab = Open(ThreeViews());

        await tab.DuplicateViewAsync(tab.ViewTabs[1]);

        Assert.Equal(["Un", "Deux", "Deux (copie)", "Trois"], tab.ViewTabs.Select(t => t.Name));
        Assert.Equal(_document.Current.Views[2].Id, tab.SelectedViewId);
        var original = _document.Current.Views[1].Placements;
        var copy = _document.Current.Views[2].Placements;
        Assert.Equal(original.Select(p => p.TypeId), copy.Select(p => p.TypeId));
        Assert.Equal(original.Select(p => p.Name), copy.Select(p => p.Name));
        Assert.Empty(original.Select(p => p.InstanceId).Intersect(copy.Select(p => p.InstanceId)));
    }

    [Fact]
    public async Task MoveView_ReordersTheTabs()
    {
        var tab = Open(ThreeViews());

        await tab.MoveViewAsync(0, 2);

        Assert.Equal(["Deux", "Trois", "Un"], tab.ViewTabs.Select(t => t.Name));
        await tab.UndoAsync();
        Assert.Equal(["Un", "Deux", "Trois"], tab.ViewTabs.Select(t => t.Name));
    }

    // ---- La liste « Sur l'écran » et les noms de cases ----

    [Fact]
    public async Task OnScreenList_ShowsTheSelectedViewCasesWithTheirNames()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews());
        Assert.Equal(["Claude Code", "Heure, date et météo"], tab.Placed.Select(p => p.Name));

        await tab.SelectViewAsync(tab.ViewTabs[1]);
        Assert.Equal(["Débit du haut", "Monitoring PC"], tab.Placed.Select(p => p.Name)); // le nom donné remplace celui du module

        await tab.SelectViewAsync(tab.ViewTabs[2]);
        Assert.Empty(tab.Placed);
        Assert.False(tab.HasPlaced);
    }

    [Fact]
    public async Task Editing_WorksOnTheSelectedView()
    {
        var tab = Open(ThreeViews());
        await tab.SelectViewAsync(tab.ViewTabs[2]); // « Trois », vide

        await tab.AddAsync(tab.AddTypes.Single(t => t.TypeId == "clock"));

        Assert.Single(_document.Current.Views[2].Placements);
        Assert.Equal(2, _document.Current.Views[0].Placements.Count); // la première vue n'a pas bougé
        Assert.Equal("clock", tab.Placed.Single().TypeId);
        Assert.Equal(tab.Placed.Single().Id, tab.SelectedId);
    }

    [Fact]
    public async Task Restore_PutsTheShelvedCaseInTheSelectedView()
    {
        var layout = ThreeViews() with { Shelf = [Placement(ShelvedId, "codex", 0, 0, 8, 4)] };
        var tab = Open(layout);
        await tab.SelectViewAsync(tab.ViewTabs[2]);

        await tab.RestoreAsync(tab.Shelved.Single());

        Assert.Equal(ShelvedId, _document.Current.Views[2].Placements.Single().InstanceId);
        Assert.Empty(tab.Shelved);
    }

    [Fact]
    public async Task CaseName_IsEditedInTheCasePanel()
    {
        _link.ShownViewId = ViewB;
        var tab = Open(ThreeViews());
        tab.Select(MonitoringId);
        Assert.Equal("", tab.CaseName);

        tab.CaseName = "  Ma machine  ";
        await tab.CommitCaseNameAsync();

        Assert.Equal("Ma machine", _document.Current.Views[1].Placements.Single(p => p.InstanceId == MonitoringId).Name);
        Assert.Equal("Ma machine", tab.CaseName);
        Assert.Equal(["Débit du haut", "Ma machine"], tab.Placed.Select(p => p.Name));
        Assert.Equal(MonitoringId, tab.SelectedId); // la case reste choisie

        await tab.UndoAsync();
        Assert.Null(_document.Current.Views[1].Placements.Single(p => p.InstanceId == MonitoringId).Name);
    }

    [Fact]
    public async Task CaseName_TooLongIsCut_AndEmptyRemovesIt()
    {
        _link.ShownViewId = ViewB;
        var tab = Open(ThreeViews());
        tab.Select(NetworkId);
        Assert.Equal("Débit du haut", tab.CaseName);
        Assert.Equal(30, LayoutTabViewModel.NameMaxLength);

        tab.CaseName = new string('x', 45);
        await tab.CommitCaseNameAsync();
        Assert.Equal(30, _document.Current.Views[1].Placements[0].Name!.Length);

        tab.CaseName = "";
        await tab.CommitCaseNameAsync();
        Assert.Null(_document.Current.Views[1].Placements[0].Name);
        Assert.Equal("Réseau", tab.Placed[0].Name); // sans nom : le nom du module
    }

    [Fact]
    public async Task CaseName_DraftIsDroppedWhenAnotherCaseIsChosen()
    {
        _link.ShownViewId = ViewB;
        var tab = Open(ThreeViews());
        tab.Select(MonitoringId);
        tab.CaseName = "pas validé";

        tab.Select(NetworkId);
        await tab.CommitCaseNameAsync();

        Assert.Equal("Débit du haut", tab.CaseName);
        Assert.Null(_document.Current.Views[1].Placements.Single(p => p.InstanceId == MonitoringId).Name);
    }

    [Fact]
    public void ShelvedList_ShowsTheCaseNames()
    {
        var layout = ThreeViews() with { Shelf = [Placement(ShelvedId, "codex", 0, 0, 8, 4, "Mon Codex")] };

        var tab = Open(layout);

        Assert.Equal("Mon Codex", tab.Shelved.Single().Name);
    }

    // ---- Les raccourcis qui visent une case ----

    private static ShortcutBinding[] Bindings() =>
    [
        new("Ctrl+Alt+Shift+A", ShortcutCommand.NextTab, 0, ShelvedId),
        new("Ctrl+Alt+Shift+B", ShortcutCommand.NextTab, 0, ClaudeId),
        new("Ctrl+Alt+Shift+C", ShortcutCommand.NextTab),
    ];

    [Fact]
    public async Task ForgetShelvedCase_RemovesTheBindingsTargetingIt()
    {
        var layout = ThreeViews() with { Shelf = [Placement(ShelvedId, "codex", 0, 0, 8, 4)], Shortcuts = Bindings() };
        var tab = Open(layout);

        await tab.ForgetAsync(tab.Shelved.Single());

        Assert.Empty(_document.Current.Shelf!);
        Assert.Equal(["Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C"], _document.Current.Shortcuts!.Select(b => b.Keys));

        // Ctrl+Z remet la case et ses raccourcis ensemble.
        await tab.UndoAsync();
        Assert.Equal(ShelvedId, _document.Current.Shelf!.Single().InstanceId);
        Assert.Equal(["Ctrl+Alt+Shift+A", "Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C"], _document.Current.Shortcuts!.Select(b => b.Keys));
    }

    [Fact]
    public async Task ForgetShelvedCase_WithoutBindings_LeavesTheShortcutsAlone()
    {
        var layout = ThreeViews() with { Shelf = [Placement(ShelvedId, "codex", 0, 0, 8, 4)] };
        var tab = Open(layout);

        await tab.ForgetAsync(tab.Shelved.Single());

        Assert.Null(_document.Current.Shortcuts);
    }

    [Fact]
    public async Task DuplicateView_CopiesDoNotTakeTheOriginalsBindings()
    {
        var layout = ThreeViews() with { Shortcuts = Bindings() };
        var tab = Open(layout);

        await tab.DuplicateViewAsync(tab.ViewTabs[0]);

        var copy = _document.Current.Views[1];
        Assert.Equal(2, copy.Placements.Count);
        Assert.Equal(["Ctrl+Alt+Shift+A", "Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C"], _document.Current.Shortcuts!.Select(b => b.Keys));
        Assert.DoesNotContain(_document.Current.Shortcuts!, b => b.Target is { } target && copy.Placements.Any(p => p.InstanceId == target));
        Assert.Contains(_document.Current.Shortcuts!, b => b.Target == ClaudeId); // l'original garde le sien
    }

    [Fact]
    public async Task DeleteView_KeepsTheBindingsOfItsCases_TheyGoToTheShelfWithThem()
    {
        var layout = ThreeViews() with { Shortcuts = Bindings() };
        var tab = Open(layout);

        await tab.DeleteViewAsync(tab.ViewTabs[0]);
        await tab.ConfirmDeleteViewAsync();

        Assert.Contains(_document.Current.Shelf!, p => p.InstanceId == ClaudeId);
        Assert.Contains(_document.Current.Shortcuts!, b => b.Target == ClaudeId);
    }

    // ---- La fenêtre ----

    [AvaloniaFact]
    public async Task Window_TabsShowTheNamesAndTheGreenDot_AndCaseNameCommitsOnEnter()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews(), followView: false);
        await tab.SelectViewAsync(tab.ViewTabs[1]);
        var view = new StackPanel { Children = { new ViewTabsBar { DataContext = tab }, new LayoutTabView { DataContext = tab } } }; // la rangée des vues est au-dessus de la bande, hors de l'onglet
        _window = new Window { Width = 1400, Height = 900, Content = view };
        _window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = view.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "ViewTabList");
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(["Un", "Deux", "Trois"], items.Select(i => i.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("tabname")).Text));
        var dots = items.Select(i => i.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().IsVisible).ToList();
        Assert.Equal([true, false, false], dots); // le point vert est sur la vue de l'écran, pas sur la vue choisie
        Assert.True(items[1].IsSelected);

        // Le nom d'une case se tape dans son panneau et se valide sur Entrée.
        tab.Select(MonitoringId);
        Dispatcher.UIThread.RunJobs();
        var box = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CaseNameBox");
        Assert.Equal(30, box.MaxLength);
        box.Focus();
        box.Text = "Ma machine";
        _window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        for (var i = 0; i < 20 && _document.Current.Views[1].Placements[1].Name is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Equal("Ma machine", _document.Current.Views[1].Placements[1].Name);
    }

    [AvaloniaFact]
    public async Task Window_TabMenu_RenamesDuplicatesAndDeletes()
    {
        _link.ShownViewId = ViewA;
        var tab = Open(ThreeViews());
        var view = new StackPanel { Children = { new ViewTabsBar { DataContext = tab }, new LayoutTabView { DataContext = tab } } }; // la rangée des vues est au-dessus de la bande, hors de l'onglet
        _window = new Window { Width = 1400, Height = 900, Content = view };
        _window.Show();
        Dispatcher.UIThread.RunJobs();

        var menu = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabMenu" && b.IsVisible);
        menu.Flyout!.ShowAt(menu);
        Dispatcher.UIThread.RunJobs();
        var entries = ((StackPanel)((Flyout)menu.Flyout).Content!).Children.OfType<Button>().Select(b => b.Content).ToList();
        Assert.Equal(["Renommer", "Dupliquer", "Supprimer"], entries);

        // « Renommer » met l'onglet en saisie ; Entrée valide.
        tab.ViewTabs[0].RenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var box = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "RenameBox" && b.IsVisible);
        Assert.Equal("Un", box.Text);
        box.Text = "Jeux";
        box.Focus();
        _window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        for (var i = 0; i < 20 && _document.Current.Views[0].Name == "Un"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Equal("Jeux", _document.Current.Views[0].Name);
        Assert.False(tab.ViewTabs[0].IsRenaming);

        // « + Nouvelle vue » et « L'écran suit la vue choisie » sont là.
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "+ Nouvelle vue"));
        var follow = view.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.Equal("L'écran suit la vue choisie", follow.Content);
        Assert.True(follow.IsChecked);
    }

    [AvaloniaFact]
    public async Task Window_DeleteConfirmation_ShowsTheQuestionAndItsButtons()
    {
        var tab = Open(ThreeViews());
        var view = new StackPanel { Children = { new ViewTabsBar { DataContext = tab }, new LayoutTabView { DataContext = tab } } }; // la rangée des vues est au-dessus de la bande, hors de l'onglet
        _window = new Window { Width = 1400, Height = 900, Content = view };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        var note = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ViewDeleteNote");
        Assert.False(note.IsVisible);

        await tab.DeleteViewAsync(tab.ViewTabs[1]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(note.IsVisible);
        Assert.Contains("Supprimer la vue « Deux » ? Ses modules vont dans « Retirés ».", note.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    }
}
