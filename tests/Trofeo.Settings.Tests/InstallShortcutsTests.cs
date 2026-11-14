using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.CommunityFixture;

namespace Trofeo.Settings.Tests;

/// <summary>
/// Les raccourcis que propose un module complémentaire : la section « Raccourcis proposés » de la boîte (installation, mise à jour), ce
/// qui est ajouté à la disposition quand on confirme, et ce qui part avec une mise à jour ou une désinstallation.
/// </summary>
public sealed class InstallShortcutsTests
{
    private const string PackagePath = @"C:\Téléchargements\alex.demo-1.0.0.avmod";

    private static readonly ModuleShortcutInfo Refresh = new("refresh", "Rafraîchir", "Ctrl+Alt+Shift+R");
    private static readonly ModuleShortcutInfo Open = new("open", "Ouvrir la page", "Ctrl+Alt+Shift+O");

    private static ShortcutBinding Bound(ModuleShortcutInfo shortcut, string moduleId = "alex.demo") =>
        new(shortcut.Keys!, ShortcutCommand.Community, 0, null, moduleId, shortcut.Id);

    private IReadOnlyList<ShortcutBinding> Effective(CommunityFixture fixture) => fixture.Document.Current.Shortcuts ?? ShortcutDefaults.All;

    private static async Task<InstallModuleDialogViewModel> OpenInstall(CommunityFixture fixture, params ModuleShortcutInfo[] shortcuts)
    {
        fixture.Link.Package = Package(shortcuts: shortcuts);
        await fixture.Section.OpenPackageAsync(PackagePath);
        return fixture.Section.Dialog!;
    }

    // ---- Installation ----

    [Fact]
    public async Task Install_FreeKey_ArrivesChecked()
    {
        using var fixture = new CommunityFixture();

        var dialog = await OpenInstall(fixture, Refresh);

        var proposed = Assert.Single(dialog.ProposedShortcuts);
        Assert.Equal("Rafraîchir", proposed.Name);
        Assert.Equal("Ctrl+Alt+Shift+R", proposed.Keys);
        Assert.Equal("Ctrl + Alt + Maj + R", proposed.KeysText);
        Assert.True(proposed.IsChecked);
        Assert.Null(proposed.TakenBy);
        Assert.True(dialog.ShowsProposedShortcuts);
        Assert.Equal("Raccourcis proposés", dialog.ProposedShortcutsTitle);
        Assert.Equal(["Ctrl+Alt+Shift+R"], fixture.Link.TestedKeys); // Windows a été interrogé sur cette touche
    }

    [Fact]
    public async Task Install_Confirm_AddsTheCheckedBindings_WithoutTarget_AndSavesThem()
    {
        using var fixture = new CommunityFixture();
        var dialog = await OpenInstall(fixture, Refresh, Open);
        dialog.ProposedShortcuts[1].IsChecked = false; // « Ouvrir la page » n'est pas voulu

        await dialog.ConfirmAsync();

        var expected = new ShortcutBinding("Ctrl+Alt+Shift+R", ShortcutCommand.Community, 0, null, "alex.demo", "refresh");
        Assert.Equal([.. ShortcutDefaults.All, expected], Effective(fixture));          // les raccourcis du logiciel sont gardés
        Assert.Equal([.. ShortcutDefaults.All, expected], fixture.Store.Load().Shortcuts); // et le fichier le dit
        Assert.Null(fixture.Section.Dialog);
        Assert.Null(fixture.Section.Message);
    }

    [Fact]
    public async Task Install_KeyTakenByTheSoftware_ArrivesUncheckedWithTheHolder()
    {
        using var fixture = new CommunityFixture();

        var dialog = await OpenInstall(fixture, new ModuleShortcutInfo("next", "Suivant", "ctrl+alt+down"));

        var proposed = Assert.Single(dialog.ProposedShortcuts);
        Assert.False(proposed.IsChecked);
        Assert.Equal("Touche déjà prise par « Onglet suivant ».", proposed.TakenBy);
        Assert.False(proposed.CanCheck); // le logiciel donne déjà ces touches à une autre action
        Assert.Empty(fixture.Link.TestedKeys); // inutile de demander à Windows : le logiciel la tient

        proposed.IsChecked = true; // même cochée de force, elle n'est pas ajoutée
        await dialog.ConfirmAsync();
        Assert.Equal(ShortcutDefaults.All, Effective(fixture));
    }

