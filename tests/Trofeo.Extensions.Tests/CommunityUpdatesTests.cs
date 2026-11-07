using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Distribution;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

// Les mises à jour proposées depuis GitHub : le rythme des vérifications (qui n'interrogent que GitHub), puis, au clic, la préparation (le paquet
// est téléchargé, vérifié par son empreinte puis par son contenu et gardé) et l'installation de ce fichier-là, sans rien retélécharger.
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

        public const string OtherId = "alex.autre";
        public const string OtherRepo = "alex/autre";

        /// <summary>Installe un second module (un autre dépôt), pour les tests qui en préparent deux.</summary>
        public async Task InstallOtherAsync()
        {
            var result = await Rig.Community.InstallAsync(
                Rig.Package(id: OtherId, version: "1.0.0", repository: OtherRepo, declared: Internet), Internet, CancellationToken.None);
            Assert.True(result.Ok, result.Message);
        }

        /// <summary>Publie une version du second module sur son propre dépôt.</summary>
        public void PublishOther(string version)
        {
            var package = File.ReadAllBytes(Rig.Package(id: OtherId, version: version, declared: Internet, repository: OtherRepo));
            Github.Api(OtherRepo, new FakeRelease("v" + version, Assets: FakeGitHub.ModuleAssets(OtherRepo, OtherId, version, package.Length)));
            Github.Package(OtherRepo, OtherId, version, package);
        }

        /// <summary>Le paquet de cette version ne finit jamais d'arriver (son empreinte, elle, est servie).</summary>
        public void StallPackage(string version)
        {
            Github.Route(FakeGitHub.AssetUrl(Repo, "v" + version, $"{Id}-{version}.avmod.sha256"), () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new string('a', 64)),
            });
            Github.Route(FakeGitHub.AssetUrl(Repo, "v" + version, $"{Id}-{version}.avmod"), () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream(new byte[100])),
            });
        }

        /// <summary>Les requêtes de fichiers (paquet ou empreinte), par opposition à celles de l'API des versions.</summary>
        public int FileRequests => Github.Requests.Count(uri => uri.Host == "github.com");

        /// <summary>Les paquets préparés qui existent sur le disque.</summary>
        public string[] PreparedFiles() =>
            Directory.Exists(TempRoot) ? Directory.GetFiles(TempRoot, "*.avmod", SearchOption.AllDirectories) : [];

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

    // --- Ce qui est proposé : une vérification n'interroge que GitHub -------------------------------------------------------------

    [WindowsHostFact]
    public async Task Check_NewVersion_IsOfferedWithItsVersionAndNotes_WithoutDownloadingAnything()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors, notes: "Ajoute les capteurs.");

        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var offer = rig.Updates.OfferFor(Id)!;
        Assert.Equal(("1.1.0", "Ajoute les capteurs."), (offer.Version, offer.Notes));
        Assert.Equal(0, rig.FileRequests); // ni le paquet ni son empreinte : seulement l'API des versions
        Assert.Equal(1, rig.Github.ApiRequests);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version); // rien n'est installé tout seul
        Assert.False(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.1.0")));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task Check_OfferedEvenWhenThePackageIsMissingOrBroken()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var junk = "pas un zip"u8.ToArray();
        rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", junk.Length)));
        // aucun fichier publié, ou un fichier abîmé : la vérification ne le sait pas, et ne le demande pas

        await rig.Updates.CheckNowAsync(CancellationToken.None);
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);

        rig.Github.Package(Repo, Id, "1.1.0", junk, checksum: new string('0', 64));
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
        Assert.Equal(0, rig.FileRequests);
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
    public async Task Check_TheScheduledOneProposesWithoutInstallingNorDownloading()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");

        rig.Clock.Advance(TimeSpan.FromSeconds(60));

        await TestWait.UntilAsync(() => rig.Updates.OfferFor(Id) is not null);
        rig.Clock.AdvanceInSteps(TimeSpan.FromDays(3), TimeSpan.FromDays(1));
        await Task.Delay(200);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.FileRequests);
    }

    [WindowsHostFact]
    public async Task Check_SameVersionAgain_StillNothingDownloaded()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)!.Version);
        Assert.Equal(0, rig.FileRequests);
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

    [WindowsHostFact]
    public async Task Check_DuringAPreparation_IsNotBlocked()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.StallPackage("1.1.0");
        var preparing = rig.Updates.PrepareAsync(Id, CancellationToken.None);
        await TestWait.UntilAsync(() => rig.Github.Requests.Any(uri => uri.AbsolutePath.EndsWith(".avmod", StringComparison.Ordinal)));

        await rig.Updates.CheckNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(preparing.IsCompleted); // la préparation, elle, attend toujours son fichier
        rig.Updates.CancelUpdate(Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
    }

    // --- La préparation : le paquet se télécharge au clic ----------------------------------------------------------------------

    [WindowsHostFact]
    public async Task Prepare_ReadsTheVersionTheNotesAndBothPermissionLists_FromTheDownloadedPackage()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors, notes: "Ajoute les capteurs.");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(preparation.Ok, preparation.Message);
        Assert.Null(preparation.Message);
        Assert.Equal(("1.1.0", "Ajoute les capteurs."), (preparation.Version, preparation.Notes));
        Assert.Equal(InternetAndSensors, preparation.Declared); // tout ce que la nouvelle version déclare
        Assert.Equal([ModulePermission.Sensors], preparation.NewPermissions); // ce qu'elle déclare en plus de l'installée
        Assert.Equal(2, rig.FileRequests); // l'empreinte et le paquet, au clic
        Assert.Single(rig.PreparedFiles());
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version); // préparer n'installe rien
    }

    [WindowsHostFact]
    public async Task Prepare_SamePermissions_NoNewPermission()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(preparation.Ok, preparation.Message);
        Assert.Equal(Internet, preparation.Declared);
        Assert.Empty(preparation.NewPermissions);
    }

    [WindowsHostFact]
    public async Task Prepare_NoOffer_IsRefused_WithoutAnyRequest()
    {
        var rig = NewRig();
        await rig.InstallAsync();

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var unknown = await rig.Updates.PrepareAsync("alex.inconnu", CancellationToken.None);

        Assert.All(new[] { preparation, unknown }, p =>
        {
            Assert.False(p.Ok);
            Assert.Equal(ModuleUpdates.NoUpdateMessage, p.Message);
            Assert.Null(p.Version);
            Assert.Empty(p.Declared);
            Assert.Empty(p.NewPermissions);
        });
        Assert.Empty(rig.Github.Requests);
        Assert.Equal(0, rig.TempEntries);
    }

    public static TheoryData<string> BadPackages =>
    [
        "autre-id", "autre-depot", "sans-depot", "autre-version", "mauvaise-empreinte", "pas-une-archive", "telechargement-impossible",
    ];

    [WindowsHostTheory]
    [MemberData(nameof(BadPackages))]
    public async Task Prepare_PackageThatIsNotTheAnnouncedOne_IsRefused_NothingOnDisk_TheOfferStays(string problem)
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var junk = "pas un zip"u8.ToArray();
        switch (problem)
        {
            case "autre-id":
                rig.Publish("1.1.0", id: "pirate.demo");
                break;
            case "autre-depot":
                rig.Publish("1.1.0", repository: "pirate/demo");
                break;
            case "sans-depot":
                rig.Publish("1.1.0", repository: null);
                break;
            case "autre-version":
                var other = File.ReadAllBytes(rig.Rig.Package(version: "1.0.5", declared: Internet));
                rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", other.Length)));
                rig.Github.Package(Repo, Id, "1.1.0", other);
                break;
            case "mauvaise-empreinte":
                rig.Publish("1.1.0", checksum: new string('0', 64));
                break;
            case "pas-une-archive":
                rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", junk.Length)));
                rig.Github.Package(Repo, Id, "1.1.0", junk);
                break;
            default:
                rig.Github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0")));
                break; // aucun fichier publié : 404
        }

        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.False(preparation.Ok);
        Assert.False(string.IsNullOrWhiteSpace(preparation.Message));
        Assert.Null(preparation.Version);
        Assert.Equal(0, rig.TempEntries);
        Assert.Empty(rig.PreparedFiles());
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version); // l'offre reste : un nouvel essai est possible
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(ModuleUpdates.PrepareFirstMessage, (await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Message);
    }

    [WindowsHostFact]
    public async Task Prepare_PackageThatNeverFinishes_IsRefusedAfterTheDeadline_NothingOnDisk()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Updates.DownloadTimeout = TimeSpan.FromMilliseconds(300);
        rig.StallPackage("1.1.0");

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(preparation.Ok);
        Assert.Contains("trop de temps", preparation.Message);
        Assert.Equal(0, rig.TempEntries);
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
    }

    [WindowsHostFact]
    public async Task Prepare_CancelledByTheCaller_StaysACancellation_NothingOnDisk()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.StallPackage("1.1.0");
        using var cts = new CancellationTokenSource();
        var preparing = rig.Updates.PrepareAsync(Id, cts.Token);
        await TestWait.UntilAsync(() => rig.Github.Requests.Any(uri => uri.AbsolutePath.EndsWith(".avmod", StringComparison.Ordinal)));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, rig.TempEntries);
        Assert.Equal(ModuleUpdates.PrepareFirstMessage, (await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Message);
    }

    [WindowsHostFact]
    public async Task Prepare_KeepsThePreparedFileOpen_SoItCannotChangeBeforeTheInstallation()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var file = Assert.Single(rig.PreparedFiles());

        Assert.Throws<IOException>(() => File.Delete(file));
        Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Move(file, file + ".autre"));
        using (var reading = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(reading.Length > 0); // on peut le lire
        }

        rig.Updates.CancelUpdate(Id);

        Assert.Empty(rig.PreparedFiles()); // l'abandon libère puis supprime
    }

    [WindowsHostFact]
    public async Task Prepare_AgainForTheSameModule_ReplacesThePreviousFile()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var first = Assert.Single(rig.PreparedFiles());

        var again = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(again.Ok, again.Message);
        var second = Assert.Single(rig.PreparedFiles());
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first));
        Assert.Equal(1, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task Prepare_ForAnotherModule_ReplacesThePreviousFile_EvenAnotherModulesOne()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        await rig.InstallOtherAsync();
        rig.Publish("1.1.0");
        rig.PublishOther("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var first = Assert.Single(rig.PreparedFiles());

        var other = await rig.Updates.PrepareAsync(UpdateRig.OtherId, CancellationToken.None);

        Assert.True(other.Ok, other.Message);
        Assert.False(File.Exists(first)); // une seule préparation pour tout le service
        Assert.Contains(UpdateRig.OtherId, Assert.Single(rig.PreparedFiles()));
        Assert.Equal(ModuleUpdates.PrepareFirstMessage, (await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Message);
    }

    [WindowsHostFact]
    public async Task Prepare_WhileAnotherIsDownloading_CancelsIt_AndLeavesOnlyTheNewFile()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.StallPackage("1.1.0");
        var slow = rig.Updates.PrepareAsync(Id, CancellationToken.None);
        await TestWait.UntilAsync(() => rig.Github.Requests.Any(uri => uri.AbsolutePath.EndsWith(".avmod", StringComparison.Ordinal)));
        rig.Publish("1.1.0"); // le fichier est de nouveau disponible

        var fast = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(fast.Ok, fast.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Single(rig.PreparedFiles());
        Assert.Equal(1, rig.TempEntries);
    }

    // --- L'abandon et l'expiration ---------------------------------------------------------------------------------------------

    [WindowsHostFact]
    public async Task CancelUpdate_RemovesThePreparedFile_AndTheInstallationIsRefused()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var requests = rig.Github.Requests.Count;

        rig.Updates.CancelUpdate(Id);
        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.Equal(0, rig.TempEntries);
        Assert.Equal(new ActionResult(false, ModuleUpdates.PrepareFirstMessage), result);
        Assert.Equal(requests, rig.Github.Requests.Count); // et sans rien télécharger
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
    }

    [WindowsHostFact]
    public async Task CancelUpdate_WhileDownloading_CancelsThePreparation_DiskEmpty()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.StallPackage("1.1.0");
        var preparing = rig.Updates.PrepareAsync(Id, CancellationToken.None);
        await TestWait.UntilAsync(() => rig.Github.Requests.Any(uri => uri.AbsolutePath.EndsWith(".avmod", StringComparison.Ordinal)));

        rig.Updates.CancelUpdate(Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task CancelUpdate_NothingPrepared_OrAnotherModulePrepared_ChangesNothing()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Updates.CancelUpdate(Id); // rien de préparé
        rig.Updates.CancelUpdate("alex.inconnu");
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        rig.Updates.CancelUpdate("alex.autre"); // un autre module : ne touche pas à celui-ci

        Assert.Single(rig.PreparedFiles());
        Assert.True((await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Ok);
    }

    [WindowsHostFact]
    public async Task Prepared_ExpiresAfterFifteenMinutes_AndTheInstallationIsRefusedWithoutAnyRequest()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var requests = rig.FileRequests;

        rig.Clock.Advance(ModuleUpdates.PreparedLifetime - TimeSpan.FromSeconds(1));
        Assert.Single(rig.PreparedFiles());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(rig.PreparedFiles());
        Assert.Equal(0, rig.TempEntries);
        Assert.Equal(TimeSpan.FromMinutes(15), ModuleUpdates.PreparedLifetime);
        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);
        Assert.Equal(new ActionResult(false, ModuleUpdates.PrepareFirstMessage), result);
        Assert.Equal(requests, rig.FileRequests); // la vérification planifiée a pu partir au passage : on compte les fichiers
    }

    [WindowsHostFact]
    public async Task ExpiryTimer_ExistsOnlyWhileAFileIsPrepared()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var baseline = rig.Clock.TimersAlive; // le minuteur des vérifications
        Assert.Equal(1, baseline); // sans fichier préparé : seulement ce minuteur-là

        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        Assert.Equal(baseline + 1, rig.Clock.TimersAlive);

        rig.Updates.CancelUpdate(Id);
        Assert.Equal(baseline, rig.Clock.TimersAlive);

        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        Assert.Equal(baseline + 1, rig.Clock.TimersAlive);
        Assert.True((await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Ok);
        Assert.Equal(baseline, rig.Clock.TimersAlive); // installée : le minuteur d'expiration est libéré
    }

    [WindowsHostFact]
    public async Task ExpiryTimer_FailedPreparation_LeavesNone()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0", checksum: new string('0', 64));
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var baseline = rig.Clock.TimersAlive;

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.False(preparation.Ok);
        Assert.Equal(baseline, rig.Clock.TimersAlive);
    }

    // --- L'installation : le fichier préparé, sans rien retélécharger ------------------------------------------------------------

    [WindowsHostFact]
    public async Task Install_AfterPreparation_InstallsThePreparedFile_WithoutAnyNetworkRequest_AndOnlyTheCheckedPermissions()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors);
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var requests = rig.Github.Requests.Count;

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None); // « capteurs » n'est pas cochée

        Assert.True(result.Ok, result.Message);
        Assert.Equal(requests, rig.Github.Requests.Count); // aucune requête de plus
        var approved = Assert.Single(rig.Rig.Approved.Modules);
        Assert.Equal("1.1.0", approved.Version);
        Assert.Equal(Internet, approved.Granted);
        Assert.True(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.1.0")));
        Assert.False(Directory.Exists(Path.Combine(rig.Rig.Root, Id, "1.0.0")));
        Assert.Null(rig.Updates.OfferFor(Id));
        Assert.Equal(0, rig.TempEntries); // le fichier préparé est supprimé
    }

    [WindowsHostFact]
    public async Task Install_CheckedPermissionsInclude_TheNewOne_WhenTheUserChecksIt()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: InternetAndSensors);
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        var result = await rig.Updates.InstallAsync(Id, InternetAndSensors, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(InternetAndSensors, Assert.Single(rig.Rig.Approved.Modules).Granted);
    }

    [WindowsHostFact]
    public async Task Install_WithoutPreparation_IsRefused_WithoutAnyRequestOrFile()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        var requests = rig.Github.Requests.Count;

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.Equal(new ActionResult(false, "Prépare d'abord cette mise à jour."), result);
        Assert.Equal(requests, rig.Github.Requests.Count);
        Assert.Equal(0, rig.TempEntries);
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
    }

    [WindowsHostFact]
    public async Task Install_NoOffer_IsRefused()
    {
        var rig = NewRig();
        await rig.InstallAsync();

        var result = await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None);

        Assert.Equal(new ActionResult(false, ModuleUpdates.NoUpdateMessage), result);
        Assert.Empty(rig.Github.Requests);
    }

    [WindowsHostFact]
    public async Task Install_UnknownModule_IsRefused()
    {
        var rig = NewRig();

        var result = await rig.Updates.InstallAsync("alex.inconnu", Internet, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [WindowsHostFact]
    public async Task Install_PreparedForAnotherModule_IsRefused_AndTheOtherFileStays()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        await rig.InstallOtherAsync();
        rig.Publish("1.1.0");
        rig.PublishOther("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        var result = await rig.Updates.InstallAsync(UpdateRig.OtherId, Internet, CancellationToken.None);

        Assert.Equal(new ActionResult(false, ModuleUpdates.PrepareFirstMessage), result);
        Assert.Single(rig.PreparedFiles());
    }

    [WindowsHostFact]
    public async Task Install_Refused_DeletesThePreparedFile_AndANewTryNeedsANewPreparation()
    {
        var rig = NewRig();
        await rig.InstallAsync("1.0.0", declared: Internet);
        rig.Publish("1.1.0", declared: Internet);
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        var refused = await rig.Updates.InstallAsync(Id, [ModulePermission.Sensors], CancellationToken.None); // une permission que le paquet ne déclare pas

        Assert.False(refused.Ok);
        Assert.Equal(0, rig.TempEntries);
        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal("1.1.0", rig.Updates.OfferFor(Id)?.Version);
        Assert.Equal(ModuleUpdates.PrepareFirstMessage, (await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Message);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        Assert.True((await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Ok);
    }

    [WindowsHostFact]
    public async Task Install_Cancelled_Throws_AndTheFileIsDeleted()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Updates.InstallAsync(Id, Internet, cts.Token));

        Assert.Equal("1.0.0", Assert.Single(rig.Rig.Approved.Modules).Version);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task PreparedFile_Disappears_WhenTheModuleIsUninstalled()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        await rig.Rig.Community.UninstallAsync(Id);

        Assert.Equal(0, rig.TempEntries);
        Assert.Equal(ModuleUpdates.NoUpdateMessage, (await rig.Updates.InstallAsync(Id, Internet, CancellationToken.None)).Message);
    }

    [WindowsHostFact]
    public async Task PreparedFile_Disappears_WhenTheSameOrANewerVersionGetsInstalledByOtherMeans()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        var package = rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var avmod = Path.Combine(rig.Rig.NewFolder(), "alex.demo-1.1.0.avmod");
        await File.WriteAllBytesAsync(avmod, package);

        var result = await rig.Rig.Community.InstallAsync(avmod, Internet, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, rig.TempEntries);
    }

    [WindowsHostFact]
    public async Task PreparedFile_StaysWhenAnotherModuleChanges()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        await rig.InstallOtherAsync(); // un autre module arrive : Changed est levé, la préparation reste

        Assert.Single(rig.PreparedFiles());
    }

    [WindowsHostFact]
    public async Task Dispose_CancelsAndDeletesEverything()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        rig.Updates.Dispose();

        Assert.Equal(0, rig.TempEntries);
        Assert.Equal(0, rig.Clock.TimersAlive);
    }

    [WindowsHostFact]
    public async Task Dispose_WhileDownloading_CancelsThePreparation_DiskEmpty()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.StallPackage("1.1.0");
        var preparing = rig.Updates.PrepareAsync(Id, CancellationToken.None);
        await TestWait.UntilAsync(() => rig.Github.Requests.Any(uri => uri.AbsolutePath.EndsWith(".avmod", StringComparison.Ordinal)));

        rig.Updates.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, rig.TempEntries);
    }

    // --- Dossiers laissés par un service tué en plein téléchargement --------------------------------------------------------------

    [WindowsHostFact]
    public async Task Orphans_AreRemovedAtTheFirstPreparation_AndNeverBefore()
    {
        var rig = NewRig();
        var orphan = Path.Combine(rig.TempRoot, "abandonne");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "alex.demo-1.0.9.avmod.part"), "reste");
        await rig.InstallAsync();
        rig.Publish("1.1.0");

        await rig.Updates.CheckNowAsync(CancellationToken.None);
        rig.Clock.AdvanceInSteps(TimeSpan.FromDays(2), TimeSpan.FromDays(1));
        Assert.True(Directory.Exists(orphan)); // ni au démarrage ni à une vérification

        var preparation = await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(preparation.Ok, preparation.Message);
        Assert.False(Directory.Exists(orphan));
        Assert.Equal(1, rig.TempEntries); // seulement la préparation en cours
    }

    [WindowsHostFact]
    public async Task Orphans_AreCleanedOnlyOncePerProcess()
    {
        var rig = NewRig();
        await rig.InstallAsync();
        rig.Publish("1.1.0");
        await rig.Updates.CheckNowAsync(CancellationToken.None);
        await rig.Updates.PrepareAsync(Id, CancellationToken.None);
        var late = Path.Combine(rig.TempRoot, "tardif");
        Directory.CreateDirectory(late); // n'importe quoi qui arrive ensuite n'est pas pris pour un orphelin

        await rig.Updates.PrepareAsync(Id, CancellationToken.None);

        Assert.True(Directory.Exists(late));
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
            var preparation = await rig.Community.PrepareUpdateAsync(Id, CancellationToken.None);
            rig.Community.CancelUpdate(Id);
            var install = await rig.Community.InstallUpdateAsync(Id, Internet, CancellationToken.None);

            Assert.Null(rig.Community.UpdateFor(Id));
            Assert.Equal(new ModuleUpdatePreparation(false, ModuleUpdates.NoUpdateMessage, null, null, [], []), preparation);
            Assert.Equal(new ActionResult(false, ModuleUpdates.NoUpdateMessage), install);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    [WindowsHostFact]
    public async Task Community_WithHttpClient_ChecksPreparesAndInstalls()
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
            Assert.Equal(("1.1.0", "Notes."), (offer.Version, offer.Notes));
            Assert.DoesNotContain(github.Requests, uri => uri.Host == "github.com"); // pas de paquet avant le clic

            var preparation = await rig.Community.PrepareUpdateAsync(Id, CancellationToken.None);
            var requests = github.Requests.Count;
            var result = await rig.Community.InstallUpdateAsync(Id, Internet, CancellationToken.None);

            Assert.True(preparation.Ok, preparation.Message);
            Assert.Equal([ModulePermission.Sensors], preparation.NewPermissions);
            Assert.True(result.Ok, result.Message);
            Assert.Equal(requests, github.Requests.Count);
            Assert.Equal("1.1.0", Assert.Single(rig.Approved.Modules).Version);
            Assert.Null(rig.Community.UpdateFor(Id));
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }
}
