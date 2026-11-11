using AshboardVision.Sdk;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// <see cref="CommunityModules.InspectDevFolder"/> : la fenêtre de réglage lit le manifeste d'un dossier de travail avant de l'ajouter. Rien ne
/// tourne, rien ne s'écrit, et chaque refus est dit dans les erreurs.
/// </summary>
[Collection("Hosts")]
public sealed class CommunityModulesDevFolderTests : IAsyncLifetime
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

    private CommunityRig NewRig()
    {
        var rig = new CommunityRig();
        _rigs.Add(rig);
        return rig;
    }

    private static string Folder(CommunityRig rig, string id, string name, IReadOnlyList<ModulePermission>? declared = null, string sdk = "1.0")
    {
        var folder = rig.NewFolder(name);
        File.WriteAllText(
            Path.Combine(folder, "manifest.json"),
            CommunityRig.ManifestJson(id, "0.1.0", declared ?? InternetAndSensors, null).Replace("\"sdk\": \"1.0\"", $"\"sdk\": \"{sdk}\""));
        File.WriteAllText(Path.Combine(folder, "Mod.dll"), "code");
        return folder;
    }

    [Fact]
    public void InspectDevFolder_ReadsTheManifest_StartsNothing_ChangesNothing()
    {
        var rig = NewRig();
        var dev = Folder(rig, "alex.dev", "mon-module");
        Assert.True(rig.Community.SetDeveloperMode(true).Ok);
        var sealedList = Path.Combine(Directory.GetParent(rig.Root)!.FullName, "settings", "modules.json");
        var storeBefore = File.ReadAllBytes(sealedList);
        var changes = rig.Changes;

        var inspection = rig.Community.InspectDevFolder(dev);

        Assert.Empty(inspection.Errors);
        Assert.Null(inspection.Conflict);
        Assert.Null(inspection.Installed);
        Assert.Equal("alex.dev", inspection.Manifest!.Id);
        Assert.Equal(InternetAndSensors, inspection.Manifest.Permissions);
        Assert.Equal(0, rig.Launcher.Count);                               // aucun hôte n'est lancé
        Assert.Empty(rig.Approved.DevFolders);                              // le dossier n'est pas ajouté
        Assert.Empty(rig.Approved.Modules);
        Assert.Empty(rig.Community.Entries);
        Assert.Equal(changes, rig.Changes);                                 // et rien n'est annoncé
        Assert.Equal(storeBefore, File.ReadAllBytes(sealedList)); // la liste scellée est intacte
    }

    [Fact]
    public void InspectDevFolder_DeveloperModeOff_IsAnError()
    {
        var rig = NewRig();
        var dev = Folder(rig, "alex.dev", "mon-module");

        var inspection = rig.Community.InspectDevFolder(dev);

        Assert.Equal(["Allume d'abord le mode développeur."], inspection.Errors);
    }

    [Fact]
    public void InspectDevFolder_TamperedList_SaysSo_NotThatTheModeIsOff()
    {
        var rig = NewRig();
        var dev = Folder(rig, "alex.dev", "mon-module");
        Assert.True(rig.Community.SetDeveloperMode(true).Ok);
        var sealedList = Path.Combine(Directory.GetParent(rig.Root)!.FullName, "settings", "modules.json");
        File.WriteAllText(sealedList, File.ReadAllText(sealedList).Replace("\"developerMode\": true", "\"developerMode\": false")); // modifiée hors du logiciel

        var inspection = rig.Community.InspectDevFolder(dev);

        Assert.Equal(["La liste des modules validés a été modifiée hors du logiciel : valide de nouveau tes modules."], inspection.Errors);
    }

    [Fact]
    public void InspectDevFolder_PathRefusals_AreTheSameAsAddDevFolder()
    {
        var rig = NewRig();
        rig.Community.SetDeveloperMode(true);

        foreach (var path in new[] { "modules", "", @"C:\ce-dossier-n-existe-pas-" + Guid.NewGuid().ToString("N") })
        {
            var inspection = rig.Community.InspectDevFolder(path);
            var added = rig.Community.AddDevFolder(path, []);

            Assert.False(added.Ok);
            Assert.Equal([added.Message], inspection.Errors);
            Assert.Null(inspection.Manifest);
        }
    }

    [Fact]
    public void InspectDevFolder_UnreadableManifest_IsAnError()
    {
        var rig = NewRig();
        rig.Community.SetDeveloperMode(true);
        var empty = rig.NewFolder("vide");
        var broken = rig.NewFolder("casse");
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{ pas du json");

        var missing = rig.Community.InspectDevFolder(empty);
        var unreadable = rig.Community.InspectDevFolder(broken);

        Assert.Null(missing.Manifest);
        Assert.Contains("manifest.json", Assert.Single(missing.Errors));
        Assert.Null(unreadable.Manifest);
        Assert.Single(unreadable.Errors);
    }

    [Fact]
    public void InspectDevFolder_IncompatibleSdk_IsAnError()
    {
        var rig = NewRig();
        rig.Community.SetDeveloperMode(true);
        var dev = Folder(rig, "alex.dev", "futur", sdk: "9.0");

        var inspection = rig.Community.InspectDevFolder(dev);

        Assert.Single(inspection.Errors);
        Assert.NotNull(inspection.Manifest);
    }

    [Fact]
    public async Task InspectDevFolder_IdTakenByAnInstalledModule_IsAnError()
    {
        var rig = NewRig();
        rig.Community.SetDeveloperMode(true);
        var installed = await rig.Community.InstallAsync(rig.Package(id: "alex.demo"), Internet, CancellationToken.None);
        Assert.True(installed.Ok, installed.Message);
        var dev = Folder(rig, "alex.demo", "copie");

        var inspection = rig.Community.InspectDevFolder(dev);

        Assert.Equal(["Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant."], inspection.Errors);
        Assert.Null(inspection.Conflict);
        Assert.Null(inspection.Installed);
    }

    [Fact]
    public void InspectDevFolder_IdTakenByAnotherDevFolder_IsAnError_ButNotByItself()
    {
        var rig = NewRig();
        rig.Community.SetDeveloperMode(true);
        var first = Folder(rig, "alex.dev", "premier");
        var second = Folder(rig, "alex.dev", "second");
        Assert.True(rig.Community.AddDevFolder(first, Internet).Ok);

        var again = rig.Community.InspectDevFolder(first);          // le dossier déjà dans la liste : pas une collision
        var clash = rig.Community.InspectDevFolder(second);

        Assert.Empty(again.Errors);
        Assert.Equal(["Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant."], clash.Errors);
        Assert.Equal([first], rig.Approved.DevFolders.Select(f => f.Path));
    }
}
