using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Rendering;
using Trofeo.Extensions;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;
using static Trofeo.Display.Tests.FakeCommunityModules;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

// Les modules complémentaires vus par le service : la traduction vers le canal de contrôle, et les cases posées.
public sealed partial class ServiceControlHandlerTests
{
    private static readonly ModulePermission[] Internet = [ModulePermission.Internet];
    private static readonly ModulePermission[] Both = [ModulePermission.Internet, ModulePermission.ThisComputer];

    // ---- Les types ----

    [Fact]
    public void GetTypes_CommunityTypeCarriesItsSheet_BuiltInOnesDoNot()
    {
        using var rig = new Rig();
        var entry = Entry("alex.demo", manifest: Manifest("alex.demo", permissions: Both));
        rig.Community.Entries = [entry];
        rig.Community.Grants["alex.demo"] = Internet;
        rig.LoopRig.Registry.SetCommunityFactories([new Trofeo.Extensions.Service.PlaceholderModuleFactory(entry)]);

        var types = rig.Handler.GetTypes();

        Assert.Null(types.Single(t => t.TypeId == "fake").Community);
        var community = types.Single(t => t.TypeId == "alex.demo");
        var sheet = Assert.IsType<CommunityTypeInfo>(community.Community);
        Assert.Equal(("Alex", "Un module de démonstration", "1.2.0"), (sheet.Author, sheet.Description, sheet.Version));
        Assert.Equal(Both, sheet.Declared);
        Assert.Equal(Internet, sheet.Granted); // ce que l'utilisateur a accordé, pas ce que le manifeste demande
        Assert.Equal("Démo", community.DisplayName);
    }

    [Fact]
    public void GetTypes_CommunityTypeOfADevFolder_UsesTheGrantsOfTheFolder()
    {
        using var rig = new Rig();
        var entry = Entry("alex.dev", dev: true, folder: @"C:\dev\mon-module", manifest: Manifest("alex.dev", permissions: Both));
        rig.Community.Entries = [entry];
        rig.Community.Grants["alex.dev"] = Internet;
        rig.LoopRig.Registry.SetCommunityFactories([new Trofeo.Extensions.Service.PlaceholderModuleFactory(entry)]);

        var sheet = rig.Handler.GetTypes().Single(t => t.TypeId == "alex.dev").Community;

        Assert.Equal(Internet, sheet!.Granted);
    }

    [Fact]
    public void Hello_ListsTheSameTypesAsGetTypes()
    {
        using var rig = new Rig();
        var entry = Entry("alex.demo");
        rig.Community.Entries = [entry];
        rig.LoopRig.Registry.SetCommunityFactories([new Trofeo.Extensions.Service.PlaceholderModuleFactory(entry)]);

        var welcome = rig.Handler.Hello();

        Assert.Equal(ControlPipe.ProtocolVersion, welcome.ProtocolVersion);
        Assert.Equal(rig.Handler.GetTypes().Select(t => t.TypeId), welcome.Types.Select(t => t.TypeId));
        Assert.Contains(welcome.Types, t => t.TypeId == "alex.demo" && t.Community is not null);
    }

    // ---- La vue d'ensemble ----

