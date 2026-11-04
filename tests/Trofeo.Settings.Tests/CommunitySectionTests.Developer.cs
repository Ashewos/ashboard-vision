using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;
using static Trofeo.Settings.Tests.CommunityFixture;

namespace Trofeo.Settings.Tests;

// Le dossier des modules, le mode développeur et la consommation.
public sealed partial class CommunitySectionTests
{
    private const string DevPath = @"C:\dev\mon-module";
    private const string OtherDevPath = @"C:\dev\autre";

    private static CommunityModuleInfo DevModule(string id = "alex.dev", string path = DevPath, ModulePermission[]? granted = null, string name = "Module en cours") =>
        Module(id, version: "0.1.0", fromDevFolder: true, name: name, granted: granted ?? [ModulePermission.Internet], devFolderPath: path);

    private static CommunityFixture WithDeveloperMode(CommunityFixture fixture, IReadOnlyList<CommunityModuleInfo> modules, params string[] folders)
    {
        fixture.Link.Community = new CommunityOverview(modules, @"C:\Modules", DeveloperMode: true, folders, StoreTampered: false);
        return fixture;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "La condition attendue ne s'est pas produite.");
    }

    // ---- Le dossier des modules ----

    [Fact]
    public async Task ModulesFolder_IsShownAsTheServiceGivesIt_Cleaned()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Community = new CommunityOverview([], "D:\\Mes\u202E modules\u0007", false, [], false);

        await fixture.Section.RefreshAsync();

        Assert.Equal("D:\\Mes modules", fixture.Section.ModulesRoot);
    }

    [Fact]
    public async Task ChangeModulesFolder_PicksAFolderAndAsksTheService_ThenReadsAgain()
    {
        using var fixture = new CommunityFixture();
        fixture.Picker.FolderResult = @"D:\Mes modules";

        await fixture.Section.ChangeModulesFolderAsync();

        Assert.Equal(1, fixture.Picker.FolderCalls);
        Assert.Equal(@"root D:\Mes modules", fixture.LastChange);
        await WaitUntilAsync(() => fixture.Section.ModulesRoot == @"D:\Mes modules"); // la liste est relue
    }

    [Fact]
    public async Task ChangeModulesFolder_PickerCancelled_AsksNothing()
    {
        using var fixture = new CommunityFixture();
        fixture.Picker.FolderResult = null;

        await fixture.Section.ChangeModulesFolderAsync();

        Assert.Empty(fixture.Link.ModuleCalls);
    }

    [Fact]
    public async Task ChangeModulesFolder_Refused_ShowsTheMessageAsIs_AndKeepsTheSection()
    {
        using var fixture = new CommunityFixture().WithModules(Module());
        await fixture.Section.RefreshAsync();
        fixture.Picker.FolderResult = @"C:\";
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Choisis un dossier, pas la racine d'un disque.\u0007");

        await fixture.Section.ChangeModulesFolderAsync();

        Assert.Equal("Choisis un dossier, pas la racine d'un disque.", fixture.Section.Message); // nettoyé, sans texte de remplacement
        Assert.Null(fixture.Section.Dialog);
        Assert.Single(fixture.Section.Modules);
    }

    // ---- Le mode développeur ----

    [Fact]
    public async Task DeveloperModeOn_ShowsBanner()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.RefreshAsync();
        Assert.False(fixture.Section.DeveloperMode);
        Assert.False(fixture.Section.ShowsDeveloperBanner);

        await fixture.Section.SetDeveloperModeAsync(true);

        Assert.Equal("dev True", fixture.LastChange);
        await WaitUntilAsync(() => fixture.Section.DeveloperMode);
        Assert.True(fixture.Section.ShowsDeveloperBanner);
        Assert.Equal("Mode développeur actif", fixture.Section.DeveloperBannerText);
    }

    [Fact]
    public async Task DeveloperModeSwitch_Refused_ComesBackAndSaysWhy()
    {
        using var fixture = new CommunityFixture();
        await fixture.Section.RefreshAsync();
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "La liste des modules validés est illisible.");
        var changed = new List<string?>();
        fixture.Section.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await fixture.Section.SetDeveloperModeAsync(true);

        Assert.False(fixture.Section.DeveloperMode);
        Assert.False(fixture.Section.ShowsDeveloperBanner);
        Assert.Equal("La liste des modules validés est illisible.", fixture.Section.Message);
        Assert.Contains(nameof(CommunitySectionViewModel.DeveloperMode), changed); // l'interrupteur revient à sa place
    }

    [Fact]
    public async Task DeveloperModeOff_ReadsTheListAgain_DevModulesDisappear()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [Module(), DevModule()], DevPath);
        await fixture.Section.RefreshAsync();
        Assert.Equal(2, fixture.Section.Modules.Count);
        var reads = fixture.Link.ModuleCalls.Count(c => c == "community");

        await fixture.Section.SetDeveloperModeAsync(false);

        await WaitUntilAsync(() => fixture.Section.Modules.Count == 1);
        Assert.True(fixture.Link.ModuleCalls.Count(c => c == "community") > reads);
        Assert.Equal("alex.demo", fixture.Section.Modules.Single().Id);
        Assert.False(fixture.Section.ShowsDeveloperBanner);
    }

    [Fact]
    public async Task DevFolders_AreListedWithTheirModule_AFolderWithoutModuleCannotReload()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(path: DevPath.ToUpperInvariant())], DevPath, OtherDevPath);

        await fixture.Section.RefreshAsync();

        var folders = fixture.Section.DevFolders;
        Assert.Equal([DevPath, OtherDevPath], folders.Select(f => f.PathText));
        Assert.Equal("Module en cours", folders[0].ModuleName);   // le même chemin, sans tenir compte de la casse
        Assert.True(folders[0].CanReloadNow);
        Assert.Equal("", folders[1].ModuleName);
        Assert.False(folders[1].CanReloadNow);                    // pas de module : « Recharger » grisé
        Assert.True(folders[1].CanRemoveNow);                     // mais « Retirer » marche
        Assert.True(fixture.Section.HasDevFolders);
    }

    [Fact]
    public async Task DevFolders_TextIsCleaned_ButTheServiceGetsTheRawPath()
    {
        const string raw = "C:\\dev\\mo\u202Edule";
        using var fixture = WithDeveloperMode(new CommunityFixture(), [], raw);
        await fixture.Section.RefreshAsync();
        var row = fixture.Section.DevFolders.Single();

        Assert.Equal(@"C:\dev\module", row.PathText);
        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Equal("removeDev " + raw, fixture.LastChange);
        await WaitUntilAsync(() => fixture.Section.DevFolders.Count == 0);
    }

    // ---- Ajouter un dossier de travail ----

    [Fact]
    public async Task AddDevFolder_AsksPermissions()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), []);
        await fixture.Section.RefreshAsync();
        fixture.Picker.FolderResult = DevPath;

        await fixture.Section.AddDevFolderAsync();

        // Le service lit le manifeste (sans rien lancer) ; rien n'est ajouté tant que l'utilisateur n'a pas confirmé.
        var dialog = Assert.IsType<InstallModuleDialogViewModel>(fixture.Section.Dialog);
        Assert.Equal("Tester un module en développement", dialog.Title);
        Assert.True(dialog.ShowsWarning);
        Assert.Equal(2, dialog.Permissions.Count);
        Assert.All(dialog.Permissions, p => Assert.False(p.IsChecked));  // un nouveau dossier : tout est décoché
        Assert.Contains("inspectDev " + DevPath, fixture.Link.ModuleCalls);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("addDev"));

        dialog.Permissions[0].IsChecked = true;
        await dialog.ConfirmAsync();

        Assert.Equal($"addDev {DevPath} Internet", fixture.LastChange);
        Assert.Null(fixture.Section.Dialog);
    }

    [Fact]
    public async Task AddDevFolder_Cancel_AddsNothing()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), []);
        fixture.Picker.FolderResult = DevPath;
        await fixture.Section.AddDevFolderAsync();

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("addDev"));
    }

    [Fact]
    public async Task AddDevFolder_WithErrors_ShowsThemAndHasNoConfirmButton()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), []);
        fixture.Picker.FolderResult = DevPath;
        fixture.Link.DevFolder = new PackageInfo(
            "alex.dev", "Démo", "Alex", "0.1.0", "x", [ModulePermission.Internet],
            ["Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant.\u0007"], null, null);

        await fixture.Section.AddDevFolderAsync();

        var dialog = fixture.Section.Dialog!;
        Assert.Equal(["Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant."], dialog.Problems);
        Assert.False(dialog.CanConfirm);
        Assert.Empty(dialog.Permissions);
        await dialog.ConfirmAsync();
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("addDev"));
    }

    [Fact]
    public async Task AddDevFolder_AlreadyListed_KeepsWhatWasGranted_AndFlagsTheRest()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(granted: [ModulePermission.Internet])], DevPath);
        await fixture.Section.RefreshAsync();
        fixture.Picker.FolderResult = DevPath;

        await fixture.Section.AddDevFolderAsync();

        var dialog = fixture.Section.Dialog!;
        Assert.Equal([true, false], dialog.Permissions.Select(p => p.IsChecked));    // R22 : l'accordé reste coché, le reste décoché
        Assert.Equal([false, true], dialog.Permissions.Select(p => p.IsNew));        // et signalé
    }

    [Fact]
    public async Task AddDevFolder_PickerCancelled_OrServiceCannotRead_DoesNotOpenADialog()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), []);
        fixture.Picker.FolderResult = null;
        await fixture.Section.AddDevFolderAsync();
        Assert.Null(fixture.Section.Dialog);

        fixture.Picker.FolderResult = DevPath;
        fixture.Link.OnInspectDevFolder = _ => Task.FromException<PackageInfo>(new ControlException("Le service ne répond pas."));
        await fixture.Section.AddDevFolderAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Equal("Le service ne répond pas.", fixture.Section.Message);
    }

    // ---- Recharger ----

    [Fact]
    public async Task Reload_CallsService()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(granted: [ModulePermission.Internet, ModulePermission.Sensors])], DevPath);
        await fixture.Section.RefreshAsync();

        await fixture.Section.DevFolders.Single().ReloadCommand.ExecuteAsync(null);

        // Le manifeste est relu d'abord ; rien de plus demandé à l'utilisateur, puis le module est rechargé (avec son identifiant brut).
        Assert.Null(fixture.Section.Dialog);
        Assert.Equal(["inspectDev " + DevPath, "reloadDev alex.dev"], fixture.Link.ModuleCalls.Where(c => c != "community"));
    }

    [Fact]
    public async Task Reload_NewPermission_AsksFirst_ThenAddsTheFolderAndReloads()
    {
        // Le dossier déclare maintenant Internet et Capteurs ; seule Internet est accordée.
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(granted: [ModulePermission.Internet])], DevPath);
        await fixture.Section.RefreshAsync();

        await fixture.Section.DevFolders.Single().ReloadCommand.ExecuteAsync(null);

        var dialog = Assert.IsType<InstallModuleDialogViewModel>(fixture.Section.Dialog);
        Assert.Equal("Tester un module en développement", dialog.Title);
        Assert.Equal([true, false], dialog.Permissions.Select(p => p.IsChecked));  // l'accordé pré-coché, la nouvelle décochée
        Assert.Equal([false, true], dialog.Permissions.Select(p => p.IsNew));      // et signalée
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("reloadDev") || c.StartsWith("addDev"));

        dialog.Permissions[1].IsChecked = true;
        await dialog.ConfirmAsync();

        Assert.Equal(["inspectDev " + DevPath, $"addDev {DevPath} Internet,Sensors", "reloadDev alex.dev"], fixture.Link.ModuleCalls.Where(c => c != "community"));
        Assert.Null(fixture.Section.Dialog);
    }

    [Fact]
    public async Task Reload_NewPermission_Cancel_ReloadsNothing()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(granted: [ModulePermission.Internet])], DevPath);
        await fixture.Section.RefreshAsync();
        await fixture.Section.DevFolders.Single().ReloadCommand.ExecuteAsync(null);

        await fixture.Section.Dialog!.CancelAsync();

        Assert.Null(fixture.Section.Dialog);
        Assert.Equal(["inspectDev " + DevPath], fixture.Link.ModuleCalls.Where(c => c != "community"));
    }

    [Fact]
    public async Task Reload_InspectionErrors_ShowTheMessage_AndReloadNothing()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule()], DevPath);
        await fixture.Section.RefreshAsync();
        fixture.Link.DevFolder = new PackageInfo(null, null, null, null, null, [], ["Le fichier manifest.json est introuvable dans le dossier du module."], null, null);

        await fixture.Section.DevFolders.Single().ReloadCommand.ExecuteAsync(null);

        Assert.Equal("Le fichier manifest.json est introuvable dans le dossier du module.", fixture.Section.Message);
        Assert.Null(fixture.Section.Dialog);
        Assert.DoesNotContain(fixture.Link.ModuleCalls, c => c.StartsWith("reloadDev") || c.StartsWith("addDev"));
    }

    [Fact]
    public async Task Reload_ServiceRefusal_ShowsItAsIs()
    {
        using var fixture = WithDeveloperMode(new CommunityFixture(), [DevModule(granted: [ModulePermission.Internet, ModulePermission.Sensors])], DevPath);
        await fixture.Section.RefreshAsync();
        fixture.Link.ModuleResult = new Trofeo.ModuleKit.ActionResult(false, "Le module ne peut pas tourner pour le moment.");

        await fixture.Section.DevFolders.Single().ReloadCommand.ExecuteAsync(null);

        Assert.Equal("Le module ne peut pas tourner pour le moment.", fixture.Section.Message);
    }

    // ---- La consommation ----

    private static int Reads(CommunityFixture fixture) => fixture.Link.ModuleCalls.Count(c => c == "consumption");

    private static async Task AdvanceUntilAsync(CommunityFixture fixture, Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "La lecture attendue n'a pas eu lieu.");
    }

    [Fact]
    public async Task Consumption_PolledOnlyWhileVisible()
    {
        using var fixture = new CommunityFixture();
        var consumption = fixture.Section.Consumption;

        // Rien n'est affiché : ni requête ni minuteur, quel que soit le temps qui passe.
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, Reads(fixture));
        Assert.False(consumption.IsPolling);

        // L'onglet s'affiche : une lecture tout de suite, puis toutes les 5 s.
        consumption.SetTabShown(true);
        Assert.True(consumption.IsPolling);
        Assert.Equal(1, Reads(fixture));
        fixture.Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(1, Reads(fixture));                     // pas avant 5 s
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Reads(fixture) == 2);
        await AdvanceUntilAsync(fixture, () => Reads(fixture) >= 3);

        // La fenêtre est réduite : plus de minuteur, plus de requête.
        consumption.SetWindowShown(false);
        Assert.False(consumption.IsPolling);
        var frozen = Reads(fixture);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(frozen, Reads(fixture));

        // Elle revient : relue tout de suite.
        consumption.SetWindowShown(true);
        Assert.True(consumption.IsPolling);
        await WaitUntilAsync(() => Reads(fixture) == frozen + 1);

        // Un autre onglet : plus rien.
        consumption.SetTabShown(false);
        Assert.False(consumption.IsPolling);
        var other = Reads(fixture);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(other, Reads(fixture));
    }

    [Fact]
    public async Task Consumption_FoldedSection_AsksNothing()
    {
        using var fixture = new CommunityFixture();
        var consumption = fixture.Section.Consumption;
        consumption.IsExpanded = false;

        consumption.SetTabShown(true);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(consumption.IsPolling);
        Assert.Equal(0, Reads(fixture));

        consumption.IsExpanded = true;                       // dépliée : relue tout de suite
        Assert.True(consumption.IsPolling);
        Assert.Equal(1, Reads(fixture));
        consumption.IsExpanded = false;
        Assert.False(consumption.IsPolling);
    }

    [Fact]
    public async Task Consumption_ServiceNotConnected_AsksNothing_UntilItIs()
    {
        using var fixture = new CommunityFixture(connected: false);
        var consumption = fixture.Section.Consumption;

        consumption.SetTabShown(true);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(consumption.IsPolling);
        Assert.Equal(0, Reads(fixture));

        fixture.Link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());

        Assert.True(consumption.IsPolling);
        Assert.Equal(1, Reads(fixture));

        fixture.Link.Set(LinkState.ServiceStopped);
        Assert.False(consumption.IsPolling);
    }

    [Fact]
    public async Task Consumption_Dispose_StopsPolling()
    {
        var fixture = new CommunityFixture();
        fixture.Section.Consumption.SetTabShown(true);
        Assert.True(fixture.Section.Consumption.IsPolling);

        fixture.Section.Dispose();
        var reads = Reads(fixture);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(fixture.Section.Consumption.IsPolling);
        Assert.Equal(reads, Reads(fixture));
        fixture.Dispose();
    }

    [Fact]
    public void Consumption_HighShowsOrangeMarker()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Consumption.AddRange(
        [
            new ModuleConsumption("originaux", "Modules d'origine", IsCommunity: false, CpuPercent: 0.4, MemoryBytes: 44L * 1024 * 1024, High: false),
            new ModuleConsumption("alex.camera", "Caméra", IsCommunity: true, CpuPercent: 3.26, MemoryBytes: 180L * 1024 * 1024, High: true),
            new ModuleConsumption("bob.meteo", "Météo", IsCommunity: true, CpuPercent: 0.04, MemoryBytes: 30L * 1024 * 1024, High: false),
        ]);

        fixture.Section.Consumption.SetTabShown(true);

        var rows = fixture.Section.Consumption.Rows;
        Assert.Equal(["Modules d'origine", "Caméra", "Météo"], rows.Select(r => r.Name));
        Assert.Equal([false, true, false], rows.Select(r => r.IsHigh));
        Assert.Equal("Consommation élevée", rows[1].HighText);
        // La couleur orange est celle du jeton Amber de la fenêtre (classe « warning ») : le repère n'est lié qu'à High.
        Assert.Equal(["0,4 %", "3,3 %", "0,0 %"], rows.Select(r => r.CpuText));
        Assert.Equal(["44 Mo", "180 Mo", "30 Mo"], rows.Select(r => r.MemoryText));
    }

    [Fact]
    public void Consumption_TextFromTheService_IsCleaned_AndTheIdStandsInForAnEmptyName()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Consumption.AddRange(
        [
            new ModuleConsumption("alex.camera", "Cam\u202Eéra\u0007", true, 1, 1024 * 1024, false),
            new ModuleConsumption("bob.meteo", "\u200B", true, 1, 1024 * 1024, false),
        ]);

        fixture.Section.Consumption.SetTabShown(true);

        Assert.Equal(["Caméra", "bob.meteo"], fixture.Section.Consumption.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Consumption_ReadFailure_KeepsTheRowsAndSaysSo()
    {
        using var fixture = new CommunityFixture();
        fixture.Link.Consumption.Add(new ModuleConsumption("originaux", "Modules d'origine", false, 1, 1024 * 1024, false));
        fixture.Section.Consumption.SetTabShown(true);
        Assert.Single(fixture.Section.Consumption.Rows);

        fixture.Link.ConsumptionFailure = new ControlException("Le service ne répond pas.\u0007");
        await AdvanceUntilAsync(fixture, () => fixture.Section.Consumption.HasFailure);

        Assert.Single(fixture.Section.Consumption.Rows);
        Assert.Equal("Le service ne répond pas.", fixture.Section.Consumption.Failure);
        fixture.Link.ConsumptionFailure = null;
        await AdvanceUntilAsync(fixture, () => !fixture.Section.Consumption.HasFailure);
    }
}
