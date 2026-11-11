using System.Net;
using AshboardVision.Sdk;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Core.Rendering;
using Trofeo.Distribution;
using Trofeo.Extensions.Updates;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

// Les mises à jour que GitHub propose pour les modules installés : une version plus récente, avec ses deux fichiers, au bon endroit.
public sealed class ModuleUpdateCheckerTests
{
    private const string Id = "alex.demo";
    private const string Repo = "alex/demo";

    private readonly FakeGitHub _github = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private ModuleUpdateChecker NewChecker() => new(new UpdateChecker(_github.Client()), _clock);

    private static ModuleManifest Manifest(string id, string version, string? repository) =>
        new(id, "Démo", "Un module de démonstration", "Alex", version, new SdkVersion(1, 0), "Mod.dll", [ModulePermission.Internet],
            repository, new GridSize(2, 1), new Dictionary<SizePreset, GridSize>(), SettingsSchema.Empty, null);

    internal static CommunityEntry Entry(
        string id = Id, string version = "1.0.0", string? repository = Repo, bool dev = false, bool approved = true,
        CommunityState state = CommunityState.Ready) =>
        new(id, $@"C:\Modules\{id}\{version}", Manifest(id, version, repository), state, null,
            approved && !dev ? new ApprovedModule(id, version, $@"C:\Modules\{id}\{version}", "hash", [ModulePermission.Internet], null, repository) : null,
            dev);

    private static FakeRelease Release(string version, bool prerelease = false, string body = "Nouveautés.", string repo = Repo, string id = Id) =>
        new("v" + version, prerelease, body, FakeGitHub.ModuleAssets(repo, id, version));

    private async Task<IReadOnlyDictionary<string, ModuleUpdate>> CheckAsync(params CommunityEntry[] entries) =>
        await NewChecker().CheckAsync(entries, CancellationToken.None);

    // --- Ce qui est proposé -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_NewerTagWithAssets_ProposesUpdate()
    {
        _github.Api(Repo, Release("1.1.0", body: "Ajoute une caméra."), Release("1.0.0"));

        var found = await CheckAsync(Entry());

        var update = Assert.Single(found).Value;
        Assert.Equal(Id, Assert.Single(found).Key);
        Assert.Equal("1.1.0", update.Version);
        Assert.Equal("Ajoute une caméra.", update.Notes);
        Assert.Equal(new Uri("https://github.com/alex/demo/releases/download/v1.1.0/alex.demo-1.1.0.avmod"), update.Package);
        Assert.Equal(new Uri("https://github.com/alex/demo/releases/download/v1.1.0/alex.demo-1.1.0.avmod.sha256"), update.Sha256);
    }

    [Fact]
    public async Task Check_SeveralNewerVersions_ProposesTheNewest_WhateverTheOrderOfTheFeed()
    {
        _github.Api(Repo, Release("1.1.0"), Release("1.3.0"), Release("1.2.0"));

        Assert.Equal("1.3.0", (await CheckAsync(Entry())).Single().Value.Version);
    }

    [Fact]
    public async Task Check_SameOrOlderVersion_ProposesNothing()
    {
        _github.Api(Repo, Release("1.0.0"), Release("0.9.0"));

        Assert.Empty(await CheckAsync(Entry(version: "1.0.0")));
    }

    [Fact]
    public async Task Check_PrereleaseIgnoredForStableInstall()
    {
        _github.Api(Repo, Release("1.2.0-beta.1", prerelease: true), Release("1.1.0"), Release("1.0.0"));

        // La bêta est plus récente, mais l'installée est une stable : c'est la stable suivante qui est proposée.
        Assert.Equal("1.1.0", (await CheckAsync(Entry())).Single().Value.Version);
    }