    [Fact]
    public void GetCommunity_MapsEntriesAndStore()
    {
        using var rig = new Rig();
        var approval = new ApprovedModule("alex.demo", "1.1.0", @"C:\Modules\alex.demo\1.1.0", "hash", Internet, null, "alex/demo");
        rig.Community.Entries =
        [
            Entry("alex.demo", CommunityState.NeedsApproval, "À valider dans la fenêtre de réglage", manifest: Manifest("alex.demo", "1.2.0", Both), approval: approval),
            Entry("alex.dev", dev: true, folder: @"C:\dev\mon-module"),
            Entry("alex.casse", CommunityState.Invalid, "Le manifeste est illisible.", noManifest: true, approval: approval with { Id = "alex.casse" }),
        ];
        rig.Community.Grants["alex.demo"] = Internet;
        rig.Community.StoreInfo = new CommunityStoreInfo(@"D:\Modules", true, [@"C:\dev\mon-module"], Tampered: true, Unreadable: false);

        var overview = rig.Handler.GetCommunity();

        Assert.Equal(@"D:\Modules", overview.ModulesRoot);
        Assert.True(overview.DeveloperMode);
        Assert.True(overview.StoreTampered);
        Assert.Equal([@"C:\dev\mon-module"], overview.DevFolders);
        Assert.Equal(["alex.demo", "alex.dev", "alex.casse"], overview.Modules.Select(m => m.Id));

        var demo = overview.Modules[0];
        Assert.Equal(("Démo", "Alex", "Un module de démonstration", "1.2.0"), (demo.Name, demo.Author, demo.Description, demo.Version));
        Assert.Equal(LinkCommunityState.NeedsApproval, demo.State);
        Assert.Equal("À valider dans la fenêtre de réglage", demo.Message);
        Assert.Equal(Both, demo.Declared);
        Assert.Equal(Internet, demo.Granted);
        Assert.False(demo.FromDevFolder);

        Assert.True(overview.Modules[1].FromDevFolder);
        Assert.Null(overview.Modules[1].Message);

        var broken = overview.Modules[2]; // sans manifeste lisible : l'identifiant tient lieu de nom, la version vient de la validation
        Assert.Equal(("alex.casse", "", ""), (broken.Name, broken.Author, broken.Description));
        Assert.Equal("1.1.0", broken.Version);
        Assert.Empty(broken.Declared);
        Assert.Equal(LinkCommunityState.Invalid, broken.State);
    }

    [Fact]
    public void GetCommunity_NoUpdateOffered_NothingIsProposed()
    {
        using var rig = new Rig();
        rig.Community.Entries = [Entry("alex.demo")];

        var module = Assert.Single(rig.Handler.GetCommunity().Modules);

        Assert.Null(module.UpdateVersion);
        Assert.Null(module.UpdateNotes);
        Assert.Empty(module.UpdateNewPermissions);
    }

    [Fact]
    public void GetCommunity_UpdateOffered_FillsTheVersionTheNotesAndTheNewPermissions()
    {
        using var rig = new Rig();
        rig.Community.Entries = [Entry("alex.demo"), Entry("alex.autre")];
        rig.Community.Offers["alex.demo"] = new ModuleUpdateOffer("1.3.0", "Ajoute une caméra.", [ModulePermission.Sensors, ModulePermission.NowPlaying]);

        var modules = rig.Handler.GetCommunity().Modules;

        var offered = modules.Single(m => m.Id == "alex.demo");
        Assert.Equal(("1.3.0", "Ajoute une caméra."), (offered.UpdateVersion, offered.UpdateNotes));
        Assert.Equal([ModulePermission.Sensors, ModulePermission.NowPlaying], offered.UpdateNewPermissions);
        var other = modules.Single(m => m.Id == "alex.autre"); // l'offre d'un module n'est pas celle d'un autre
        Assert.Null(other.UpdateVersion);
        Assert.Empty(other.UpdateNewPermissions);
    }

    [Fact]
    public void GetCommunity_UpdateOfferedForADevFolderModule_IsStillWhatTheCommunityReports()
    {
        // Le service ne décide pas qui a des mises à jour : il montre ce que ICommunityModules propose.
        using var rig = new Rig();
        rig.Community.Entries = [Entry("alex.dev", dev: true, folder: @"C:\dev\mon-module")];
        rig.Community.Offers["alex.dev"] = new ModuleUpdateOffer("2.0.0", "", []);

        Assert.Equal("2.0.0", Assert.Single(rig.Handler.GetCommunity().Modules).UpdateVersion);
    }

    [Fact]
    public void GetCommunity_NothingInstalled_IsEmpty()
    {
        using var rig = new Rig();

        var overview = rig.Handler.GetCommunity();

        Assert.Empty(overview.Modules);
        Assert.False(overview.DeveloperMode);
        Assert.False(overview.StoreTampered);
    }

