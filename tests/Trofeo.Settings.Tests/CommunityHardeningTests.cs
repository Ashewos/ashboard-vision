using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.CommunityFixture;

namespace Trofeo.Settings.Tests;

/// <summary>Corrections de la relecture (tâche 12, round 1) : noms nettoyés partout, boîte plus stricte, ligne de commande locale.</summary>
public sealed class CommunityHardeningTests : IDisposable
{
    private const string Path = @"C:\Téléchargements\alex.demo-1.0.0.avmod";
    private const string Hostile = "Mine\u202Ecraft\u200B";

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trofeo-settings-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public CommunityHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModuleTypeInfo Hostile_Type(SettingField[]? fields = null, GridSize? min = null, bool commands = false) => new(
        "alex.minecraft", Hostile, min ?? new GridSize(3, 1), new Dictionary<SizePreset, GridSize>(), commands,
        new SettingsSchema(fields ?? [new PortField("port", "Port", 25565)]),
        new CommunityTypeInfo("Alex", "Un serveur.", "1.0.0", [ModulePermission.ThisComputer], [ModulePermission.ThisComputer]));

    private (FakeServiceLink Link, LayoutDocument Document, EditSession Session) Open(DashboardLayout layout)
    {
        var link = new FakeServiceLink();
        link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        var store = new LayoutStore(System.IO.Path.Combine(_dir, "layout.json"));
        store.Save(layout);
        var document = new LayoutDocument(store, link);
        document.Load();
        return (link, document, new EditSession(document));
    }

    // ---- I1 : les noms nettoyés partout ----

    [Fact]
    public async Task Import_KeptSettingsMessage_ShowsTheCleanedNameOfAModule()
    {
        var (link, document, session) = Open(new DashboardLayout(180, [new ModulePlacement(Guid.NewGuid(), "alex.minecraft", 0, 0, 4, 2, TextSize.Normal, Json("""{"port":25565}"""))]));
        link.SetTypes([.. SampleData.Types(), Hostile_Type()]);
        var picker = new FakeFilePicker();
        var tab = new SettingsTabViewModel(link, session, new PreferencesStore(new EditorPreferences(), System.IO.Path.Combine(_dir, "p.json")), SystemServices.None, picker);
        var shared = System.IO.Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(shared, LayoutTransfer.Export(new DashboardLayout(180, [document.Current.Placements[0] with { Settings = Json("""{"port":3389}""") }])));
        picker.Result = shared;

        await tab.ImportAsync();

        Assert.Contains("Réglages gardés pour Minecraft", tab.ShareMessage);
        // Caract\u00E8re par caract\u00E8re : la comparaison culturelle d'une cha\u00EEne ignore les caract\u00E8res invisibles.
        Assert.False(tab.ShareMessage!.Contains('\u202E') || tab.ShareMessage.Contains('\u200B'));
    }

    [Fact]
    public async Task Import_TooSmallMessage_ShowsTheCleanedNameOfAModule()
    {
        var (link, document, session) = Open(new DashboardLayout(180, []));
        link.SetTypes([.. SampleData.Types(), Hostile_Type([new NumberField("n", "N", 1, 5, 2)], min: new GridSize(4, 2))]);
        var picker = new FakeFilePicker();
        var tab = new SettingsTabViewModel(link, session, new PreferencesStore(new EditorPreferences(), System.IO.Path.Combine(_dir, "p.json")), SystemServices.None, picker);
        var shared = System.IO.Path.Combine(_dir, "ami.trofeo.json");
        File.WriteAllText(shared, LayoutTransfer.Export(new DashboardLayout(180, [new ModulePlacement(Guid.NewGuid(), "alex.minecraft", 0, 0, 2, 1, TextSize.Normal, Json("{}"))])));
        picker.Result = shared;

        await tab.ImportAsync();

        Assert.True(tab.ShareMessageIsError);
        Assert.Equal("« Minecraft » est plus petit que son minimum (4 × 2).", tab.ShareMessage); // exact : StartsWith ignorerait les caractères invisibles
        _ = document;
    }

