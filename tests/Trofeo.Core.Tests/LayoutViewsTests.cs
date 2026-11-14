using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

/// <summary>Le format des vues et des noms de cases : lecture indulgente, migration d'une ancienne disposition, import.</summary>
public sealed class LayoutViewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-tests-" + Guid.NewGuid().ToString("N"));

    public LayoutViewsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "layout.json");

    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid C = new("aaaaaaaa-0000-0000-0000-000000000003");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Case(Guid id, int column, string typeId = "clock", string? nameJson = null) =>
        $$"""{"InstanceId":"{{id}}","TypeId":"{{typeId}}","Column":{{column}},"Row":0,"Columns":4,"Rows":2,"TextSize":"Normal","Settings":{}{{(nameJson is null ? "" : ",\"Name\":" + nameJson)}}}""";

    private static string View(Guid id, string name, params string[] cases) =>
        $$"""{"Id":"{{id}}","Name":"{{name}}","Placements":[{{string.Join(",", cases)}}]}""";

    private static string File180(params string[] views) => $$"""{"Rotation":180,"Views":[{{string.Join(",", views)}}]}""";

    private LayoutLoadResult Read(string json, List<string>? warnings = null)
    {
        File.WriteAllText(FilePath, json);
        return new LayoutStore(FilePath, warnings is null ? null : warnings.Add).TryLoad();
    }

    private static ModulePlacement Place(Guid id, int column, string? name = null) =>
        new(id, "clock", column, 0, 4, 2, TextSize.Normal, Json("{}"), name);

    [Fact]
    public void Load_LegacyPlacements_BecomesOneViewNamedVue1()
    {
        var result = Read($$"""{"Rotation":180,"Placements":[{{Case(A, 0)}},{{Case(B, 4, "network")}}]}""");

        Assert.Null(result.Error);
        var layout = result.Layout!;
        var view = Assert.Single(layout.Views);
        Assert.Equal("Vue 1", view.Name);
        Assert.NotEqual(Guid.Empty, view.Id);
        Assert.Equal([A, B], view.Placements.Select(p => p.InstanceId));
        Assert.Equal(["clock", "network"], view.Placements.Select(p => p.TypeId));
    }

    [Fact]
    public void Load_LegacyPlacements_AreReportedAsWithoutViews()
    {
        LayoutJson.Parse($$"""{"Rotation":180,"Placements":[{{Case(A, 0)}}]}""", null, out var legacyHadViews);
        LayoutJson.Parse(File180(View(A, "Vue 1")), null, out var hadViews);
        LayoutJson.Parse("""{"Rotation":180,"Views":null}""", null, out var nullViews);

        Assert.False(legacyHadViews);
        Assert.True(hadViews);
        Assert.True(nullViews); // la propriété est là, même vide
    }

    [Fact]
    public void RoundTrip_ThreeViews_KeepsOrderNamesAndCaseNames()
    {
        var shelf = Place(C, 0, "Rangée");
        var layout = new DashboardLayout(180,
        [
            new LayoutView(A, "Travail", [Place(Guid.NewGuid(), 0, "Horloge du bureau"), Place(Guid.NewGuid(), 4)]),
            new LayoutView(B, "Jeu", []),
            new LayoutView(C, "Écoute", [Place(Guid.NewGuid(), 8, "Musique")]),
        ], Shelf: [shelf]);
        var store = new LayoutStore(FilePath);

        store.Save(layout);
        var back = store.Load();

        Assert.Equal(["Travail", "Jeu", "Écoute"], back.Views.Select(v => v.Name));
        Assert.Equal([A, B, C], back.Views.Select(v => v.Id));
        Assert.Equal(["Horloge du bureau", null], back.Views[0].Placements.Select(p => p.Name));
        Assert.Equal(layout.Views[0].Placements.Select(p => p.InstanceId), back.Views[0].Placements.Select(p => p.InstanceId));
        Assert.Empty(back.Views[1].Placements);
        Assert.Equal("Musique", Assert.Single(back.Views[2].Placements).Name);
        Assert.Equal("Rangée", Assert.Single(back.Shelf!).Name);

        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(doc.RootElement.TryGetProperty("Placements", out _)); // plus de propriété à la racine
        Assert.False(doc.RootElement.TryGetProperty("AllPlacements", out _));
        var cases = doc.RootElement.GetProperty("Views")[0].GetProperty("Placements");
        Assert.True(cases[0].TryGetProperty("Name", out _));
        Assert.False(cases[1].TryGetProperty("Name", out _)); // pas de nom : pas de propriété
    }

    [Fact]
    public void Load_NoViewAtAll_GetsOneEmptyView()
    {
        var result = Read("""{"Rotation":180}""");

        Assert.Null(result.Error);
        var view = Assert.Single(result.Layout!.Views);
        Assert.Equal("Vue 1", view.Name);
        Assert.Empty(view.Placements);
    }

    [Fact]
    public void Load_EmptyViewsArray_GetsOneEmptyView()
    {
        var result = Read("""{"Rotation":180,"Views":[]}""");

        Assert.Null(result.Error);
        Assert.Empty(Assert.Single(result.Layout!.Views).Placements);
    }

    [Fact]
    public void Load_TenViews_KeepsNineAndWarns()
    {
        var warnings = new List<string>();
        var views = Enumerable.Range(1, 10).Select(i => View(Guid.NewGuid(), $"Vue {i}")).ToArray();

        var result = Read(File180(views), warnings);

        Assert.Null(result.Error);
        Assert.Equal(9, result.Layout!.Views.Count);
        Assert.Equal("Vue 9", result.Layout.Views[^1].Name);
        Assert.Contains(warnings, w => w.Contains("10 vues") && w.Contains('9'));
        Assert.Equal(warnings, result.Warnings);
    }

    [Fact]
    public void Load_NameLongerThan30_IsCut()
    {
        var warnings = new List<string>();
        var longName = new string('x', 45);

        var result = Read(File180(View(A, longName, Case(B, 0, nameJson: $"\"{longName}\""))), warnings);

        Assert.Null(result.Error);
        var view = Assert.Single(result.Layout!.Views);
        Assert.Equal(new string('x', 30), view.Name);
        Assert.Equal(new string('x', 30), Assert.Single(view.Placements).Name);
        Assert.Equal(2, warnings.Count(w => w.Contains("coupé")));
    }

    [Fact]
    public void Load_BlankOrMissingNames_AreCleaned()
    {
        var warnings = new List<string>();
        var json = $$"""{"Rotation":180,"Views":[{"Id":"{{A}}","Placements":[{{Case(B, 0, nameJson: "\"   \"")}}]},{"Id":"{{C}}","Name":"  ","Placements":[]}]}""";

        var result = Read(json, warnings);

        Assert.Null(result.Error);
        Assert.Equal(["Vue 1", "Vue 2"], result.Layout!.Views.Select(v => v.Name));
        Assert.Null(result.Layout.Views[0].Placements[0].Name);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_CaseNameNotText_IsDroppedWithWarning()
    {
        var warnings = new List<string>();

        var result = Read(File180(View(A, "Vue 1", Case(B, 0, nameJson: "12"))), warnings);

        Assert.Null(result.Error);
        Assert.Null(Assert.Single(result.Layout!.Views[0].Placements).Name);
        Assert.Contains(warnings, w => w.Contains("Nom de case"));
    }

    [Fact]
    public void Load_SameInstanceInTwoViews_KeepsTheFirstAndWarns()
    {
        var warnings = new List<string>();

        var result = Read(File180(View(A, "Un", Case(C, 0)), View(B, "Deux", Case(C, 0), Case(Guid.NewGuid(), 4))), warnings);

        Assert.Null(result.Error);
        var layout = result.Layout!;
        Assert.Single(layout.Views[0].Placements);
        Assert.Equal(C, layout.Views[0].Placements[0].InstanceId);
        var second = Assert.Single(layout.Views[1].Placements);
        Assert.NotEqual(C, second.InstanceId);
        Assert.Single(warnings);
    }

    [Fact]
    public void Load_SameInstanceTwiceInOneView_IsRefused()
    {
        var result = Read(File180(View(A, "Un", Case(C, 0), Case(C, 8))));

        Assert.Null(result.Layout);
        Assert.Equal("Identifiant de module en double.", result.Error);
    }

    [Fact]
    public void Load_DuplicateViewIds_RenumbersAndWarns()
    {
        var warnings = new List<string>();

        var result = Read(File180(View(A, "Un", Case(B, 0)), View(A, "Deux")), warnings);

        Assert.Null(result.Error);
        var views = result.Layout!.Views;
        Assert.Equal(A, views[0].Id);
        Assert.NotEqual(A, views[1].Id);
        Assert.NotEqual(Guid.Empty, views[1].Id);
        Assert.Equal(["Un", "Deux"], views.Select(v => v.Name));
        Assert.Single(warnings);
    }

    [Fact]
    public void Load_ViewWithoutId_GetsOneAndWarns()
    {
        var warnings = new List<string>();

        var result = Read("""{"Rotation":180,"Views":[{"Name":"Sans id","Placements":[]}]}""", warnings);

        Assert.Null(result.Error);
        Assert.NotEqual(Guid.Empty, Assert.Single(result.Layout!.Views).Id);
        Assert.Single(warnings);
    }

    [Fact]
    public void Load_ViewsNotAnArray_WarnsAndKeepsTheFile()
    {
        var warnings = new List<string>();

        var result = Read($$"""{"Rotation":180,"Views":42,"Placements":[{{Case(A, 0)}}]}""", warnings);

        Assert.Null(result.Error);
        Assert.Equal(LayoutJson.ViewsDropped, Assert.Single(warnings));
        Assert.Equal(A, Assert.Single(Assert.Single(result.Layout!.Views).Placements).InstanceId);
    }

    [Fact]
    public void Load_ViewEntryNotAnObject_IsSkippedWithWarning()
    {
        var warnings = new List<string>();

        var result = Read($$"""{"Rotation":180,"Views":[7,{{View(A, "Bonne")}}]}""", warnings);

        Assert.Null(result.Error);
        Assert.Equal("Bonne", Assert.Single(result.Layout!.Views).Name);
        Assert.Single(warnings);
    }

    [Fact]
    public void Load_ViewsWinOverLegacyPlacements()
    {
        var result = Read($$"""{"Rotation":180,"Views":[{{View(A, "Vraie", Case(B, 0))}}],"Placements":[{{Case(C, 4)}}]}""");

        Assert.Null(result.Error);
        Assert.Equal(B, Assert.Single(Assert.Single(result.Layout!.Views).Placements).InstanceId);
    }

    [Fact]
    public void Load_SameSpotInTwoViews_IsAllowed_ButNotInOneView()
    {
        var apart = Read(File180(View(A, "Un", Case(Guid.NewGuid(), 0)), View(B, "Deux", Case(Guid.NewGuid(), 0))));
        var together = Read(File180(View(A, "Un", Case(Guid.NewGuid(), 0), Case(Guid.NewGuid(), 2))));

        Assert.Null(apart.Error);
        Assert.Null(together.Layout);
        Assert.Contains("se chevauchent", together.Error);
    }

    [Fact]
    public void Load_ViewWithAnInvalidCase_IsRefusedLikeBefore()
    {
        var result = Read(File180(View(A, "Un", Case(B, 99))));

        Assert.Null(result.Layout);
        Assert.Contains("hors du damier", result.Error);
    }

    [Fact]
    public void Load_DuplicateKeyFile_StillReadsViewsAndLegacyPlacements()
    {
        // Chemin d'avant (propriété en double) : même résultat que la lecture indulgente.
        var legacy = $$"""{"Rotation":0,"Rotation":180,"Placements":[{{Case(A, 0)}}]}""";
        var modern = $$"""{"Rotation":0,"Rotation":180,"Views":[{{View(B, "Seule", Case(C, 0))}}]}""";

        var oldFile = Read(legacy);
        var newFile = Read(modern);

        Assert.Equal(180, oldFile.Layout!.Rotation);
        Assert.Equal("Vue 1", Assert.Single(oldFile.Layout.Views).Name);
        Assert.Equal(A, oldFile.Layout.Views[0].Placements[0].InstanceId);
        Assert.Equal(B, Assert.Single(newFile.Layout!.Views).Id);
    }

    [Fact]
    public void Default_HasOneViewNamedVue1_WithAFixedId()
    {
        var layout = DashboardLayout.Default;

        var view = Assert.Single(layout.Views);
        Assert.Equal("Vue 1", view.Name);
        Assert.Equal(DashboardLayout.DefaultViewId, view.Id);
        Assert.Equal(5, view.Placements.Count);
        Assert.Equal(layout.Views[0].Placements, layout.AllPlacements);
    }

    [Fact]
    public void SingleView_HasAFreshIdEachTime()
    {
        var one = DashboardLayout.SingleView(180, []);
        var two = DashboardLayout.SingleView(180, []);

        Assert.Equal("Vue 1", one.Views[0].Name);
        Assert.NotEqual(one.Views[0].Id, two.Views[0].Id);
    }

    [Fact]
    public void View_UnknownId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => DashboardLayout.Default.View(Guid.NewGuid()));
        Assert.Same(DashboardLayout.Default.Views[0], DashboardLayout.Default.View(DashboardLayout.DefaultViewId));
    }

    [Fact]
    public void AllPlacements_ListsEveryViewInOrder_WithoutTheShelf()
    {
        var shelved = Place(Guid.NewGuid(), 0);
        var layout = new DashboardLayout(180,
        [
            new LayoutView(A, "Un", [Place(B, 0)]),
            new LayoutView(C, "Deux", [Place(Guid.NewGuid(), 0), Place(Guid.NewGuid(), 4)]),
        ], Shelf: [shelved]);

        Assert.Equal(3, layout.AllPlacements.Count());
        Assert.Equal(B, layout.AllPlacements.First().InstanceId);
        Assert.DoesNotContain(layout.AllPlacements, p => p == shelved);
    }

    [Fact]
    public void Import_LegacySharedLayout_IsReadAsOneView()
    {
        // Une disposition partagée par la 1.1.0 : format de partage inchangé, cases à la racine de « Layout ».
        var shared = $$$"""{"Format":"{{{LayoutTransfer.Format}}}","Layout":{"Rotation":180,"Placements":[{{{Case(A, 0)}}},{{{Case(B, 4)}}}]}}""";

        var result = LayoutTransfer.Import(shared);

        Assert.Null(result.Error);
        var view = Assert.Single(result.Layout!.Views);
        Assert.Equal("Vue 1", view.Name);
        Assert.Equal([A, B], view.Placements.Select(p => p.InstanceId));
    }

    [Fact]
    public void Import_ViewWithUnknownCommunityType_KeepsTheCase()
    {
        var shared = $$$"""{"Format":"{{{LayoutTransfer.Format}}}","Layout":{"Rotation":180,"Views":[{{{View(A, "Jeu", Case(B, 0, "alex.minecraft", "\"Mon serveur\""))}}},{{{View(C, "Travail", Case(Guid.NewGuid(), 0))}}}]}}""";

        var result = LayoutTransfer.Import(shared);

        Assert.Null(result.Error);
        var minecraft = Assert.Single(result.Layout!.Views[0].Placements);
        Assert.Equal("alex.minecraft", minecraft.TypeId); // le type absent de ce PC n'est pas jugé ici : il garde sa case
        Assert.Equal("Mon serveur", minecraft.Name);
        Assert.Equal(["Jeu", "Travail"], result.Layout.Views.Select(v => v.Name));
    }

    [Fact]
    public void Export_ThenImport_KeepsViewsAndNames()
    {
        var layout = new DashboardLayout(180,
        [
            new LayoutView(A, "Un", [Place(Guid.NewGuid(), 0, "Nommée")]),
            new LayoutView(B, "Deux", []),
        ]);

        var back = LayoutTransfer.Import(LayoutTransfer.Export(layout)).Layout!;

        Assert.Equal(["Un", "Deux"], back.Views.Select(v => v.Name));
        Assert.Equal("Nommée", back.Views[0].Placements[0].Name);
    }
}