    [Fact]
    public void LinkCommunityState_MirrorsCommunityStateByName()
    {
        Assert.Equal(
            Enum.GetNames<CommunityState>().Order(StringComparer.Ordinal),
            Enum.GetNames<LinkCommunityState>().Order(StringComparer.Ordinal));
        using var rig = new Rig();
        foreach (var state in Enum.GetValues<CommunityState>())
        {
            rig.Community.Entries = [Entry("alex.demo", state)];
            Assert.Equal(state.ToString(), rig.Handler.GetCommunity().Modules[0].State.ToString());
        }
    }

    // ---- Le paquet ----

    [Fact]
    public void InspectPackage_CopiesTheInspection()
    {
        using var rig = new Rig();
        var installed = new ApprovedModule("alex.demo", "1.0.0", @"C:\Modules\alex.demo\1.0.0", "hash", Internet, null, "alex/demo");
        rig.Community.Inspection = new PackageInspection(
            Manifest("alex.demo", "1.3.0", Both), ["Une erreur."], "Un autre module porte déjà cet identifiant", installed);

        var package = rig.Handler.InspectPackage(@"C:\Téléchargements\alex.demo-1.3.0.avmod");

        Assert.Equal(("alex.demo", "Démo", "Alex", "1.3.0", "Un module de démonstration"),
            (package.Id, package.Name, package.Author, package.Version, package.Description));
        Assert.Equal(Both, package.Declared);
        Assert.Equal(["Une erreur."], package.Errors);
        Assert.Equal("Un autre module porte déjà cet identifiant", package.Conflict);
        Assert.Equal("1.0.0", package.InstalledVersion);
        Assert.Equal([@"inspect C:\Téléchargements\alex.demo-1.3.0.avmod"], rig.Community.Calls);
    }

    [Fact]
    public void InspectPackage_RefusedPackage_HasNoIdentity()
    {
        using var rig = new Rig();

        var package = rig.Handler.InspectPackage("x.avmod");

        Assert.Null(package.Id);
        Assert.Null(package.Name);
        Assert.Null(package.Version);
        Assert.Empty(package.Declared);
        Assert.Equal(["Archive illisible."], package.Errors);
        Assert.Null(package.InstalledVersion);
    }

    // ---- Chaque demande passe à ICommunityModules ----

    [Fact]
    public async Task EveryOperation_IsDelegatedWithItsArgumentsAndTheResultComesBack()
    {
        using var rig = new Rig();
        rig.Community.Result = new ActionResult(false, "Refusé.");
        var h = rig.Handler;
        var ct = CancellationToken.None;

        var results = new[]
        {
            await h.InstallPackageAsync("a.avmod", Both, ct),
            h.ApproveModule("alex.demo", Internet),
            await h.SetModulePermissionsAsync("alex.demo", [], ct),
            await h.UninstallModuleAsync("alex.demo", ct),
            await h.RelaunchModuleAsync("alex.demo", ct),
            h.SetModulesRoot(@"D:\Modules"),
            h.SetDeveloperMode(true),
            h.AddDevFolder(@"C:\dev\demo", Internet),
            h.RemoveDevFolder(@"C:\dev\demo"),
            await h.ReloadDevModuleAsync("alex.dev", ct),
        };

        Assert.All(results, r => Assert.Equal(new ActionResult(false, "Refusé."), r));
        Assert.Equal(
            [
                "install a.avmod Internet,ThisComputer", "approve alex.demo Internet", "setPermissions alex.demo ", "uninstall alex.demo",
                "relaunch alex.demo", @"setModulesRoot D:\Modules", "setDeveloperMode True", @"addDevFolder C:\dev\demo Internet",
                @"removeDevFolder C:\dev\demo", "reloadDevModule alex.dev",
            ],
            rig.Community.Calls);
    }

    // ---- Mises à jour des modules et consommation ----

    [Fact]
    public async Task CheckModuleUpdates_AsksTheCommunityModulesToCheck()
    {
        using var rig = new Rig();

        await rig.Handler.CheckModuleUpdatesAsync(CancellationToken.None);

        Assert.Equal(["checkUpdates"], rig.Community.Calls);
    }

