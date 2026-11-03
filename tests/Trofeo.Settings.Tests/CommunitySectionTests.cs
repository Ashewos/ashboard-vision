using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.CommunityFixture;

namespace Trofeo.Settings.Tests;

/// <summary>La section « Modules complémentaires » de l'onglet Paramètres : la liste, les boutons, la désinstallation, la mise à jour en deux temps.</summary>
public sealed class CommunitySectionTests
{
    private const string Path = @"C:\Téléchargements\alex.demo-1.0.0.avmod";

    // ---- La liste ----

    [Fact]
    public async Task Section_ListsEachModule_WithItsStateInPlainWords()
    {
        using var fixture = new CommunityFixture().WithModules(
            Module(),
            Module(id: "bob.meteo", name: "Météo", author: "Bob", version: "2.0.0", state: LinkCommunityState.SetAside, message: "Il s'est arrêté trois fois en dix minutes.", granted: []),
            Module(id: "carl.mc", name: "Minecraft", author: "Carl", version: "0.3.0", state: LinkCommunityState.NeedsApproval, message: "À valider dans la fenêtre de réglage"));

        await fixture.Section.RefreshAsync();

        Assert.Equal(["Démo", "Météo", "Minecraft"], fixture.Section.Modules.Select(m => m.Name));
        Assert.Equal(["par Alex · version 1.2.0", "par Bob · version 2.0.0", "par Carl · version 0.3.0"], fixture.Section.Modules.Select(m => m.AuthorLine));
        Assert.Equal(["Actif", "Mis de côté : il s'est arrêté trop souvent", "À valider"], fixture.Section.Modules.Select(m => m.StateText));
        Assert.Equal(["Permissions accordées : Internet.", "Aucune permission accordée.", "Permissions accordées : Internet."],
            fixture.Section.Modules.Select(m => m.PermissionsText));
        Assert.True(fixture.Section.HasModules);
        Assert.False(fixture.Section.HasNoModules);
    }

    [Fact]
    public async Task Section_OfferedUpdate_IsShownOnItsRow()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0", updateNotes: "Nouveau réglage."));

        await fixture.Section.RefreshAsync();

