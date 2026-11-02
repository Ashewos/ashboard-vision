using AshboardVision.Sdk;
using Trofeo.Distribution;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

// Les mises à jour proposées depuis GitHub, du rythme des vérifications à l'installation : rien n'est installé sans action de l'utilisateur,
// et ce qui est téléchargé est vérifié par son empreinte, puis par son contenu, avant de passer par l'installation ordinaire.
[Collection("Hosts")]
public sealed class CommunityUpdatesTests : IAsyncLifetime
{
    private const string Id = "alex.demo";
    private const string Repo = "alex/demo";
    private static readonly ModulePermission[] Internet = [ModulePermission.Internet];
    private static readonly ModulePermission[] InternetAndSensors = [ModulePermission.Internet, ModulePermission.Sensors];

    private readonly List<UpdateRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    private UpdateRig NewRig()
    {
        var rig = new UpdateRig();
        _rigs.Add(rig);
        return rig;
    }

    /// <summary>Un service avec un module installé, un faux GitHub, et le moteur des mises à jour branché dessus.</summary>
    private sealed class UpdateRig : IAsyncDisposable
    {
        public UpdateRig()
        {
            Rig = new CommunityRig();
            TempRoot = Path.Combine(Rig.NewFolder(), "telechargements");
            Updates = new ModuleUpdates(Rig.Community, new ModuleUpdateChecker(new UpdateChecker(Github.Client()), Clock), Github.Client(), Clock, TempRoot);
        }

        public CommunityRig Rig { get; }

        public FakeGitHub Github { get; } = new();

        public CountingClock Clock { get; } = new();

        public string TempRoot { get; }

        public ModuleUpdates Updates { get; }

        public async Task InstallAsync(string version = "1.0.0", string? repository = Repo, IReadOnlyList<ModulePermission>? declared = null)
        {
            var result = await Rig.Community.InstallAsync(Rig.Package(version: version, repository: repository, declared: declared ?? Internet), declared ?? Internet, CancellationToken.None);
            Assert.True(result.Ok, result.Message);
        }

        /// <summary>Publie une version sur le faux GitHub, avec son paquet (fabriqué vraiment) et son empreinte.</summary>
        public byte[] Publish(
            string version, IReadOnlyList<ModulePermission>? declared = null, string id = Id, string? repository = Repo, string notes = "Nouveautés.",
            string content = "neuf", string? checksum = null, params FakeRelease[] others)
        {
            var package = File.ReadAllBytes(Rig.Package(id: id, version: version, declared: declared ?? Internet, repository: repository, content: content));
            Github.Api(Repo, [new FakeRelease("v" + version, Body: notes, Assets: FakeGitHub.ModuleAssets(Repo, Id, version, package.Length)), .. others]);
            Github.Package(Repo, Id, version, package, checksum);
            return package;
        }

        public int TempEntries => Directory.Exists(TempRoot) ? Directory.GetFileSystemEntries(TempRoot).Length : 0;

        public async ValueTask DisposeAsync()
        {
            Updates.Dispose();
            await Rig.DisposeAsync();
        }
    }

    // --- Le rythme ----------------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task NothingInstalled_NoTimerAndNoRequest()
    {
        var rig = NewRig();

        rig.Clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(0, rig.Clock.TimersCreated);
        Assert.Empty(rig.Github.Requests);
        await Task.CompletedTask;
    }

    [WindowsHostFact]
    public async Task ModuleWithoutRepository_NoTimerAndNoRequest()
    {
        var rig = NewRig();
        await rig.InstallAsync(repository: null);

        rig.Clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(0, rig.Clock.TimersCreated);
        Assert.Empty(rig.Github.Requests);
    }