    [Fact]
    public async Task CheckModuleUpdates_CancelledByTheCaller_IsPassedOn()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.Community.CheckGate = new TaskCompletionSource();

        var check = rig.Handler.CheckModuleUpdatesAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task InstallModuleUpdate_PassesTheCheckedPermissionsAndTheAnswer()
    {
        using var rig = new Rig();
        rig.Community.Result = new ActionResult(true, "« Démo » 1.3.0 est mis à jour.");

        var result = await rig.Handler.InstallModuleUpdateAsync("alex.demo", Both, CancellationToken.None);

        Assert.Equal(new ActionResult(true, "« Démo » 1.3.0 est mis à jour."), result);
        Assert.Equal(["installUpdate alex.demo Internet,ThisComputer"], rig.Community.Calls);
    }

    [Fact]
    public async Task InstallModuleUpdate_Refused_ReturnsTheRefusal()
    {
        using var rig = new Rig();
        rig.Community.Result = new ActionResult(false, "Aucune mise à jour proposée pour ce module.");

        var result = await rig.Handler.InstallModuleUpdateAsync("alex.demo", Internet, CancellationToken.None);

        Assert.Equal(new ActionResult(false, "Aucune mise à jour proposée pour ce module."), result);
    }

    [Fact]
    public void GetConsumption_GivesTheCommunityTheProcessOfTheService_AndReturnsItsRows()
    {
        using var rig = new Rig();
        var rows = new[]
        {
            new ModuleConsumption("origine", "Modules d'origine (dans le service)", false, 0.5, 1234, false),
            new ModuleConsumption("alex.demo", "Démo", true, 3.1, 90_000_000, true),
        };
        rig.Community.ConsumptionRows = rows;

        var result = rig.Handler.GetConsumption();

        Assert.Equal(rows, result);
        Assert.Equal(["consumption"], rig.Community.Calls);
        Assert.Equal(new ProcessInfo(1, 2, 0), rig.Community.LastServiceProcess); // la mesure du service, celle de l'état
    }

    // ---- Les hôtes suivent le plafond d'images et l'apparence ----

    [Fact]
    public async Task Reload_PassesMaxFpsAndAppearanceToTheHosts_EvenWhenOnlyMaxFpsChanged()
    {
        using var rig = new Rig();
        var appearance = new AppearanceSettings("pixel");
        rig.Store.Save(Layout(Place(A, "fake")) with { MaxFps = 8, Appearance = appearance });
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        rig.Store.Save(Layout(Place(A, "fake")) with { MaxFps = 5, Appearance = appearance }); // seul le plafond change : l'apparence reste
        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal([(8, appearance), (5, appearance)], rig.Community.Options);
    }

