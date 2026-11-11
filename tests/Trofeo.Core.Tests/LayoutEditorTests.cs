using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public class LayoutEditorTests
{
    private static ModuleDescriptor Descriptor(
        string id, GridSize min, GridSize compact, GridSize ideal, GridSize large) =>
        new(id, id, min, new Dictionary<SizePreset, GridSize>
        {
            [SizePreset.Compact] = compact,
            [SizePreset.Ideal] = ideal,
            [SizePreset.Large] = large,
        });

    // Tailles reprises des fabriques réelles (Core.Tests ne référence pas les projets de modules).
    private static readonly IReadOnlyDictionary<string, ModuleDescriptor> Types =
        new Dictionary<string, ModuleDescriptor>
        {
            ["claudecode"] = Descriptor("claudecode", new(6, 3), new(6, 3), new(8, 4), new(10, 4)),
            ["codex"] = Descriptor("codex", new(6, 3), new(6, 3), new(8, 4), new(10, 4)),
            ["clock"] = Descriptor("clock", new(3, 1), new(3, 1), new(4, 2), new(6, 2)),
            ["monitoring"] = Descriptor("monitoring", new(3, 2), new(4, 2), new(6, 2), new(6, 4)),
            ["network"] = Descriptor("network", new(3, 1), new(3, 1), new(4, 2), new(6, 2)),
            ["nowplaying"] = Descriptor("nowplaying", new(3, 1), new(3, 1), new(4, 2), new(6, 2)),
        };

    private static readonly LayoutEditor Editor = new(Types);

    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid MonitoringId = new("246ac06d-eec2-4d64-9930-24b64603a03b");
    private static readonly Guid NowPlayingId = new("8ebcc647-ab4b-401d-b2b3-f7a0ebd97b4b");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");
    private static readonly Guid NetworkId = new("a2002423-c807-4403-9c5a-2a983fb6a371");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModulePlacement Placement(
        string typeId, int column, int row, int columns, int rows, Guid? id = null, string settings = "{}",
        TextSize textSize = TextSize.Normal) =>
        new(id ?? Guid.NewGuid(), typeId, column, row, columns, rows, textSize, Json(settings));

    private static DashboardLayout Layout(params ModulePlacement[] placements) => DashboardLayout.SingleView(180, placements);

    private static Guid ViewOf(DashboardLayout layout) => layout.Views[0].Id;

    private static ModulePlacement Find(DashboardLayout layout, Guid id) =>
        layout.Views[0].Placements.Single(p => p.InstanceId == id);

    [Fact]
    public void Move_ToFreeSpot_Moves()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        var result = Editor.Move(layout, ViewOf(layout), ClockId, 8, 2);

        Assert.True(result.Ok);
        Assert.Null(result.Blocker);
        var moved = Find(result.Layout, ClockId);
        Assert.Equal((8, 2, 4, 2), (moved.Column, moved.Row, moved.Columns, moved.Rows));
        // L'entrée n'est pas modifiée.
        Assert.Equal(12, Find(layout, ClockId).Column);
    }

    [Fact]
    public void Move_ToItsOwnSpot_IsAllowed()
    {
        var result = Editor.Move(DashboardLayout.Default, ViewOf(DashboardLayout.Default), ClockId, 12, 0);

        Assert.True(result.Ok);
    }

    [Fact]
    public void Move_OntoNeighbour_RefusedWithBlocker()
    {
        var layout = DashboardLayout.Default;

        var result = Editor.Move(layout, ViewOf(layout), ClockId, 8, 0);

        Assert.Equal(PlacementError.Overlap, result.Error);
        Assert.False(result.Ok);
        Assert.Equal(MonitoringId, result.Blocker);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void Move_OntoSeveralModules_NamesTheFirstInPlacementsOrder()
    {
        // Heure à cheval sur En écoute (rangée 2) et Monitoring (rangée 0), colonnes 8 à 11 ; Monitoring vient avant.
        var result = Editor.Move(DashboardLayout.Default, ViewOf(DashboardLayout.Default), ClockId, 8, 1);
        Assert.Equal(MonitoringId, result.Blocker);

        // Réseau en (10, 1) : touche Monitoring, En écoute et Heure ; Monitoring est le premier de Placements.
        var onClockAndNowPlaying = Editor.Move(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId, 10, 1);
        Assert.Equal(PlacementError.Overlap, onClockAndNowPlaying.Error);
        Assert.Equal(MonitoringId, onClockAndNowPlaying.Blocker);
    }

    [Fact]
    public void Move_OffGrid_IsOutOfBounds()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        var right = Editor.Move(layout, ViewOf(layout), ClockId, 13, 0);
        var bottom = Editor.Move(layout, ViewOf(layout), ClockId, 12, 3);
        var negative = Editor.Move(layout, ViewOf(layout), ClockId, -1, 0);

        Assert.Equal(PlacementError.OutOfBounds, right.Error);
        Assert.Equal(PlacementError.OutOfBounds, bottom.Error);
        Assert.Equal(PlacementError.OutOfBounds, negative.Error);
        Assert.Same(layout, right.Layout);
        Assert.Null(right.Blocker);
    }

    [Fact]
    public void Move_UnknownInstance_IsUnknownType()
    {
        var result = Editor.Move(DashboardLayout.Default, ViewOf(DashboardLayout.Default), Guid.NewGuid(), 0, 0);

        Assert.Equal(PlacementError.UnknownType, result.Error);
        Assert.Same(DashboardLayout.Default, result.Layout);
    }

    [Fact]
    public void Resize_LeftEdgeGrowsLeft()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        var result = Editor.Resize(layout, ViewOf(layout), ClockId, ResizeEdge.Left, -1, 0);

        Assert.True(result.Ok);
        var resized = Find(result.Layout, ClockId);
        Assert.Equal((11, 0, 5, 2), (resized.Column, resized.Row, resized.Columns, resized.Rows));
    }

    [Fact]
    public void Resize_LeftEdgePositiveShrinks()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        var resized = Find(Editor.Resize(layout, ViewOf(layout), ClockId, ResizeEdge.Left, 1, 0).Layout, ClockId);

        Assert.Equal((13, 0, 3, 2), (resized.Column, resized.Row, resized.Columns, resized.Rows));
    }

    [Fact]
    public void Resize_TopEdgeGrowsUp_BottomAndRightGrowDown()
    {
        var layout = Layout(Placement("monitoring", 8, 1, 4, 2, MonitoringId));

        var top = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Top, -1, -1).Layout, MonitoringId);
        Assert.Equal((8, 0, 4, 3), (top.Column, top.Row, top.Columns, top.Rows)); // le delta de colonnes est ignoré

        var bottom = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Bottom, 0, 1).Layout, MonitoringId);
        Assert.Equal((8, 1, 4, 3), (bottom.Column, bottom.Row, bottom.Columns, bottom.Rows));

        var right = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Right, 2, 0).Layout, MonitoringId);
        Assert.Equal((8, 1, 6, 2), (right.Column, right.Row, right.Columns, right.Rows));
    }

    [Fact]
    public void Resize_Corners_CombineBothAxes()
    {
        var layout = Layout(Placement("monitoring", 8, 1, 4, 2, MonitoringId));

        var bottomRight = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.BottomRight, 1, 1).Layout, MonitoringId);
        Assert.Equal((8, 1, 5, 3), (bottomRight.Column, bottomRight.Row, bottomRight.Columns, bottomRight.Rows));

        var topLeft = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.TopLeft, -1, -1).Layout, MonitoringId);
        Assert.Equal((7, 0, 5, 3), (topLeft.Column, topLeft.Row, topLeft.Columns, topLeft.Rows));

        var topRight = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.TopRight, 1, -1).Layout, MonitoringId);
        Assert.Equal((8, 0, 5, 3), (topRight.Column, topRight.Row, topRight.Columns, topRight.Rows));

        var bottomLeft = Find(Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.BottomLeft, -2, 1).Layout, MonitoringId);
        Assert.Equal((6, 1, 6, 3), (bottomLeft.Column, bottomLeft.Row, bottomLeft.Columns, bottomLeft.Rows));
    }

    [Fact]
    public void Resize_BelowMin_IsRefused()
    {
        var layout = Layout(Placement("monitoring", 8, 0, 4, 2, MonitoringId));

        var narrower = Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Right, -2, 0); // 2 colonnes, minimum 3
        var shorter = Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Bottom, 0, -1);  // 1 rangée, minimum 2
        var vanished = Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Left, 4, 0);    // 0 colonne

        Assert.Equal(PlacementError.BelowMinSize, narrower.Error);
        Assert.Equal(PlacementError.BelowMinSize, shorter.Error);
        Assert.Equal(PlacementError.BelowMinSize, vanished.Error);
        Assert.Same(layout, narrower.Layout);
    }

    [Fact]
    public void Resize_IntoNeighbour_IsOverlapWithBlocker()
    {
        var layout = DashboardLayout.Default;

        var result = Editor.Resize(layout, ViewOf(layout), MonitoringId, ResizeEdge.Bottom, 0, 1); // sur En écoute

        Assert.Equal(PlacementError.Overlap, result.Error);
        Assert.Equal(NowPlayingId, result.Blocker);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void Resize_PastTheGrid_IsOutOfBounds()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        Assert.Equal(PlacementError.OutOfBounds, Editor.Resize(layout, ViewOf(layout), ClockId, ResizeEdge.Right, 1, 0).Error);
        Assert.Equal(PlacementError.OutOfBounds, Editor.Resize(layout, ViewOf(layout), ClockId, ResizeEdge.Top, 0, -1).Error);
        Assert.Equal(PlacementError.OutOfBounds, Editor.Resize(layout, ViewOf(layout), ClockId, ResizeEdge.Left, int.MinValue, 0).Error);
    }

    [Fact]
    public void ApplyPreset_KeepsTopLeft()
    {
        var layout = Layout(Placement("clock", 2, 1, 4, 2, ClockId));

        var result = Editor.ApplyPreset(layout, ViewOf(layout), ClockId, SizePreset.Compact);

        var clock = Find(result.Layout, ClockId);
        Assert.True(result.Ok);
        Assert.Equal((2, 1, 3, 1), (clock.Column, clock.Row, clock.Columns, clock.Rows));
    }

    [Fact]
    public void ApplyPreset_AtRightEdge_ShiftsLeftToFit()
    {
        var layout = Layout(Placement("clock", 12, 0, 4, 2, ClockId));

        var result = Editor.ApplyPreset(layout, ViewOf(layout), ClockId, SizePreset.Large); // 6 × 2

        var clock = Find(result.Layout, ClockId);
        Assert.True(result.Ok);
        Assert.Equal((10, 0, 6, 2), (clock.Column, clock.Row, clock.Columns, clock.Rows));
    }

    [Fact]
    public void ApplyPreset_AtBottom_ShiftsUpToFit()
    {
        var layout = Layout(Placement("monitoring", 8, 2, 4, 2, MonitoringId));

        var result = Editor.ApplyPreset(layout, ViewOf(layout), MonitoringId, SizePreset.Large); // 6 × 4

        var monitoring = Find(result.Layout, MonitoringId);
        Assert.True(result.Ok);
        Assert.Equal((8, 0, 6, 4), (monitoring.Column, monitoring.Row, monitoring.Columns, monitoring.Rows));
    }

    [Fact]
    public void ApplyPreset_OntoNeighbour_IsOverlapWithBlocker()
    {
        var layout = DashboardLayout.Default;

        var result = Editor.ApplyPreset(layout, ViewOf(layout), ClockId, SizePreset.Large); // 6 × 2 à (10, 0) : sur Monitoring

        Assert.Equal(PlacementError.Overlap, result.Error);
        Assert.Equal(MonitoringId, result.Blocker);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void Add_ShelvedType_RestoresItsSettings()
    {
        var removed = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId);
        Assert.True(removed.Ok);

        var result = Editor.Add(removed.Layout, ViewOf(removed.Layout), "network", Guid.NewGuid());

        Assert.True(result.Ok);
        var back = Find(result.Layout, NetworkId);
        Assert.Equal("pc", back.Settings.GetProperty("source").GetString());
        Assert.Equal((12, 2, 4, 2), (back.Column, back.Row, back.Columns, back.Rows));
        Assert.DoesNotContain(result.Layout.Shelf ?? [], p => p.InstanceId == NetworkId);
        Assert.Equal(DashboardLayout.Default.Views[0].Placements.Count, result.Layout.Views[0].Placements.Count);
    }

    [Fact]
    public void Add_ShelvedType_ReusesTheLastShelvedOne()
    {
        var first = Placement("network", 0, 0, 4, 2, settings: """{"source":"a"}""", textSize: TextSize.Large);
        var second = Placement("network", 4, 0, 4, 2, settings: """{"source":"b"}""", textSize: TextSize.Small);
        var layout = DashboardLayout.SingleView(180, [], shelf: [first, second]);
        var ignored = Guid.NewGuid();

        var result = Editor.Add(layout, ViewOf(layout), "network", ignored);

        Assert.True(result.Ok);
        var placed = Assert.Single(result.Layout.Views[0].Placements);
        Assert.Equal(second.InstanceId, placed.InstanceId);
        Assert.Equal("b", placed.Settings.GetProperty("source").GetString());
        Assert.Equal(TextSize.Small, placed.TextSize);
        Assert.NotEqual(ignored, placed.InstanceId);
        Assert.Equal(first.InstanceId, Assert.Single(result.Layout.Shelf!).InstanceId);
    }

    [Fact]
    public void Add_NewType_CreatesInstanceWithEmptySettings()
    {
        var layout = Layout(Placement("claudecode", 0, 0, 8, 4, ClaudeId));
        var newId = Guid.NewGuid();

        var result = Editor.Add(layout, ViewOf(layout), "clock", newId);

        Assert.True(result.Ok);
        var clock = Find(result.Layout, newId);
        Assert.Equal((8, 0, 4, 2), (clock.Column, clock.Row, clock.Columns, clock.Rows)); // Idéal, première place libre
        Assert.Equal("{}", clock.Settings.GetRawText());
        Assert.Equal(TextSize.Normal, clock.TextSize);
        Assert.Equal(2, result.Layout.Views[0].Placements.Count);
        Assert.Same(layout.Views[0].Placements[0], result.Layout.Views[0].Placements[0]);
    }

    [Fact]
    public void Add_UnknownType_IsUnknownType()
    {
        var result = Editor.Add(DashboardLayout.Default, ViewOf(DashboardLayout.Default), "meteo", Guid.NewGuid());

        Assert.Equal(PlacementError.UnknownType, result.Error);
        Assert.Same(DashboardLayout.Default, result.Layout);
    }

    [Fact]
    public void Add_FullGrid_IsNoRoom()
    {
        var layout = DashboardLayout.Default;

        var result = Editor.Add(layout, ViewOf(layout), "clock", Guid.NewGuid());

        Assert.Equal(PlacementError.NoRoom, result.Error);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void Add_FullGrid_WithShelvedType_IsNoRoomAndKeepsShelf()
    {
        var shelved = Placement("network", 0, 0, 4, 2, NetworkId, """{"source":"pc"}""");
        var layout = DashboardLayout.SingleView(180, [Placement("clock", 0, 0, 16, 4, ClockId)], shelf: [shelved]);

        var result = Editor.Add(layout, ViewOf(layout), "network", Guid.NewGuid());

        Assert.Equal(PlacementError.NoRoom, result.Error);
        Assert.Same(layout, result.Layout);
        Assert.Equal(NetworkId, Assert.Single(result.Layout.Shelf!).InstanceId);
    }

    [Fact]
    public void Add_TriesIdealThenCompactThenMin()
    {
        // Idéal de Monitoring : 6 × 2 ; Compact : 4 × 2 ; minimum : 3 × 2.
        var wide = Layout(Placement("claudecode", 0, 0, 8, 4, ClaudeId));
        var ideal = Editor.Add(wide, ViewOf(wide), "monitoring", Guid.NewGuid()).Layout.Views[0].Placements[1];
        Assert.Equal((8, 0, 6, 2), (ideal.Column, ideal.Row, ideal.Columns, ideal.Rows));

        var four = Layout(Placement("claudecode", 0, 0, 12, 4, ClaudeId));
        var compact = Editor.Add(four, ViewOf(four), "monitoring", Guid.NewGuid()).Layout.Views[0].Placements[1];
        Assert.Equal((12, 0, 4, 2), (compact.Column, compact.Row, compact.Columns, compact.Rows));

        var three = Layout(Placement("claudecode", 0, 0, 13, 4, ClaudeId));
        var min = Editor.Add(three, ViewOf(three), "monitoring", Guid.NewGuid()).Layout.Views[0].Placements[1];
        Assert.Equal((13, 0, 3, 2), (min.Column, min.Row, min.Columns, min.Rows));

        var two = Layout(Placement("claudecode", 0, 0, 14, 4, ClaudeId));
        Assert.Equal(PlacementError.NoRoom, Editor.Add(two, ViewOf(two), "monitoring", Guid.NewGuid()).Error);
    }

    [Fact]
    public void Add_SearchesRowByRowThenColumnByColumn()
    {
        // Rangée 0 pleine : le module va en rangée 1, à la première colonne libre.
        var layout = Layout(Placement("clock", 0, 0, 16, 1, ClockId));

        var placed = Editor.Add(layout, ViewOf(layout), "monitoring", Guid.NewGuid()).Layout.Views[0].Placements[1];

        Assert.Equal((0, 1, 6, 2), (placed.Column, placed.Row, placed.Columns, placed.Rows));

        // Colonnes 0 à 3 occupées sur la rangée 0 : première colonne libre, 4.
        var left = Layout(Placement("clock", 0, 0, 4, 2, ClockId));
        var next = Editor.Add(left, ViewOf(left), "monitoring", Guid.NewGuid()).Layout.Views[0].Placements[1];
        Assert.Equal((4, 0, 6, 2), (next.Column, next.Row, next.Columns, next.Rows));
    }

    [Fact]
    public void Remove_KeepsSettingsInShelf()
    {
        var layout = DashboardLayout.Default;

        var result = Editor.Remove(layout, ViewOf(layout), NetworkId);

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Layout.Views[0].Placements, p => p.InstanceId == NetworkId);
        var shelved = Assert.Single(result.Layout.Shelf!);
        Assert.Equal(NetworkId, shelved.InstanceId);
        Assert.Equal("pc", shelved.Settings.GetProperty("source").GetString());
        Assert.Equal(4, result.Layout.Views[0].Placements.Count);
        // L'entrée n'est pas modifiée.
        Assert.Equal(5, layout.Views[0].Placements.Count);
        Assert.Null(layout.Shelf);
    }

    [Fact]
    public void Remove_AppendsToTheEndOfShelf()
    {
        var first = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId).Layout;

        var second = Editor.Remove(first, ViewOf(first), ClockId).Layout;

        Assert.Equal([NetworkId, ClockId], second.Shelf!.Select(p => p.InstanceId));
    }

    [Fact]
    public void Remove_UnknownInstance_IsRefused()
    {
        var result = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), Guid.NewGuid());

        Assert.Equal(PlacementError.UnknownType, result.Error);
        Assert.Same(DashboardLayout.Default, result.Layout);
    }

    [Fact]
    public void Forget_RemovesFromShelf()
    {
        var removed = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId).Layout;

        var result = Editor.Forget(removed, NetworkId);

        Assert.True(result.Ok);
        Assert.Empty(result.Layout.Shelf ?? []);
        Assert.Single(removed.Shelf!); // l'entrée n'est pas modifiée

        var missing = Editor.Forget(removed, Guid.NewGuid());
        Assert.Equal(PlacementError.UnknownType, missing.Error);
        Assert.Same(removed, missing.Layout);
    }

    [Fact]
    public void SetTextSize_ChangesPlacedAndShelved()
    {
        var removed = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId).Layout;

        var placed = Editor.SetTextSize(removed, ClockId, TextSize.Large);
        var shelved = Editor.SetTextSize(removed, NetworkId, TextSize.Small);

        Assert.True(placed.Ok);
        Assert.Equal(TextSize.Large, Find(placed.Layout, ClockId).TextSize);
        Assert.Equal(TextSize.Normal, Find(removed, ClockId).TextSize);
        Assert.True(shelved.Ok);
        Assert.Equal(TextSize.Small, shelved.Layout.Shelf!.Single().TextSize);

        Assert.Equal(PlacementError.UnknownType, Editor.SetTextSize(removed, Guid.NewGuid(), TextSize.Small).Error);
    }

    [Fact]
    public void SetSettings_ChangesPlacedAndShelved()
    {
        var removed = Editor.Remove(DashboardLayout.Default, ViewOf(DashboardLayout.Default), NetworkId).Layout;
        var settings = Json("""{"source":"phone"}""");

        var shelved = Editor.SetSettings(removed, NetworkId, settings);
        var placed = Editor.SetSettings(removed, MonitoringId, settings);

        Assert.True(shelved.Ok);
        Assert.Equal("phone", shelved.Layout.Shelf!.Single().Settings.GetProperty("source").GetString());
        Assert.True(placed.Ok);
        Assert.Equal("phone", Find(placed.Layout, MonitoringId).Settings.GetProperty("source").GetString());
        Assert.Equal("{}", Find(removed, MonitoringId).Settings.GetRawText());

        Assert.Equal(PlacementError.UnknownType, Editor.SetSettings(removed, Guid.NewGuid(), settings).Error);
    }

    [Fact]
    public void Edits_KeepRotationShortcutsAppearanceAndMaxFps()
    {
        var layout = DashboardLayout.Default with { Shortcuts = [], MaxFps = 8 };

        var moved = Editor.Remove(layout, ViewOf(layout), NetworkId).Layout;

        Assert.Equal(180, moved.Rotation);
        Assert.Same(layout.Shortcuts, moved.Shortcuts);
        Assert.Equal(8, moved.MaxFps);
    }

    [Fact]
    public void Move_ChecksOverlapOnlyInsideItsView()
    {
        var firstView = Guid.NewGuid();
        var secondView = Guid.NewGuid();
        var layout = new DashboardLayout(180,
        [
            new LayoutView(firstView, "Un", [Placement("clock", 0, 0, 4, 2, ClockId)]),
            new LayoutView(secondView, "Deux", [Placement("network", 8, 0, 4, 2, NetworkId), Placement("monitoring", 0, 2, 4, 2, MonitoringId)]),
        ]);

        // Même endroit que l'Heure de l'autre vue : permis.
        var apart = Editor.Move(layout, secondView, NetworkId, 0, 0);
        Assert.True(apart.Ok);
        Assert.Equal((0, 0), (Find(apart.Layout, secondView, NetworkId).Column, Find(apart.Layout, secondView, NetworkId).Row));
        Assert.Equal(0, Find(apart.Layout, firstView, ClockId).Column); // l'autre vue n'est pas touchée

        // Dans sa propre vue, le chevauchement reste refusé et nomme le gêneur.
        var together = Editor.Move(layout, secondView, NetworkId, 0, 1);
        Assert.Equal(PlacementError.Overlap, together.Error);
        Assert.Equal(MonitoringId, together.Blocker);

        // Une case cherchée dans la mauvaise vue, ou une vue inconnue : refus.
        Assert.Equal(PlacementError.UnknownType, Editor.Move(layout, firstView, NetworkId, 4, 0).Error);
        Assert.Equal(PlacementError.UnknownType, Editor.Move(layout, Guid.NewGuid(), ClockId, 4, 0).Error);
    }

    [Fact]
    public void Add_GoesIntoTheChosenViewOnly()
    {
        var firstView = Guid.NewGuid();
        var secondView = Guid.NewGuid();
        var layout = new DashboardLayout(180,
        [
            new LayoutView(firstView, "Un", [Placement("claudecode", 0, 0, 16, 4, ClaudeId)]),
            new LayoutView(secondView, "Deux", []),
        ]);

        var full = Editor.Add(layout, firstView, "clock", Guid.NewGuid());
        var free = Editor.Add(layout, secondView, "clock", Guid.NewGuid());

        Assert.Equal(PlacementError.NoRoom, full.Error);
        Assert.True(free.Ok);
        Assert.Single(free.Layout.View(firstView).Placements);
        var added = Assert.Single(free.Layout.View(secondView).Placements);
        Assert.Equal((0, 0), (added.Column, added.Row));
        Assert.Equal(PlacementError.UnknownType, Editor.Add(layout, Guid.NewGuid(), "clock", Guid.NewGuid()).Error);
    }

    [Fact]
    public void Remove_SendsTheCaseOfThatViewToTheShelf()
    {
        var firstView = Guid.NewGuid();
        var secondView = Guid.NewGuid();
        var layout = new DashboardLayout(180,
        [
            new LayoutView(firstView, "Un", [Placement("clock", 0, 0, 4, 2, ClockId)]),
            new LayoutView(secondView, "Deux", [Placement("network", 0, 0, 4, 2, NetworkId, """{"source":"pc"}""")]),
        ]);

        var result = Editor.Remove(layout, secondView, NetworkId);

        Assert.True(result.Ok);
        Assert.Empty(result.Layout.View(secondView).Placements);
        Assert.Single(result.Layout.View(firstView).Placements);
        Assert.Equal("pc", Assert.Single(result.Layout.Shelf!).Settings.GetProperty("source").GetString());
        Assert.Equal(PlacementError.UnknownType, Editor.Remove(layout, firstView, NetworkId).Error);
    }

    [Fact]
    public void SetName_NamesAPlacedCaseInAnyView()
    {
        var secondView = Guid.NewGuid();
        var layout = new DashboardLayout(180,
        [
            new LayoutView(Guid.NewGuid(), "Un", [Placement("clock", 0, 0, 4, 2, ClockId)]),
            new LayoutView(secondView, "Deux", [Placement("network", 0, 0, 4, 2, NetworkId)]),
        ]);

        var named = Editor.SetName(layout, NetworkId, "  Réseau du salon ");

        Assert.True(named.Ok);
        Assert.Equal("Réseau du salon", named.Layout.View(secondView).Placements[0].Name);
        Assert.Null(named.Layout.Views[0].Placements[0].Name);
        Assert.Null(Editor.SetName(named.Layout, NetworkId, "   ").Layout.View(secondView).Placements[0].Name);
        Assert.Null(Editor.SetName(named.Layout, NetworkId, null).Layout.View(secondView).Placements[0].Name);
        Assert.Equal(PlacementError.UnknownType, Editor.SetName(layout, Guid.NewGuid(), "x").Error);
    }

    [Fact]
    public void SetName_LongName_IsCutAt30()
    {
        var layout = Layout(Placement("clock", 0, 0, 4, 2, ClockId));

        var result = Editor.SetName(layout, ClockId, new string('a', 40));

        Assert.Equal(new string('a', 30), Find(result.Layout, ClockId).Name);
    }

    [Fact]
    public void SetName_IsKeptOnTheShelfToo()
    {
        var shelved = Placement("network", 0, 0, 4, 2, NetworkId);
        var layout = DashboardLayout.SingleView(180, [], shelf: [shelved]);

        var named = Editor.SetName(layout, NetworkId, "Garage");
        var back = Editor.Add(named.Layout, ViewOf(named.Layout), "network", Guid.NewGuid());

        Assert.True(named.Ok);
        Assert.Equal("Garage", Assert.Single(named.Layout.Shelf!).Name);
        Assert.Empty(named.Layout.Views[0].Placements);
        Assert.Equal("Garage", Assert.Single(back.Layout.Views[0].Placements).Name); // le nom suit la case remise sur l'écran
    }

    private static ModulePlacement Find(DashboardLayout layout, Guid viewId, Guid id) =>
        layout.View(viewId).Placements.Single(p => p.InstanceId == id);
}
