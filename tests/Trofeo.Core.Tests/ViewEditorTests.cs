using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public sealed class ViewEditorTests
{
    private static readonly Guid A = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = new("bbbbbbbb-0000-0000-0000-000000000003");

    private static ModulePlacement Place(string typeId, int column, string settings = "{}", string? name = null) =>
        new(Guid.NewGuid(), typeId, column, 0, 4, 2, TextSize.Large, JsonDocument.Parse(settings).RootElement.Clone(), name);

    private static DashboardLayout ThreeViews() => new(180,
    [
        new LayoutView(A, "Un", [Place("clock", 0, name: "Horloge")]),
        new LayoutView(B, "Deux", [Place("network", 0, """{"source":"unifi"}"""), Place("clock", 4)]),
        new LayoutView(C, "Trois", []),
    ]);

    private static DashboardLayout NineViews() => new(180,
        [.. Enumerable.Range(1, LayoutView.MaxViews).Select(i => new LayoutView(Guid.NewGuid(), $"Vue {i}", []))]);

    [Fact]
    public void AddView_AppendsAnEmptyView()
    {
        var layout = ThreeViews();
        var id = Guid.NewGuid();

        var result = ViewEditor.AddView(layout, "  Jeu  ", id);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Layout.Views.Count);
        var added = result.Layout.Views[^1];
        Assert.Equal((id, "Jeu"), (added.Id, added.Name));
        Assert.Empty(added.Placements);
        Assert.Equal(3, layout.Views.Count); // l'entrée n'est pas modifiée
    }

    [Fact]
    public void AddView_TenthView_IsRefused()
    {
        var layout = NineViews();

        var result = ViewEditor.AddView(layout, "Dixième", Guid.NewGuid());

        Assert.Equal(ViewError.TooManyViews, result.Error);
        Assert.False(result.Ok);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void AddView_EmptyName_IsRefused()
    {
        var result = ViewEditor.AddView(ThreeViews(), "   ", Guid.NewGuid());

        Assert.Equal(ViewError.EmptyName, result.Error);
    }

    [Fact]
    public void DeleteView_LastView_IsRefused()
    {
        var layout = DashboardLayout.SingleView(180, [Place("clock", 0)]);

        var result = ViewEditor.DeleteView(layout, layout.Views[0].Id);

        Assert.Equal(ViewError.LastView, result.Error);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void UnknownView_IsRefusedByEveryOperation()
    {
        var layout = ThreeViews();

        Assert.Equal(ViewError.UnknownView, ViewEditor.DeleteView(layout, Guid.NewGuid()).Error);
        Assert.Equal(ViewError.UnknownView, ViewEditor.RenameView(layout, Guid.NewGuid(), "x").Error);
        Assert.Equal(ViewError.UnknownView, ViewEditor.MoveView(layout, Guid.NewGuid(), 0).Error);
        Assert.Equal(ViewError.UnknownView, ViewEditor.DuplicateView(layout, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid).Error);
    }

    [Fact]
    public void DeleteView_SendsItsCasesToTheShelfWithTheirSettings()
    {
        var layout = ThreeViews() with { Shelf = [Place("monitoring", 0)] };
        var doomed = layout.Views[1].Placements;

        var result = ViewEditor.DeleteView(layout, B);

        Assert.True(result.Ok);
        Assert.Equal([A, C], result.Layout.Views.Select(v => v.Id));
        var shelf = result.Layout.Shelf!;
        Assert.Equal(3, shelf.Count);
        Assert.Equal("monitoring", shelf[0].TypeId); // l'étagère d'avant reste en tête
        Assert.Equal(doomed.Select(p => p.InstanceId), shelf.Skip(1).Select(p => p.InstanceId)); // mêmes identifiants
        Assert.Equal("unifi", shelf[1].Settings.GetProperty("source").GetString());
        Assert.Equal(TextSize.Large, shelf[1].TextSize);
    }

    [Fact]
    public void DuplicateView_CopiesCasesWithNewIdsAndSameSettings()
    {
        var layout = ThreeViews();
        var newView = Guid.NewGuid();
        var made = new List<Guid>();

        var result = ViewEditor.DuplicateView(layout, B, newView, () =>
        {
            var id = Guid.NewGuid();
            made.Add(id);
            return id;
        });

        Assert.True(result.Ok);
        Assert.Equal([A, B, newView, C], result.Layout.Views.Select(v => v.Id)); // juste après l'original
        var copy = result.Layout.Views[2];
        Assert.Equal("Deux (copie)", copy.Name);
        Assert.Equal(made, copy.Placements.Select(p => p.InstanceId));
        Assert.Empty(copy.Placements.Select(p => p.InstanceId).Intersect(layout.Views[1].Placements.Select(p => p.InstanceId)));
        Assert.Equal("unifi", copy.Placements[0].Settings.GetProperty("source").GetString());
        Assert.Equal(layout.Views[1].Placements.Select(p => (p.TypeId, p.Column, p.Row, p.Columns, p.Rows, p.TextSize)),
            copy.Placements.Select(p => (p.TypeId, p.Column, p.Row, p.Columns, p.Rows, p.TextSize)));
    }

    [Fact]
    public void DuplicateView_LongName_StaysWithin30()
    {
        var layout = new DashboardLayout(180, [new LayoutView(A, new string('x', 30), [])]);

        var copy = ViewEditor.DuplicateView(layout, A, B, Guid.NewGuid).Layout.Views[1];

        Assert.Equal(30, copy.Name.Length);
        Assert.EndsWith(" (copie)", copy.Name);
    }

    [Fact]
    public void DuplicateView_TenthView_IsRefused()
    {
        var layout = NineViews();

        Assert.Equal(ViewError.TooManyViews, ViewEditor.DuplicateView(layout, layout.Views[0].Id, Guid.NewGuid(), Guid.NewGuid).Error);
    }

    [Fact]
    public void MoveView_ReordersViews()
    {
        var layout = ThreeViews();

        Assert.Equal([B, C, A], ViewEditor.MoveView(layout, A, 2).Layout.Views.Select(v => v.Id));
        Assert.Equal([C, A, B], ViewEditor.MoveView(layout, C, 0).Layout.Views.Select(v => v.Id));
        Assert.Equal([A, C, B], ViewEditor.MoveView(layout, B, 99).Layout.Views.Select(v => v.Id)); // borné
        Assert.Equal([B, A, C], ViewEditor.MoveView(layout, B, -5).Layout.Views.Select(v => v.Id));
        Assert.Equal([A, B, C], layout.Views.Select(v => v.Id)); // l'entrée n'est pas modifiée
    }

    [Fact]
    public void RenameView_EmptyName_IsRefused()
    {
        var layout = ThreeViews();

        var result = ViewEditor.RenameView(layout, A, "  ");

        Assert.Equal(ViewError.EmptyName, result.Error);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void RenameView_LongName_IsCutAt30()
    {
        var result = ViewEditor.RenameView(ThreeViews(), A, new string('é', 50));

        Assert.True(result.Ok);
        Assert.Equal(new string('é', 30), result.Layout.Views[0].Name);
        Assert.Equal("Deux", result.Layout.Views[1].Name);
    }
}
