using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Le nettoyage des modules complémentaires à la désinstallation. Rien de réel n'est touché : un dossier temporaire tient lieu de
/// dossier des réglages et de dossier Documents, et la suppression des conteneurs comme celle des portées du coffre sont des faux.
/// </summary>
public sealed partial class CommunityCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _profiles = [];
    private readonly List<string> _scopes = [];
    private readonly List<string> _warnings = [];

    public CommunityCleanupTests()
    {
        Directory.CreateDirectory(SettingsDir);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Purge(new DirectoryInfo(_root));

    /// <summary>Supprime le dossier de test sans jamais suivre une jonction (elle est retirée seule) et même si des fichiers sont en lecture seule.</summary>
    private static void Purge(DirectoryInfo folder)
    {
        if (!folder.Exists) return;
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            folder.Delete();
            return;
        }

        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                Purge(directory);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        folder.Delete();
    }

    private string SettingsDir => Path.Combine(_root, "reglages");

    private string ListPath => Path.Combine(SettingsDir, "modules.json");

    private string KeyPath => Path.Combine(SettingsDir, "modules.key");

    /// <summary>Le dossier « Documents\Ashboard Vision » de l'utilisateur.</summary>
    private string ProductFolder => Path.Combine(_root, "Documents", "Ashboard Vision");

    private string DefaultFolder => Path.Combine(ProductFolder, "Modules");

    private string Folder(params string[] parts) => Path.Combine([_root, .. parts]);

    private ApprovedModulesStore Store() => new(ListPath, KeyPath, _ => { });

    private CommunityCleanup Cleanup(Action<string>? deleteProfile = null, Action<string>? removeScope = null) => new(
        SettingsDir,
        deleteProfile ?? _profiles.Add,
        removeScope ?? _scopes.Add,
        DefaultFolder,
        _warnings.Add);

    private static ApprovedModule Module(string id) => new(id, "1.0.0", "x", "sha256:abc", [], null, null);

    private void SaveList(string? modulesRoot, string[] ids, params DevFolder[] devFolders) =>
        Store().Save(new ApprovedModules(modulesRoot, DeveloperMode: devFolders.Length > 0, devFolders, [.. ids.Select(Module)]));

    /// <summary>Le dossier par défaut avec un module installé dedans.</summary>
    private void FillDefaultFolder(string id = "alex.demo")
    {
        var version = Path.Combine(DefaultFolder, id, "1.0.0");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "Mod.dll"), "code");
    }

    private string DevFolderWithManifest(string name, string id)
    {
        var folder = Folder("dev", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), CommunityRig.ManifestJson(id, "0.1.0", [], null));
        return folder;
    }

    [GeneratedRegex("\"mac\": \"[^\"]*\"")]
    private static partial Regex MacProperty();

    /// <summary>Le sceau de la liste ne tient plus : quelqu'un a modifié le fichier.</summary>
    private void BreakSeal() =>
        File.WriteAllText(ListPath, MacProperty().Replace(File.ReadAllText(ListPath), "\"mac\": \"AAAA\""));

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private string Outside(string name)
    {
        var folder = Folder("dehors", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "important.txt"), "à garder");
        return folder;
    }

    // --- Les quatre cas du plan ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_DeletesModuleProfilesAndSecrets()
    {
        var dev = DevFolderWithManifest("mon-module", "dev.alpha");
        SaveList(null, ["alex.demo", "alex.depot-github"], new DevFolder(dev, []));

        Cleanup().Run(deleteSettings: false);

        Assert.Equal(["alex.demo", "alex.depot-github", "dev.alpha"], _profiles.Order());
        Assert.Equal(["alex.demo", "alex.depot-github", "dev.alpha"], _scopes.Order());
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Cleanup_EraseRemovesDefaultModulesFolder()
    {
        SaveList(null, ["alex.demo"]);
        FillDefaultFolder();

        Cleanup().Run(deleteSettings: true);

        Assert.False(Directory.Exists(DefaultFolder));
        Assert.False(Directory.Exists(ProductFolder)); // resté vide : retiré avec lui
        Assert.False(File.Exists(ListPath));
        Assert.False(File.Exists(KeyPath));
        Assert.Equal(["alex.demo"], _profiles);
    }

    [Fact]
    public void Cleanup_EraseKeepsCustomModulesFolder()
    {
        var custom = Folder("mes-modules");
        Directory.CreateDirectory(Path.Combine(custom, "alex.demo", "1.0.0"));
        File.WriteAllText(Path.Combine(custom, "alex.demo", "1.0.0", "Mod.dll"), "code");
        SaveList(custom, ["alex.demo"]);
        FillDefaultFolder("autre.module"); // un reste du dossier par défaut n'est pas le dossier choisi : on n'y touche pas non plus

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(custom, "alex.demo", "1.0.0", "Mod.dll")));
        Assert.True(Directory.Exists(DefaultFolder));
        Assert.False(File.Exists(ListPath));
        Assert.Equal(["alex.demo"], _profiles); // son conteneur part quand même
    }

    [Fact]
    public void Cleanup_EraseKeepsEmptyCustomModulesFolder()
    {
        var custom = Folder("mes-modules");
        Directory.CreateDirectory(custom);
        SaveList(custom, []);

        Cleanup().Run(deleteSettings: true);

        Assert.True(Directory.Exists(custom));
    }

    [Fact]
    public void Cleanup_KeepLeavesModulesFolder()
    {
        SaveList(null, ["alex.demo"]);
        FillDefaultFolder();

        Cleanup().Run(deleteSettings: false);

        Assert.True(File.Exists(Path.Combine(DefaultFolder, "alex.demo", "1.0.0", "Mod.dll")));
        Assert.True(File.Exists(ListPath));
        Assert.True(File.Exists(KeyPath));
        Assert.Equal(["alex.demo"], _profiles); // les conteneurs et les secrets, eux, partent toujours
        Assert.Equal(["alex.demo"], _scopes);
    }

    // --- R27 : ce que la liste dit n'est cru que si son sceau tient ---------------------------------------------------------------

    [Fact]
    public void Cleanup_NoList_UsesDefaultFolder()
    {
        FillDefaultFolder();

        Cleanup().Run(deleteSettings: true);

        Assert.False(Directory.Exists(DefaultFolder));
        Assert.Empty(_profiles);
        Assert.Empty(_scopes);
    }

    [Fact]
    public void Cleanup_NoList_KeepLeavesDefaultFolder()
    {
        FillDefaultFolder();

        Cleanup().Run(deleteSettings: false);

        Assert.True(Directory.Exists(DefaultFolder));
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("key-missing")]
    [InlineData("not-json")]
    public void Cleanup_UntrustedList_UsesOnlyConformingIdsAndKeepsTheFolder(string damage)
    {
        var victim = Outside("victime");
        string[] ids =
        [
            "good.one", "network", "clock", @"..\..\evil", "Good.Upper", "a.b/c", "ashboard-vision.mod.x.y", "a b.c", "", "-a.b",
            new string('a', 39) + ".b",
        ];
        SaveList(victim, ids);
        FillDefaultFolder();
        switch (damage)
        {
            case "seal":
                BreakSeal();
                break;
            case "key-missing":
                File.Delete(KeyPath);
                break;
            default:
                File.WriteAllText(ListPath, "{ pas du json");
                break;
        }

        Cleanup().Run(deleteSettings: true);

        // Jamais autre chose qu'un identifiant conforme à la règle (un type d'origine n'a pas de point), jamais un dossier.
        string[] expected = damage == "not-json" ? [] : ["good.one"];
        Assert.Equal(expected, _profiles);
        Assert.Equal(expected, _scopes);
        Assert.True(File.Exists(Path.Combine(victim, "important.txt")));
        Assert.True(File.Exists(Path.Combine(DefaultFolder, "alex.demo", "1.0.0", "Mod.dll"))); // son emplacement n'est pas sûr
        Assert.False(File.Exists(ListPath)); // la liste elle-même part avec les réglages
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Cleanup_UntrustedList_SayingDefaultFolder_StillKeepsIt()
    {
        SaveList(DefaultFolder, ["good.one"]);
        FillDefaultFolder();
        BreakSeal();

        Cleanup().Run(deleteSettings: true);

        Assert.True(Directory.Exists(DefaultFolder));
    }

    [Fact]
    public void Cleanup_UntrustedList_ReadsDevFolderManifestsOnLocalPathsOnly()
    {
        var dev = DevFolderWithManifest("mon-module", "dev.alpha");
        SaveList(null, [], new DevFolder(dev, []), new DevFolder(@"\\serveur-inconnu\partage\module", []));
        BreakSeal();

        Cleanup().Run(deleteSettings: false);

        Assert.Equal(["dev.alpha"], _profiles);
    }

    [Theory]
    [InlineData(@"\\serveur\partage\module", false)]
    [InlineData(@"\\?\C:\module", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"relatif\module", false)]
    [InlineData("", false)]
    [InlineData(@"C:\module", true)]
    [InlineData(@"D:\dev\mon-module", true)]
    public void IsLocalAbsolutePath_AcceptsOnlyLocalFullyQualifiedPaths(string path, bool expected) =>
        Assert.Equal(expected, CommunityCleanup.IsLocalAbsolutePath(path));

    [Fact]
    public void Cleanup_UnreadableList_ChangesNothingAndKeepsTheFolder()
    {
        SaveList(null, ["good.one"]);
        FillDefaultFolder();

        using (new FileStream(ListPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Cleanup().Run(deleteSettings: true);
        }

        Assert.Empty(_profiles); // une liste verrouillée n'est pas une liste altérée, mais on n'en tire rien non plus
        Assert.True(Directory.Exists(DefaultFolder));
    }

    // --- Dossiers de travail ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_DevFolders_ProvideTheirIdsButAreNeverDeleted()
    {
        var dev = DevFolderWithManifest("mon-module", "dev.alpha");
        var missing = Folder("dev", "disparu");
        var broken = Folder("dev", "casse");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{ pas du json");
        var wrongId = DevFolderWithManifest("type-origine", "network");
        SaveList(null, ["alex.demo"], new DevFolder(dev, []), new DevFolder(missing, []), new DevFolder(broken, []), new DevFolder("relatif", []),
            new DevFolder(wrongId, []));
        FillDefaultFolder();

        Cleanup().Run(deleteSettings: true);

        Assert.Equal(["alex.demo", "dev.alpha"], _profiles.Order());
        Assert.Equal(["alex.demo", "dev.alpha"], _scopes.Order());
        Assert.True(File.Exists(Path.Combine(dev, "manifest.json")));
        Assert.True(Directory.Exists(broken));
        Assert.True(Directory.Exists(wrongId));
        Assert.False(Directory.Exists(DefaultFolder));
    }

    [Fact]
    public void Cleanup_DevFolderInsideDefaultFolder_IsKeptWithItsAncestors()
    {
        var dev = Path.Combine(DefaultFolder, "mon-module-en-cours");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "manifest.json"), CommunityRig.ManifestJson("dev.alpha", "0.1.0", [], null));
        FillDefaultFolder();
        SaveList(null, ["alex.demo"], new DevFolder(dev, []));

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(dev, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(DefaultFolder, "alex.demo")));
        Assert.True(Directory.Exists(ProductFolder));
    }

    // --- Comparaison du dossier, liens, dossier parent --------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_RootNamingTheDefaultFolderAnotherWay_IsTheDefaultFolder()
    {
        FillDefaultFolder();
        SaveList(DefaultFolder.ToUpperInvariant() + Path.DirectorySeparatorChar + @".\", ["alex.demo"]);

        Cleanup().Run(deleteSettings: true);

        Assert.False(Directory.Exists(DefaultFolder));
    }

    [Fact]
    public void Cleanup_RelativeRoot_IsNeverTheDefaultFolder()
    {
        FillDefaultFolder();
        SaveList(@"Modules", ["alex.demo"]);

        Cleanup().Run(deleteSettings: true);

        Assert.True(Directory.Exists(DefaultFolder));
    }

    [Fact]
    public void Cleanup_Erase_DoesNotFollowAJunctionInsideTheDefaultFolder()
    {
        var outside = Outside("cible");
        FillDefaultFolder();
        Junction(Path.Combine(DefaultFolder, "lien"), outside);
        SaveList(null, ["alex.demo"]);

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(outside, "important.txt")));
        Assert.False(Directory.Exists(DefaultFolder));
    }

    [Fact]
    public void Cleanup_Erase_DoesNotFollowAJunctionAsTheDefaultFolder()
    {
        var outside = Outside("cible");
        Directory.CreateDirectory(ProductFolder);
        Junction(DefaultFolder, outside);
        SaveList(null, []);

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(outside, "important.txt")));
        Assert.False(Directory.Exists(DefaultFolder)); // seul le lien est parti
    }

    [Fact]
    public void Cleanup_Erase_LeavesEverythingWhenTheProductFolderIsAJunction()
    {
        var outside = Outside("cible");
        Directory.CreateDirectory(Path.Combine(outside, "Modules", "alex.demo"));
        File.WriteAllText(Path.Combine(outside, "Modules", "alex.demo", "Mod.dll"), "code");
        Directory.CreateDirectory(Path.GetDirectoryName(ProductFolder)!);
        Junction(ProductFolder, outside);
        SaveList(null, []);

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(outside, "Modules", "alex.demo", "Mod.dll")));
        Assert.True(File.Exists(Path.Combine(outside, "important.txt")));
    }

    [Fact]
    public void Cleanup_Erase_KeepsTheProductFolderWhenSomethingElseIsInIt()
    {
        FillDefaultFolder();
        File.WriteAllText(Path.Combine(ProductFolder, "notes.txt"), "à moi");
        SaveList(null, []);

        Cleanup().Run(deleteSettings: true);

        Assert.False(Directory.Exists(DefaultFolder));
        Assert.True(File.Exists(Path.Combine(ProductFolder, "notes.txt")));
    }

    [Fact]
    public void Cleanup_Erase_RemovesReadOnlyFiles()
    {
        FillDefaultFolder();
        var file = Path.Combine(DefaultFolder, "alex.demo", "1.0.0", "Mod.dll");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        SaveList(null, []);

        Cleanup().Run(deleteSettings: true);

        Assert.False(Directory.Exists(DefaultFolder));
    }

    // --- Chaque étape est tentée --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_AFailingStepDoesNotStopTheOthers()
    {
        SaveList(null, ["alex.un", "alex.deux", "alex.trois"]);
        FillDefaultFolder();
        var attempted = new List<string>();

        Cleanup(
            deleteProfile: id =>
            {
                attempted.Add("profil " + id);
                if (id == "alex.un") throw new COMException("profil refusé");
            },
            removeScope: id =>
            {
                attempted.Add("portée " + id);
                if (id == "alex.deux") throw new IOException("coffre verrouillé");
            }).Run(deleteSettings: true);

        Assert.Equal(
            ["profil alex.un", "portée alex.un", "profil alex.deux", "portée alex.deux", "profil alex.trois", "portée alex.trois"],
            attempted);
        Assert.Equal(2, _warnings.Count);
        Assert.False(Directory.Exists(DefaultFolder));
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public void Cleanup_DuplicateIds_AreHandledOnce()
    {
        var dev = DevFolderWithManifest("mon-module", "alex.demo");
        SaveList(null, ["alex.demo", "alex.demo"], new DevFolder(dev, []));

        Cleanup().Run(deleteSettings: false);

        Assert.Equal(["alex.demo"], _profiles);
    }

    // --- Correctifs de la relecture --------------------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_Erase_AnUnreadableSubfolderDoesNotStopTheRest()
    {
        // I1 : un sous-dossier dont l'énumération échoue reste en place ; ses frères, la liste et sa clé partent quand même.
        SaveList(null, ["alex.demo"]);
        FillDefaultFolder("alex.demo");
        FillDefaultFolder("alex.autre");
        var blocked = Path.Combine(DefaultFolder, "bloque");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "Mod.dll"), "code");
        var info = new DirectoryInfo(blocked);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            Cleanup().Run(deleteSettings: true);
        }
        finally
        {
            security.RemoveAccessRule(rule); // les droits reviennent avant le nettoyage du test
            info.SetAccessControl(security);
        }

        Assert.False(Directory.Exists(Path.Combine(DefaultFolder, "alex.demo")));
        Assert.False(Directory.Exists(Path.Combine(DefaultFolder, "alex.autre")));
        Assert.True(File.Exists(Path.Combine(blocked, "Mod.dll")));
        Assert.False(File.Exists(ListPath));
        Assert.False(File.Exists(KeyPath));
        Assert.Contains(_warnings, w => w.Contains(blocked, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsRedirecting_OnlyALinkWithATargetRedirects()
    {
        // I2 : un point d'analyse sans cible (dossier synchronisé par OneDrive…) se traite comme un dossier normal.
        Assert.False(CommunityCleanup.IsRedirecting(() => null));
        Assert.True(CommunityCleanup.IsRedirecting(() => @"D:illeurs"));
        Assert.True(CommunityCleanup.IsRedirecting(() => throw new IOException("illisible")));
        Assert.True(CommunityCleanup.IsRedirecting(() => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void Cleanup_Erase_RelativeDefaultFolder_DoesNothing()
    {
        // M2 : « Documents » vide donnerait un dossier par défaut relatif, résolu depuis le dossier courant.
        var relative = "av-cleanup-relatif-" + Guid.NewGuid().ToString("N");
        var full = Path.GetFullPath(relative);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "temoin.txt"), "à garder");
        try
        {
            new CommunityCleanup(SettingsDir, _profiles.Add, _scopes.Add, relative, _warnings.Add).Run(deleteSettings: true);

            Assert.True(File.Exists(Path.Combine(full, "temoin.txt")));
        }
        finally
        {
            Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public void UsableDevFolders_NeverReachTheNetwork_EvenWithASealedList()
    {
        // M3 : un dossier de travail sur un partage réseau n'est pas ouvert, même dans une liste dont le sceau tient ; son conteneur et
        // ses secrets restent alors en place (son identifiant ne peut pas être lu sans toucher au réseau).
        var inspection = new ListInspection(ListTrust.Verified, null, [], [@"\serveur\partage\module", @"D:\dev\mon-module", "relatif"]);

        Assert.Equal([@"D:\dev\mon-module"], CommunityCleanup.UsableDevFolders(inspection));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("child")]
    [InlineData("device")]
    public void Cleanup_SealedRootNextToTheDefaultFolder_KeepsEverything(string form)
    {
        // M5 : ni le parent du dossier par défaut, ni un de ses enfants, ni sa forme « \\?\ » ne sont le dossier par défaut.
        FillDefaultFolder();
        var root = form switch
        {
            "parent" => ProductFolder,
            "child" => Path.Combine(DefaultFolder, "alex.demo"),
            _ => @"\\?\" + DefaultFolder,
        };
        SaveList(root, ["alex.demo"]);

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(DefaultFolder, "alex.demo", "1.0.0", "Mod.dll")));
    }

    [Fact]
    public void Cleanup_DevFolderEqualToTheDefaultFolder_IsNotDeleted()
    {
        FillDefaultFolder();
        File.WriteAllText(Path.Combine(DefaultFolder, "manifest.json"), CommunityRig.ManifestJson("dev.alpha", "0.1.0", [], null));
        SaveList(null, [], new DevFolder(DefaultFolder, []));

        Cleanup().Run(deleteSettings: true);

        Assert.True(File.Exists(Path.Combine(DefaultFolder, "manifest.json")));
        Assert.Equal(["dev.alpha"], _profiles);
    }
}
