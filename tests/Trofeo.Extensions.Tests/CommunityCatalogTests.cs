using AshboardVision.Sdk;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public sealed class CommunityCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av-catalog-" + Guid.NewGuid().ToString("N"));

    public CommunityCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ModulesRoot => Path.Combine(_root, "modules");

    private static string Manifest(string id, string version, string sdk = "1.0") => $$"""
        {
          "id": "{{id}}",
          "name": "Module de test",
          "description": "Un module pour les tests.",
          "author": "Test",
          "version": "{{version}}",
          "sdk": "{{sdk}}",
          "entry": "Mod.dll",
          "permissions": [],
          "minSize": { "columns": 3, "rows": 1 },
          "presets": {},
          "settings": []
        }
        """;

    /// <summary>Crée <c>&lt;racine&gt;\&lt;id&gt;\&lt;version&gt;</c> avec un manifeste et une DLL.</summary>
    private string Install(string id, string version, string? manifest = null, string content = "code")
    {
        var directory = Path.Combine(ModulesRoot, id, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest ?? Manifest(id, version));
        File.WriteAllText(Path.Combine(directory, "Mod.dll"), content);
        return directory;
    }

    private static ApprovedModule Approve(string id, string version, string directory, string? setAside = null) =>
        new(id, version, directory, ContentHash.Compute(directory), [], setAside, null);

    private ApprovedModules State(IEnumerable<ApprovedModule>? modules = null, bool developerMode = false, IEnumerable<DevFolder>? devFolders = null) =>
        new(ModulesRoot, developerMode, (devFolders ?? []).ToList(), (modules ?? []).ToList());

    private static CommunityCatalog Catalog(ApprovedModules state) =>
        new(() => new ApprovedLoad(state, Tampered: false));

    [Fact]
    public void Scan_ApprovedAndUnchanged_IsReady()
    {
        var directory = Install("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", directory)])).Scan());

        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.Null(entry.Message);
        Assert.Equal("alex.demo", entry.Id);
        Assert.Equal(directory, entry.Directory);
        Assert.NotNull(entry.Manifest);
        Assert.NotNull(entry.Approval);
        Assert.False(entry.FromDevFolder);
    }

    [Fact]
    public void Scan_NotApproved_NeedsApproval()
    {
        Install("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.NeedsApproval, entry.State);
        Assert.Null(entry.Approval);
        Assert.NotNull(entry.Manifest);
    }

    [Fact]
    public void Scan_FileChanged_IsChanged()
    {
        var directory = Install("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", directory);
        File.WriteAllText(Path.Combine(directory, "Mod.dll"), "code modifié");

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(CommunityState.Changed, entry.State);
        Assert.Equal("Le contenu du module a changé depuis sa validation : valide-le de nouveau.", entry.Message);
    }

    [Fact]
    public void Scan_FileAdded_IsChanged()
    {
        var directory = Install("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", directory);
        File.WriteAllText(Path.Combine(directory, "extra.dll"), "autre");

        Assert.Equal(CommunityState.Changed, Assert.Single(Catalog(State([approval])).Scan()).State);
    }

    [Fact]
    public void Scan_NewerSdk_NeedsNewerApp()
    {
        var directory = Install("alex.demo", "1.0.0", Manifest("alex.demo", "1.0.0", sdk: "1.9"));

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", directory)])).Scan());

        Assert.Equal(CommunityState.NeedsNewerApp, entry.State);
        Assert.Equal(Compatibility.Message(CompatibilityKind.NeedsNewerApp), entry.Message);
    }

    [Fact]
    public void Scan_OldSdk_IsTooOld()
    {
        Install("alex.demo", "1.0.0", Manifest("alex.demo", "1.0.0", sdk: "0.9"));

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.TooOld, entry.State);
        Assert.Equal(Compatibility.Message(CompatibilityKind.TooOld), entry.Message);
    }

    [Fact]
    public void Scan_InvalidManifest_IsInvalid()
    {
        Install("alex.demo", "1.0.0", manifest: "{ pas du json");

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.Invalid, entry.State);
        Assert.Null(entry.Manifest);
        Assert.False(string.IsNullOrWhiteSpace(entry.Message));
    }

    [Fact]
    public void Scan_MissingManifest_IsInvalid()
    {
        var directory = Install("alex.demo", "1.0.0");
        File.Delete(Path.Combine(directory, "manifest.json"));

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.Invalid, entry.State);
        Assert.Equal("Le fichier manifest.json est introuvable dans le dossier du module.", entry.Message);
    }

    [Fact]
    public void Scan_SetAside_IsSetAside()
    {
        var directory = Install("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", directory, setAside: "Trop de pannes")])).Scan());

        Assert.Equal(CommunityState.SetAside, entry.State);
        Assert.Equal("Trop de pannes", entry.Message);
    }

    [Fact]
    public void Scan_TamperedStore_EverythingNeedsApproval()
    {
        var directory = Install("alex.demo", "1.0.0");
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), Manifest("alex.dev", "0.1.0"));
        var state = State([Approve("alex.demo", "1.0.0", directory)], developerMode: true, [new DevFolder(dev, [])]);
        // Après une lecture valide, une liste altérée (rendue vide par le magasin) ne valide ni ne développe rien.
        var (catalog, next) = Sequenced();
        next(new ApprovedLoad(state, Tampered: false));
        catalog.Scan();
        next(new ApprovedLoad(ApprovedModules.Empty, Tampered: true));

        var entries = catalog.Scan();

        var entry = Assert.Single(entries);
        Assert.Equal(CommunityState.NeedsApproval, entry.State);
        Assert.Null(entry.Approval);
    }

    [Fact]
    public void Scan_UnreadableStore_IsUnavailableNotNeedsApproval_AndDevFoldersAreNotListed()
    {
        var directory = Install("alex.demo", "1.0.0");
        Install("alex.vieux", "1.0.0", Manifest("alex.vieux", "1.0.0", sdk: "0.9"));
        Install("alex.casse", "1.0.0", manifest: "pas du json");
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), Manifest("alex.dev", "0.1.0"));
        var state = State([Approve("alex.demo", "1.0.0", directory)], developerMode: true, [new DevFolder(dev, [])]);

        var (catalog, next) = Sequenced();
        next(new ApprovedLoad(state, Tampered: false));
        catalog.Scan();
        next(new ApprovedLoad(ApprovedModules.Empty, Tampered: false, Unreadable: true));

        var entries = catalog.Scan();

        Assert.Equal(["alex.casse", "alex.demo", "alex.vieux"], entries.Select(e => e.Id).ToArray());
        Assert.DoesNotContain(entries, e => e.FromDevFolder);
        var demo = entries.Single(e => e.Id == "alex.demo");
        Assert.Equal(CommunityState.Unavailable, demo.State);
        Assert.Equal("Liste des modules validés illisible pour le moment : réessaie dans un instant.", demo.Message);
        Assert.DoesNotContain(entries, e => e.State is CommunityState.NeedsApproval or CommunityState.Ready or CommunityState.Changed);
        // Ce qui ne dépend pas des validations reste jugé comme d'habitude.
        Assert.Equal(CommunityState.Invalid, entries.Single(e => e.Id == "alex.casse").State);
        Assert.Equal(CommunityState.TooOld, entries.Single(e => e.Id == "alex.vieux").State);
    }

    /// <summary>Un catalogue dont la lecture de la liste change d'un scan à l'autre (le magasin réel rend <see cref="ApprovedModules.Empty"/> si elle est altérée ou illisible).</summary>
    private static (CommunityCatalog Catalog, Action<ApprovedLoad> Next) Sequenced()
    {
        ApprovedLoad current = new(ApprovedModules.Empty, Tampered: false);
        return (new CommunityCatalog(() => current), load => current = load);
    }

    [Fact]
    public void Scan_UnreadableStoreAfterGoodLoad_KeepsCustomRootAndIsUnavailable()
    {
        var directory = Install("alex.demo", "1.0.0");
        var (catalog, next) = Sequenced();
        next(new ApprovedLoad(State([Approve("alex.demo", "1.0.0", directory)]), Tampered: false));
        Assert.Equal(CommunityState.Ready, Assert.Single(catalog.Scan()).State);

        next(new ApprovedLoad(ApprovedModules.Empty, Tampered: false, Unreadable: true));
        var entry = Assert.Single(catalog.Scan());

        Assert.Equal(directory, entry.Directory);
        Assert.Equal(CommunityState.Unavailable, entry.State);
        Assert.Equal("Liste des modules validés illisible pour le moment : réessaie dans un instant.", entry.Message);
    }

    [Fact]
    public void Scan_TamperedStoreAfterGoodLoad_KeepsCustomRootAndNeedsApproval()
    {
        var directory = Install("alex.demo", "1.0.0");
        var (catalog, next) = Sequenced();
        next(new ApprovedLoad(State([Approve("alex.demo", "1.0.0", directory)]), Tampered: false));
        catalog.Scan();

        next(new ApprovedLoad(ApprovedModules.Empty, Tampered: true));
        var entry = Assert.Single(catalog.Scan());

        Assert.Equal(directory, entry.Directory);
        Assert.Equal(CommunityState.NeedsApproval, entry.State);
        Assert.Null(entry.Approval);
    }

    [Fact]
    public void Scan_GoodLoadChangesRoot_LaterBadLoadUsesTheNewestGoodRoot()
    {
        var directory = Install("alex.demo", "1.0.0");
        var (catalog, next) = Sequenced();
        next(new ApprovedLoad(State(), Tampered: false));
        catalog.Scan();
        // L'utilisateur change de dossier : la dernière lecture réussie fait foi.
        next(new ApprovedLoad(State() with { ModulesRoot = Path.Combine(_root, "autre") }, Tampered: false));
        catalog.Scan();
        next(new ApprovedLoad(ApprovedModules.Empty, Tampered: true));

        Assert.Empty(catalog.Scan());
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ResolveRoot_NoGoodLoadYet_UsesDefaultWithoutScanningIt()
    {
        var catalog = Catalog(State());

        Assert.Equal(ModulesFolder.Default, catalog.ResolveRoot(new ApprovedLoad(ApprovedModules.Empty, Tampered: true)));
        Assert.Equal(ModulesFolder.Default, catalog.ResolveRoot(new ApprovedLoad(ApprovedModules.Empty, Tampered: false, Unreadable: true)));
        Assert.Equal(ModulesRoot, catalog.ResolveRoot(new ApprovedLoad(State(), Tampered: false)));
        Assert.Equal(ModulesRoot, catalog.ResolveRoot(new ApprovedLoad(ApprovedModules.Empty, Tampered: true)));
    }

    [Fact]
    public void Scan_DevFolderWithModeOff_Ignored()
    {
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), Manifest("alex.dev", "0.1.0"));

        var entries = Catalog(State(developerMode: false, devFolders: [new DevFolder(dev, [])])).Scan();

        Assert.Empty(entries);
    }

    [Fact]
    public void Scan_DevFolderWithModeOn_IsReadyWithoutHashCheck()
    {
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), Manifest("alex.dev", "0.1.0"));

        var entry = Assert.Single(Catalog(State(developerMode: true, devFolders: [new DevFolder(dev, [])])).Scan());

        Assert.True(entry.FromDevFolder);
        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.Equal("alex.dev", entry.Id);
        Assert.Equal(dev, entry.Directory);
        Assert.Null(entry.Approval);
    }

    [Fact]
    public void Scan_DevFolderWithBadManifestOrNewerSdk_IsNotReady()
    {
        var bad = Path.Combine(_root, "dev-bad");
        var newer = Path.Combine(_root, "dev-newer");
        var empty = Path.Combine(_root, "dev-empty");
        Directory.CreateDirectory(bad);
        Directory.CreateDirectory(newer);
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(bad, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(newer, "manifest.json"), Manifest("alex.dev", "0.1.0", sdk: "1.9"));

        var entries = Catalog(State(developerMode: true,
            devFolders: [new DevFolder(bad, []), new DevFolder(newer, []), new DevFolder(empty, [])])).Scan();

        Assert.Equal(
            [CommunityState.Invalid, CommunityState.NeedsNewerApp, CommunityState.Invalid],
            entries.Select(e => e.State).ToArray());
        Assert.Equal("dev-bad", entries[0].Id);
    }

    [Fact]
    public void Scan_DevFolderCollidingWithInstalled_IsInvalid()
    {
        var installed = Install("alex.demo", "1.0.0");
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), Manifest("alex.demo", "9.9.9"));

        var entries = Catalog(State([Approve("alex.demo", "1.0.0", installed)], developerMode: true, [new DevFolder(dev, [])])).Scan();

        Assert.Equal(2, entries.Count);
        Assert.Equal(CommunityState.Ready, entries.Single(e => !e.FromDevFolder).State);
        var devEntry = entries.Single(e => e.FromDevFolder);
        Assert.Equal(CommunityState.Invalid, devEntry.State);
        Assert.Equal("Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant.", devEntry.Message);
    }

    [Fact]
    public void Scan_TwoDevFoldersWithTheSameId_SecondIsInvalid()
    {
        var first = Path.Combine(_root, "dev1");
        var second = Path.Combine(_root, "dev2");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "manifest.json"), Manifest("alex.dev", "0.1.0"));
        File.WriteAllText(Path.Combine(second, "manifest.json"), Manifest("alex.dev", "0.2.0"));

        var entries = Catalog(State(developerMode: true, devFolders: [new DevFolder(first, []), new DevFolder(second, [])])).Scan();

        Assert.Equal([CommunityState.Ready, CommunityState.Invalid], entries.Select(e => e.State).ToArray());
    }

    [Fact]
    public void Scan_FolderNameDiffersFromManifestId_IsInvalid()
    {
        // Un module ne prend pas l'identité d'un autre en se glissant dans son dossier.
        Install("alex.demo", "1.0.0", Manifest("autre.module", "1.0.0"));

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal("alex.demo", entry.Id);
        Assert.Equal(CommunityState.Invalid, entry.State);
        Assert.Null(entry.Manifest);
        Assert.Contains("autre.module", entry.Message);
    }

    [Fact]
    public void Scan_FolderVersionDiffersFromManifestVersion_IsInvalid()
    {
        Install("alex.demo", "1.0.0", Manifest("alex.demo", "2.0.0"));

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.Invalid, entry.State);
        Assert.Null(entry.Manifest);
        Assert.Contains("2.0.0", entry.Message);
    }

    [Fact]
    public void Scan_SeveralVersions_ApprovedOneWins()
    {
        var old = Install("alex.demo", "1.0.0");
        Install("alex.demo", "2.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal("1.0.0", entry.Manifest!.Version);
        Assert.Equal(CommunityState.Ready, entry.State);
    }

    [Fact]
    public void Scan_SeveralVersionsNoneApproved_MostRecentByVersionOrderNotByString()
    {
        Install("alex.demo", "1.9.0");
        Install("alex.demo", "1.10.0");
        Install("alex.demo", "1.10.0-beta.1");

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal("1.10.0", entry.Manifest!.Version);
        Assert.Equal(CommunityState.NeedsApproval, entry.State);
    }

    [Fact]
    public void Scan_NewVersionOfApprovedModule_NeedsApprovalAndKeepsOldApproval()
    {
        var old = Install("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);
        Directory.Delete(old, recursive: true);
        Install("alex.demo", "1.1.0");

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(CommunityState.NeedsApproval, entry.State);
        Assert.Equal("1.1.0", entry.Manifest!.Version);
        Assert.Same(approval, entry.Approval);
    }

    [Fact]
    public void Scan_UnparsableVersionFolder_IsInvalidEntry()
    {
        Install("alex.demo", "1.0.0");
        Directory.CreateDirectory(Path.Combine(ModulesRoot, "alex.demo", "pas-une-version"));

        var entries = Catalog(State()).Scan();

        Assert.Equal(2, entries.Count);
        var invalid = entries.Single(e => e.State == CommunityState.Invalid);
        Assert.Equal("alex.demo", invalid.Id);
        Assert.Contains("pas-une-version", invalid.Message);
        Assert.Equal(CommunityState.NeedsApproval, entries.Single(e => e.State != CommunityState.Invalid).State);
    }

    [Fact]
    public void Scan_MissingModulesFolder_IsEmpty()
    {
        Assert.Empty(Catalog(State()).Scan());
    }

    [Fact]
    public void Scan_FileHeldOpenExclusively_IsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Install("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", directory);
        using var held = new FileStream(Path.Combine(directory, "Mod.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(CommunityState.Unavailable, entry.State);
        Assert.Equal("Fichiers du module indisponibles (OneDrive ?) : choisis un dossier non synchronisé dans les Paramètres.", entry.Message);
    }

    [Fact]
    public void Scan_ManifestHeldOpenExclusively_IsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Install("alex.demo", "1.0.0");
        using var held = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var entry = Assert.Single(Catalog(State()).Scan());

        Assert.Equal(CommunityState.Unavailable, entry.State);
    }

    // ---- Dossiers qui ne sont pas des modules ----

    [Fact]
    public void Scan_FolderWhoseNameIsNotAModuleId_IsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(ModulesRoot, "Photos", "2024"));
        File.WriteAllText(Path.Combine(ModulesRoot, "Photos", "2024", "vacances.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(ModulesRoot, "network", "1.0.0")); // un type d'origine n'est pas un module de la communauté
        var directory = Install("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", directory)])).Scan());

        Assert.Equal("alex.demo", entry.Id);
    }

    // ---- Modules validés dans l'ancien dossier des modules ----

    /// <summary>Crée un module dans un ancien dossier des modules (<c>ancien\id\version</c>), comme après un changement de dossier.</summary>
    private string InstallInOldRoot(string id, string version, string content = "code")
    {
        var directory = Path.Combine(_root, "ancien", id, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), Manifest(id, version));
        File.WriteAllText(Path.Combine(directory, "Mod.dll"), content);
        return directory;
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_IsStillListedAndReady()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", old)])).Scan());

        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.Equal(old, entry.Directory);
        Assert.NotNull(entry.Approval);
    }

    [Fact]
    public void Scan_OldFolderInsideTheNewModulesFolder_KeepsTheModuleVisible()
    {
        // Le nouveau dossier des modules est le parent de l'ancien : le scan du nouveau dossier ne lit pas « Neuf », mais le module reste là.
        var directory = Path.Combine(ModulesRoot, "Neuf", "alex.demo", "1.0.0");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), Manifest("alex.demo", "1.0.0"));
        File.WriteAllText(Path.Combine(directory, "Mod.dll"), "code");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", directory)])).Scan());

        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.Equal(directory, entry.Directory);
    }

    [Fact]
    public void ForeignVersionDirectory_IsNullOnlyForTheExactOrdinaryShape()
    {
        var ordinary = Path.Combine(ModulesRoot, "alex.demo", "1.0.0");
        var inside = Path.Combine(ModulesRoot, "Neuf", "alex.demo", "1.0.0");

        static ApprovedModule At(string directory) => new("alex.demo", "1.0.0", directory, "h", [], null, null);

        Assert.Null(CommunityCatalog.ForeignVersionDirectory(At(ordinary), ModulesRoot));
        Assert.Null(CommunityCatalog.ForeignVersionDirectory(At(ordinary + Path.DirectorySeparatorChar), ModulesRoot));
        Assert.Equal(inside, CommunityCatalog.ForeignVersionDirectory(At(inside), ModulesRoot));
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_ChangedContent_IsChanged()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);
        File.WriteAllText(Path.Combine(old, "Mod.dll"), "autre code");

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(CommunityState.Changed, entry.State);
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_SetAside_StaysSetAside()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([Approve("alex.demo", "1.0.0", old, setAside: "Plantages répétés.")])).Scan());

        Assert.Equal(CommunityState.SetAside, entry.State);
    }

    [Fact]
    public void Scan_ApprovedModuleWhoseFolderIsGone_IsNotListed()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);
        Directory.Delete(old, recursive: true);

        Assert.Empty(Catalog(State([approval])).Scan());
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_AndTheSameIdInTheRoot_ChoosesTheOneWhoseHashMatches()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0", content: "code validé");
        var approval = Approve("alex.demo", "1.0.0", old);
        Install("alex.demo", "1.0.0", content: "code déposé à la main"); // même identifiant, même version, autre contenu

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(CommunityState.Ready, entry.State);
        Assert.Equal(old, entry.Directory);
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_ChangedAndACopyInTheRoot_ShowsTheCopyInTheRoot()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);
        File.WriteAllText(Path.Combine(old, "Mod.dll"), "abîmé");
        var copy = Install("alex.demo", "1.0.0");

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(copy, entry.Directory);
        Assert.Equal(CommunityState.Ready, entry.State); // la copie a l'empreinte validée : elle prend la place de l'original abîmé
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_ChangedAndADifferentCopyInTheRoot_ShowsTheCopyInTheRootAsChanged()
    {
        var old = InstallInOldRoot("alex.demo", "1.0.0");
        var approval = Approve("alex.demo", "1.0.0", old);
        File.WriteAllText(Path.Combine(old, "Mod.dll"), "abîmé");
        var copy = Install("alex.demo", "1.0.0", content: "autre contenu");

        var entry = Assert.Single(Catalog(State([approval])).Scan());

        Assert.Equal(copy, entry.Directory);
        Assert.Equal(CommunityState.Changed, entry.State);
    }

    [Fact]
    public void Scan_ApprovedModuleInAnotherRoot_SortsById()
    {
        var old = InstallInOldRoot("zed.demo", "1.0.0");
        var inRoot = Install("alex.demo", "1.0.0");

        var entries = Catalog(State([Approve("zed.demo", "1.0.0", old), Approve("alex.demo", "1.0.0", inRoot)])).Scan();

        Assert.Equal(["alex.demo", "zed.demo"], entries.Select(e => e.Id));
    }

    [Theory]
    [InlineData("pas-le-bon-id", "1.0.0")] // le dossier de l'identifiant ne porte pas l'identifiant
    [InlineData("alex.demo", "9.9.9")] // le dossier de la version ne porte pas la version
    public void Scan_ApprovalWhoseFolderDoesNotEndWithIdAndVersion_IsIgnored(string idFolder, string versionFolder)
    {
        var folder = Path.Combine(_root, "ailleurs", idFolder, versionFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest("alex.demo", "1.0.0"));
        File.WriteAllText(Path.Combine(folder, "Mod.dll"), "code");

        Assert.Empty(Catalog(State([Approve("alex.demo", "1.0.0", folder)])).Scan());
    }

    [Fact]
    public void Scan_ApprovalWithARelativeOrHostileFolder_IsIgnored()
    {
        var documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(documents);
        var approvals = new[]
        {
            new ApprovedModule("alex.demo", "1.0.0", documents, "h", [], null, null),
            new ApprovedModule("alex.demo", "1.0.0", @"alex.demo\1.0.0", "h", [], null, null),
            new ApprovedModule("alex.demo", "1.0.0", "", "h", [], null, null),
        };

        foreach (var approval in approvals)
        {
            Assert.Empty(Catalog(State([approval])).Scan());
        }
    }

    [Fact]
    public void ModulesFolder_Resolve_UsesChosenRootOrDefault()
    {
        Assert.Equal(@"D:\Mes modules", ModulesFolder.Resolve(State() with { ModulesRoot = @"D:\Mes modules" }));
        Assert.Equal(ModulesFolder.Default, ModulesFolder.Resolve(State() with { ModulesRoot = null }));
        Assert.Equal(ModulesFolder.Default, ModulesFolder.Resolve(ApprovedModules.Empty));
        Assert.EndsWith(Path.Combine("Ashboard Vision", "Modules"), ModulesFolder.Default);
    }
}
