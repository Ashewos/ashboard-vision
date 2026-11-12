using System.Text.Json;
using Avalonia.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Raccourcis et les raccourcis des modules complémentaires : leurs noms, leurs actions, le point jaune, les conflits.</summary>
public sealed class ShortcutsTabCommunityTests : IDisposable
{
    private const string GithubId = "ashewos.github-repo";
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid GithubCaseId = new("9a1e0000-0000-4000-8000-000000000001");
    private static readonly Guid GithubOtherCaseId = new("9a1e0000-0000-4000-8000-000000000002");

    private static readonly ModuleShortcutInfo Refresh = new("refresh", "Rafraîchir", "Ctrl+Alt+Shift+R");
    private static readonly ModuleShortcutInfo Open = new("open", "Ouvrir la page", null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-shortcuts-community-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public ShortcutsTabCommunityTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModulePlacement Placement(Guid id, string typeId, int column, string? name = null) =>
        new(id, typeId, column, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone(), name);

    private static ShortcutBinding Bound(ModuleShortcutInfo shortcut, string moduleId = GithubId, Guid? target = null) =>
        new(shortcut.Keys ?? "Ctrl+Alt+Shift+O", ShortcutCommand.Community, 0, target, moduleId, shortcut.Id);

    private static CommunityModuleInfo Module(
        string id, string name, LinkCommunityState state = LinkCommunityState.Ready, params ModuleShortcutInfo[] shortcuts) =>
        new(id, name, "Alex", "Un module.", "1.0.0", state, null, [], [], false, null, null, null, shortcuts);

    /// <summary>
    /// Deux cases du module « Dépôt GitHub » (l'une nommée) et Claude Code sur l'écran ; le service connaît le module, la fenêtre
    /// l'ouvre. Les raccourcis du fichier : les raccourcis par défaut puis <paramref name="extra"/>.
    /// </summary>
    private async Task<ShortcutsTabViewModel> OpenAsync(CommunityModuleInfo[] modules, params ShortcutBinding[] extra)
    {
        _link.Community = new CommunityOverview(modules, @"C:\Modules", false, [], false);
        _store.Save(DashboardLayout.SingleView(180,
            [Placement(ClaudeId, "claudecode", 0), Placement(GithubCaseId, GithubId, 4, "Mon dépôt"), Placement(GithubOtherCaseId, GithubId, 8)],
            [.. ShortcutDefaults.All, .. extra]));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        var tab = new ShortcutsTabViewModel(_link, _session);
        await tab.ActivateAsync();
        return tab;
    }

    private IReadOnlyList<ShortcutBinding> Saved => _document.Current.Shortcuts ?? ShortcutDefaults.All;

    private const KeyModifiers CtrlAltShift = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;

    [Fact]
    public async Task ShortcutsTab_CommunityBinding_HasTheYellowMark()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh, Open])], Bound(Refresh));

        var community = Assert.Single(tab.Rows, r => r.IsCommunity);
        Assert.Equal("Rafraîchir", community.ActionText);   // le nom que le manifeste donne au raccourci
        Assert.Equal("Dépôt GitHub", community.ModuleText); // et le module, dessous
        Assert.True(community.HasModuleText);
        Assert.True(community.HasTarget);
        Assert.Equal("Ctrl + Alt + Maj + R", community.KeysText);
        Assert.All(tab.Rows.Where(r => !r.IsCommunity), r => Assert.False(r.HasModuleText));
        Assert.Equal(14, tab.Rows.Count);
    }

    [Fact]
    public async Task ShortcutsTab_Choices_GroupCommunityShortcutsByModule()
    {
        var tab = await OpenAsync(
        [
            Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh, Open]),
            Module("ashewos.demo", "Démo", shortcuts: [new ModuleShortcutInfo("color", "Changer de couleur", "Ctrl+Alt+Shift+C")]),
            Module("ashewos.mis-de-cote", "Mis de côté", LinkCommunityState.SetAside, new ModuleShortcutInfo("go", "Aller", null)),
            Module("ashewos.a-valider", "À valider", LinkCommunityState.NeedsApproval, new ModuleShortcutInfo("no", "Non", null)),
            Module("ashewos.sans-raccourci", "Sans raccourci"),
        ]);

        var builtIn = tab.ActionChoices.Take(22).ToList();
        Assert.All(builtIn, c => Assert.NotEqual(ShortcutCommand.Community, c.Command));
        var community = tab.ActionChoices.Skip(22).ToList();
        Assert.Equal(
            [("Dépôt GitHub", "Rafraîchir"), ("Dépôt GitHub", "Ouvrir la page"), ("Démo", "Changer de couleur"), ("Mis de côté", "Aller")],
            community.Select(c => (c.Group!, c.Text)));  // un module après l'autre, dans l'ordre du service ; celui qui attend sa validation n'y est pas
        Assert.All(community, c => Assert.Equal(ShortcutCommand.Community, c.Command));
        Assert.Equal([GithubId, GithubId, "ashewos.demo", "ashewos.mis-de-cote"], community.Select(c => c.ModuleId));
        Assert.Equal(["refresh", "open", "color", "go"], community.Select(c => c.ShortcutId));
        Assert.All(community, c => Assert.True(c.HasGroup));
    }

    [Fact]
    public void ShortcutsTab_NotOpenedYet_AsksTheServiceForNothing()
    {
        _link.Community = new CommunityOverview([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], @"C:\Modules", false, [], false);
        _store.Save(DashboardLayout.SingleView(180, [Placement(ClaudeId, "claudecode", 0)]));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);

        var tab = new ShortcutsTabViewModel(_link, _session);

        Assert.DoesNotContain("community", _link.ModuleCalls); // la fenêtre qui s'ouvre ne coûte rien de plus au service
        Assert.Equal(22, tab.ActionChoices.Count);
    }

    [Fact]
    public async Task ShortcutsTab_ModulesChange_ChoicesAndNamesFollow()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], Bound(Refresh));
        Assert.Equal("Rafraîchir", tab.Rows[^1].ActionText);

        _link.Community = new CommunityOverview([Module(GithubId, "Dépôt GitHub", shortcuts: [new ModuleShortcutInfo("refresh", "Actualiser", "Ctrl+Alt+Shift+R")])], @"C:\Modules", false, [], false);
        _link.SetTypes([.. SampleData.Types()]); // les types ont changé : un module a été touché

        Assert.Equal("Actualiser", tab.Rows[^1].ActionText);
        Assert.Equal("Actualiser", tab.ActionChoices[^1].Text);
    }

    [Fact]
    public async Task ShortcutsTab_HostileNames_AreCleaned()
    {
        var tab = await OpenAsync(
            [Module(GithubId, "Dépôt\u202E GitHub\u0007", shortcuts: [new ModuleShortcutInfo("refresh", "Rafra\u202Eîchir\nvite", "Ctrl+Alt+Shift+R")])],
            Bound(Refresh));

        var row = tab.Rows[^1];
        Assert.Equal("Rafraîchir vite", row.ActionText);
        Assert.Equal("Dépôt GitHub", row.ModuleText);
        Assert.Equal("Rafraîchir vite", tab.ActionChoices[^1].Text);
        Assert.Equal("Dépôt GitHub", tab.ActionChoices[^1].Group);
    }

    [Fact]
    public async Task ShortcutsTab_UnknownModule_FallsBackToTheShortcutId()
    {
        var tab = await OpenAsync([], Bound(Refresh)); // le module n'est plus installé : la liaison reste lisible

        var row = tab.Rows[^1];
        Assert.True(row.IsCommunity);
        Assert.Equal("refresh", row.ActionText);
        Assert.Null(row.ModuleText);
    }

    [Fact]
    public async Task ShortcutsTab_RefusedKey_ShowsTheMessage()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], Bound(Refresh));
        Assert.All(tab.Rows, r => Assert.False(r.HasFailure));

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { RefusedShortcutKeys = ["alt+ctrl+shift+r"] });

        var refused = Assert.Single(tab.Rows, r => r.HasFailure);
        Assert.Equal("Rafraîchir", refused.ActionText);
        Assert.Equal("Touche déjà prise par un autre logiciel.", refused.Failure);
    }

    [Fact]
    public async Task ShortcutsTab_SetAsideModuleBinding_StillReservesItsKey()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", LinkCommunityState.SetAside, Refresh)], Bound(Refresh));
        tab.BeginCapture(tab.Rows[0]); // « Onglet précédent » veut les touches de « Rafraîchir »

        await tab.CaptureAsync(Key.R, CtrlAltShift);

        Assert.Equal("Déjà pris par « Rafraîchir ».", tab.Rows[0].Refusal);
        Assert.Equal("Ctrl+Alt+Up", Saved[0].Keys);
        Assert.Contains(Bound(Refresh), Saved); // la liaison du module mis de côté est toujours là
    }

    [Fact]
    public async Task ShortcutsTab_AddingACommunityAction_SavesTheBindingWithoutTarget()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh, Open])]);
        tab.AddShortcutCommand.Execute(null);
        var draft = tab.Rows[^1];
        draft.SelectedAction = tab.ActionChoices.First(c => c.ShortcutId == "open");

        await tab.CaptureAsync(Key.O, CtrlAltShift);

        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Shift+O", ShortcutCommand.Community, 0, null, GithubId, "open"), Saved[^1]);
        Assert.Equal("Ouvrir la page", tab.Rows[^1].ActionText);
    }

    [Fact]
    public async Task ShortcutsTab_CommunityBinding_ListsTheCasesOfItsModuleOnly()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], Bound(Refresh));

        var targets = tab.Rows[^1].Targets;

        Assert.Equal(["Toutes les cases du module", "Mon dépôt", "Dépôt GitHub"], targets.Select(t => t.Name)); // la case nommée, puis l'autre
        Assert.Equal([null, GithubCaseId, GithubOtherCaseId], targets.Select(t => t.Id));
        Assert.Null(tab.Rows[^1].SelectedTarget!.Id);
        Assert.DoesNotContain(tab.Rows[0].Targets, t => t.Id == GithubCaseId); // les autres raccourcis ne proposent que les modules à onglets
    }

    [Fact]
    public async Task ShortcutsTab_CommunityBinding_TargetingACase_IsKeptWhenSelected()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], Bound(Refresh));
        var row = tab.Rows[^1];

        await tab.SetTargetAsync(row, row.Targets.First(t => t.Id == GithubCaseId));

        Assert.Equal(GithubCaseId, Saved[^1].Target);
        Assert.Equal(GithubCaseId, tab.Rows[^1].SelectedTarget!.Id);
    }

    [Fact]
    public async Task ShortcutsTab_ViewAndCommunityRows_HaveTheRightBoxes()
    {
        var tab = await OpenAsync([Module(GithubId, "Dépôt GitHub", shortcuts: [Refresh])], Bound(Refresh));

        // Une action de vue ne vise aucun module (pas de liste) ; une action de module en a une.
        Assert.False(tab.Rows.First(r => r.Binding.Command == ShortcutCommand.NextView).HasTarget);
        Assert.True(tab.Rows.First(r => r.Binding.Command == ShortcutCommand.NextTab).HasTarget);
        Assert.True(tab.Rows[^1].HasTarget);
    }
}