    [Fact]
    public void FirstRun_ListsACommunityTypeWithACleanedNameAndTheBadge()
    {
        var link = new FakeServiceLink();
        var welcome = SampleData.Welcome() with { Types = [.. SampleData.Types(), Hostile_Type()] };
        link.Set(LinkState.Connected, welcome, SampleData.Status());
        var store = new LayoutStore(System.IO.Path.Combine(_dir, "layout.json"));
        store.Save(DashboardLayout.Default);
        var document = new LayoutDocument(store, link);
        document.Load();
        var model = new MainViewModel(link, document, new EditorPreferences(FirstRunDone: false), null, ["Arial"]);
        _disposables.Add(model);

        var item = model.FirstRun.Modules.Single(m => m.TypeId == "alex.minecraft");

        Assert.Equal("Minecraft", item.Name);
        Assert.True(item.IsCommunity);
        Assert.All(model.FirstRun.Modules.Where(m => m.TypeId != "alex.minecraft"), m => Assert.False(m.IsCommunity));
    }

    [Fact]
    public void Shortcuts_TargetNameOfACommunityType_IsCleaned()
    {
        var layout = DashboardLayout.Default with
        {
            Placements = [new ModulePlacement(Guid.NewGuid(), "alex.minecraft", 0, 0, 4, 2, TextSize.Normal, Json("{}"))],
        };
        var (link, _, session) = Open(layout);
        link.Welcome = SampleData.Welcome() with { Types = [.. SampleData.Types(), Hostile_Type(commands: true)] };
        var tab = new ShortcutsTabViewModel(link, session);
        _disposables.Add(tab);

        var names = tab.Rows.SelectMany(r => r.Targets).Select(t => t.Name).Distinct().ToList();

        Assert.Contains("Minecraft", names);
        Assert.DoesNotContain(names, n => n.Contains('\u202E') || n.Contains('\u200B'));
    }

    // ---- M1 : une boîte sans problème après nettoyage ----

    [Fact]
    public async Task Dialog_ErrorsThatShrinkToNothing_StillHideTheInstallButton()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(errors: ["\u202E", "\u200B\u0007"], conflict: "\u2028");

        await fixture.Section.OpenPackageAsync(Path);

