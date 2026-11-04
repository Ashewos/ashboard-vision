using AshboardVision.Sdk;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public sealed class ApprovedModulesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "av-approved-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    public ApprovedModulesStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "modules.json");

    private string KeyPath => Path.Combine(_dir, "modules.key");

    private ApprovedModulesStore NewStore(string? file = null, string? key = null) =>
        new(file ?? FilePath, key ?? KeyPath, _warnings.Add);

    private static ApprovedModules Sample() => new(
        @"D:\Modules",
        DeveloperMode: true,
        [new DevFolder(@"D:\dev\mon-module", [ModulePermission.Sensors])],
        [
            new ApprovedModule("alex.depot-github", "1.2.0", @"D:\Modules\alex.depot-github\1.2.0", "sha256:abc",
                [ModulePermission.Internet, ModulePermission.ThisComputer], null, "Ashewos/depot"),
            new ApprovedModule("alex.demo", "0.1.0", @"D:\Modules\alex.demo\0.1.0", "sha256:def", [], "Trop de pannes", null),
        ]);

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = NewStore();
        var saved = Sample();

        store.Save(saved);
        var load = store.Load();

        Assert.False(load.Tampered);
        Assert.Equal(saved.ModulesRoot, load.Modules.ModulesRoot);
        Assert.True(load.Modules.DeveloperMode);
        var dev = Assert.Single(load.Modules.DevFolders);
        Assert.Equal(@"D:\dev\mon-module", dev.Path);
        Assert.Equal([ModulePermission.Sensors], dev.Granted);
        Assert.Equal(2, load.Modules.Modules.Count);
        var first = load.Modules.Modules[0];
        Assert.Equal("alex.depot-github", first.Id);
        Assert.Equal("1.2.0", first.Version);
        Assert.Equal(@"D:\Modules\alex.depot-github\1.2.0", first.Directory);
        Assert.Equal("sha256:abc", first.ContentHash);
        Assert.Equal([ModulePermission.Internet, ModulePermission.ThisComputer], first.Granted);
        Assert.Null(first.SetAsideReason);
        Assert.Equal("Ashewos/depot", first.Repository);
        var second = load.Modules.Modules[1];
        Assert.Empty(second.Granted);
        Assert.Equal("Trop de pannes", second.SetAsideReason);
        Assert.Null(second.Repository);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Save_WritesPermissionsAsManifestNames()
    {
        NewStore().Save(Sample());

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"ce-pc\"", text);
        Assert.Contains("\"internet\"", text);
    }

    [Fact]
    public void Load_Missing_IsEmptyNotTampered()
    {
        var load = NewStore().Load();

        Assert.False(load.Tampered);
        Assert.Same(ApprovedModules.Empty, load.Modules);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Load_EditedByHand_IsTampered()
    {
        var store = NewStore();
        store.Save(Sample());
        var text = File.ReadAllText(FilePath);
        var edited = text.Replace("\"internet\"", "\"internet\", \"capteurs\"");
        Assert.NotEqual(text, edited);
        File.WriteAllText(FilePath, edited);

        var load = store.Load();

        Assert.True(load.Tampered);
        Assert.Empty(load.Modules.Modules);
        Assert.Single(_warnings);
    }

    [Fact]
    public void Load_PropertiesReorderedByHand_IsTampered()
    {
        var store = NewStore();
        store.Save(new ApprovedModules(@"D:\M", false, [], []));
        var text = File.ReadAllText(FilePath);
        var reordered = text
            .Replace("\"modulesRoot\": \"D:\\\\M\",", "@@")
            .Replace("\"developerMode\": false,", "\"developerMode\": false, \"modulesRoot\": \"D:\\\\M\",")
            .Replace("@@", string.Empty);
        Assert.NotEqual(text, reordered);
        File.WriteAllText(FilePath, reordered);

        Assert.True(store.Load().Tampered);
    }

    [Fact]
    public void Load_WhitespaceChangedInData_IsTampered()
    {
        var store = NewStore();
        store.Save(Sample());
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("\"developerMode\": true", "\"developerMode\":  true"));

        Assert.True(store.Load().Tampered);
    }

    [Fact]
    public void Load_KeyReplaced_IsTampered()
    {
        var store = NewStore();
        store.Save(Sample());
        var otherKey = Path.Combine(_dir, "autre", "modules.key");
        NewStore(Path.Combine(_dir, "autre", "modules.json"), otherKey).Save(ApprovedModules.Empty);
        File.Copy(otherKey, KeyPath, overwrite: true);

        var load = store.Load();

        Assert.True(load.Tampered);
        Assert.Empty(load.Modules.Modules);
    }

    [Fact]
    public void Load_KeyMissing_IsTampered()
    {
        var store = NewStore();
        store.Save(Sample());
        File.Delete(KeyPath);

        Assert.True(store.Load().Tampered);
    }

    [Fact]
    public void Load_Garbage_IsTampered()
    {
        File.WriteAllText(FilePath, "ceci n'est pas du JSON");

        var load = NewStore().Load();

        Assert.True(load.Tampered);
        Assert.Same(ApprovedModules.Empty, load.Modules);
        Assert.Single(_warnings);
    }

    [Fact]
    public void Load_WellFormedJsonWithoutSeal_IsTampered()
    {
        var store = NewStore();
        store.Save(Sample());
        File.WriteAllText(FilePath, "{ \"data\": { \"developerMode\": true, \"modules\": [] } }");

        Assert.True(store.Load().Tampered);
    }

    [Fact]
    public void Save_WhenKeyUnreadable_CreatesNewKeyAndLoadsAgain()
    {
        var store = NewStore();
        store.Save(Sample());
        File.WriteAllText(KeyPath, "clé illisible");
        Assert.True(store.Load().Tampered);

        store.Save(Sample());
        var load = store.Load();

        Assert.False(load.Tampered);
        Assert.Equal(2, load.Modules.Modules.Count);
    }

    [Fact]
    public void Load_FileLocked_IsUnreadableNotTampered()
    {
        var store = NewStore();
        store.Save(Sample());

        ApprovedLoad load;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load = store.Load();
        }

        Assert.True(load.Unreadable);
        Assert.False(load.Tampered);
        Assert.Empty(load.Modules.Modules);
        Assert.Single(_warnings);
        Assert.False(store.Load().Unreadable);
        Assert.Equal(2, store.Load().Modules.Modules.Count);
    }

    [Fact]
    public void Load_KeyLocked_IsUnreadableNotTampered()
    {
        var store = NewStore();
        store.Save(Sample());

        ApprovedLoad load;
        using (new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load = store.Load();
        }

        Assert.True(load.Unreadable);
        Assert.False(load.Tampered);
    }

    [Fact]
    public void Save_KeyLocked_ThrowsAndKeepsKeyAndList()
    {
        var store = NewStore();
        store.Save(Sample());
        var keyBefore = File.ReadAllBytes(KeyPath);
        var listBefore = File.ReadAllBytes(FilePath);

        using (new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Save(ApprovedModules.Empty));
        }

        Assert.Equal(keyBefore, File.ReadAllBytes(KeyPath));
        Assert.Equal(listBefore, File.ReadAllBytes(FilePath));
        var load = store.Load();
        Assert.False(load.Tampered);
        Assert.Equal(2, load.Modules.Modules.Count);
    }

    [Fact]
    public void Save_NewKey_DoesNotTouchExistingListWhenListTemporaryFileCannotBeWritten()
    {
        var store = NewStore();
        store.Save(Sample());
        File.Delete(KeyPath);
        var listBefore = File.ReadAllBytes(FilePath);
        using (new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(ApprovedModules.Empty));
        }

        Assert.False(File.Exists(KeyPath));
        Assert.Equal(listBefore, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Load_FolderMissing_IsEmptyNotTamperedNorUnreadable()
    {
        var load = NewStore(Path.Combine(_dir, "absent", "modules.json")).Load();

        Assert.False(load.Tampered);
        Assert.False(load.Unreadable);
    }

    [Fact]
    public void Save_Twice_ReusesKeyAndLeavesNoTemporaryFile()
    {
        var store = NewStore();
        store.Save(Sample());
        var key = File.ReadAllBytes(KeyPath);

        store.Save(ApprovedModules.Empty);

        Assert.Equal(key, File.ReadAllBytes(KeyPath));
        Assert.False(store.Load().Tampered);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // --- Inspect : ce que la désinstallation peut lire, et jusqu'où elle peut le croire -----------------------------------------------

    [Fact]
    public void Inspect_Missing_IsAbsent()
    {
        var inspection = NewStore().Inspect();

        Assert.Equal(ListTrust.Absent, inspection.Trust);
        Assert.Empty(inspection.ModuleIds);
        Assert.Empty(inspection.DevFolderPaths);
    }

    [Fact]
    public void Inspect_Sealed_IsVerifiedWithTheContent()
    {
        var store = NewStore();
        store.Save(Sample());

        var inspection = store.Inspect();

        Assert.Equal(ListTrust.Verified, inspection.Trust);
        Assert.Equal(@"D:\Modules", inspection.ModulesRoot);
        Assert.Equal(["alex.depot-github", "alex.demo"], inspection.ModuleIds);
        Assert.Equal([@"D:\dev\mon-module"], inspection.DevFolderPaths);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Inspect_EditedByHand_IsUnverifiedButStillReadable()
    {
        var store = NewStore();
        store.Save(Sample());
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("alex.demo", "alex.autre"));

        var inspection = store.Inspect();

        Assert.Equal(ListTrust.Unverified, inspection.Trust);
        Assert.Null(inspection.ModulesRoot); // un emplacement qu'on ne peut pas croire n'est pas rendu
        Assert.Equal(["alex.depot-github", "alex.autre"], inspection.ModuleIds);
        Assert.Equal([@"D:\dev\mon-module"], inspection.DevFolderPaths);
        Assert.Single(_warnings);
    }

    [Fact]
    public void Inspect_KeyMissing_IsUnverified()
    {
        var store = NewStore();
        store.Save(Sample());
        File.Delete(KeyPath);

        var inspection = store.Inspect();

        Assert.Equal(ListTrust.Unverified, inspection.Trust);
        Assert.Equal(2, inspection.ModuleIds.Count);
    }

    [Fact]
    public void Inspect_Garbage_IsUnverifiedAndEmpty()
    {
        File.WriteAllText(FilePath, "{ pas du json");

        var inspection = NewStore().Inspect();

        Assert.Equal(ListTrust.Unverified, inspection.Trust);
        Assert.Empty(inspection.ModuleIds);
        Assert.Empty(inspection.DevFolderPaths);
    }

    [Fact]
    public void Inspect_WrongTypesInData_IsUnverifiedAndEmpty()
    {
        File.WriteAllText(FilePath, "{ \"data\": { \"modules\": [ { \"id\": 12 } ] }, \"mac\": \"AAAA\" }");

        var inspection = NewStore().Inspect();

        Assert.Equal(ListTrust.Unverified, inspection.Trust);
        Assert.Empty(inspection.ModuleIds);
    }

    [Fact]
    public void Inspect_FileLocked_IsUnreadable()
    {
        NewStore().Save(Sample());

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(ListTrust.Unreadable, NewStore().Inspect().Trust);
        }
    }
}