    [Fact]
    public async Task Reload_NoMaxFpsNoAppearance_UsesTheDefaults()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout(Place(A, "fake")));

        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal([(ServiceControlHandler.HostMaxFpsCap, AppearanceSettings.Default)], rig.Community.Options);
    }

    [Fact]
    public async Task Reload_MaxFpsAboveTheCap_IsClampedForTheHosts()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout(Place(A, "fake")) with { MaxFps = 30 });

        await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.Equal(ServiceControlHandler.HostMaxFpsCap, Assert.Single(rig.Community.Options).MaxFps);
    }

    [Fact]
    public async Task Reload_InvalidFile_DoesNotTouchTheHosts()
    {
        using var rig = new Rig();
        rig.Store.Save(Layout(Place(A, "fake"), Place(B, "other"))); // se chevauchent

        var result = await rig.Pump(rig.Handler.ReloadAsync(CancellationToken.None));

        Assert.False(result.Applied);
        Assert.Empty(rig.Community.Options);
    }

    // ---- Les cases déjà posées suivent ce que leur type exécute (R20) ----

    // Le banc ne connaît que « fake » et « other » comme types qui comptent leurs instances : « other » joue le module complémentaire,
    // et les entrées données sont l'état que le handler voit au départ.
    private static Rig PlacedRig(params CommunityEntry[] atStart) =>
        new(Layout(Place(A, "fake"), Place(B, "other", column: 6)), communityEntries: atStart);

    /// <summary>La boucle fait ses travaux en attente (un redémarrage demandé tourne au début d'un passage).</summary>
    private static void Settle(Rig rig)
    {
        rig.LoopRig.Loop.Tick();
        rig.LoopRig.Loop.Tick();
    }

    [Fact]
    public void Changed_ModuleBecomesRunnable_RestartsItsPlacedCases()
    {
        using var rig = PlacedRig(Entry("other", CommunityState.NeedsApproval, "À valider dans la fenêtre de réglage"));
        var before = rig.LoopRig.Of(B).Count();
        var untouched = rig.LoopRig.Of(A).Count();

        rig.Community.Entries = [Entry("other", CommunityState.Ready)]; // validé
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before + 1, rig.LoopRig.Of(B).Count());
        Assert.Equal(1, rig.LoopRig.Of(B).First().Disposed); // l'ancien objet est libéré, le nouveau vient de la fabrique d'aujourd'hui
        Assert.Equal(untouched, rig.LoopRig.Of(A).Count()); // un autre type n'est pas touché
    }

    [Fact]
    public void Changed_NewVersionFolder_RestartsItsPlacedCases()
    {
        using var rig = PlacedRig(Entry("other", folder: @"C:\Modules\other\1.0.0"));
        var before = rig.LoopRig.Of(B).Count();

        rig.Community.Entries = [Entry("other", folder: @"C:\Modules\other\1.1.0")]; // mise à jour
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before + 1, rig.LoopRig.Of(B).Count());
    }

    [Fact]
    public void Changed_ModuleAppears_RestartsTheCaseWaitingForIt()
    {
        using var rig = PlacedRig();
        var before = rig.LoopRig.Of(B).Count();

        rig.Community.Entries = [Entry("other")];
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before + 1, rig.LoopRig.Of(B).Count());
    }

    [Fact]
    public void Changed_SetAsideAlone_DoesNotRestartAnything()
    {
        using var rig = PlacedRig(Entry("other"));
        var before = rig.LoopRig.Created.Count;

        // Mis de côté : la case garde l'objet du superviseur, qui dit lui-même pourquoi. Aucun redémarrage, aucune boucle.
        rig.Community.Entries = [Entry("other", CommunityState.SetAside, "Mis de côté : le module s'est arrêté trop souvent.")];
        rig.Community.Raise();
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before, rig.LoopRig.Created.Count);
    }

    [Fact]
    public void Changed_RelaunchAfterSetAside_RestartsItsPlacedCases()
    {
        using var rig = PlacedRig(Entry("other"));
        rig.Community.Entries = [Entry("other", CommunityState.SetAside, "Mis de côté : le module s'est arrêté trop souvent.")];
        rig.Community.Raise();
        Settle(rig);
        var before = rig.LoopRig.Of(B).Count();

        rig.Community.Entries = [Entry("other")]; // remis en route : une case posée pendant la mise de côté tient encore l'objet « mis de côté »
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before + 1, rig.LoopRig.Of(B).Count());
    }

    [Fact]
    public void Changed_SameThingsAgain_DoesNotRestart()
    {
        using var rig = PlacedRig(Entry("other"));
        var before = rig.LoopRig.Created.Count;

        rig.Community.Grants["other"] = Internet; // des permissions changées : le superviseur relance son hôte, la case reste
        rig.Community.Entries = [Entry("other")];
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before, rig.LoopRig.Created.Count);
    }

    [Fact]
    public void Changed_ModuleWithoutPlacedCase_StartsNothing()
    {
        using var rig = new Rig();
        var before = rig.LoopRig.Created.Count;

        rig.Community.Entries = [Entry("alex.nouveau")];
        rig.Community.Raise();
        Settle(rig);

        Assert.Equal(before, rig.LoopRig.Created.Count);
    }

    [Fact]
    public void Dispose_StopsListeningToChanges()
    {
        using var rig = PlacedRig(Entry("other"));
        Assert.True(rig.Community.HasSubscribers);

        rig.Handler.Dispose();

        Assert.False(rig.Community.HasSubscribers);
    }
}
