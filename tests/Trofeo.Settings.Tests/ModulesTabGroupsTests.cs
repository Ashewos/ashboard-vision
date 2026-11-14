using Trofeo.Core.Layout;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.ModulesFixture;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Modules avec plusieurs vues : un dossier par vue, et les noms donnés aux cases.</summary>
public sealed class ModulesTabGroupsTests
{
    private static readonly Guid GameViewId = new("9a7e7c10-5b2d-4f3e-8c1a-6d4b2e0f9a11");

    /// <summary>La disposition par défaut en deux vues : « Bureau » sans Réseau, et « Jeu » avec Réseau nommé « Débit ».</summary>
    private static DashboardLayout TwoViews(DashboardLayout layout)
    {
        var first = layout.Views[0];
        var network = first.Placements.Single(p => p.InstanceId == NetworkId) with { Name = "Débit" };
        return layout with
        {
            Views =
            [
                first with { Name = "Bureau", Placements = [.. first.Placements.Where(p => p.InstanceId != NetworkId)] },
                new LayoutView(GameViewId, "Jeu", [network]),
            ],
        };
    }

    [Fact]
    public void OneView_ShowsOneListWithoutHeader()
    {
        using var f = new ModulesFixture();

        var group = Assert.Single(f.Tab.Groups);
        Assert.False(group.ShowsHeader);
        Assert.True(group.ShowsItems);
        Assert.Equal(f.Tab.Placed, group.Items);
    }

    [Fact]
    public void SeveralViews_OneFolderPerView_WithNameCountAndGreenDot()
    {
        using var f = new ModulesFixture(TwoViews);

        Assert.Equal(["Bureau", "Jeu"], f.Tab.Groups.Select(g => g.Name));
        Assert.All(f.Tab.Groups, g => Assert.True(g.ShowsHeader));
        Assert.Equal(["4 modules", "1 module"], f.Tab.Groups.Select(g => g.CountText));
        Assert.Equal([NetworkId], f.Tab.Groups[1].Items.Select(i => i.Id));
        Assert.Equal(5, f.Tab.Placed.Count); // toutes vues confondues
        Assert.All(f.Tab.Groups[0].Items, item => Assert.Contains(item, f.Tab.Placed)); // les mêmes lignes : le point d'état suit

        f.Link.PushViewShown(GameViewId);
        Assert.Equal([false, true], f.Tab.Groups.Select(g => g.IsShown));
    }

    [Fact]
    public async Task Folder_Collapses_AndStaysClosedAfterAnEdit()
    {
        using var f = new ModulesFixture(TwoViews);
        var office = f.Tab.Groups[0];

        office.ToggleCommand.Execute(null);
        Assert.False(office.IsExpanded);
        Assert.False(office.ShowsItems);

        f.Tab.Select(NetworkId);
        await Rename(f.Tab, "Mon débit"); // une modification refait les listes

        Assert.Same(office, f.Tab.Groups[0]);
        Assert.False(f.Tab.Groups[0].IsExpanded);
        Assert.True(f.Tab.Groups[1].IsExpanded);
    }

    [Fact]
    public void ChoosingInAFolder_SelectsTheModule()
    {
        using var f = new ModulesFixture(TwoViews);
        var game = f.Tab.Groups[1];

        game.SelectedItem = game.Items[0];

        Assert.Equal(NetworkId, f.Tab.SelectedId);
        Assert.Same(game.Items[0], game.SelectedItem);
        Assert.Null(f.Tab.Groups[0].SelectedItem); // une seule ligne choisie dans tout l'onglet
    }

    [Fact]
    public void CaseName_ShowsInTheListAndTheTitle_WithTheModuleNameUnder()
    {
        using var f = new ModulesFixture(TwoViews);

        var item = f.Tab.Placed.Single(p => p.Id == NetworkId);
        Assert.Equal("Débit", item.Name);
        Assert.Equal("Réseau", item.TypeName);

        f.Tab.Select(NetworkId);
        Assert.Equal("Débit", f.Tab.SelectedName);
        Assert.Equal("Débit", f.Tab.CaseTitle.Title);
        Assert.Equal("Réseau", f.Tab.CaseTitle.OfficialName);
        Assert.True(f.Tab.CaseTitle.HasCustomName);

        f.Tab.Select(MonitoringId);
        Assert.False(f.Tab.CaseTitle.HasCustomName);
    }

    [Fact]
    public async Task Rename_FromTheModulesTab_WritesTheLayout_AndTheListsFollow()
    {
        using var f = new ModulesFixture(TwoViews);
        f.Tab.Select(MonitoringId);
        Assert.True(f.Tab.CaseTitle.ShowsPencil);

        await Rename(f.Tab, "PC du salon");

        Assert.Equal("PC du salon", f.Document.Current.Views[0].Placements.Single(p => p.InstanceId == MonitoringId).Name);
        Assert.Equal("PC du salon", f.Tab.Placed.Single(p => p.Id == MonitoringId).Name);
        Assert.Contains(f.Tab.Groups[0].Items, i => i.Name == "PC du salon");
        Assert.Equal("PC du salon", f.Tab.CaseTitle.Title);
        Assert.Equal(MonitoringId, f.Tab.SelectedId);

        await f.Session.UndoAsync(); // un seul pas d'annulation, partagé avec les autres onglets
        Assert.Null(f.Document.Current.Views[0].Placements.Single(p => p.InstanceId == MonitoringId).Name);
    }

    private static async Task Rename(ModulesTabViewModel tab, string name)
    {
        tab.CaseTitle.StartRename();
        tab.CaseTitle.EditName = name;
        await tab.CaseTitle.CommitAsync();
    }
}