    [WindowsHostFact]
    public async Task ModuleWithRepository_FirstCheckAfterSixtySeconds_ThenEveryTwentyFourHours()
    {
        var rig = NewRig();
        rig.Publish("1.1.0");
        await rig.InstallAsync();

        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(await TestWait.StaysFalseAsync(() => rig.Github.ApiRequests > 0, TimeSpan.FromMilliseconds(200)));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => rig.Github.ApiRequests == 1);

        rig.Clock.Advance(TimeSpan.FromHours(23));
        Assert.True(await TestWait.StaysFalseAsync(() => rig.Github.ApiRequests > 1, TimeSpan.FromMilliseconds(200)));
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await TestWait.UntilAsync(() => rig.Github.ApiRequests == 2);
        Assert.Equal(1, rig.Clock.TimersAlive); // un seul minuteur, le même d'une fois à l'autre
    }

    [WindowsHostFact]
    public async Task LastModuleWithRepositoryUninstalled_TheTimerIsReleased()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        Assert.Equal(1, rig.Clock.TimersAlive);

        var result = await rig.Rig.Community.UninstallAsync(Id);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, rig.Clock.TimersAlive);
        rig.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(rig.Github.Requests);
    }

    [WindowsHostFact]
    public async Task Dispose_ReleasesTheTimer()
    {
        var rig = NewRig();
        await rig.InstallAsync();

        rig.Updates.Dispose();

        Assert.Equal(0, rig.Clock.TimersAlive);
        rig.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(rig.Github.Requests);
    }

    [WindowsHostFact]
    public async Task ManyChanges_ArmOnlyOneTimer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Rig.Community.Refresh();
        rig.Rig.Community.Refresh();

        Assert.Equal(1, rig.Clock.TimersCreated);
    }

    // --- Ce qui est proposé -------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Check_NewVersion_IsOfferedWithItsNotesAndItsNewPermissions_AndNothingIsInstalled()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors, notes: "Ajoute les capteurs.");

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var offer = rig.Updates.OfferFor(Id)!;
        Assert.Equal("1.1.0", offer.Version);
        Assert.Equal("Ajoute les capteurs.", offer.Notes);
        Assert.Equal([ModulePermission.Sensors], offer.NewPermissions);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version); // rien n'est installé tout seul
        Assert.False(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.1.0")));
        Assert.Equal(0, rig.TempEntries); // le paquet téléchargé pour lire son manifeste n'est pas gardé
    }

    [WindowsHostFact]
    public async Task Check_SamePermissions_NoNewPermission()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Empty(rig.Updates.OfferFor(Id)!.NewPermissions);
    }

    [WindowsHostFact]
    public async Task Check_NothingNewer_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Github.Api(Repo, new FakeRelease("v1.0.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.0.0")));

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_TheScheduledOneProposesWithoutInstalling()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");

        rig.Clock.Advance(TimeSpan.FromSeconds(60));

        await TestWait.UntilAsync(() => rig.Updates.OfferFor(Id) is not null);
        rig.Clock.AdvanceInSteps(TimeSpan.FromDays(3), TimeSpan.FromDays(1));
        await Task.Delay(200);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
    }

    [WindowsHostFact]
    public async Task Check_SameVersionAgain_IsNotDownloadedAgain()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var downloads = rig.Github.Requests.Count(uri => uri.Host == "github.com");
        Assert.Equal(2, downloads); // l'empreinte et le paquet

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Equal(downloads, rig.Github.Requests.Count(uri => uri.Host == "github.com"));
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)!.Version);
    }

    [WindowsHostFact]
    public async Task Check_NewerVersionReplacesTheOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        // La nouvelle version est publiée avant que le temps avance : la vérification planifiée, qui part au passage, voit la même chose.
        rig.Publish("1.2.0", notes: "Encore mieux.");
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var offer = rig.Updates.OfferFor(Id)!;
        Assert.Equal(("1.2.0", "Encore mieux."), (offer.Version, offer.Notes));
    }

    [WindowsHostFact]
    public async Task Check_VersionWithdrawn_OfferDisappears()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        rig.Github.Api(Repo); // plus aucune version
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_GitHubDown_KeepsWhatWasOffered_NoException()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Github.ApiRaw(Repo, () => throw new HttpRequestException("Pas de réseau."));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version); // un réseau absent ne retire pas ce qu'on savait
    }

    [WindowsHostFact]
    public async Task OfferDisappears_WhenTheProposedVersionGetsInstalledByOtherMeans()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var package = rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var avmod = Path.Combine(rig.Rig.NewFolder(), "alex.demo-1.1.0.avmod");
        await File.WriteAllBytesAsync(avmod, package);

        var result = await rig.Rig.Community.InstallAsync(avmod, Internet, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task OfferDisappears_WhenTheModuleIsUninstalled()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        await rig.Rig.Community.UninstallAsync(Id);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    // --- Un paquet qui n'est pas celui qu'il prétend être --------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Check_PackageCarryingAnotherId_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0", id: "pirate.demo");

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_PackageNamingAnotherRepository_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0", repository: "pirate/demo");

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_PackageWithoutRepository_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0", repository: null);

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_PackageWhoseVersionIsNotTheTag_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var package = File.ReadAllBytes(rig.Rig.Package(version: "1.0.5", declared: Internet));
        rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", package.Length)));
        rig.Github.Package(Repo, Id, "1.1.0", package);

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    [WindowsHostFact]
    public async Task Check_WrongChecksum_NoOffer_NothingLeftBehind()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0", checksum: new string('0', 64));

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task Check_NotAnArchive_NoOffer()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var junk = "pas un zip"u8.ToArray();
        rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", junk.Length)));
        rig.Github.Package(Repo, Id, "1.1.0", junk);

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task Check_PackageThatNeverFinishes_NoOffer_AfterTheDeadline_AndTheNextCheckIsNotBlocked()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Updates.DownloadTimeout = TimeSpan.FromMilliseconds(300);
        rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0")));
        rig.Github.Route(FakeGitHub.AssetUrl(Repo, "v1.1.0", "alex.demo-1.1.0.avmod.sha256"), () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(new string('a', 64)),
        });
        rig.Github.Route(FakeGitHub.AssetUrl(Repo, "v1.1.0", "alex.demo-1.1.0.avmod"), () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream(new byte[100])),
        });

        await rig.Updates.CheckNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        await rig.Updates.CheckNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); // la porte n'est pas restée fermée

        Assert.Null(rig.Updates.OfferFor(Id));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_PackageThatNeverFinishes_FailsAfterTheDeadline_NothingLeftBehind()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Updates.DownloadTimeout = TimeSpan.FromMilliseconds(300);
        rig.Github.Route(FakeGitHub.AssetUrl(Repo, "v1.1.0", "alex.demo-1.1.0.avmod"), () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream(new byte[100])),
        });

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.Ok);
        Assert.Contains("trop de temps", result.Message);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task Check_PackageDownloadFails_NoOffer_NoException()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0")));
        // aucun fichier publié : 404

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Null(rig.Updates.OfferFor(Id));
    }

    // --- L'installation -----------------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task InstallUpdate_NoOffer_IsRefused()
    {
        var rig = NewRig();
        await rig.InstallAsync();

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.Equal(new ActionResult(false, "Aucune mise à jour proposée pour ce module."), result);
        Assert.DoesNotContain(rig.Github.Requests, uri => uri.Host == "github.com");
    }

    [WindowsHostFact]
    public async Task InstallUpdate_InstallsTheVerifiedPackage_WithOnlyTheCheckedPermissions()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors);
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None); // « capteurs » n'est pas cochée

        Assert.True(result.Ok, result.Message);
        var approved = Assert.Single(rig.Rig.Approved.Modules);
        Assert.Equal("1.1.0", approved.Version);
        Assert.Equal(Internet, approved.Granted);
        Assert.True(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.1.0")));
        Assert.False(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.0.0")));
        Assert.Null(rig.Updates.OfferFor(Id));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_CheckedPermissionsInclude_TheNewOne_WhenTheUserChecksIt()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors);
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var result = await rig.Updates.InstallAsync(Id, InternetAndSensors, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(InternetAndSensors, Assert.Single(rig.Rig.Approved.Modules).Granted);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_PermissionTheNewVersionDoesNotDeclare_IsRefusedAndTheOfferStays()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: Internet);
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var result = await rig.Updates.InstallAsync(Id, [ModulePermission.Sensors], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_FileChangedSinceTheCheck_IsRefused_NothingChanges_NothingLeftBehind()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Publish("1.1.0", content: "autre contenu", checksum: new string('a', 64)); // le fichier publié n'a plus la bonne empreinte

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("empreinte", result.Message);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.False(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.1.0")));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_PackageSwappedForAnotherModule_IsRefused()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        // Le fichier et son empreinte sont cohérents, mais c'est le paquet d'un autre module.
        rig.Publish("1.1.0", id: "pirate.demo");

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Null(rig.Rig.Community.Entries.FirstOrDefault(e => e.Id == "pirate.demo"));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_PackageSwappedForAnotherRepository_IsRefused()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Publish("1.1.0", repository: "pirate/demo");

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_DownloadFails_Refused_NothingLeftBehind()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Github.Route(FakeGitHub.AssetUrl(Repo, "v1.1.0", "alex.demo-1.1.0.avmod"), () => throw new HttpRequestException("Connexion coupée."));

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_Cancelled_Throws_NothingLeftBehind()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Updates.InstallAsync(Id, Internet, cts.Token));

        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task InstallUpdate_UnknownModule_IsRefused()
    {
        var rig = NewRig();

        var result = await rig.Updates.InstallAsync("alex.inconnu", Internet, CancellationToken.None);

        Assert.False(result.Ok);
    }

    // --- Par CommunityModules --------------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Community_WithoutHttpClient_HasNoUpdateEngine()
    {
        var rig = new CommunityRig();
        try
        {
            var result = await rig.Community.InstallAsync(rig.Package(), Internet, CancellationToken.None);
            Assert.True(result.Ok, result.Message);

            await rig.Community.CheckUpdatesAsync(CancellationToken.None);
            var install = await rig.Community.InstallUpdateAsync(Id, Internet, CancellationToken.None);

            Assert.Null(rig.Community.UpdateFor(Id));
            Assert.Equal(new ActionResult(false, "Aucune mise à jour proposée pour ce module."), install);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    [WindowsHostFact]
    public async Task Community_WithHttpClient_ChecksOffersAndInstalls()
    {
        var github = new FakeGitHub();
        var rig = new CommunityRig(updateHttp: github.Client());
        try
        {
            Assert.True((await rig.Community.InstallAsync(rig.Package(version: "1.0.0"), Internet, CancellationToken.None)).Ok);
            var package = File.ReadAllBytes(rig.Package(version: "1.1.0", declared: InternetAndSensors, content: "neuf"));
            github.Api(Repo, new FakeRelease("v1.1.0", Body: "Notes.", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", package.Length)));
            github.Package(Repo, Id, "1.1.0", package);

            await rig.Community.CheckUpdatesAsync(CancellationToken.None);
            var offer = rig.Community.UpdateFor(Id)!;
            var result = await rig.Community.InstallUpdateAsync(Id, Internet, CancellationToken.None);

            Assert.Equal(("1.1.0", "Notes."), (offer.Version, offer.Notes));
            Assert.Equal([ModulePermission.Sensors], offer.NewPermissions);
            Assert.True(result.Ok, result.Message);
            Assert.Equal("1.1.0", Assert.Single(rig.Approved.Modules).Version);
            Assert.Null(rig.Community.UpdateFor(Id));
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }
}