    [Fact]
    public async Task Check_OnlyAPrereleaseIsNewer_StableInstallGetsNothing()
    {
        _github.Api(Repo, Release("1.1.0-beta.1", prerelease: true), Release("1.0.0"));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_PrereleaseFlagWithoutPrereleaseTag_IsStillIgnoredForStable()
    {
        _github.Api(Repo, Release("1.1.0", prerelease: true));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_PrereleaseInstalled_AcceptsNewerPrereleases()
    {
        _github.Api(Repo, Release("1.1.0-beta.2", prerelease: true), Release("1.1.0-beta.1", prerelease: true));

        var found = await CheckAsync(Entry(version: "1.1.0-beta.1"));

        Assert.Equal("1.1.0-beta.2", found.Single().Value.Version);
    }

    [Fact]
    public async Task Check_PrereleaseInstalled_StableOfTheSameNumberIsNewer()
    {
        _github.Api(Repo, Release("1.1.0"), Release("1.1.0-beta.2", prerelease: true));

        Assert.Equal("1.1.0", (await CheckAsync(Entry(version: "1.1.0-beta.2"))).Single().Value.Version);
    }

    // --- Ce qui n'est pas proposé ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_MissingSha256_NoUpdate()
    {
        var onlyThePackage = new FakeRelease("v1.1.0", Assets: [FakeGitHub.ModuleAssets(Repo, Id, "1.1.0")[0]]);
        _github.Api(Repo, onlyThePackage);

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_MissingPackage_NoUpdate()
    {
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: [FakeGitHub.ModuleAssets(Repo, Id, "1.1.0")[1]]));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_NewestReleaseIncomplete_DoesNotFallBackToAnOlderOne()
    {
        // La 1.2.0 est publiée sans ses fichiers (leur envoi est peut-être en cours) : on ne propose pas la 1.1.0 à la place.
        _github.Api(Repo, new FakeRelease("v1.2.0"), Release("1.1.0"));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_AssetsNamedForAnotherModuleOrVersion_NoUpdate()
    {
        var wrongId = FakeGitHub.ModuleAssets(Repo, "alex.autre", "1.1.0");
        var wrongVersion = FakeGitHub.ModuleAssets(Repo, Id, "1.0.9");
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: wrongId), new FakeRelease("v1.2.0", Assets: wrongVersion));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Theory]
    [InlineData("https://evil.example/alex/demo/releases/download/v1.1.0/")]
    [InlineData("http://github.com/alex/demo/releases/download/v1.1.0/")]
    [InlineData("https://github.com.evil.example/alex/demo/releases/download/v1.1.0/")]
    [InlineData("https://user:pass@github.com/alex/demo/releases/download/v1.1.0/")]
    [InlineData("https://github.com/pirate/demo/releases/download/v1.1.0/")]
    [InlineData("https://github.com/alex/autre/releases/download/v1.1.0/")]
    [InlineData("https://github.com/alex/demo/archive/")]
    [InlineData("https://github.com/alex/demo/releases/download/v9.9.9/")]
    [InlineData("https://github.com/alex/demo/releases/download/v1.1.0/../../../../pirate/demo/releases/download/v1.1.0/")]
    [InlineData("https://github.com/alex/demo/releases/download/v1.1.0/sous/")]
    [InlineData("https://github.com/alex/demo/releases/download/v1.1.0/?x=")]
    public async Task Check_AssetNotFromTheExpectedRepositoryOnGitHub_NoUpdate(string prefix)
    {
        // Le fichier du paquet, puis celui de l'empreinte : chacun doit venir du dépôt attendu, sur github.com, en https.
        var package = $"{Id}-1.1.0.avmod";
        var checksum = package + ".sha256";
        var good = FakeGitHub.AssetUrl(Repo, "v1.1.0", package);
        var goodChecksum = FakeGitHub.AssetUrl(Repo, "v1.1.0", checksum);
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: [new(package, prefix + package, 4096), new(checksum, goodChecksum, 100)]));
        Assert.Empty(await CheckAsync(Entry()));