    [Fact]
    public async Task Install_KeyTakenByAnotherModule_NamesTheOtherModulesShortcut()
    {
        var layout = WithDemo() with { Shortcuts = [.. ShortcutDefaults.All, Bound(new ModuleShortcutInfo("sync", "Synchroniser", "Ctrl+Alt+Shift+R"), "alex.autre")] };
        using var fixture = new CommunityFixture(layout).WithModules(Module("alex.autre", name: "Autre", shortcuts: [new("sync", "Synchroniser", "Ctrl+Alt+Shift+R")]));

        var dialog = await OpenInstall(fixture, Refresh);

        Assert.Equal("Touche déjà prise par « Synchroniser ».", dialog.ProposedShortcuts.Single().TakenBy);
        Assert.False(dialog.ProposedShortcuts.Single().IsChecked);
    }

    [Fact]
    public async Task Install_TwoShortcutsOfTheModuleWithTheSameKey_SecondArrivesUnchecked()
    {
        using var fixture = new CommunityFixture();

        var dialog = await OpenInstall(fixture, Refresh, new ModuleShortcutInfo("again", "Encore", "Ctrl+Alt+Shift+R"));

        Assert.Equal([true, false], dialog.ProposedShortcuts.Select(p => p.IsChecked));
        Assert.Equal("Touche déjà prise par « Rafraîchir ».", dialog.ProposedShortcuts[1].TakenBy);
    }

    [Fact]
    public async Task Install_KeyTakenByAnotherProgram_ArrivesUnchecked()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.KeyStates["Ctrl+Alt+Shift+R"] = ShortcutKeyState.TakenByAnotherProgram;

        var dialog = await OpenInstall(fixture, Refresh, Open);

