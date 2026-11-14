using System.IO.Compression;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Core.Rendering;
using Trofeo.Core.Secrets;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Tout ce qu'il faut autour d'un <see cref="CommunityModules"/> de test : des dossiers temporaires (jamais ceux de l'utilisateur), une
/// liste scellée à soi, un faux lanceur qui compte les démarrages d'hôtes, un coffre à secrets réel dans un fichier temporaire, et
/// l'enregistrement de ce que le service publie dans son registre.
/// </summary>
internal sealed class CommunityRig : IAsyncDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "av-community-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<IModule> _cases = [];
    private readonly object _publishedGate = new();
    private IReadOnlyList<IModuleFactory> _published = [];
    private int _changes;

    public CommunityRig(bool autoShutdown = true, HttpClient? updateHttp = null, Func<string, bool?>? volumeHasAcls = null)
    {
        Directory.CreateDirectory(Root);
        Store = new ApprovedModulesStore(Path.Combine(_work, "settings", "modules.json"), Path.Combine(_work, "settings", "modules.key"), _ => { });
        Store.Save(ApprovedModules.Empty with { ModulesRoot = Root });
        Vault = new DpapiSecretVault(Path.Combine(_work, "settings", "secrets.dat"));
        if (autoShutdown)
        {
            Launcher.OnMessage = (host, message) =>
            {
                if (message is Shutdown)
                {
                    host.Exit(0); // un hôte poli : l'arrêt demandé par le service ne prend pas les trois secondes de grâce
                }
            };
        }

        Community = new CommunityModules(
            Store,
            factories =>
            {
                lock (_publishedGate)
                {
                    _published = factories;
                }
            },
            Vault.Scope,
            Vault.RemoveScope,
            Data,
            @"C:\inconnu\Trofeo.ModuleHost.exe",
            Time,
            Launcher,
            id => Profiles.Add(id),
            (id, directory) => Grants.Add((id, directory)),
            updateHttp,
            volumeHasAcls ?? (_ => true));
        Community.Changed += (_, _) => Interlocked.Increment(ref _changes);
    }

    public string Root => Path.Combine(_work, "modules");

    public ApprovedModulesStore Store { get; }

    public DpapiSecretVault Vault { get; }

    public FakeHostFactory Launcher { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    public FakeDataBroker Data { get; } = new();

    public CommunityModules Community { get; }

    public List<string> Profiles { get; } = [];

    public List<(string Id, string Directory)> Grants { get; } = [];

    public int Changes => Volatile.Read(ref _changes);

    public IReadOnlyList<IModuleFactory> Published
    {
        get
        {
            lock (_publishedGate)
            {
                return _published;
            }
        }
    }

    public ApprovedModules Approved => Store.Load().Modules;

    public string NewFolder(string? name = null)
    {
        var folder = Path.Combine(_work, name ?? "dossier-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string ManifestJson(string id, string version, IReadOnlyList<ModulePermission> declared, string? repository, string entry = "Mod.dll") => $$"""
        {
          "id": "{{id}}",
          "name": "Module de test",
          "description": "Un module pour les tests.",
          "author": "Test",
          "version": "{{version}}",
          "sdk": "1.0",
          "entry": "{{entry}}",
          "permissions": [{{string.Join(", ", declared.Select(p => $"\"{ModulePermissions.ToManifest(p)}\""))}}],
          {{(repository is null ? "" : $"\"repository\": \"{repository}\",")}}
          "minSize": { "columns": 2, "rows": 1 }
        }
        """;

    /// <summary>Un <c>.avmod</c> fabriqué dans un dossier de travail.</summary>
    public string Package(
        string id = "alex.demo", string version = "1.0.0", IReadOnlyList<ModulePermission>? declared = null, string? repository = "alex/demo",
        string content = "code")
    {
        var folder = NewFolder();
        File.WriteAllText(Path.Combine(folder, "manifest.json"), ManifestJson(id, version, declared ?? [ModulePermission.Internet], repository));
        File.WriteAllText(Path.Combine(folder, "Mod.dll"), content);
        var output = Path.Combine(NewFolder(), $"{id}-{version}.avmod");
        AvmodWriter.Pack(folder, output);
        return output;
    }

    /// <summary>Un module déposé à la main dans le dossier des modules (ni installé, ni validé).</summary>
    public string Drop(string id = "alex.demo", string version = "1.0.0", IReadOnlyList<ModulePermission>? declared = null, string content = "code")
    {
        var folder = Path.Combine(Root, id, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), ManifestJson(id, version, declared ?? [ModulePermission.Internet], "alex/demo"));
        File.WriteAllText(Path.Combine(folder, "Mod.dll"), content);
        return folder;
    }

    public IModuleFactory? Factory(string id) => Published.FirstOrDefault(f => f.Descriptor.TypeId == id);

    /// <summary>Pose une case : la fabrique publiée la crée, et l'hôte du module est lancé si elle est prête à tourner.</summary>
    public IModule Open(string id)
    {
        var factory = Factory(id) ?? throw new InvalidOperationException($"Aucune fabrique publiée pour « {id} ».");
        var module = factory.Create(new ModuleContext(Guid.NewGuid(), JsonDocument.Parse("{}").RootElement.Clone(), Time, new FakeModuleLog(), Vault.Scope(id)));
        _cases.Add(module);
        return module;
    }

    public CommunityEntry Entry(string id) => Community.Entries.Single(e => e.Id == id);

    public async ValueTask DisposeAsync()
    {
        foreach (var module in _cases)
        {
            module.Dispose();
        }

        await Community.DisposeAsync();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[Collection("Hosts")]
public sealed class CommunityModulesTests : IAsyncLifetime
{
    private static readonly ModulePermission[] Internet = [ModulePermission.Internet];
    private static readonly ModulePermission[] InternetAndSensors = [ModulePermission.Internet, ModulePermission.Sensors];

    private readonly List<CommunityRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    private CommunityRig NewRig(bool autoShutdown = true)
    {
        var rig = new CommunityRig(autoShutdown);
        _rigs.Add(rig);
        return rig;
    }

    private static async Task InstallAsync(CommunityRig rig, string package, IReadOnlyList<ModulePermission>? granted = null)
    {
        var result = await rig.Community.InstallAsync(package, granted ?? Internet, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
    }

    // --- Installation -----------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task SendShortcut_UnknownModuleOrNoHostRunning_ReturnsFalse()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());

        Assert.False(rig.Community.SendShortcut("nobody.here", "refresh", [Guid.NewGuid()])); // aucun module de ce nom
        Assert.False(rig.Community.SendShortcut("alex.demo", "refresh", [Guid.NewGuid()])); // installé, mais aucune case : aucun hôte
        Assert.Equal(0, rig.Launcher.Count);
    }

    [WindowsHostFact]
    public async Task Install_ValidPackage_IsReadyAndRegistered()
    {
        var rig = NewRig();
        var changes = rig.Changes;

        var result = await rig.Community.InstallAsync(rig.Package(), Internet, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var directory = Path.Combine(rig.Root, "alex.demo", "1.0.0");
        Assert.True(File.Exists(Path.Combine(directory, "Mod.dll")));
        var approved = Assert.Single(rig.Approved.Modules);
        Assert.Equal(("alex.demo", "1.0.0", directory), (approved.Id, approved.Version, approved.Directory));
        Assert.Equal(ContentHash.Compute(directory), approved.ContentHash); // l'empreinte du dossier tel qu'il est maintenant
        Assert.Equal(Internet, approved.Granted);
        Assert.Equal(Internet, rig.Community.GrantedTo(rig.Entry("alex.demo")));
        Assert.Null(approved.SetAsideReason);
        Assert.Equal("alex/demo", approved.Repository);
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
        Assert.IsType<RemoteModuleFactory>(rig.Factory("alex.demo"));
        Assert.Equal([("alex.demo", directory)], rig.Grants); // la lecture est accordée à son conteneur
        Assert.True(rig.Changes > changes);
        Assert.Equal(0, rig.Launcher.Count); // installer ne lance rien : il faut une case
    }

    [WindowsHostFact]
    public async Task Install_GrantingUndeclaredPermission_IsRefused()
    {
        var rig = NewRig();

        var result = await rig.Community.InstallAsync(rig.Package(declared: Internet), [ModulePermission.Sensors], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Capteurs", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Approved.Modules);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Root)); // rien n'a été écrit
        Assert.Empty(rig.Community.Entries);
    }

    [WindowsHostFact]
    public async Task Install_SameIdOtherRepository_IsConflict()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0", repository: "alex/demo"));
        var other = rig.Package(version: "2.0.0", repository: "pirate/demo");

        var inspection = rig.Community.Inspect(other);
        var result = await rig.Community.InstallAsync(other, Internet, CancellationToken.None);

        Assert.Equal("Un autre module porte déjà cet identifiant", inspection.Conflict);
        Assert.Equal("1.0.0", inspection.Installed?.Version);
        Assert.False(result.Ok);
        Assert.Equal("Un autre module porte déjà cet identifiant", result.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo", "2.0.0")));
        Assert.Equal("1.0.0", Assert.Single(rig.Approved.Modules).Version);
    }

    [WindowsHostFact]
    public async Task Install_SameIdWithoutRepositoryOnEitherSide_IsConflict()
    {
        // La conception n'accepte une mise à jour que si le paquet « nomme le même dépôt » : sans dépôt, on ne peut pas le savoir.
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0", repository: null));

        var inspection = rig.Community.Inspect(rig.Package(version: "2.0.0", repository: null));

        Assert.Equal(CommunityModules.ConflictMessage, inspection.Conflict);
    }

    [WindowsHostFact]
    public async Task Install_NewVersionSameRepository_ReplacesOld()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0"));

        var inspection = rig.Community.Inspect(rig.Package(version: "1.1.0", content: "neuf"));
        Assert.Null(inspection.Conflict);
        Assert.Empty(inspection.Errors);
        Assert.Equal("1.0.0", inspection.Installed?.Version);
        await InstallAsync(rig, rig.Package(version: "1.1.0", content: "neuf"));

        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo", "1.0.0")), "l'ancienne version est supprimée");
        Assert.True(Directory.Exists(Path.Combine(rig.Root, "alex.demo", "1.1.0")));
        Assert.Equal("1.1.0", Assert.Single(rig.Approved.Modules).Version);
        var entry = Assert.Single(rig.Community.Entries);
        Assert.Equal((CommunityState.Ready, "1.1.0"), (entry.State, entry.Manifest?.Version));
    }

    [WindowsHostFact]
    public async Task Install_NewVersionWithNewPermission_NotGrantedUntilChecked()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0", declared: Internet), Internet);

        await InstallAsync(rig, rig.Package(version: "1.1.0", declared: InternetAndSensors, content: "neuf"), Internet);

        var approved = Assert.Single(rig.Approved.Modules);
        Assert.Equal(Internet, approved.Granted); // « capteurs » n'a pas été cochée
        rig.Open("alex.demo");
        var hello = await (await rig.Launcher.WaitForStartAsync()).WaitAsync<Hello>();
        Assert.Equal(Internet, hello.Granted);
    }

    [WindowsHostFact]
    public async Task Install_NewVersion_ClearsSetAside()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0"));
        var approved = Assert.Single(rig.Approved.Modules);
        rig.Store.Save(rig.Approved with { Modules = [approved with { SetAsideReason = "Mis de côté : le module s'est arrêté 3 fois en 10 minutes." }] });
        rig.Community.Refresh();
        Assert.Equal(CommunityState.SetAside, rig.Entry("alex.demo").State);

        await InstallAsync(rig, rig.Package(version: "1.1.0", content: "corrigé"));

        Assert.Null(Assert.Single(rig.Approved.Modules).SetAsideReason);
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
    }

    [WindowsHostFact]
    public async Task Install_SameVersionAlreadyThere_IsRefusedWithoutTouchingIt()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var before = ContentHash.Compute(Path.Combine(rig.Root, "alex.demo", "1.0.0"));

        var result = await rig.Community.InstallAsync(rig.Package(content: "autre"), Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("déjà", result.Message);
        Assert.Equal(before, ContentHash.Compute(Path.Combine(rig.Root, "alex.demo", "1.0.0")));
    }

    // --- Un paquet hostile -------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Install_PackageWithPathTraversal_IsRefusedBeforeAnythingIsWritten()
    {
        var rig = NewRig();
        var package = Path.Combine(rig.NewFolder(), "piege.avmod");
        using (var stream = File.Create(package))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", CommunityRig.ManifestJson("alex.demo", "1.0.0", Internet, null));
            Write(zip, "Mod.dll", "code");
            Write(zip, "..\\..\\evil.txt", "dehors");
        }

        var inspection = rig.Community.Inspect(package);
        var result = await rig.Community.InstallAsync(package, Internet, CancellationToken.None);

        Assert.NotEmpty(inspection.Errors);
        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Root));
        Assert.Empty(rig.Approved.Modules);
        Assert.False(File.Exists(Path.Combine(rig.Root, "..", "evil.txt")));
    }

    [WindowsHostFact]
    public async Task Install_IdThatWindowsReserves_IsRefusedAsAFolderName()
    {
        // « con.x » passe la forme auteur.module, mais Windows ne crée pas un dossier nommé ainsi.
        var rig = NewRig();

        var package = rig.Package(id: "con.x");
        var inspection = rig.Community.Inspect(package);
        var result = await rig.Community.InstallAsync(package, Internet, CancellationToken.None);

        Assert.Contains(inspection.Errors, e => e.Contains("nom de dossier"));
        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Root));
    }

    [WindowsHostFact]
    public async Task Install_NotAZipFile_IsRefused()
    {
        var rig = NewRig();
        var file = Path.Combine(rig.NewFolder(), "faux.avmod");
        await File.WriteAllTextAsync(file, "ceci n'est pas un zip");

        var result = await rig.Community.InstallAsync(file, Internet, CancellationToken.None);
        var missing = rig.Community.Inspect(Path.Combine(rig.NewFolder(), "absent.avmod"));

        Assert.False(result.Ok);
        Assert.NotEmpty(missing.Errors);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Root));
    }

    [WindowsHostFact]
    public void Extract_ManifestDifferentFromTheOneShown_IsRemoved()
    {
        // Le fichier a pu être remplacé entre l'inspection et l'extraction : ce qui est écrit doit être ce qu'on a montré.
        var rig = NewRig();
        var package = rig.Package(declared: InternetAndSensors);
        var shown = AvmodPackage.Inspect(package).Manifest! with { Permissions = Internet };
        var target = Path.Combine(rig.Root, "alex.demo", "1.0.0");

        var extracted = new ModuleInstaller((_, _) => { }).Extract(package, target, shown);

        Assert.NotNull(extracted.Error);
        Assert.Null(extracted.Manifest);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [WindowsHostTheory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    public void Extract_WhenPreparingTheContainerFails_LeavesNoUnvalidatedFolder(Type failure)
    {
        var rig = NewRig();
        var package = rig.Package();
        var expected = AvmodPackage.Inspect(package).Manifest!;
        var target = Path.Combine(rig.Root, "alex.demo", "1.0.0");

        var extracted = new ModuleInstaller((_, _) => throw (Exception)Activator.CreateInstance(failure, "refus")!).Extract(package, target, expected);

        Assert.NotNull(extracted.Error);
        Assert.Null(extracted.Manifest);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [WindowsHostTheory]
    [InlineData(true, new[] { "alex.demo" })]
    [InlineData(false, new string[0])] // une mise à jour : le conteneur existait déjà (avec ses données), on n'y touche pas
    public void Extract_WhenPreparingTheContainerFails_DeletesTheContainerOnlyOfANewModule(bool newModule, string[] expectedDeleted)
    {
        var rig = NewRig();
        var package = rig.Package();
        var expected = AvmodPackage.Inspect(package).Manifest!;
        var target = Path.Combine(rig.Root, "alex.demo", "1.0.0");
        var deleted = new List<string>();

        // Le conteneur est créé par l'accord de la lecture, puis l'accord échoue (disque sans ACL, par exemple).
        var extracted = new ModuleInstaller((_, _) => throw new InvalidOperationException("refus"), deleted.Add).Extract(package, target, expected, newModule);

        Assert.NotNull(extracted.Error);
        Assert.Equal(expectedDeleted, deleted);
    }

    [WindowsHostFact]
    public void Extract_Succeeding_NeverDeletesTheContainer()
    {
        var rig = NewRig();
        var package = rig.Package();
        var expected = AvmodPackage.Inspect(package).Manifest!;
        var deleted = new List<string>();

        var extracted = new ModuleInstaller((_, _) => { }, deleted.Add).Extract(package, Path.Combine(rig.Root, "alex.demo", "1.0.0"), expected, newModule: true);

        Assert.Null(extracted.Error);
        Assert.Empty(deleted);
    }

    [WindowsHostFact]
    public async Task Install_NewModuleWhenTheListCannotBeWritten_DeletesTheContainerItCreated()
    {
        var rig = NewRig();
        var blocker = Path.Combine(rig.Root, "..", "settings", "modules.json.tmp");
        Directory.CreateDirectory(blocker); // l'écriture du fichier temporaire de la liste échoue

        var result = await rig.Community.InstallAsync(rig.Package(), Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(["alex.demo"], rig.Profiles);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")));
    }

    [WindowsHostFact]
    public async Task Install_UpdateWhenTheListCannotBeWritten_KeepsTheContainerOfTheInstalledModule()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0"));
        var blocker = Path.Combine(rig.Root, "..", "settings", "modules.json.tmp");
        Directory.CreateDirectory(blocker);

        var result = await rig.Community.InstallAsync(rig.Package(version: "1.1.0"), Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(rig.Profiles);
        Assert.True(Directory.Exists(Path.Combine(rig.Root, "alex.demo", "1.0.0")));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    // --- Rien ne tourne sans validation ---------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task InstalledFileModifiedLater_IsNotLaunched()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State); // le catalogue l'a vu intact

        // Quelqu'un change un fichier après coup, avant qu'une case ne lance l'hôte : le verrou d'exécution recalcule l'empreinte.
        File.WriteAllText(Path.Combine(rig.Root, "alex.demo", "1.0.0", "Mod.dll"), "code remplacé");
        var module = rig.Open("alex.demo");

        Assert.True(await TestWait.StaysFalseAsync(() => rig.Launcher.Count > 0, TimeSpan.FromMilliseconds(500)));
        await TestWait.UntilAsync(() => module.Status.ActionMessage == ModuleHostSupervisor.FilesChangedText);
        Assert.Equal(0, rig.Launcher.Count);
    }

    [WindowsHostFact]
    public async Task InstalledFileModified_AfterAnExamination_IsChangedAndNeverStartsAHost()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        File.WriteAllText(Path.Combine(rig.Root, "alex.demo", "1.0.0", "Mod.dll"), "code remplacé");

        rig.Community.Refresh();
        var module = rig.Open("alex.demo");

        Assert.Equal(CommunityState.Changed, rig.Entry("alex.demo").State);
        Assert.IsType<PlaceholderModuleFactory>(rig.Factory("alex.demo"));
        Assert.True(await TestWait.StaysFalseAsync(() => rig.Launcher.Count > 0, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(ModuleHostSupervisor.FilesChangedText, module.Status.ActionMessage);
        Assert.Equal(0, rig.Launcher.Count);
    }

    [WindowsHostFact]
    public async Task DroppedFolderNotApproved_NeverStartsAHost()
    {
        var rig = NewRig();
        rig.Drop();

        rig.Community.Refresh();
        var module = rig.Open("alex.demo");
        await module.StartAsync(CancellationToken.None);

        Assert.Equal(CommunityState.NeedsApproval, rig.Entry("alex.demo").State);
        Assert.IsType<PlaceholderModuleFactory>(rig.Factory("alex.demo"));
        Assert.Equal(PlaceholderModuleFactory.NeedsApprovalText, module.Status.ActionMessage);
        Assert.True(await TestWait.StaysFalseAsync(() => rig.Launcher.Count > 0, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(0, rig.Launcher.Count);
        Assert.Empty(rig.Approved.Modules);
    }

    [WindowsHostFact]
    public async Task Approve_DroppedFolder_ThenItRuns()
    {
        var rig = NewRig();
        var folder = rig.Drop(declared: InternetAndSensors);
        rig.Community.Refresh();

        var refused = rig.Community.Approve("alex.demo", [ModulePermission.NowPlaying]);
        var result = rig.Community.Approve("alex.demo", InternetAndSensors);

        Assert.False(refused.Ok);
        Assert.True(result.Ok, result.Message);
        var approved = Assert.Single(rig.Approved.Modules);
        Assert.Equal((folder, ContentHash.Compute(folder)), (approved.Directory, approved.ContentHash));
        Assert.Equal(InternetAndSensors, approved.Granted);
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
        rig.Open("alex.demo");
        Assert.Equal(InternetAndSensors, (await (await rig.Launcher.WaitForStartAsync()).WaitAsync<Hello>()).Granted);
    }

    [WindowsHostFact]
    public async Task Approve_ChangedModule_ValidatesItsNewContent()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var file = Path.Combine(rig.Root, "alex.demo", "1.0.0", "Mod.dll");
        File.WriteAllText(file, "code remplacé");
        rig.Community.Refresh();
        Assert.Equal(CommunityState.Changed, rig.Entry("alex.demo").State);

        var result = rig.Community.Approve("alex.demo", Internet);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
        Assert.Equal(ContentHash.Compute(Path.GetDirectoryName(file)!), Assert.Single(rig.Approved.Modules).ContentHash);
        Assert.False(rig.Community.Approve("alex.demo", Internet).Ok, "déjà validé");
    }

    // --- Permissions, relance, mise de côté ------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task SetPermissions_RestartsHostWithNewCapabilities()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(declared: InternetAndSensors), Internet);
        var module = rig.Open("alex.demo");
        var first = await rig.Launcher.WaitForStartAsync();
        Assert.Equal(Internet, (await first.WaitAsync<Hello>()).Granted);

        // Une permission de plus : l'hôte repart avec la nouvelle capacité.
        var added = await rig.Community.SetPermissionsAsync("alex.demo", InternetAndSensors);

        Assert.True(added.Ok, added.Message);
        Assert.True(first.Killed || first.HasExited);
        var second = await rig.Launcher.WaitForStartAsync(2);
        Assert.Equal(InternetAndSensors, (await second.WaitAsync<Hello>()).Granted);
        Assert.Equal(InternetAndSensors, second.Start.Granted);
        Assert.Equal(InternetAndSensors, Assert.Single(rig.Approved.Modules).Granted);

        // Une permission retirée pendant que le module tourne : les capacités d'un conteneur sont fixées au lancement, l'hôte repart.
        var removed = await rig.Community.SetPermissionsAsync("alex.demo", [ModulePermission.Sensors]);

        Assert.True(removed.Ok, removed.Message);
        var third = await rig.Launcher.WaitForStartAsync(3);
        Assert.Equal([ModulePermission.Sensors], (await third.WaitAsync<Hello>()).Granted);
        Assert.Equal([ModulePermission.Sensors], third.Start.Granted);
        Assert.True(second.HasExited);
        Assert.Equal(3, rig.Launcher.Count);
        module.Dispose();
    }

    [WindowsHostFact]
    public async Task SetPermissions_UndeclaredOrUnknownModule_IsRefused()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(declared: Internet));

        var undeclared = await rig.Community.SetPermissionsAsync("alex.demo", [ModulePermission.Sensors]);
        var unknown = await rig.Community.SetPermissionsAsync("autre.module", Internet);

        Assert.False(undeclared.Ok);
        Assert.False(unknown.Ok);
        Assert.Equal(Internet, Assert.Single(rig.Approved.Modules).Granted);
    }

    [WindowsHostFact]
    public async Task Relaunch_ClearsSetAside()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var approved = Assert.Single(rig.Approved.Modules);
        rig.Store.Save(rig.Approved with { Modules = [approved with { SetAsideReason = "Mis de côté : le module s'est arrêté 3 fois en 10 minutes." }] });
        rig.Community.Refresh();
        Assert.Equal(CommunityState.SetAside, rig.Entry("alex.demo").State);
        var late = rig.Open("alex.demo"); // une case posée pendant la mise de côté le dit, sans lancer d'hôte
        Assert.Equal("Mis de côté : le module s'est arrêté 3 fois en 10 minutes.", late.Status.ActionMessage);
        Assert.Equal(0, rig.Launcher.Count);

        var result = await rig.Community.RelaunchAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.Null(Assert.Single(rig.Approved.Modules).SetAsideReason);
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
        rig.Open("alex.demo");
        await rig.Launcher.WaitForStartAsync();
        Assert.False((await rig.Community.RelaunchAsync("inconnu.module")).Ok);
    }

    [WindowsHostFact]
    public async Task ThreeCrashes_AreRecordedAsSetAside_AndRelaunchRevivesTheOpenCase()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var module = rig.Open("alex.demo");

        await CrashAsync(rig, module, 1);
        rig.Time.Advance(TimeSpan.FromSeconds(1.1));
        await CrashAsync(rig, module, 2);
        rig.Time.Advance(TimeSpan.FromSeconds(2.1));
        await CrashAsync(rig, module, 3);

        // La raison est rangée dans la liste scellée, et le catalogue la relit.
        await TestWait.UntilAsync(() => rig.Approved.Modules.Single().SetAsideReason is not null);
        await TestWait.UntilAsync(() => rig.Entry("alex.demo").State == CommunityState.SetAside);
        Assert.StartsWith("Mis de côté", rig.Approved.Modules.Single().SetAsideReason);
        Assert.StartsWith("Mis de côté", module.Status.ActionMessage);

        var result = await rig.Community.RelaunchAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.Null(rig.Approved.Modules.Single().SetAsideReason);
        await rig.Launcher.WaitForStartAsync(4); // la case déjà ouverte repart, sans qu'on la repose
        await TestWait.UntilAsync(() => module.Status.Health == ModuleHealth.Online);
    }

    private static async Task CrashAsync(CommunityRig rig, IModule module, int startNumber)
    {
        var host = await rig.Launcher.WaitForStartAsync(startNumber);
        await host.WaitAsync<Hello>();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            var message = module.Status.ActionMessage;
            if (module.Status.Health == ModuleHealth.Offline
                && (message == ModuleHostSupervisor.HostStoppedText || message?.StartsWith("Mis de côté", StringComparison.Ordinal) == true))
            {
                announced.TrySetResult();
            }
        }

        module.StatusChanged += Check;
        try
        {
            host.Exit(1);
            await announced.Task.WaitAsync(TestWait.Patience);
        }
        finally
        {
            module.StatusChanged -= Check;
        }
    }

    // --- Désinstallation ----------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Uninstall_RemovesFolderSecretsAndProfile()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        rig.Vault.Scope("alex.demo").Set("jeton", "secret");
        rig.Vault.Scope("autre.module").Set("jeton", "ne-pas-toucher");
        var module = rig.Open("alex.demo");
        var host = await rig.Launcher.WaitForStartAsync();
        await host.WaitAsync<Hello>();
        var changes = rig.Changes;

        var result = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")), "le dossier du module (verrouillé par son hôte) est supprimé");
        Assert.True(host.HasExited);
        Assert.Equal(["alex.demo"], rig.Profiles);
        Assert.Null(rig.Vault.Get("alex.demo", "jeton"));
        Assert.Equal("ne-pas-toucher", rig.Vault.Get("autre.module", "jeton"));
        Assert.Empty(rig.Approved.Modules);
        Assert.Empty(rig.Community.Entries);
        Assert.Null(rig.Factory("alex.demo"));
        Assert.True(rig.Changes > changes);
        Assert.False((await rig.Community.UninstallAsync("alex.demo")).Ok);
        module.Dispose();
    }

    [WindowsHostFact]
    public async Task Uninstall_DroppedFolderNeverValidated_RemovesItToo()
    {
        var rig = NewRig();
        rig.Drop();
        rig.Community.Refresh();

        var result = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")));
        Assert.Empty(rig.Community.Entries);
    }

    [WindowsHostFact]
    public async Task Uninstall_FolderThatIsNotAModule_IsNeitherListedNorDeleted()
    {
        // Un dossier « network » n'est pas un module de la communauté (pas de point) : il n'est pas dans le catalogue, il n'est jamais supprimé,
        // et le coffre « network » est celui du module Réseau d'origine.
        var rig = NewRig();
        var folder = Path.Combine(rig.Root, "network", "1.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fichier.txt"), "x");
        rig.Vault.Scope("network").Set("freebox", "jeton-freebox");
        rig.Community.Refresh();

        var result = await rig.Community.UninstallAsync("network");

        Assert.Empty(rig.Community.Entries);
        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(folder, "fichier.txt")), "le dossier déposé reste");
        Assert.Equal("jeton-freebox", rig.Vault.Get("network", "freebox"));
        Assert.Empty(rig.Profiles);
    }

    [WindowsHostFact]
    public async Task Uninstall_FolderOfPhotosInTheModulesFolder_DeletesNothing()
    {
        // Choisir un dossier qui contient déjà des choses (« Photos\2024 ») ne fait pas de ces choses des modules à désinstaller.
        var rig = NewRig();
        var photos = Path.Combine(rig.Root, "Photos", "2024");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "vacances.jpg"), "x");
        await InstallAsync(rig, rig.Package());
        rig.Community.Refresh();

        var result = await rig.Community.UninstallAsync("Photos");

        Assert.Equal(["alex.demo"], rig.Community.Entries.Select(e => e.Id));
        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(photos, "vacances.jpg")));
        Assert.Single(rig.Approved.Modules);
    }

    [WindowsHostFact]
    public async Task Uninstall_WithAForeignFolderInTheSealedList_NeverDeletesAboveTheModuleFolder()
    {
        // La liste scellée est de confiance, mais un dossier qui ne finit pas par <id>\<version> n'est pas celui d'un module installé.
        var rig = NewRig();
        var documents = rig.NewFolder("Documents");
        File.WriteAllText(Path.Combine(documents, "important.txt"), "x");
        rig.Store.Save(rig.Approved with
        {
            Modules = [new ApprovedModule("alex.demo", "1.0.0", documents, "h", Internet, null, null)],
        });
        rig.Community.Refresh();

        var result = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(documents, "important.txt")));
        Assert.Empty(rig.Approved.Modules);
    }

    [WindowsHostFact]
    public async Task Approve_AndUninstall_WithAStrayTempFolderBesideTheModule_StillFindTheRightEntry()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var module = Path.Combine(rig.Root, "alex.demo");
        Directory.CreateDirectory(Path.Combine(module, "1.0.0.tmp-0123456789abcdef")); // le reste d'une extraction interrompue
        File.WriteAllText(Path.Combine(module, "1.0.0", "Mod.dll"), "code remplacé");
        rig.Community.Refresh();
        Assert.Equal(CommunityState.Invalid, rig.Community.Entries.First(e => e.Id == "alex.demo").State); // l'entrée parasite passe la première

        var approved = rig.Community.Approve("alex.demo", Internet);
        var removed = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(approved.Ok, approved.Message);
        Assert.True(removed.Ok, removed.Message);
        Assert.Equal(["alex.demo"], rig.Profiles);
    }

    [WindowsHostFact]
    public async Task Relaunch_WhenTheListCannotBeWritten_DoesNotRestartTheHostLater()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        rig.Open("alex.demo");
        await (await rig.Launcher.WaitForStartAsync()).WaitAsync<Hello>();
        var blocker = Path.Combine(rig.Root, "..", "settings", "modules.json.tmp");
        Directory.CreateDirectory(blocker); // l'écriture du fichier temporaire de la liste échoue

        var failed = await rig.Community.RelaunchAsync("alex.demo");
        Directory.Delete(blocker);
        rig.Community.Refresh();

        Assert.False(failed.Ok);
        Assert.True(await TestWait.StaysFalseAsync(() => rig.Launcher.Count > 1, TimeSpan.FromMilliseconds(500)), "l'échec ne laisse pas de relance en attente");
    }

    // --- Dossier des modules, mode développeur --------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task SetModulesRoot_ChangesWhereModulesAreLooked()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var elsewhere = Path.Combine(rig.NewFolder(), "autres-modules");

        Assert.False(rig.Community.SetModulesRoot("modules").Ok);
        Assert.False(rig.Community.SetModulesRoot(Path.GetPathRoot(elsewhere)!).Ok);
        var result = rig.Community.SetModulesRoot(elsewhere);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(elsewhere, rig.Approved.ModulesRoot);
        Assert.Equal(elsewhere, rig.Community.StoreInfo.ModulesRoot);
        // « Ceux qui sont déjà installés restent où ils sont » : ils continuent d'être lus, validés, depuis leur dossier.
        var kept = rig.Entry("alex.demo");
        Assert.Equal(CommunityState.Ready, kept.State);
        Assert.Equal(Path.Combine(rig.Root, "alex.demo", "1.0.0"), kept.Directory);
        await InstallAsync(rig, rig.Package(id: "alex.autre"));
        Assert.True(Directory.Exists(Path.Combine(elsewhere, "alex.autre", "1.0.0")));
        Assert.Equal(["alex.autre", "alex.demo"], rig.Community.Entries.Select(e => e.Id));
    }

    [WindowsHostTheory]
    [InlineData(false, false)] // un disque sans droits d'accès : refusé
    [InlineData(true, true)]
    [InlineData(null, true)] // on ne sait pas : on ne bloque rien
    public void SetModulesRoot_OnAVolumeWithoutAcls_IsRefusedWithAClearMessage(bool? volumeHasAcls, bool accepted)
    {
        var rig = new CommunityRig(volumeHasAcls: _ => volumeHasAcls);
        _rigs.Add(rig);
        var elsewhere = Path.Combine(rig.NewFolder(), "cle-usb");

        var result = rig.Community.SetModulesRoot(elsewhere);

        Assert.Equal(accepted, result.Ok);
        if (!accepted)
        {
            Assert.Contains("NTFS", result.Message);
            Assert.False(Directory.Exists(elsewhere), "rien n'est créé sur un disque refusé");
            Assert.NotEqual(elsewhere, rig.Approved.ModulesRoot);
        }
    }

    [WindowsHostFact]
    public async Task SetModulesRoot_AnInstalledModuleStillRunsFromItsOldFolder()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        Assert.True(rig.Community.SetModulesRoot(Path.Combine(rig.NewFolder(), "autres-modules")).Ok);

        Assert.IsType<RemoteModuleFactory>(rig.Factory("alex.demo"));
        rig.Open("alex.demo");
        var host = await rig.Launcher.WaitForStartAsync();
        var hello = await host.WaitAsync<Hello>();

        Assert.Equal(Path.Combine(rig.Root, "alex.demo", "1.0.0"), hello.ModuleDirectory);
    }

    [WindowsHostFact]
    public async Task Uninstall_AfterTheModulesFolderChanged_RemovesTheModuleFolderInTheOldRootAndNothingElse()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        await InstallAsync(rig, rig.Package(id: "alex.autre"));
        File.WriteAllText(Path.Combine(rig.Root, "garder.txt"), "x"); // à côté des modules, dans l'ancien dossier
        rig.Vault.Scope("alex.demo").Set("jeton", "secret");
        var elsewhere = Path.Combine(rig.NewFolder(), "autres-modules");
        Assert.True(rig.Community.SetModulesRoot(elsewhere).Ok);

        var result = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")), "<ancien dossier>\\<id> est supprimé");
        Assert.True(Directory.Exists(Path.Combine(rig.Root, "alex.autre", "1.0.0")), "les autres modules de l'ancien dossier restent");
        Assert.True(File.Exists(Path.Combine(rig.Root, "garder.txt")), "rien au-dessus de <id>");
        Assert.True(Directory.Exists(elsewhere));
        Assert.Equal(["alex.demo"], rig.Profiles);
        Assert.Null(rig.Vault.Get("alex.demo", "jeton"));
        Assert.Equal(["alex.autre"], rig.Approved.Modules.Select(m => m.Id));
        Assert.Equal(["alex.autre"], rig.Community.Entries.Select(e => e.Id));
    }

    [WindowsHostFact]
    public async Task Uninstall_LastModuleOfTheOldRoot_LeavesTheRootFolder()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        Assert.True(rig.Community.SetModulesRoot(Path.Combine(rig.NewFolder(), "autres-modules")).Ok);

        var result = await rig.Community.UninstallAsync("alex.demo");

        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(rig.Root), "le dossier des modules d'avant n'est pas à nous : on ne supprime que <id>");
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")));
    }

    [WindowsHostFact]
    public async Task Install_NewVersionAfterTheModulesFolderChanged_RemovesTheOldVersionFromTheOldRoot()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0"));
        var elsewhere = Path.Combine(rig.NewFolder(), "autres-modules");
        Assert.True(rig.Community.SetModulesRoot(elsewhere).Ok);

        await InstallAsync(rig, rig.Package(version: "1.1.0"));

        var approved = Assert.Single(rig.Approved.Modules);
        Assert.Equal(Path.Combine(elsewhere, "alex.demo", "1.1.0"), approved.Directory);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo", "1.0.0")), "l'ancienne version, dans l'ancien dossier, est retirée");
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")), "son dossier <id> vide aussi");
        Assert.True(Directory.Exists(rig.Root));
        Assert.Equal(CommunityState.Ready, rig.Entry("alex.demo").State);
    }

    [WindowsHostFact]
    public async Task Install_SameVersionAgainAfterTheModulesFolderChanged_MovesItAndRemovesTheOldCopy()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package(version: "1.0.0"));
        var elsewhere = Path.Combine(rig.NewFolder(), "autres-modules");
        Assert.True(rig.Community.SetModulesRoot(elsewhere).Ok);

        await InstallAsync(rig, rig.Package(version: "1.0.0"));

        Assert.Equal(Path.Combine(elsewhere, "alex.demo", "1.0.0"), Assert.Single(rig.Approved.Modules).Directory);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.demo")));
        Assert.Equal(Path.Combine(elsewhere, "alex.demo", "1.0.0"), rig.Entry("alex.demo").Directory);
    }

    [WindowsHostFact]
    public async Task AddDevFolder_RunsWithGrantedPermissionsOnly_NotTheManifestOnes()
    {
        var rig = NewRig();
        var dev = rig.NewFolder("mon-module");
        File.WriteAllText(Path.Combine(dev, "manifest.json"), CommunityRig.ManifestJson("alex.dev", "0.1.0", InternetAndSensors, null));
        File.WriteAllText(Path.Combine(dev, "Mod.dll"), "code");

        Assert.False(rig.Community.AddDevFolder(dev, Internet).Ok, "le mode développeur est éteint");
        Assert.True(rig.Community.SetDeveloperMode(true).Ok);
        Assert.False(rig.Community.AddDevFolder(dev, [ModulePermission.NowPlaying]).Ok, "permission non déclarée");
        var added = rig.Community.AddDevFolder(dev, Internet);

        Assert.True(added.Ok, added.Message);
        var entry = rig.Entry("alex.dev");
        Assert.True(entry.FromDevFolder);
        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.True(rig.Community.StoreInfo.DeveloperMode);
        Assert.Equal([dev], rig.Community.StoreInfo.DevFolders);
        Assert.Equal(Internet, rig.Community.GrantedTo(entry)); // celles du dossier, pas celles du manifeste
        rig.Open("alex.dev");
        var host = await rig.Launcher.WaitForStartAsync();
        Assert.Equal(Internet, host.Start.Granted); // jamais les permissions du manifeste
        Assert.Equal(Internet, (await host.WaitAsync<Hello>()).Granted);
        Assert.Empty(rig.Approved.Modules);
    }

    [WindowsHostFact]
    public async Task DeveloperModeOff_HidesDevModulesAndStopsTheirHost()
    {
        var rig = NewRig();
        var dev = rig.NewFolder("mon-module");
        File.WriteAllText(Path.Combine(dev, "manifest.json"), CommunityRig.ManifestJson("alex.dev", "0.1.0", Internet, null));
        File.WriteAllText(Path.Combine(dev, "Mod.dll"), "code");
        rig.Community.SetDeveloperMode(true);
        Assert.True(rig.Community.AddDevFolder(dev, Internet).Ok);
        rig.Open("alex.dev");
        var host = await rig.Launcher.WaitForStartAsync();

        Assert.True(rig.Community.SetDeveloperMode(false).Ok);

        await TestWait.UntilAsync(() => host.HasExited);
        Assert.Empty(rig.Community.Entries);
        Assert.Null(rig.Factory("alex.dev"));
        Assert.Single(rig.Approved.DevFolders); // la liste des dossiers est gardée pour la prochaine fois
    }

    [WindowsHostFact]
    public async Task ReloadDevModule_RestartsTheHostWithTheFolderFiles()
    {
        var rig = NewRig();
        var dev = rig.NewFolder("mon-module");
        File.WriteAllText(Path.Combine(dev, "manifest.json"), CommunityRig.ManifestJson("alex.dev", "0.1.0", Internet, null));
        File.WriteAllText(Path.Combine(dev, "Mod.dll"), "code");
        rig.Community.SetDeveloperMode(true);
        Assert.True(rig.Community.AddDevFolder(dev, Internet).Ok);
        rig.Open("alex.dev");
        var first = await rig.Launcher.WaitForStartAsync();
        await first.WaitAsync<Hello>();
        File.WriteAllText(Path.Combine(dev, "Mod.dll"), "code recompilé"); // un dossier de développement change sans arrêt

        var result = await rig.Community.ReloadDevModuleAsync("alex.dev");

        Assert.True(result.Ok, result.Message);
        Assert.True(first.HasExited);
        var second = await rig.Launcher.WaitForStartAsync(2);
        await second.WaitAsync<Hello>();
        Assert.False((await rig.Community.ReloadDevModuleAsync("alex.inconnu")).Ok);
        Assert.True(rig.Community.RemoveDevFolder(dev).Ok);
        Assert.False(rig.Community.RemoveDevFolder(dev).Ok);
        Assert.Empty(rig.Community.Entries);
    }

    // --- Liste scellée : jamais écrasée quand elle est illisible, accès sérialisés --------------------------------------------------------

    [WindowsHostFact]
    public async Task UnreadableStore_IsNeverOverwritten_ByAnyChange()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var file = Path.Combine(rig.Root, "..", "settings", "modules.json");
        var before = File.ReadAllBytes(file);

        ActionResult developer, approve, root;
        Task<ActionResult> install, permissions;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) // un antivirus, OneDrive, un éditeur…
        {
            developer = rig.Community.SetDeveloperMode(true);
            approve = rig.Community.Approve("alex.demo", Internet);
            root = rig.Community.SetModulesRoot(Path.Combine(rig.NewFolder(), "autre"));
            install = rig.Community.InstallAsync(rig.Package(id: "alex.deux"), Internet, CancellationToken.None);
            permissions = rig.Community.SetPermissionsAsync("alex.demo", []);
            await Task.WhenAll(install, permissions);
        }

        Assert.All(new[] { developer, approve, root, install.Result, permissions.Result }, r => Assert.False(r.Ok));
        Assert.Contains("illisible", developer.Message);
        Assert.Equal(before, File.ReadAllBytes(file)); // pas un octet changé
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "alex.deux")), "rien n'est installé sans pouvoir le noter");
        Assert.Equal("alex.demo", Assert.Single(rig.Approved.Modules).Id); // une fois libérée, la liste est intacte
    }

    [WindowsHostFact]
    public void TamperedStore_StartsAgainFromNothing_KeepingTheModulesFolder()
    {
        var rig = NewRig();
        var folder = rig.Drop();
        var file = Path.Combine(rig.Root, "..", "settings", "modules.json");
        rig.Community.Refresh();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"developerMode\": false", "\"developerMode\": true"));
        rig.Community.Refresh();
        Assert.True(rig.Community.StoreInfo.Tampered);
        Assert.Equal(CommunityState.NeedsApproval, rig.Entry("alex.demo").State);

        var result = rig.Community.Approve("alex.demo", Internet);

        Assert.True(result.Ok, result.Message);
        var load = rig.Store.Load();
        Assert.False(load.Tampered);
        Assert.Equal(rig.Root, load.Modules.ModulesRoot); // le dossier connu n'est pas oublié
        Assert.Equal(folder, Assert.Single(load.Modules.Modules).Directory);
        Assert.False(rig.Community.StoreInfo.Tampered);
    }

    [WindowsHostFact]
    public async Task ConcurrentWritesAndExaminations_NeverSeeATamperedList()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        var tampered = 0;

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 15; i++)
            {
                rig.Community.SetDeveloperMode((i + w) % 2 == 0);
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 15; i++)
            {
                rig.Community.Refresh();
                if (rig.Community.StoreInfo.Tampered)
                {
                    Interlocked.Increment(ref tampered);
                }
            }
        }));
        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(0, tampered);
        Assert.False(rig.Store.Load().Tampered);
        Assert.Equal("alex.demo", Assert.Single(rig.Approved.Modules).Id);
    }

    // --- Affichage ---------------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task DisplayOptions_ReachRunningAndFutureHosts()
    {
        var rig = NewRig();
        await InstallAsync(rig, rig.Package());
        rig.Community.SetDisplayOptions(7, AppearanceSettings.Default);
        rig.Open("alex.demo");
        var host = await rig.Launcher.WaitForStartAsync();
        Assert.Equal(7, (await host.WaitAsync<Hello>()).MaxFps);

        rig.Community.SetDisplayOptions(3, AppearanceSettings.Default);

        Assert.Equal(3, (await host.WaitAsync<Limits>()).MaxFps);
    }
}