        var other = new FakeGitHub();
        other.Api(Repo, new FakeRelease("v1.1.0", Assets: [new(package, good, 4096), new(checksum, prefix + checksum, 100)]));
        Assert.Empty(await new ModuleUpdateChecker(new UpdateChecker(other.Client()), _clock).CheckAsync([Entry()], CancellationToken.None));
    }

    [Fact]
    public async Task Check_RepositoryCaseDiffersFromTheOneInTheLinks_IsAccepted()
    {
        // GitHub ne distingue pas les majuscules d'un dépôt : le manifeste peut ne pas les avoir écrites comme l'adresse.
        _github.Api("alex/demo", new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets("Alex/Demo", Id, "1.1.0")));

        Assert.Single(await CheckAsync(Entry()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Check_PackageWithoutASize_NoUpdate(long size)
    {
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", size)));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_PackageBiggerThanTheLimit_NoUpdate()
    {
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: FakeGitHub.ModuleAssets(Repo, Id, "1.1.0", ModuleUpdateChecker.MaxPackageBytes + 1)));

        Assert.Empty(await CheckAsync(Entry()));
    }

    // --- Pas de dépôt, pas de requête ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_NoRepository_NoRequest()
    {
        var found = await CheckAsync(Entry(repository: null), Entry(id: "alex.autre", repository: null));

        Assert.Empty(found);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Check_NothingInstalled_NoRequest()
    {
        Assert.Empty(await NewChecker().CheckAsync([], CancellationToken.None));
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Check_DevFolderModule_NoRequest()
    {
        // Un module du mode développeur ne se met pas à jour tout seul : le créateur le recharge.
        var found = await CheckAsync(Entry(dev: true));

        Assert.Empty(found);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Check_ModuleNotYetApproved_NoRequest()
    {
        var found = await CheckAsync(Entry(approved: false));

        Assert.Empty(found);
        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("alex/..")]
    [InlineData("alex/demo?per_page=100")]
    [InlineData("alex/demo/extra")]
    [InlineData("../../x")]
    [InlineData("")]
    [InlineData("alex")]
    public async Task Check_HostileRepositoryName_NoRequest(string repository)
    {
        // Même si la liste scellée en portait un (le manifeste en refuse, mais l'entrée n'est qu'une donnée) : jamais une adresse construite avec.
        var found = await CheckAsync(Entry(repository: repository));

        Assert.Empty(found);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Check_ApprovalWithoutRepository_TheFolderManifestCannotAddOne()
    {
        // Installé sans dépôt : un manifeste retouché dans le dossier ne lui en donne pas un (et l'envoie encore moins ailleurs).
        var entry = Entry(repository: null) with { Manifest = Manifest(Id, "1.0.0", Repo) };

        var found = await CheckAsync(entry);

        Assert.Empty(found);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Check_RepositoryComesFromTheSealedApproval_NotFromTheFolder()
    {
        // Le manifeste du dossier peut avoir été retouché : le dépôt d'où viennent les mises à jour est celui de l'installation.
        _github.Api(Repo, Release("1.1.0"));
        var entry = Entry(repository: Repo) with { Manifest = Manifest(Id, "1.0.0", "pirate/demo") };

        var found = await CheckAsync(entry);

        Assert.Single(found);
        Assert.Equal([new Uri("https://api.github.com/repos/alex/demo/releases?per_page=10")], _github.Requests);
    }

    // --- Les requêtes -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_TwoModulesInTheSameRepository_OneRequest()
    {
        // Une seule version du dépôt, avec les fichiers des deux modules (un dépôt peut en publier plusieurs).
        _github.Api(Repo, new FakeRelease("v1.1.0", Assets: [.. FakeGitHub.ModuleAssets(Repo, Id, "1.1.0"), .. FakeGitHub.ModuleAssets(Repo, "alex.autre", "1.1.0")]));

        var found = await CheckAsync(Entry(), Entry(id: "alex.autre"));

        Assert.Equal(["alex.autre", "alex.demo"], found.Keys.Order());
        Assert.Equal(1, _github.ApiRequests);
    }

    [Fact]
    public async Task Check_TwoRepositories_OneRequestEach_AndOneFailureDoesNotHideTheOther()
    {
        _github.Api(Repo, Release("1.1.0"));
        _github.ApiRaw("pirate/down", () => throw new HttpRequestException("Hôte inconnu."));

        var found = await CheckAsync(Entry(), Entry(id: "pirate.down", repository: "pirate/down"));

        Assert.Equal([Id], found.Keys);
        Assert.Equal(2, _github.ApiRequests);
    }

    [Fact]
    public async Task Check_RepositoryThatMakesTheReaderThrow_IsUnknown_AndTheOthersAreStillChecked()
    {
        // Une exception inattendue (ici celle du gestionnaire HTTP) ne coupe pas la vérification des autres modules.
        _github.Api(Repo, Release("1.1.0"));
        _github.ApiRaw("pirate/boom", () => throw new InvalidOperationException("inattendu"));

        var check = await NewChecker().CheckWithStatusAsync([Entry(id: "pirate.boom", repository: "pirate/boom"), Entry()], CancellationToken.None);

        Assert.Equal([Id], check.Found.Keys);
        Assert.Equal(["pirate.boom"], check.Unknown);
    }

    [Fact]
    public async Task Check_RepositoryThatMakesTheReaderThrow_NeverHidesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        _github.ApiRaw(Repo, () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewChecker().CheckAsync([Entry()], cts.Token));
    }

    [Theory]
    [InlineData("\"4096\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("1e999")]
    public async Task Check_AssetSizeOfTheWrongType_OnlyThatModuleGetsNoUpdate(string size)
    {
        // Le fichier d'un module dont la taille est mal typée n'est pas proposé ; le module d'un autre dépôt l'est toujours.
        var package = Id + "-1.1.0.avmod";
        var badAssets = "[ { \"name\": \"" + package + "\", \"size\": " + size + ", \"browser_download_url\": \"" + FakeGitHub.AssetUrl(Repo, "v1.1.0", package) + "\" },"
            + " { \"name\": \"" + package + ".sha256\", \"size\": 100, \"browser_download_url\": \"" + FakeGitHub.AssetUrl(Repo, "v1.1.0", package + ".sha256") + "\" } ]";
        _github.ApiRaw(Repo, () => FakeGitHub.Json("[ { \"tag_name\": \"v1.1.0\", \"draft\": false, \"prerelease\": false, \"assets\": " + badAssets + " } ]"));
        _github.Api("alex/autre", Release("1.1.0", repo: "alex/autre", id: "alex.autre"));

        var found = await CheckAsync(Entry(), Entry(id: "alex.autre", repository: "alex/autre"));

        Assert.Equal(["alex.autre"], found.Keys);
    }

    [Fact]
    public async Task Check_SameRepositoryAskedAgainWithinAMinute_ReusesTheAnswer_ThenAsksAgain()
    {
        _github.Api(Repo, Release("1.1.0"));
        var checker = NewChecker();

        await checker.CheckAsync([Entry()], CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var again = await checker.CheckAsync([Entry()], CancellationToken.None);

        Assert.Single(again);
        Assert.Equal(1, _github.ApiRequests);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await checker.CheckAsync([Entry()], CancellationToken.None);
        Assert.Equal(2, _github.ApiRequests);
    }

    [Fact]
    public async Task Check_FailureIsNotRemembered_TheNextCheckAsksAgain()
    {
        var checker = NewChecker();
        _github.ApiRaw(Repo, () => FakeGitHub.Json("[]", HttpStatusCode.InternalServerError));
        Assert.Empty(await checker.CheckAsync([Entry()], CancellationToken.None));

        _github.Api(Repo, Release("1.1.0"));
        var found = await checker.CheckAsync([Entry()], CancellationToken.None);

        Assert.Single(found);
        Assert.Equal(2, _github.ApiRequests);
    }

    [Fact]
    public async Task Check_MoreRepositoriesThanTheLimit_AsksForTheFirstOnesOnly()
    {
        var entries = Enumerable.Range(0, ModuleUpdateChecker.MaxRepositories + 20)
            .Select(i => Entry(id: $"alex.m{i}", repository: $"alex/m{i}")).ToArray();

        await CheckAsync(entries);

        Assert.Equal(ModuleUpdateChecker.MaxRepositories, _github.ApiRequests);
    }

    // --- Pannes réseau et réponses douteuses --------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_NetworkDown_NoUpdate_NoException()
    {
        _github.ApiRaw(Repo, () => throw new HttpRequestException("Hôte inconnu."));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_ErrorStatus_NoUpdate_NoException(HttpStatusCode status)
    {
        _github.ApiRaw(Repo, () => FakeGitHub.Json(FakeGitHub.ToJson([Release("1.1.0")]), status));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Theory]
    [InlineData("<html>pas du json</html>")]
    [InlineData("{}")]
    [InlineData("[{\"tag_name\": 12, \"assets\": 3}]")]
    [InlineData("")]
    public async Task Check_GarbledAnswer_NoUpdate_NoException(string body)
    {
        _github.ApiRaw(Repo, () => FakeGitHub.Json(body));

        Assert.Empty(await CheckAsync(Entry()));
    }

    [Fact]
    public async Task Check_Cancelled_IsACancellation_NotASilentNothing()
    {
        _github.Api(Repo, Release("1.1.0"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewChecker().CheckAsync([Entry()], cts.Token));
    }

    [Fact]
    public async Task Check_ModuleWhoseVersionDoesNotParse_IsSkipped()
    {
        _github.Api(Repo, Release("1.1.0"));
        var broken = Entry() with { Manifest = Manifest(Id, "pas une version", Repo), Approval = new ApprovedModule(Id, "pas non plus", @"C:\x", "h", [], null, Repo) };

        Assert.Empty(await CheckAsync(broken));
    }

    // --- Du texte de l'internet ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_Notes_AreCleanedAndBounded()
    {
        _github.Api(Repo, Release("1.1.0", body: "Ligne 1\r\n\u202ELigne 2\u0007" + new string('x', 10_000)));

        var notes = (await CheckAsync(Entry())).Single().Value.Notes;

        Assert.StartsWith("Ligne 1\nLigne 2xxx", notes);
        Assert.True(notes.Length <= ReleaseFeed.MaxNotesLength);
        Assert.DoesNotContain('\u202E', notes);
        Assert.DoesNotContain('\u0007', notes);
    }

    [Fact]
    public async Task Check_TagLongerThanTheLimit_IsIgnored()
    {
        var longTag = "1.1.0-" + new string('a', 200);
        _github.Api(Repo, new FakeRelease("v" + longTag, Assets: FakeGitHub.ModuleAssets(Repo, Id, longTag)));

        Assert.Empty(await CheckAsync(Entry()));
    }
}