        var dialog = fixture.Section.Dialog!;
        Assert.True(dialog.HasProblems);
        Assert.False(dialog.CanConfirm);
        Assert.Equal(["Ce paquet n'est pas valide."], dialog.Problems);
        Assert.Empty(dialog.Permissions);
        await dialog.ConfirmAsync();
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("install"));
    }

    [Fact]
    public async Task Dialog_SomeErrorsEmptyAfterCleaning_KeepsTheOthers()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Package = Package(errors: ["\u200B", "Archive illisible."]);

        await fixture.Section.OpenPackageAsync(Path);

        Assert.Equal(["Archive illisible."], fixture.Section.Dialog!.Problems);
    }

    [Fact]
    public async Task Update_PreparationRefusedWithoutAMessage_SaysItCouldNotBePrepared()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0"));
        await fixture.Section.RefreshAsync();
        fixture.Link.Preparation = new ModuleUpdatePreparation(false, "", null, null, [], []);

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(["La mise à jour n'a pas pu être préparée."], dialog.Problems);
        Assert.True(dialog.HasProblems);
        Assert.False(dialog.CanConfirm);
    }

    [Fact]
    public async Task Update_PreparationRefusedWithANullMessage_SaysItCouldNotBePrepared()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0"));
        await fixture.Section.RefreshAsync();
        fixture.Link.Preparation = new ModuleUpdatePreparation(false, null, null, null, [], []);

        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["La mise à jour n'a pas pu être préparée."], fixture.Section.Dialog!.Problems);
    }

    // ---- M3 : annuler pendant une installation ----

    [Fact]
    public async Task Dialog_CancelWhileTheServiceInstalls_DoesNothing_AndEscapeToo()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);
        var dialog = fixture.Section.Dialog!;
        var gate = new TaskCompletionSource();
        fixture.Link.ModuleGate = gate.Task;

        var installing = dialog.ConfirmAsync();

        Assert.True(dialog.IsBusy);
        Assert.False(dialog.CanCancel);
        await dialog.CancelAsync();            // Annuler (ou Échap : même appel) ne ferme rien
        Assert.Same(dialog, fixture.Section.Dialog);
        gate.SetResult();
        await installing;
        Assert.Null(fixture.Section.Dialog);   // l'installation a abouti, la boîte se ferme d'elle-même
        Assert.Equal($"install {Path} ", fixture.LastChange);
    }

    [Fact]
    public async Task Dialog_CancelWhileAnUpdateInstalls_DoesNotCancelTheUpdate()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0"));
        await fixture.Section.RefreshAsync();
        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        var dialog = fixture.Section.Dialog!;
        var gate = new TaskCompletionSource();
        fixture.Link.ModuleGate = gate.Task;

        var installing = dialog.ConfirmAsync();
        await dialog.CancelAsync();

        Assert.Same(dialog, fixture.Section.Dialog);
        Assert.DoesNotContain("cancel alex.demo", fixture.Link.ModuleCalls);
        gate.SetResult();
        await installing;
    }

    [Fact]
    public async Task Dialog_CancelIsAvailableAgainAfterARefusedInstall()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);
        var dialog = fixture.Section.Dialog!;
        fixture.Link.ModuleResult = new ActionResult(false, "Refusé.");

        await dialog.ConfirmAsync();

        Assert.True(dialog.CanCancel);
        await dialog.CancelAsync();
        Assert.Null(fixture.Section.Dialog);
    }

    [Fact]
    public async Task Dispose_WhileTheServiceInstalls_IsStillPossible()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0"));
        await fixture.Section.RefreshAsync();
        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);
        fixture.Link.ModuleGate = new TaskCompletionSource().Task;
        _ = fixture.Section.Dialog!.ConfirmAsync();

        fixture.Section.Dispose(); // la fermeture de la fenêtre n'attend personne

        Assert.Contains("cancel alex.demo", fixture.Link.ModuleCalls);
    }

    // ---- M4 : deux ouvertures simultanées ----

    [Fact]
    public async Task OpenPackage_SecondCallFinishingAfterADialogIsOpen_DoesNotReplaceIt_AndOpensWhenItCloses()
    {
        using var fixture = new CommunityFixture();
        var slow = new TaskCompletionSource<PackageInfo>();
        fixture.Link.OnInspect = path => path == "A.avmod" ? slow.Task : Task.FromResult(Package(name: "B"));

        var first = fixture.Section.OpenPackageAsync("A.avmod");    // l'inspection de A traîne
        await fixture.Section.OpenPackageAsync("B.avmod");          // B s'ouvre
        var dialogB = fixture.Section.Dialog!;
        Assert.Equal("B", dialogB.Name);

        slow.SetResult(Package(name: "A"));
        await first;

        Assert.Same(dialogB, fixture.Section.Dialog);               // A ne remplace pas B
        fixture.Link.OnInspect = _ => Task.FromResult(Package(name: "A"));
        await dialogB.CancelAsync();
        Assert.Equal("A", fixture.Section.Dialog!.Name);            // mais elle n'est pas perdue
    }

    [Fact]
    public async Task OpenPackageWhenConnected_WithADialogAlreadyOpen_KeepsThePathForWhenItCloses()
    {
        using var fixture = new CommunityFixture(connected: false).WithModules(Module(state: LinkCommunityState.NeedsApproval));
        await fixture.Section.RefreshAsync();
        fixture.Section.Modules.Single().ApproveCommand.Execute(null);
        var approval = fixture.Section.Dialog!;
        fixture.Section.OpenPackageWhenConnected(Path);

        fixture.Link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());

        Assert.Same(approval, fixture.Section.Dialog);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("inspect"));
        await approval.CancelAsync();
        Assert.Equal(["inspect " + Path], fixture.Link.ModuleCalls.Where(c => c.StartsWith("inspect")));
        Assert.Equal(ModuleDialogMode.Install, fixture.Section.Dialog!.Mode);
    }

    // ---- M5 ----

    [Fact]
    public async Task Dispose_WithAnUpdateDialogOpen_TellsTheService()
    {
        using var fixture = new CommunityFixture().WithModules(Module(updateVersion: "1.3.0"));
        await fixture.Section.RefreshAsync();
        await fixture.Section.Modules.Single().UpdateCommand.ExecuteAsync(null);

        fixture.Section.Dispose();

        Assert.Equal("cancel alex.demo", fixture.Link.ModuleCalls.Last());
    }

    [Fact]
    public async Task Dispose_WithAnInstallDialogOpen_SendsNoCancel()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.OpenPackageAsync(Path);

        fixture.Section.Dispose();

        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("cancel"));
    }

    [Fact]
    public async Task Section_ErrorOfAFailedRead_GoesAwayAfterASuccessfulRead()
    {
        using var fixture = new CommunityFixture().WithModules(Module());
        fixture.Link.CommunityFailure = new ControlException("Le service d'affichage ne répond pas.");

        await fixture.Section.RefreshAsync();
        Assert.Equal("Le service d'affichage ne répond pas.", fixture.Section.Message);

        fixture.Link.CommunityFailure = null;
        await fixture.Section.RefreshAsync();

        Assert.Null(fixture.Section.Message);
        Assert.Single(fixture.Section.Modules);
    }

    [Fact]
    public async Task Section_ErrorOfARefusedRequest_SurvivesTheReadThatFollowsIt()
    {
        using var fixture = new CommunityFixture().WithModules(Module(state: LinkCommunityState.SetAside));
        await fixture.Section.RefreshAsync();
        fixture.Link.ModuleResult = new ActionResult(false, "Les fichiers du module sont verrouillés.");

        await fixture.Section.Modules.Single().RelaunchCommand.ExecuteAsync(null);

        Assert.Equal("Les fichiers du module sont verrouillés.", fixture.Section.Message);
    }

    // ---- M7 : la ligne de commande ne touche que le disque local ----

    private string TempAvmod(string name = "m.avmod")
    {
        var file = System.IO.Path.Combine(_dir, name);
        File.WriteAllText(file, "x");
        return file;
    }

    [Fact]
    public void PackageArgument_Find_AcceptsAnExistingLocalFile_InAnyCase()
    {
        var file = TempAvmod("Y.AVMOD");

        Assert.Equal(file, PackageArgument.Find([file]));
        Assert.Equal(file, PackageArgument.Find(["--x", file]));
    }

    [Fact]
    public void PackageArgument_Find_AcceptsAnyCaseAndQuotes()
    {
        var file = TempAvmod("x y.avmod");

        Assert.Equal(file, PackageArgument.Find(["\"" + file + "\""]));
        Assert.Equal(file, PackageArgument.Find(["  \"" + file + "\"  "]));
    }

    [Fact]
    public void PackageArgument_Find_RefusesAFileThatDoesNotExist()
    {
        Assert.Null(PackageArgument.Find([System.IO.Path.Combine(_dir, "absent.avmod")]));
        Assert.Null(PackageArgument.Find([_dir + "\\"]));
        Assert.Null(PackageArgument.Find(null));
        Assert.Null(PackageArgument.Find(["bad\0name.avmod"]));
        Assert.Null(PackageArgument.Find(["module.zip"]));
    }

    [Theory]
    [InlineData(@"\\hote\partage\x.avmod")]
    [InlineData(@"//hote/partage/x.avmod")]
    [InlineData(@"\\?\UNC\hote\partage\x.avmod")]
    [InlineData(@"\\.\UNC\hote\partage\x.avmod")]
    [InlineData(@"\\127.0.0.1\c$\x.avmod")]
    public void PackageArgument_Find_RefusesNetworkPaths_WithoutTouchingTheDisk(string arg)
    {
        var touched = new List<string>();

        var found = PackageArgument.Find([arg], path => { touched.Add("exists " + path); return true; }, path => { touched.Add("drive " + path); return DriveType.Fixed; });

        Assert.Null(found);
        Assert.Empty(touched); // pas un accès : la fenêtre ne se connecte à aucun hôte
    }

    [Fact]
    public void PackageArgument_Find_RefusesAMappedNetworkDrive_BeforeLookingForTheFile()
    {
        var touched = new List<string>();

        var found = PackageArgument.Find([@"Z:\modules\x.avmod"], path => { touched.Add("exists"); return true; }, path => DriveType.Network);

        Assert.Null(found);
        Assert.Empty(touched);
    }

    [Fact]
    public void PackageArgument_Find_AcceptsAFixedOrRemovableDrive()
    {
        Assert.Equal(@"D:\modules\x.avmod", PackageArgument.Find([@"D:\modules\x.avmod"], _ => true, _ => DriveType.Fixed));
        Assert.Equal(@"E:\x.avmod", PackageArgument.Find([@"E:\x.avmod"], _ => true, _ => DriveType.Removable));
    }
}