        Assert.Equal([false, true], dialog.ProposedShortcuts.Select(p => p.IsChecked));
        Assert.Equal("Touche déjà prise par un autre logiciel.", dialog.ProposedShortcuts[0].TakenBy);
        Assert.Null(dialog.ProposedShortcuts[1].TakenBy);
        Assert.True(dialog.ProposedShortcuts[0].CanCheck); // l'utilisateur peut la cocher : Windows dira si la touche s'est libérée
    }

    [Fact]
    public async Task Install_ServiceNotAnswering_KeysAreTreatedAsFree_AndInstallIsNotBlocked()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.KeyTestFailure = new ControlException("Le service d'affichage ne répond pas.");

        var dialog = await OpenInstall(fixture, Refresh);

        Assert.True(dialog.ProposedShortcuts.Single().IsChecked);
        Assert.True(dialog.CanConfirm);
        await dialog.ConfirmAsync();
        Assert.Contains(Bound(Refresh), Effective(fixture));
    }

    [Fact]
    public async Task Install_ShortcutWithoutKeys_IsNotShown()
    {
        using var fixture = new CommunityFixture();

        var dialog = await OpenInstall(fixture, new ModuleShortcutInfo("open", "Ouvrir la page", null), new ModuleShortcutInfo("blank", "Vide", "  "));

        Assert.Empty(dialog.ProposedShortcuts);
        Assert.False(dialog.ShowsProposedShortcuts);
        Assert.Empty(fixture.Link.TestedKeys);
    }

    [Fact]
    public async Task Install_UnreadableKeysFromTheService_AreNotShown()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.KeyStates["Ctrl+Alt+Shift+R"] = ShortcutKeyState.Unreadable;

        var dialog = await OpenInstall(fixture, Refresh, new ModuleShortcutInfo("odd", "Étrange", "Ctrl+Alt+Ouvre-toi"));

        Assert.Empty(dialog.ProposedShortcuts);
    }

    [Fact]
    public async Task Install_HostileNameAndKeys_AreCleaned()
    {
        using var fixture = new CommunityFixture();

        var dialog = await OpenInstall(fixture, new ModuleShortcutInfo("odd", "Vrai\u202E\u0007\nnom", " alt + ctrl + shift + r\n"));

        var proposed = Assert.Single(dialog.ProposedShortcuts);
        Assert.Equal("Vrai nom", proposed.Name); // ni sens d'écriture, ni caractère de contrôle, ni retour à la ligne
        Assert.Equal("Ctrl+Alt+Shift+R", proposed.Keys);
    }

    [Fact]
    public async Task Install_NoShortcutChecked_AddsNothing()
    {
        using var fixture = new CommunityFixture();
        var dialog = await OpenInstall(fixture, Refresh);
        dialog.ProposedShortcuts[0].IsChecked = false;

        await dialog.ConfirmAsync();

        Assert.Null(fixture.Document.Current.Shortcuts); // la disposition n'a pas été touchée
    }

    [Fact]
    public async Task Install_RefusedByTheService_AddsNoBinding_AndKeepsTheDialogOpen()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Installation refusée.");
        var dialog = await OpenInstall(fixture, Refresh);

        await dialog.ConfirmAsync();

        Assert.Same(dialog, fixture.Section.Dialog);
        Assert.Null(fixture.Document.Current.Shortcuts);
    }

    // ---- Mise à jour ----

    private static CommunityFixture WithInstalled(ShortcutBinding[] bindings, params ModuleShortcutInfo[] declared)
    {
        var layout = WithDemo() with { Shortcuts = [.. ShortcutDefaults.All, .. bindings] };
        var fixture = new CommunityFixture(layout).WithModules(Module(
            declared: [ModulePermission.Internet], granted: [ModulePermission.Internet], updateVersion: "1.3.0", shortcuts: declared));
        fixture.Section.RefreshAsync().GetAwaiter().GetResult();
        return fixture;
    }

    private static ModuleUpdatePreparation Prepared(params ModuleShortcutInfo[] shortcuts) =>
        new(true, null, "1.3.0", "Notes.", [ModulePermission.Internet], [], shortcuts);

    [Fact]
    public async Task Update_OnlyNewShortcutsAreProposed()
    {
        using var fixture = WithInstalled([Bound(Refresh)], Refresh); // « Rafraîchir » est déjà là, avec sa liaison
        fixture.Link.Preparation = Prepared(Refresh, Open);

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;

        var proposed = Assert.Single(dialog.ProposedShortcuts);
        Assert.Equal("open", proposed.ShortcutId);
        Assert.True(proposed.IsChecked);

        await dialog.ConfirmAsync();
        Assert.Equal([.. ShortcutDefaults.All, Bound(Refresh), Bound(Open)], Effective(fixture)); // l'ancienne liaison est gardée
    }

    [Fact]
    public async Task Update_AShortcutTheUserRemovedBefore_IsNotProposedAgain()
    {
        using var fixture = WithInstalled([], Refresh); // la version installée la déclarait, mais l'utilisateur a retiré sa liaison
        fixture.Link.Preparation = Prepared(Refresh);

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Section.Dialog!.ProposedShortcuts);
    }

    [Fact]
    public async Task Update_ChangedKeys_KeepsTheUserBinding()
    {
        // L'utilisateur a changé les touches de « Rafraîchir » ; la nouvelle version en propose d'autres pour le même identifiant.
        var userBinding = new ShortcutBinding("Ctrl+Alt+Shift+Q", ShortcutCommand.Community, 0, null, "alex.demo", "refresh");
        using var fixture = WithInstalled([userBinding], Refresh);
        fixture.Link.Preparation = Prepared(Refresh with { Keys = "Ctrl+Alt+Shift+T" });

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;
        Assert.Empty(dialog.ProposedShortcuts); // pas un nouveau raccourci : rien n'est reproposé
        await dialog.ConfirmAsync();

        Assert.Equal([.. ShortcutDefaults.All, userBinding], Effective(fixture)); // ses touches à lui, intactes
        Assert.DoesNotContain(Effective(fixture), b => b.Keys == "Ctrl+Alt+Shift+T");
    }

    [Fact]
    public async Task Update_RemovedShortcut_LosesItsBindings()
    {
        var other = Bound(new ModuleShortcutInfo("sync", "Synchroniser", "Ctrl+Alt+Shift+S"), "alex.autre");
        using var fixture = WithInstalled([Bound(Refresh), Bound(Open), other], Refresh, Open);
        fixture.Link.Preparation = Prepared(Refresh); // « Ouvrir la page » disparaît du manifeste

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Section.Dialog!.ProposedShortcuts);
        await fixture.Section.Dialog.ConfirmAsync();

        Assert.Equal([.. ShortcutDefaults.All, Bound(Refresh), other], Effective(fixture)); // celle d'un autre module n'a pas bougé
        Assert.Equal([.. ShortcutDefaults.All, Bound(Refresh), other], fixture.Store.Load().Shortcuts);
    }

    [Fact]
    public async Task Update_AbandonedDialog_ChangesNothing()
    {
        using var fixture = WithInstalled([Bound(Open)], Refresh, Open);
        fixture.Link.Preparation = Prepared(Refresh);
        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Contains(Bound(Open), Effective(fixture));
    }

    [Fact]
    public async Task Update_ServiceDoesNotSayWhatTheVersionDeclares_RemovesNothing()
    {
        using var fixture = WithInstalled([Bound(Open)], Refresh, Open);
        fixture.Link.Preparation = new ModuleUpdatePreparation(true, null, "1.3.0", "Notes.", [ModulePermission.Internet], []); // Shortcuts : non dits

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        await fixture.Section.Dialog!.ConfirmAsync();

        Assert.Contains(Bound(Open), Effective(fixture));
    }

    [Fact]
    public async Task Update_ByFile_ProposesOnlyNewShortcuts_AndDropsTheVanishedOnes()
    {
        using var fixture = WithInstalled([Bound(Open)], Open);
        fixture.Link.Package = Package(installedVersion: "1.2.0", version: "1.3.0", shortcuts: [Refresh]); // « Ouvrir » disparaît, « Rafraîchir » arrive

        await fixture.Section.OpenPackageAsync(PackagePath);
        var dialog = fixture.Section.Dialog!;
        Assert.Equal(["refresh"], dialog.ProposedShortcuts.Select(p => p.ShortcutId));
        await dialog.ConfirmAsync();

        Assert.Equal([.. ShortcutDefaults.All, Bound(Refresh)], Effective(fixture));
    }

    // ---- Désinstallation ----

    [Fact]
    public async Task Uninstall_RemovesTheModuleBindings()
    {
        var keep = Bound(new ModuleShortcutInfo("sync", "Synchroniser", "Ctrl+Alt+Shift+S"), "alex.autre");
        using var fixture = WithInstalled([Bound(Refresh), Bound(Open), keep], Refresh, Open);

        await fixture.Section.Modules.Single().ConfirmUninstallCommand.ExecuteAsync(null);

        Assert.Equal([.. ShortcutDefaults.All, keep], Effective(fixture));
        Assert.Equal([.. ShortcutDefaults.All, keep], fixture.Store.Load().Shortcuts);
        Assert.DoesNotContain(fixture.Document.Current.Views[0].Placements, p => p.TypeId == "alex.demo");
    }

    [Fact]
    public async Task Uninstall_BindingsOnly_NoCaseLeft_StillRemovesTheBindings_AndUndoBringsThemBack()
    {
        var layout = DashboardLayout.Default with { Shortcuts = [.. ShortcutDefaults.All, Bound(Refresh)] };
        using var fixture = new CommunityFixture(layout).WithModules(Module(shortcuts: [Refresh]));
        await fixture.Section.RefreshAsync();

        await fixture.Section.Modules.Single().ConfirmUninstallCommand.ExecuteAsync(null);
        Assert.Equal(ShortcutDefaults.All, Effective(fixture));

        await fixture.Session.UndoAsync();
        Assert.Contains(Bound(Refresh), Effective(fixture));
    }
}
