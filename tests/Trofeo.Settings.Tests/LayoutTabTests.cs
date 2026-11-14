using System.Text.Json;
using Avalonia;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Disposition sans fenêtre : un faux lien, un vrai fichier dans un dossier temporaire, des points en pixels de l'écran.</summary>
public sealed class LayoutTabTests : IDisposable
{
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    private static readonly Guid NetworkId = new("a2002423-c807-4403-9c5a-2a983fb6a371");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-layout-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public LayoutTabTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModulePlacement Placement(
        Guid id, string typeId, int column, int row, int columns, int rows, string settings = "{}") =>
        new(id, typeId, column, row, columns, rows, TextSize.Normal, Json(settings));

    private LayoutTabViewModel Open(DashboardLayout layout)
    {
        _store.Save(layout);
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new LayoutTabViewModel(_link, _session);
    }

    private static DashboardLayout Layout(params ModulePlacement[] placements) => DashboardLayout.SingleView(180, placements);

    private static GridSpacing Classic => GridSpacing.Default;

    /// <summary>Le centre d'une case, en pixels de l'écran.</summary>
    private static Point Center(int column, int row, GridSpacing? spacing = null)
    {
        var rect = GridGeometry.ModuleRect(column, row, 1, 1, spacing ?? Classic);
        return new Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0);
    }

    /// <summary>Le bord de colonne <c>edge</c> (1 à 15), au milieu de l'écart.</summary>
    private static double EdgeX(int edge) => GridGeometry.ModuleRect(edge - 1, 0, 1, 1, Classic).Right + Classic.Gap / 2.0;

    private static double EdgeY(int edge) => GridGeometry.ModuleRect(0, edge - 1, 1, 1, Classic).Bottom + Classic.Gap / 2.0;

    private static ModulePlacement Of(DashboardLayout layout, Guid id) => layout.Views[0].Placements.Single(p => p.InstanceId == id);

    [Fact]
    public async Task Drag_ToFreeCells_SavesMovedLayout()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));

        Assert.True(tab.IsDragging);
        Assert.NotNull(tab.Ghost);
        Assert.True(tab.Ghost!.Ok);
        Assert.Equal((12, 2, 4, 2), (tab.Ghost.Column, tab.Ghost.Row, tab.Ghost.Columns, tab.Ghost.Rows));
        Assert.Equal(0, _link.ReloadCount);  // le fantôme ne sauvegarde rien

        await tab.EndDragAsync();

        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);
        Assert.Equal(1, _link.ReloadCount);
        var moved = Of(_document.Current, ClockId);
        Assert.Equal((12, 2, 4, 2), (moved.Column, moved.Row, moved.Columns, moved.Rows));
        Assert.Equal((12, 2), (Of(_store.Load(), ClockId).Column, Of(_store.Load(), ClockId).Row));
    }

    [Fact]
    public async Task Drag_KeepsTheGrabOffsetInCells()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 8, 0, 4, 2)));

        // On saisit le module par sa troisième colonne (10) et on le lâche sur la colonne 14 : il avance de 4 cases.
        tab.BeginMove(ClockId, Center(10, 1));
        tab.UpdateDrag(Center(12, 3));

        Assert.True(tab.Ghost!.Ok);
        Assert.Equal((10, 2), (tab.Ghost.Column, tab.Ghost.Row));
        await tab.EndDragAsync();
        Assert.Equal((10, 2), (Of(_document.Current, ClockId).Column, Of(_document.Current, ClockId).Row));
    }

    [Fact]
    public async Task Drag_Refused_DoesNotSave()
    {
        var tab = Open(Layout(Placement(MonitoringId, "monitoring", 8, 0, 4, 2), Placement(ClockId, "clock", 12, 0, 4, 2)));
        var before = File.ReadAllText(Path.Combine(_dir, "layout.json"));

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(10, 0));

        Assert.False(tab.Ghost!.Ok);
        Assert.Equal("Place prise par Monitoring PC.", tab.Ghost.Reason);
        Assert.Equal("Place prise par Monitoring PC.", tab.RefusalText);
        Assert.True(tab.HasRefusal);

        await tab.EndDragAsync();

        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "layout.json")));
        Assert.Equal(12, Of(_document.Current, ClockId).Column);
        Assert.Equal("Place prise par Monitoring PC.", tab.RefusalText);  // le motif reste lisible après le lâcher...

        tab.BeginMove(ClockId, Center(12, 0));
        Assert.False(tab.HasRefusal);  // ... jusqu'au prochain glissement
    }

    [Fact]
    public void Drag_OffTheScreen_SaysSo()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(15, 0));

        Assert.False(tab.Ghost!.Ok);
        Assert.Equal("Hors de l'écran.", tab.Ghost.Reason);
        // Le cadre fantôme garde la taille du module, même hors du damier.
        Assert.Equal((15, 0, 4, 2), (tab.Ghost.Column, tab.Ghost.Row, tab.Ghost.Columns, tab.Ghost.Rows));
    }

    [Fact]
    public void Resize_BelowMinimum_SaysTheMinimum()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.BeginResize(ClockId, ResizeEdge.Right);
        tab.UpdateDrag(new Point(EdgeX(13), Center(12, 0).Y));  // bord droit à la colonne 13 : 1 case de large

        Assert.False(tab.Ghost!.Ok);
        Assert.Equal("Plus petit que le minimum de Heure, date et météo (3 × 1).", tab.Ghost.Reason);
    }

    [Fact]
    public async Task Drag_WithoutMovement_DoesNotSaveNorFillTheHistory()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 0));
        await tab.EndDragAsync();

        Assert.Equal(0, _link.ReloadCount);
        Assert.False(tab.CanUndo);
    }

    [Fact]
    public async Task Resize_BottomRightHandle_GrowsByCells()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 8, 0, 4, 2)));

        tab.BeginResize(ClockId, ResizeEdge.BottomRight);
        tab.UpdateDrag(new Point(EdgeX(14), EdgeY(3)));

        Assert.True(tab.Ghost!.Ok);
        Assert.Equal((8, 0, 6, 3), (tab.Ghost.Column, tab.Ghost.Row, tab.Ghost.Columns, tab.Ghost.Rows));
        await tab.EndDragAsync();

        var resized = Of(_document.Current, ClockId);
        Assert.Equal((8, 0, 6, 3), (resized.Column, resized.Row, resized.Columns, resized.Rows));
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Resize_TopLeftHandle_MovesTheOrigin()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 8, 2, 4, 2)));

        tab.BeginResize(ClockId, ResizeEdge.TopLeft);
        tab.UpdateDrag(new Point(EdgeX(6), EdgeY(1)));  // bord gauche à 6, bord haut à 1

        Assert.True(tab.Ghost!.Ok);
        Assert.Equal((6, 1, 6, 3), (tab.Ghost.Column, tab.Ghost.Row, tab.Ghost.Columns, tab.Ghost.Rows));
        await tab.EndDragAsync();
        Assert.Equal((6, 1, 6, 3), (Of(_document.Current, ClockId).Column, Of(_document.Current, ClockId).Row,
            Of(_document.Current, ClockId).Columns, Of(_document.Current, ClockId).Rows));
    }

    [Fact]
    public async Task Preset_Large_AppliesWhenRoom()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 8, 0, 4, 2)));
        tab.Select(ClockId);

        await tab.ApplyPresetAsync(SizePreset.Large);

        var clock = Of(_document.Current, ClockId);
        Assert.Equal((8, 0, 6, 2), (clock.Column, clock.Row, clock.Columns, clock.Rows));
        Assert.Equal(1, _link.ReloadCount);
        Assert.True(tab.IsPresetLarge);
        Assert.False(tab.IsPresetIdeal);
    }

    [Fact]
    public async Task Preset_Large_RefusedWhenNeighbourInTheWay()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 8, 0, 4, 2), Placement(NetworkId, "network", 12, 0, 4, 2)));
        tab.Select(ClockId);

        await tab.ApplyPresetAsync(SizePreset.Large);

        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal("Place prise par Réseau.", tab.RefusalText);
        Assert.Equal(4, Of(_document.Current, ClockId).Columns);
    }

    [Fact]
    public async Task TextSize_IsSavedForTheSelectedModule()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.Select(ClockId);
        Assert.True(tab.IsTextNormal);

        await tab.SetTextSizeAsync(TextSize.Large);

        Assert.Equal(TextSize.Large, Of(_document.Current, ClockId).TextSize);
        Assert.True(tab.IsTextLarge);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Remove_ThenRestore_KeepsSettings()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4),
            Placement(ClockId, "clock", 12, 0, 4, 2, """{"format":"24h"}""")));
        tab.Select(ClockId);

        await tab.RemoveSelectedAsync();

        Assert.Single(tab.Placed);
        Assert.Null(tab.SelectedItem);
        var shelved = Assert.Single(tab.Shelved);
        Assert.Equal("Heure, date et météo", shelved.Name);
        Assert.Single(_document.Current.Shelf!);

        await tab.RestoreAsync(shelved);

        Assert.Empty(tab.Shelved);
        var clock = Of(_document.Current, ClockId);  // même instance : mêmes réglages
        Assert.Equal("24h", clock.Settings.GetProperty("format").GetString());
        Assert.Equal(2, tab.Placed.Count);
        Assert.Equal(2, _link.ReloadCount);
    }

    [Fact]
    public async Task Forget_AsksFirst_ThenDropsTheSettings()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.Select(ClockId);
        await tab.RemoveSelectedAsync();
        var shelved = tab.Shelved.Single();

        tab.AskForget(shelved);
        Assert.True(shelved.ConfirmingForget);
        Assert.Single(_document.Current.Shelf!);  // rien n'est perdu avant la confirmation

        tab.CancelForget(shelved);
        Assert.False(shelved.ConfirmingForget);

        tab.AskForget(shelved);
        await tab.ForgetAsync(shelved);

        Assert.Empty(tab.Shelved);
        Assert.Empty(_document.Current.Shelf!);
    }

    [Fact]
    public async Task Add_Codex_FirstFreeSpot()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4)));
        var codex = tab.AddTypes.Single(t => t.TypeId == "codex");

        await tab.AddAsync(codex);

        var added = _document.Current.Views[0].Placements.Single(p => p.TypeId == "codex");
        Assert.Equal((8, 0, 8, 4), (added.Column, added.Row, added.Columns, added.Rows));  // Idéal 8 × 4, première place libre
        Assert.Equal(2, tab.Placed.Count);
        Assert.Equal(added.InstanceId, tab.SelectedItem!.Id);  // le nouveau module est choisi
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Add_AlreadyPlacedType_IsStillOffered()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        Assert.Contains(tab.AddTypes, t => t.TypeId == "clock");
        await tab.AddAsync(tab.AddTypes.Single(t => t.TypeId == "clock"));

        Assert.Equal(2, _document.Current.Views[0].Placements.Count(p => p.TypeId == "clock"));
    }

    [Fact]
    public async Task Add_WithoutRoom_SaysSo_AndSavesNothing()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 16, 4)));

        await tab.AddAsync(tab.AddTypes.Single(t => t.TypeId == "codex"));

        Assert.Equal("Plus de place libre : retire ou réduis un module.", tab.RefusalText);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Single(_document.Current.Views[0].Placements);
    }

    [Fact]
    public async Task Undo_RestoresPreviousAndSaves()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        Assert.False(tab.CanUndo);

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));
        await tab.EndDragAsync();
        Assert.True(tab.CanUndo);
        Assert.False(tab.CanRedo);

        await tab.UndoAsync();

        Assert.Equal(0, Of(_document.Current, ClockId).Row);
        Assert.Equal(0, Of(_store.Load(), ClockId).Row);   // l'annulation est écrite aussi
        Assert.Equal(2, _link.ReloadCount);
        Assert.False(tab.CanUndo);
        Assert.True(tab.CanRedo);

        await tab.RedoAsync();

        Assert.Equal(2, Of(_document.Current, ClockId).Row);
        Assert.Equal(3, _link.ReloadCount);
        Assert.True(tab.CanUndo);
        Assert.False(tab.CanRedo);
    }

    [Fact]
    public async Task ExternalChange_ClearsHistoryAndDropsTheDrag()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.Select(ClockId);
        await tab.SetTextSizeAsync(TextSize.Large);
        Assert.True(tab.CanUndo);

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));
        _store.Save(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));  // le fichier change sous nos pieds
        await tab.EndDragAsync();

        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);
        Assert.False(tab.CanUndo);
        Assert.Single(tab.Placed);  // la liste montre le fichier relu
        Assert.Equal(Texts.LayoutChangedOutside, _session.Message);
    }

    [Fact]
    public async Task SaveRefusedByService_ShowsTheMessage_AndKeepsTheDocumentLayout()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Disposition invalide : trop de modules.", []));
        tab.Select(ClockId);

        await tab.SetTextSizeAsync(TextSize.Large);

        Assert.Equal("Disposition invalide : trop de modules.", _session.Message);   // la fenêtre le montre, sous la bande, dans tous les onglets
        Assert.Equal(TextSize.Large, Of(_document.Current, ClockId).TextSize);  // la disposition montrée est celle du document
        Assert.True(tab.IsTextLarge);

        // Le message dure jusqu'à la prochaine édition.
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        await tab.SetTextSizeAsync(TextSize.Small);
        Assert.Null(_session.Message);
    }

    [Fact]
    public async Task Drag_Start_ClearsTheServiceMessage()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Refusé.", []));
        tab.Select(ClockId);
        await tab.SetTextSizeAsync(TextSize.Large);
        Assert.Equal("Refusé.", _session.Message);

        tab.BeginMove(ClockId, Center(12, 0));

        Assert.Null(_session.Message);
    }

    [Fact]
    public void SizeInPixels_UsesThemeSpacing()
    {
        var pixel = Layout(Placement(ClockId, "clock", 12, 0, 4, 2)) with { Appearance = new AppearanceSettings("pixel") };
        var tab = Open(pixel);
        tab.Select(ClockId);

        var rect = GridGeometry.ModuleRect(12, 0, 4, 2, new GridSpacing(18, 20));
        Assert.Equal(new GridSpacing(18, 20), tab.Spacing);
        Assert.Equal("456 × 203 px", tab.SelectedPixels);
        Assert.Equal($"{rect.Width} × {rect.Height} px", tab.SelectedPixels);

        var classic = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        classic.Select(ClockId);
        var normal = GridGeometry.ModuleRect(12, 0, 4, 2);
        Assert.Equal($"{normal.Width} × {normal.Height} px", classic.SelectedPixels);
        Assert.NotEqual(tab.SelectedPixels, classic.SelectedPixels);
    }

    [Fact]
    public void SelectedModule_ShowsNameCellsAndMinimum()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4)));
        tab.Select(ClaudeId);

        Assert.Equal("Claude Code", tab.SelectedName);
        Assert.Equal("8 × 4 cases", tab.SelectedCells);
        Assert.Equal("minimum 6 × 3", tab.SelectedMinimum);
        Assert.True(tab.IsPresetIdeal);
        Assert.False(tab.IsPresetCompact);
        Assert.True(tab.HasSelection);

        tab.Select(null);
        Assert.False(tab.HasSelection);
    }

    [Fact]
    public void PlacedList_ShowsNameSizeAndHealth()
    {
        var status = SampleData.Status() with
        {
            Modules =
            [
                new ModuleState(ClaudeId, "claudecode", ModuleHealth.Online, null, []),
                new ModuleState(MonitoringId, "monitoring", ModuleHealth.Offline, null, []),
            ],
        };
        _link.Set(LinkState.Connected, SampleData.Welcome(), status);
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(MonitoringId, "monitoring", 8, 0, 4, 2),
            Placement(ClockId, "clock", 12, 0, 4, 2)));

        Assert.Equal(["Claude Code", "Monitoring PC", "Heure, date et météo"], tab.Placed.Select(p => p.Name));
        Assert.Equal(["8 × 4", "4 × 2", "4 × 2"], tab.Placed.Select(p => p.SizeText));
        Assert.True(tab.Placed[0].IsOnline);
        Assert.True(tab.Placed[1].IsRefuse);
        Assert.True(tab.Placed[2].IsIdle);  // le service ne dit rien de lui : non configuré
    }

    [Fact]
    public void PlacedList_UnknownType_UsesTheTypeId()
    {
        var tab = Open(Layout(Placement(ClockId, "futur", 12, 0, 4, 2)));

        Assert.Equal("futur", tab.Placed.Single().Name);
    }

    [Fact]
    public void Status_UpdatesHealthWithoutRebuildingTheList()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4)));
        var item = tab.Placed.Single();
        Assert.True(item.IsIdle);

        var status = SampleData.Status() with { Modules = [new ModuleState(ClaudeId, "claudecode", ModuleHealth.Online, null, [])] };
        _link.Set(LinkState.Connected, SampleData.Welcome(), status);

        Assert.Same(item, tab.Placed.Single());
        Assert.True(item.IsOnline);
    }

    [Fact]
    public void Select_FromTheList_SelectsTheModule()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.SelectedItem = tab.Placed[1];

        Assert.Equal(ClockId, tab.SelectedId);
        Assert.Equal("Heure, date et météo", tab.SelectedName);
    }

    [Fact]
    public async Task BeforeTheServiceAnswers_NothingCanBeEdited()
    {
        _link.Set(LinkState.Connecting);
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));
        await tab.EndDragAsync();

        Assert.False(tab.CanEdit);
        Assert.False(tab.IsDragging);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal("clock", Of(_document.Current, ClockId).TypeId);
    }

    [Fact]
    public void CancelDrag_DropsTheGhost()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));

        tab.CancelDrag();

        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task WhileASaveIsPending_TheNewLayoutIsShown_AndNoDragStarts()
    {
        var tab = Open(Layout(Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)));
        var release = new TaskCompletionSource<Reloaded>();
        _link.OnReload = () => release.Task;

        tab.BeginMove(ClockId, Center(12, 0));
        tab.UpdateDrag(Center(12, 2));
        var ending = tab.EndDragAsync();  // le service n'a pas encore répondu

        Assert.False(ending.IsCompleted);
        Assert.Null(tab.Ghost);
        Assert.Equal(2, tab.Placed.Single(p => p.Id == ClockId).Row);  // le nouveau cadre est déjà montré, le contour ne revient pas en arrière
        Assert.Equal(2, tab.Placed.Single(p => p.Id == ClockId).Row);
        Assert.True(_session.IsBusy);

        tab.BeginMove(ClockId, Center(12, 2));  // une saisie pendant l'attente est refusée
        tab.BeginResize(ClockId, ResizeEdge.Right);
        Assert.False(tab.IsDragging);

        release.SetResult(new Reloaded(true, null, []));
        await ending;

        Assert.False(_session.IsBusy);
        tab.BeginMove(ClockId, Center(12, 2));
        Assert.True(tab.IsDragging);
    }

    [Fact]
    public void WhenEditingBecomesImpossible_TheDragIsDropped()
    {
        var tab = Open(Layout(Placement(ClockId, "clock", 12, 0, 4, 2)));
        tab.BeginMove(ClockId, Center(12, 0));
        Assert.True(tab.IsDragging);

        _link.Set(LinkState.Connecting);

        Assert.False(tab.CanEdit);
        Assert.False(tab.IsDragging);
        Assert.Null(tab.Ghost);
    }
}