        var row = fixture.Section.Modules.Single();
        Assert.Equal("Mise à jour proposée : 1.3.0", row.UpdateOffer);
        Assert.True(row.CanUpdate);
    }

    [Fact]
    public async Task Section_Buttons_FollowTheStateOfTheModule()
    {
        using var fixture = new CommunityFixture().WithModules(
            Module(id: "a.ready"),
            Module(id: "a.aside", state: LinkCommunityState.SetAside),
            Module(id: "a.approve", state: LinkCommunityState.NeedsApproval),
            Module(id: "a.changed", state: LinkCommunityState.Changed),
            Module(id: "a.newer", state: LinkCommunityState.NeedsNewerApp),
            Module(id: "a.dev", fromDevFolder: true, updateVersion: "9.9.9"));

        await fixture.Section.RefreshAsync();

        string Buttons(string id)
        {
            var row = fixture.Section.Modules.Single(m => m.Id == id);
            return string.Concat(row.CanApprove ? "V" : "", row.CanPermissions ? "P" : "", row.CanUpdate ? "M" : "", row.CanRelaunch ? "R" : "", row.CanUninstall ? "D" : "");
        }

        Assert.Equal("PD", Buttons("a.ready"));
        Assert.Equal("PRD", Buttons("a.aside"));          // « Relancer » : seulement un module mis de côté
        Assert.Equal("VD", Buttons("a.approve"));         // à valider : la validation d'abord
        Assert.Equal("VD", Buttons("a.changed"));
        Assert.Equal("D", Buttons("a.newer"));            // on peut toujours le retirer
        Assert.Equal("P", Buttons("a.dev"));              // un dossier de développement se retire avec son dossier
    }

    [Fact]
    public async Task Section_TamperedStore_ShowsBanner()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Community = new CommunityOverview([], @"C:\Modules", false, [], StoreTampered: true);

        await fixture.Section.RefreshAsync();

        Assert.True(fixture.Section.StoreTampered);
        Assert.Equal("La liste des modules validés a été modifiée hors du logiciel : valide de nouveau tes modules.", fixture.Section.TamperedText);
    }

    [Fact]
    public async Task Section_IntactStore_ShowsNoBanner()
    {
        using var fixture = new CommunityFixture().WithModules(Module());

        await fixture.Section.RefreshAsync();

        Assert.False(fixture.Section.StoreTampered);
    }

    [Fact]
    public async Task Section_TextFromTheModules_IsCleaned()
    {
        using var fixture = new CommunityFixture().WithModules(
            Module(name: "Dé\u202Emo\u200B", author: "Al\u2028ex", message: "Refusé\u0007 : trop gros."));

        await fixture.Section.RefreshAsync();

        var row = fixture.Section.Modules.Single();
        Assert.Equal("Démo", row.Name);
        Assert.Equal("par Alex · version 1.2.0", row.AuthorLine);
        Assert.Equal("Refusé : trop gros.", row.Message);
    }

    [Fact]
    public async Task Section_IsFree_UntilTheTabIsOpened()
    {
        using var fixture = new CommunityFixture();

        fixture.Link.SetTypes([.. SampleData.Types(), DemoType(), DemoType("bob.autre")]);
        Assert.DoesNotContain("community", fixture.Link.ModuleCalls); // rien n'est demandé tant que l'onglet n'a pas été ouvert

        fixture.Section.Activate();
        Assert.Equal(1, fixture.Link.ModuleCalls.Count(c => c == "community"));

        fixture.Link.SetTypes([.. SampleData.Types(), DemoType()]);   // d'autres types : la liste se relit
        Assert.Equal(2, fixture.Link.ModuleCalls.Count(c => c == "community"));

        fixture.Link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status()); // l'état seul change : rien
        Assert.Equal(2, fixture.Link.ModuleCalls.Count(c => c == "community"));
    }

    // ---- Installer un fichier ----

    [Fact]
    public async Task Section_InstallButton_PicksAnAvmodThenInspectsIt()
    {
        using var fixture = new CommunityFixture();
        fixture.Picker.Result = Path;

        await fixture.Section.InstallFromFileAsync();

        Assert.Equal(["avmod"], fixture.Picker.Extensions);
        Assert.Equal(["inspect " + Path], fixture.Link.ModuleCalls);
        Assert.NotNull(fixture.Section.Dialog);
    }

    [Fact]
    public async Task Section_InstallButton_PickerCancelled_DoesNothing()
    {
        using var fixture = new CommunityFixture();
        fixture.Picker.Result = null;

        await fixture.Section.InstallFromFileAsync();

        Assert.Empty(fixture.Link.ModuleCalls);
        Assert.Null(fixture.Section.Dialog);
    }

    [Fact]
    public async Task Section_InstallButton_PickerFailure_ShowsItsMessage()
    {
        using var fixture = new CommunityFixture();
        fixture.Picker.Failure = "Impossible d'ouvrir le sélecteur de fichier.";

        await fixture.Section.InstallFromFileAsync();

        Assert.Equal("Impossible d'ouvrir le sélecteur de fichier.", fixture.Section.Message);
    }

    [Fact]
    public void Program_AvmodArgument_OpensInstallDialogAfterConnect()
    {
        using var fixture = new CommunityFixture(connected: false);

        // Le chemin vient de la ligne de commande ; la liaison n'est pas encore établie : rien ne part.
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trofeo-arg-" + Guid.NewGuid().ToString("N") + ".avmod");
        File.WriteAllText(file, "x");
        try
        {
            RunProgramArgument(fixture, file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static void RunProgramArgument(CommunityFixture fixture, string file)
    {
        var path = PackageArgument.Find(["--ignore", file]);
        Assert.Equal(file, path);
        fixture.Section.OpenPackageWhenConnected(path!);
        Assert.Empty(fixture.Link.ModuleCalls);
        Assert.Null(fixture.Section.Dialog);

        // Le service est joint : la boîte s'ouvre, après l'inspection du fichier — et rien n'est installé.
        fixture.Link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());

        Assert.Equal(["inspect " + file], fixture.Link.ModuleCalls);
        Assert.NotNull(fixture.Section.Dialog);
        Assert.Equal(ModuleDialogMode.Install, fixture.Section.Dialog.Mode);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, call => call.StartsWith("install"));
    }

    [Fact]
    public void Program_AvmodArgument_AlreadyConnected_OpensAtOnce()
    {
        using var fixture = new CommunityFixture();

        fixture.Section.OpenPackageWhenConnected(Path);

        Assert.NotNull(fixture.Section.Dialog);
    }

    [Fact]
    public void Program_AvmodArgument_OnlyDialogOpens_NothingInstallsWithoutAClick()
    {
        using var fixture = new CommunityFixture();
        fixture.Section.OpenPackageWhenConnected(Path);

        Assert.Equal(["inspect " + Path], fixture.Link.ModuleCalls);
        Assert.True(fixture.Section.HasDialog);
    }

    [Theory]
    [InlineData("Trofeo.Settings.exe", null)]
    [InlineData("module.zip", null)]
    [InlineData("C:\\a\\b\\", null)]
    public void PackageArgument_Find_IgnoresWhatIsNotAnAvmod(string arg, string? expected) => Assert.Equal(expected, PackageArgument.Find([arg]));

    // ---- Relancer, désinstaller ----

    [Fact]
    public async Task Section_Relaunch_AsksTheServiceThenReads()
    {
        using var fixture = new CommunityFixture().WithModules(Module(state: LinkCommunityState.SetAside));
        await fixture.Section.RefreshAsync();

        await fixture.Section.Modules.Single().RelaunchCommand.ExecuteAsync(null);

        Assert.Contains("relaunch alex.demo", fixture.Link.ModuleCalls);
        Assert.Null(fixture.Section.Message);
    }

    [Fact]
    public async Task Section_Relaunch_Refused_ShowsTheMessageAsIs()
    {
        using var fixture = new CommunityFixture().WithModules(Module(state: LinkCommunityState.SetAside));
        await fixture.Section.RefreshAsync();
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Les fichiers du module sont verrouillés.");

        await fixture.Section.Modules.Single().RelaunchCommand.ExecuteAsync(null);

        Assert.Equal("Les fichiers du module sont verrouillés.", fixture.Section.Message);
    }

    [Fact]
    public async Task Section_Uninstall_AsksForConfirmationFirst()
    {
        using var fixture = new CommunityFixture().WithModules(Module());
        await fixture.Section.RefreshAsync();
        var row = fixture.Section.Modules.Single();

        row.AskUninstallCommand.Execute(null);

        Assert.True(row.ConfirmingUninstall);
        Assert.Equal("Ses cases seront retirées de l'écran.", row.UninstallConfirmText);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("uninstall"));
        row.CancelUninstallCommand.Execute(null);
        Assert.False(row.ConfirmingUninstall);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("uninstall"));
    }

    [Fact]
    public async Task Section_Uninstall_RemovesPlacementsThenReloads()
    {
        using var fixture = new CommunityFixture().WithModules(Module());
        await fixture.Section.RefreshAsync();
        var row = fixture.Section.Modules.Single();
        Assert.Contains(fixture.Document.Current.Placements, p => p.TypeId == "alex.demo");
        var reloads = fixture.Link.ReloadCount;

        await row.ConfirmUninstallCommand.ExecuteAsync(null);

        Assert.Contains("uninstall alex.demo", fixture.Link.ModuleCalls);
        // Les cases placées et rangées du module ont disparu de la disposition, en mémoire comme dans le fichier.
        Assert.DoesNotContain(fixture.Document.Current.Placements, p => p.TypeId == "alex.demo");
        Assert.DoesNotContain(fixture.Document.Current.Shelf ?? [], p => p.TypeId == "alex.demo");
        Assert.DoesNotContain(fixture.Store.Load().Placements, p => p.TypeId == "alex.demo");
        Assert.DoesNotContain(fixture.Store.Load().Shelf ?? [], p => p.TypeId == "alex.demo");
        // Les autres modules n'ont pas bougé, et le service a rechargé la disposition.
        Assert.Contains(fixture.Document.Current.Placements, p => p.TypeId == "claudecode");
        Assert.Equal(reloads + 1, fixture.Link.ReloadCount);
    }

    [Fact]
    public async Task Section_Uninstall_OtherModulesOfTheSameAuthor_AreKept()
    {
        var layout = WithDemo() with
        {
            Shelf = [.. WithDemo().Shelf!, new Trofeo.Core.Layout.ModulePlacement(Guid.NewGuid(), "alex.autre", 0, 2, 4, 2, Trofeo.ModuleKit.TextSize.Normal, Json("{}"))],
        };
        using var fixture = new CommunityFixture(layout).WithModules(Module());
        await fixture.Section.RefreshAsync();

        await fixture.Section.Modules.Single().ConfirmUninstallCommand.ExecuteAsync(null);

        Assert.Equal(["alex.autre"], (fixture.Document.Current.Shelf ?? []).Select(p => p.TypeId));
    }

    [Fact]
    public async Task Section_Uninstall_WithNoPlacementLeft_StillReloads_AndTheUnknownTypeWarningShows()
    {
        // Rien à retirer de la disposition (une case d'un autre type du même module, restée là, est signalée par le service).
        using var fixture = new CommunityFixture(DashboardLayout_NoDemo()).WithModules(Module());
        fixture.Link.OnReload = () => Task.FromResult(new Reloaded(true, null, ["Module ignoré : type inconnu « alex.demo » (d3a10000-0000-4000-8000-000000000001)."]));
        await fixture.Section.RefreshAsync();
        var reloads = fixture.Link.ReloadCount;

        await fixture.Section.Modules.Single().ConfirmUninstallCommand.ExecuteAsync(null);

        Assert.Equal(reloads + 1, fixture.Link.ReloadCount);
        Assert.Equal(["Module ignoré : type inconnu « alex.demo » (d3a10000-0000-4000-8000-000000000001)."], fixture.Session.OtherWarnings);
    }

    [Fact]
    public async Task Section_Uninstall_Refused_KeepsTheCases_AndShowsTheMessage()
    {
        using var fixture = new CommunityFixture().WithModules(Module());
        await fixture.Section.RefreshAsync();
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Le dossier du module est utilisé par un autre programme.");
        var reloads = fixture.Link.ReloadCount;

        await fixture.Section.Modules.Single().ConfirmUninstallCommand.ExecuteAsync(null);

        Assert.Equal("Le dossier du module est utilisé par un autre programme.", fixture.Section.Message);
        Assert.Contains(fixture.Document.Current.Placements, p => p.TypeId == "alex.demo");
        Assert.Equal(reloads, fixture.Link.ReloadCount);
    }

    private static Trofeo.Core.Layout.DashboardLayout DashboardLayout_NoDemo() => Trofeo.Core.Layout.DashboardLayout.Default;

    // ---- La mise à jour, en deux temps (R21) ----

    private static CommunityFixture WithUpdate(out CommunityModuleRow row, ModulePermission[]? granted = null)
    {
        var fixture = new CommunityFixture().WithModules(Module(
            declared: [ModulePermission.Internet, ModulePermission.Sensors], granted: granted ?? [ModulePermission.Internet, ModulePermission.Sensors],
            updateVersion: "1.3.0", updateNotes: "Notes de la liste."));
        fixture.Section.RefreshAsync().GetAwaiter().GetResult();
        row = fixture.Section.Modules.Single();
        return fixture;
    }

    private static ModuleUpdatePreparation Prepared(ModulePermission[] declared, ModulePermission[]? added = null, string notes = "Notes du paquet.") =>
        new(true, null, "1.3.0", notes, declared, added ?? []);

    [Fact]
    public async Task Update_ButtonStartsThePreparation_AndShowsThatTheDownloadIsRunning()
    {
        using var fixture = WithUpdate(out var row);
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        fixture.Link.OnPrepare = _ => gate.Task;

        var running = row.UpdateCommand.ExecuteAsync(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(["community", "prepare alex.demo"], fixture.Link.ModuleCalls);
        Assert.Equal(ModuleDialogMode.Update, dialog.Mode);
        Assert.True(dialog.IsPreparing);
        Assert.Equal("Téléchargement de la version 1.3.0…", dialog.PreparingText);
        Assert.Equal("Notes de la liste.", dialog.Notes);           // la liste connaît la version et les notes
        Assert.Equal("Version 1.3.0 (tu as la 1.2.0)", dialog.VersionText);
        Assert.Empty(dialog.Permissions);
        Assert.False(dialog.CanConfirm);
        Assert.False(row.CanUpdateNow);                              // le bouton est grisé pendant la préparation
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("update"));

        gate.SetResult(Prepared([ModulePermission.Internet, ModulePermission.Sensors]));
        await running;

        Assert.False(dialog.IsPreparing);
        Assert.True(dialog.CanConfirm);
        Assert.Equal("Notes du paquet.", dialog.Notes);              // les notes du paquet lu remplacent celles de la liste
    }

    [Fact]
    public async Task Update_PreparationFillsTheDialog_NotesDeclaredBoxesAndNewPermissions()
    {
        using var fixture = WithUpdate(out var row, granted: [ModulePermission.Internet]);
        fixture.Link.Preparation = Prepared(
            [ModulePermission.Internet, ModulePermission.Sensors, ModulePermission.NowPlaying], added: [ModulePermission.Sensors, ModulePermission.NowPlaying]);

        await row.UpdateCommand.ExecuteAsync(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal("Mettre à jour un module complémentaire", dialog.Title);
        Assert.Equal("Mettre à jour", dialog.ConfirmLabel);
        Assert.Equal("Notes du paquet.", dialog.Notes);
        Assert.Equal(["Internet", "Capteurs", "Musique en cours"], dialog.Permissions.Select(p => p.Label));
        Assert.Equal([false, true, true], dialog.Permissions.Select(p => p.IsNew));
        Assert.True(dialog.HasNewPermissions);
        Assert.Equal("Cette version demande de nouvelles permissions. Elles ne sont pas cochées : coche celles que tu acceptes.", dialog.NewPermissionsText);
    }

    [Fact]
    public async Task Update_AlreadyGrantedAndStillDeclared_ArePreChecked_NewOnesAreNot_RemovedOnesAreHidden()
    {
        // Accordées à la version installée : Internet et Capteurs. La nouvelle version ne déclare plus Capteurs et demande Musique.
        using var fixture = WithUpdate(out var row, granted: [ModulePermission.Internet, ModulePermission.Sensors]);
        fixture.Link.Preparation = Prepared([ModulePermission.Internet, ModulePermission.NowPlaying], added: [ModulePermission.NowPlaying]);

        await row.UpdateCommand.ExecuteAsync(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal([ModulePermission.Internet, ModulePermission.NowPlaying], dialog.Permissions.Select(p => p.Permission));
        Assert.Equal([true, false], dialog.Permissions.Select(p => p.IsChecked)); // Internet déjà validée ; Musique, nouvelle, décochée
        Assert.Equal([false, true], dialog.Permissions.Select(p => p.IsNew));
        Assert.DoesNotContain(dialog.Permissions, p => p.Permission == ModulePermission.Sensors);   // plus déclarée : pas montrée
    }

    [Fact]
    public async Task Update_ANewPermission_IsUncheckedEvenIfTheServiceDoesNotListItAsNew()
    {
        // Le service n'a rien signalé de nouveau, mais la version installée ne déclarait pas « Musique » : elle ne se coche pas seule.
        using var fixture = WithUpdate(out var row, granted: [ModulePermission.Internet]);
        fixture.Link.Preparation = Prepared([ModulePermission.Internet, ModulePermission.NowPlaying], added: []);

        await row.UpdateCommand.ExecuteAsync(null);

        Assert.Equal([true, false], fixture.Section.Dialog!.Permissions.Select(p => p.IsChecked));
        Assert.Equal([false, true], fixture.Section.Dialog.Permissions.Select(p => p.IsNew));
    }

    [Fact]
    public async Task Update_APermissionTheServiceCallsNew_IsNeverPreChecked()
    {
        // Même si la liste dit l'avoir accordée à la version installée : le service dit qu'elle est nouvelle, elle se coche à la main.
        using var fixture = WithUpdate(out var row, granted: [ModulePermission.Internet, ModulePermission.Sensors]);
        fixture.Link.Preparation = Prepared([ModulePermission.Internet, ModulePermission.Sensors], added: [ModulePermission.Sensors]);

        await row.UpdateCommand.ExecuteAsync(null);

        Assert.Equal([true, false], fixture.Section.Dialog!.Permissions.Select(p => p.IsChecked));
        Assert.Equal([false, true], fixture.Section.Dialog.Permissions.Select(p => p.IsNew));
    }

    [Fact]
    public async Task Update_InstallSendsExactlyWhatTheUserSeesChecked()
    {
        using var fixture = WithUpdate(out var row, granted: [ModulePermission.Internet, ModulePermission.Sensors]);
        fixture.Link.Preparation = Prepared(
            [ModulePermission.Internet, ModulePermission.Sensors, ModulePermission.NowPlaying], added: [ModulePermission.NowPlaying]);
        await row.UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;
        dialog.Permissions[1].IsChecked = false;  // l'utilisateur retire Capteurs
        dialog.Permissions[2].IsChecked = true;   // et accepte la nouvelle

        await dialog.ConfirmAsync();

        Assert.Equal("update alex.demo Internet,NowPlaying", fixture.LastChange);
        Assert.Null(fixture.Section.Dialog);
        Assert.Equal(1, fixture.Link.ModuleCalls.Count(c => c.StartsWith("prepare")));   // rien n'est retéléchargé
    }

    [Fact]
    public async Task Update_PreparationRefused_ShowsTheMessageAsIs_WithoutAnInstallButton()
    {
        using var fixture = WithUpdate(out var row);
        fixture.Link.Preparation = new ModuleUpdatePreparation(false, "L'empreinte du paquet ne correspond pas : téléchargement abandonné.", null, null, [], []);

        await row.UpdateCommand.ExecuteAsync(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(["L'empreinte du paquet ne correspond pas : téléchargement abandonné."], dialog.Problems);
        Assert.False(dialog.CanConfirm);
        Assert.False(dialog.IsPreparing);
        Assert.Empty(dialog.Permissions);
        await dialog.ConfirmAsync();
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("update"));
    }

    [Fact]
    public async Task Update_PreparationThrows_WhenNotAbandoned_ShowsTheMessage()
    {
        using var fixture = WithUpdate(out var row);
        fixture.Link.OnPrepare = _ => Task.FromException<ModuleUpdatePreparation>(new ControlException("Le service d'affichage ne répond pas."));

        await row.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["Le service d'affichage ne répond pas."], fixture.Section.Dialog!.Problems);
        Assert.False(fixture.Section.Dialog.CanConfirm);
    }

    [Fact]
    public async Task Update_CancelDuringTheDownload_TellsTheService_AndTheLateCancellationIsNotAnError()
    {
        using var fixture = WithUpdate(out var row);
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        fixture.Link.OnPrepare = _ => gate.Task;
        var running = row.UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;

        await dialog.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Contains("cancel alex.demo", fixture.Link.ModuleCalls);
        Assert.True(row.CanUpdateNow);

        // La préparation annulée revient comme une erreur du canal : c'est un abandon, pas un échec.
        gate.SetException(new ControlException("La mise à jour a été abandonnée."));
        await running;

        Assert.Null(fixture.Section.Dialog);
        Assert.Null(fixture.Section.Message);
        Assert.Empty(dialog.Problems);
        Assert.Null(dialog.ResultMessage);
    }

    [Fact]
    public async Task Update_CancelDuringTheDownload_ALatePreparationThatSucceedsAnyway_ChangesNothing()
    {
        using var fixture = WithUpdate(out var row);
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        fixture.Link.OnPrepare = _ => gate.Task;
        var running = row.UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;
        await dialog.CancelAsync();

        gate.SetResult(Prepared([ModulePermission.Internet]));
        await running;

        Assert.Null(fixture.Section.Dialog);
        Assert.Empty(dialog.Permissions);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("update"));
    }

    [Fact]
    public async Task Update_CancelOnceReady_TellsTheService()
    {
        using var fixture = WithUpdate(out var row);
        await row.UpdateCommand.ExecuteAsync(null);

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Equal(["community", "prepare alex.demo", "cancel alex.demo"], fixture.Link.ModuleCalls);
    }

    [Fact]
    public async Task Update_ServiceGoneWhileCancelling_IsQuiet()
    {
        using var fixture = WithUpdate(out var row);
        await row.UpdateCommand.ExecuteAsync(null);
        fixture.Link.CancelFailure = new ControlException("Le service d'affichage ne répond pas.");

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Null(fixture.Section.Message);
    }

    [Fact]
    public async Task Update_InstallRefusedByTheService_ShowsItsMessage_AndKeepsTheDialog()
    {
        using var fixture = WithUpdate(out var row);
        await row.UpdateCommand.ExecuteAsync(null);
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Aucune mise à jour préparée pour ce module : recommence.");

        await fixture.Section.Dialog!.ConfirmAsync();

        Assert.Equal("Aucune mise à jour préparée pour ce module : recommence.", fixture.Section.Dialog!.ResultMessage);
    }

    [Fact]
    public async Task Update_SecondClickWhileADialogIsOpen_DoesNothing()
    {
        using var fixture = WithUpdate(out var row);
        var gate = new TaskCompletionSource<ModuleUpdatePreparation>();
        fixture.Link.OnPrepare = _ => gate.Task;
        var first = row.UpdateCommand.ExecuteAsync(null);

        await fixture.Section.UpdateAsync(row);

        Assert.Equal(1, fixture.Link.ModuleCalls.Count(c => c.StartsWith("prepare")));
        gate.SetResult(Prepared([ModulePermission.Internet]));
        await first;
    }

    [Fact]
    public async Task Update_TextsFromTheModule_AreCleaned()
    {
        using var fixture = WithUpdate(out var row);
        fixture.Link.Preparation = Prepared([ModulePermission.Internet], notes: "Un\u202E changement\n\u200Bimportant\u0007");

        await row.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Un changement\nimportant", fixture.Section.Dialog!.Notes);
    }
}
