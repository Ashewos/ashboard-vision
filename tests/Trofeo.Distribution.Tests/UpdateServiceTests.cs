using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Core;

namespace Trofeo.Distribution.Tests;

internal sealed class FakeLauncher : IInstallerLauncher
{
    public InstallerLaunch Result { get; set; } = InstallerLaunch.Started;
    public List<string> Launched { get; } = [];
    public Action<string>? OnLaunch { get; set; }

    public InstallerLaunch Launch(string setupPath)
    {
        Launched.Add(setupPath);
        OnLaunch?.Invoke(setupPath);
        return Result;
    }
}

/// <summary>Le service, ses faux (réseau, lanceur, horloge) et des dossiers temporaires.</summary>
internal sealed class UpdateHarness : IDisposable
{
    public static readonly byte[] Setup = Enumerable.Range(0, 5_000).Select(i => (byte)(i * 7)).ToArray();

    private readonly TempDir _dir = new();

    public UpdateHarness(string installed, params (string Tag, bool Prerelease)[] published)
    {
        Installed = Version(installed);
        Published = published;
        SettingsDir = _dir.Combine("ashboard-vision");
        BackupsRoot = _dir.Combine("ashboard-vision-sauvegardes");
        DownloadDir = _dir.Combine("mise-a-jour");
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(Path.Combine(SettingsDir, "layout.json"), "{}");

        Server = new FakeHandler(Respond);
        Store = new UpdatePreferencesStore(Path.Combine(SettingsDir, "mises-a-jour.json"));
        Launcher = new FakeLauncher();
        Service = new UpdateService(
            new UpdateChecker(Server.Client()), new UpdateDownloader(Server.Client()), Launcher, Store,
            Installed, SettingsDir, BackupsRoot, DownloadDir, Time);
    }

    public AppVersion Installed { get; }
    public (string Tag, bool Prerelease)[] Published { get; set; }
    public string SettingsDir { get; }
    public string BackupsRoot { get; }
    public string DownloadDir { get; }
    public FakeTimeProvider Time { get; } = new();
    public FakeHandler Server { get; }
    public UpdatePreferencesStore Store { get; }
    public FakeLauncher Launcher { get; }
    public UpdateService Service { get; }

    /// <summary>Remplace la réponse du flux GitHub (par défaut, la liste <see cref="Published"/>).</summary>
    public Func<HttpResponseMessage>? FeedOverride { get; set; }

    /// <summary>Remplace le contenu du fichier d'empreinte (par défaut, la bonne empreinte).</summary>
    public string? ChecksumOverride { get; set; }

    /// <summary>Si renseignée, tout téléchargement de l'installateur ou de son empreinte lève cette exception.</summary>
    public Exception? DownloadFailure { get; set; }

    public int FeedRequests => Server.Requests.Count(u => u.AbsoluteUri == ReleaseFeed.Url);
    public int SetupRequests => Server.Requests.Count(u => u.AbsoluteUri.EndsWith(".exe", StringComparison.Ordinal));

    public static AppVersion Version(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        return version;
    }

    private static string AssetUrl(string tag, string name) =>
        $"https://github.com/{ProductIdentity.Repository}/releases/download/{tag}/{name}";

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == ReleaseFeed.Url)
        {
            return FeedOverride?.Invoke() ?? FakeHandler.Text(FeedJson());
        }

        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }

        return url.EndsWith(".sha256", StringComparison.Ordinal)
            ? FakeHandler.Text(ChecksumOverride ?? Convert.ToHexString(SHA256.HashData(Setup)).ToLowerInvariant())
            : FakeHandler.Bytes(Setup);
    }

    private string FeedJson() => JsonSerializer.Serialize(Published.Select(p =>
    {
        var name = ProductIdentity.SetupFileName(p.Tag.TrimStart('v'));
        return new
        {
            tag_name = p.Tag,
            draft = false,
            prerelease = p.Prerelease,
            published_at = "2026-10-29T10:00:00Z",
            assets = new[]
            {
                new { name, size = Setup.Length, browser_download_url = AssetUrl(p.Tag, name) },
                new { name = name + ".sha256", size = 80, browser_download_url = AssetUrl(p.Tag, name + ".sha256") },
            },
        };
    }));

    /// <summary>
    /// Attend, au plus quelques secondes, que la condition soit vraie. Les faux répondent tout de suite : l'attente
    /// ne dure que le temps de laisser une tâche lancée par le minuteur se terminer.
    /// </summary>
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condition n'est jamais devenue vraie.");
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        Service.Dispose();
        _dir.Dispose();
    }
}

public sealed class UpdateServiceTests
{
    private static readonly (string, bool) Stable100 = ("v1.0.0", false);
    private static readonly (string, bool) Stable110 = ("v1.1.0", false);
    private static readonly (string, bool) Beta110 = ("v1.1.0-beta.1", true);

    [Fact]
    public async Task Start_FirstCheckAfter60Seconds_ThenEvery24Hours()
    {
        using var h = new UpdateHarness("1.0.0", Stable100);
        h.Service.Start();

        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, h.FeedRequests);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await UpdateHarness.Until(() => h.FeedRequests == 1 && h.Service.Current.State == UpdateState.UpToDate);

        h.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(1, h.FeedRequests);

        h.Time.Advance(TimeSpan.FromHours(1));
        await UpdateHarness.Until(() => h.FeedRequests == 2 && h.Service.Current.State == UpdateState.UpToDate);
    }

    [Fact]
    public async Task Check_NetworkError_StateFailed_ServiceKeepsSchedule()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        h.FeedOverride = () => throw new HttpRequestException("Hôte inconnu.");
        h.Service.Start();

        h.Time.Advance(TimeSpan.FromSeconds(60));
        await UpdateHarness.Until(() => h.Service.Current.State == UpdateState.Failed);

        Assert.False(string.IsNullOrWhiteSpace(h.Service.Current.Message));
        Assert.StartsWith("Impossible de vérifier les mises à jour : ", h.Service.Current.Message);

        // La panne ne décale rien : pas de nouvel essai avant l'heure, puis un essai à l'heure prévue, qui réussit.
        h.FeedOverride = null;
        h.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(1, h.FeedRequests);
        h.Time.Advance(TimeSpan.FromHours(1));
        await UpdateHarness.Until(() => h.Service.Current.State == UpdateState.Available);
        Assert.Equal("1.1.0", h.Service.Current.Version);
    }

    [Fact]
    public async Task Check_NoStableYet_StateNoStableYet()
    {
        using var h = new UpdateHarness("1.0.0-beta.1", ("v1.0.0-beta.2", true));

        await h.Service.CheckNowAsync(CancellationToken.None);

        Assert.Equal(UpdateState.NoStableYet, h.Service.Current.State);
        Assert.Equal("Aucune version stable pour l'instant.", h.Service.Current.Message);
    }

    [Fact]
    public async Task Check_UpToDate_AndAvailable_Messages()
    {
        using var h = new UpdateHarness("1.0.0", Stable100);
        await h.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.UpToDate, h.Service.Current.State);
        Assert.Equal("Tu as la dernière version.", h.Service.Current.Message);

        h.Published = [Stable100, Stable110];
        await h.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, h.Service.Current.State);
        Assert.Equal("1.1.0", h.Service.Current.Version);
        Assert.False(h.Service.Current.IsDowngrade);
        Assert.Equal("La version 1.1.0 est disponible.", h.Service.Current.Message);
    }

    [Fact]
    public async Task Install_Started_ShowsDownloadThenInstalling_AndLaunchesTheVerifiedSetup()
    {
        using var h = new UpdateHarness("1.0.0", Stable100, Stable110);
        await h.Service.CheckNowAsync(CancellationToken.None);
        var seen = new List<UpdateStatus>();
        h.Service.Changed += (_, _) => seen.Add(h.Service.Current);

        var (ok, message) = await h.Service.InstallAsync(false, false, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Windows va demander l'autorisation d'installer la mise à jour.", message);
        Assert.Equal(UpdateState.Installing, h.Service.Current.State);
        Assert.Contains(seen, s => s.State == UpdateState.Downloading && s.Percent == 0 && s.Message == "Téléchargement… 0 %");
        Assert.Contains(seen, s => s.State == UpdateState.Downloading && s.Percent == 100);
        var launched = Assert.Single(h.Launcher.Launched);
        Assert.Equal(Path.Combine(h.DownloadDir, ProductIdentity.SetupFileName("1.1.0")), launched);
        // Pas de retour en arrière : pas de copie des réglages.
        Assert.False(Directory.Exists(h.BackupsRoot));
    }

    [Fact]
    public async Task Install_TwiceInARow_OneDownloadOneLaunch()
    {
        using var h = new UpdateHarness("1.0.0", Stable100, Stable110);
        await h.Service.CheckNowAsync(CancellationToken.None);

        // Deux fenêtres, ou la fenêtre et le menu de l'icône : la seconde demande attend la première, puis est refusée.
        var results = await Task.WhenAll(
            h.Service.InstallAsync(false, true, CancellationToken.None),
            h.Service.InstallAsync(false, true, CancellationToken.None));

        Assert.Single(results, r => r.Ok);
        var refused = Assert.Single(results, r => !r.Ok);
        Assert.Equal("L'installation de la mise à jour est déjà lancée.", refused.Message);
        Assert.Equal(1, h.SetupRequests);
        Assert.Single(h.Launcher.Launched);
        Assert.Equal(UpdateState.Installing, h.Service.Current.State);
        Assert.True(File.Exists(h.Launcher.Launched[0]));

        // Une demande tardive est refusée de la même façon.
        var late = await h.Service.InstallAsync(false, false, CancellationToken.None);
        Assert.False(late.Ok);
        Assert.Equal(1, h.SetupRequests);
        Assert.Single(h.Launcher.Launched);
    }

    [Fact]
    public async Task Install_SetupChangedBeforeLaunch_StateFailed_NeverLaunches()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        await h.Service.CheckNowAsync(CancellationToken.None);
        var setupPath = Path.Combine(h.DownloadDir, ProductIdentity.SetupFileName("1.1.0"));
        // Le fichier est remplacé juste avant le lancement (l'état « installation » est publié à ce moment-là).
        h.Service.Changed += (_, _) =>
        {
            if (h.Service.Current.State == UpdateState.Installing)
            {
                File.WriteAllBytes(setupPath, [1, 2, 3]);
            }
        };

        var (ok, message) = await h.Service.InstallAsync(false, reopenSettings: true, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("a changé", message);
        Assert.Equal(UpdateState.Failed, h.Service.Current.State);
        Assert.Empty(h.Launcher.Launched);
        Assert.False(File.Exists(setupPath));
        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));
    }

    [Fact]
    public async Task Install_NothingOffered_DoesNothing()
    {
        using var h = new UpdateHarness("1.0.0", Stable100);
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, message) = await h.Service.InstallAsync(false, false, CancellationToken.None);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(h.Launcher.Launched);
        Assert.Equal(0, h.SetupRequests);
    }

    [Fact]
    public async Task Install_LauncherCancelled_StateBackToAvailable_MessageCancelled()
    {
        using var h = new UpdateHarness("1.0.0", Stable100, Stable110);
        h.Launcher.Result = InstallerLaunch.Cancelled;
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, message) = await h.Service.InstallAsync(false, reopenSettings: true, CancellationToken.None);

        const string cancelled = "Mise à jour annulée : l'autorisation n'a pas été donnée.";
        Assert.False(ok);
        Assert.Equal(cancelled, message);
        Assert.Equal(UpdateState.Available, h.Service.Current.State);
        Assert.Equal(cancelled, h.Service.Current.Message);
        Assert.Equal("1.1.0", h.Service.Current.Version);
        // L'installateur n'a pas démarré : la fenêtre ne doit pas se rouvrir toute seule plus tard.
        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));
        // Et on peut réessayer.
        h.Launcher.Result = InstallerLaunch.Started;
        Assert.True((await h.Service.InstallAsync(false, false, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Install_LauncherFailed_StateFailed()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        h.Launcher.Result = InstallerLaunch.Failed;
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, message) = await h.Service.InstallAsync(false, false, CancellationToken.None);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Equal(UpdateState.Failed, h.Service.Current.State);
        Assert.Equal(message, h.Service.Current.Message);
    }

    [Fact]
    public async Task Install_Downgrade_WithoutConfirmation_Refused()
    {
        using var h = new UpdateHarness("1.1.0-beta.1", Stable100, Beta110);
        await h.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, h.Service.Current.State);
        Assert.True(h.Service.Current.IsDowngrade);

        var (ok, message) = await h.Service.InstallAsync(confirmDowngrade: false, false, CancellationToken.None);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(h.Launcher.Launched);
        Assert.Equal(0, h.SetupRequests);
        Assert.False(Directory.Exists(h.BackupsRoot));
        Assert.Equal(UpdateState.Available, h.Service.Current.State);
    }

    [Fact]
    public async Task Install_Downgrade_BackupCreatedBeforeLaunch()
    {
        using var h = new UpdateHarness("1.1.0-beta.1", Stable100, Beta110);
        h.Time.SetUtcNow(new DateTimeOffset(2026, 10, 29, 14, 5, 9, TimeSpan.Zero));
        string? backupSeenByLauncher = null;
        h.Launcher.OnLaunch = _ =>
        {
            var backups = Directory.Exists(h.BackupsRoot) ? Directory.GetDirectories(h.BackupsRoot) : [];
            backupSeenByLauncher = backups.Length == 1 && File.Exists(Path.Combine(backups[0], "layout.json")) ? backups[0] : null;
        };
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, _) = await h.Service.InstallAsync(confirmDowngrade: true, false, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(backupSeenByLauncher);
        Assert.EndsWith("-1.1.0-beta.1", backupSeenByLauncher);
        Assert.Equal(h.BackupsRoot, Path.GetDirectoryName(backupSeenByLauncher));
    }

    [Fact]
    public async Task Install_Downgrade_BackupFails_DoesNotLaunch()
    {
        using var h = new UpdateHarness("1.1.0-beta.1", Stable100, Beta110);
        // Un fichier à la place du dossier des copies : la copie ne peut pas se faire.
        File.WriteAllText(h.BackupsRoot, "je suis un fichier");
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, message) = await h.Service.InstallAsync(confirmDowngrade: true, reopenSettings: true, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("copie des réglages", message);
        Assert.Empty(h.Launcher.Launched);
        Assert.Equal(UpdateState.Failed, h.Service.Current.State);
        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));
    }

    [Fact]
    public async Task Install_UnexpectedDownloadException_StateFailed_SoberMessage_OfferKept()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        await h.Service.CheckNowAsync(CancellationToken.None);
        h.DownloadFailure = new InvalidOperationException("détail interne qui ne doit pas s'afficher");

        var (ok, message) = await h.Service.InstallAsync(false, reopenSettings: true, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Le téléchargement a échoué de façon inattendue : la mise à jour n'est pas installée.", message);
        Assert.DoesNotContain("détail interne", message);
        Assert.Equal(UpdateState.Failed, h.Service.Current.State);
        Assert.Equal(message, h.Service.Current.Message);
        Assert.Equal("1.1.0", h.Service.Current.Version);
        Assert.Empty(h.Launcher.Launched);
        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));

        // L'offre est gardée : on peut réessayer sans revérifier.
        h.DownloadFailure = null;
        Assert.True((await h.Service.InstallAsync(false, false, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Install_ChecksumFails_NeverLaunches()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        h.ChecksumOverride = new string('a', 64);
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, message) = await h.Service.InstallAsync(false, reopenSettings: true, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("empreinte", message);
        Assert.Empty(h.Launcher.Launched);
        Assert.Equal(UpdateState.Failed, h.Service.Current.State);
        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));
        Assert.Empty(Directory.GetFileSystemEntries(h.DownloadDir));
    }

    [Fact]
    public async Task SetPreferences_WaitForNextStable_SavesAndRechecks()
    {
        using var h = new UpdateHarness("1.1.0-beta.1", Stable100, Beta110);
        await h.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, h.Service.Current.State);
        Assert.Equal(1, h.FeedRequests);
        var wait = new UpdatePreferences(UpdateChannel.Stable, true);

        await h.Service.SetPreferencesAsync(wait, CancellationToken.None);

        Assert.Equal(wait, h.Store.Load());
        Assert.Equal(wait, h.Service.Current.Preferences);
        Assert.Equal(2, h.FeedRequests);
        Assert.Equal(UpdateState.UpToDate, h.Service.Current.State);
    }

    [Fact]
    public void Constructor_LoadsSavedPreferences()
    {
        using var h = new UpdateHarness("1.0.0", Stable100);
        var beta = new UpdatePreferences(UpdateChannel.Beta, false);
        h.Store.Save(beta);

        using var other = new UpdateService(
            new UpdateChecker(h.Server.Client()), new UpdateDownloader(h.Server.Client()), h.Launcher, h.Store,
            h.Installed, h.SettingsDir, h.BackupsRoot, h.DownloadDir, h.Time);

        Assert.Equal(beta, other.Current.Preferences);
        Assert.Equal(UpdateState.Unknown, other.Current.State);
    }

    [Fact]
    public async Task Install_ReopenSettings_MarkerCreatedBeforeLaunch()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        var markerSeenByLauncher = false;
        h.Launcher.OnLaunch = _ => markerSeenByLauncher = File.Exists(Path.Combine(h.SettingsDir, "rouvrir-reglages"));
        await h.Service.CheckNowAsync(CancellationToken.None);

        var (ok, _) = await h.Service.InstallAsync(false, reopenSettings: true, CancellationToken.None);

        Assert.True(ok);
        Assert.True(markerSeenByLauncher);
        Assert.True(ReopenSettingsMarker.Consume(h.SettingsDir));
    }

    [Fact]
    public async Task Install_WithoutReopenSettings_CreatesNoMarker()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        await h.Service.CheckNowAsync(CancellationToken.None);

        await h.Service.InstallAsync(false, reopenSettings: false, CancellationToken.None);

        Assert.False(ReopenSettingsMarker.Consume(h.SettingsDir));
    }

    [Fact]
    public void ReopenSettingsMarker_ConsumeTwice_SecondFalse()
    {
        using var dir = new TempDir();
        var settings = dir.Combine("pas-encore-cree");

        Assert.False(ReopenSettingsMarker.Consume(settings));

        ReopenSettingsMarker.Create(settings);

        Assert.True(ReopenSettingsMarker.Consume(settings));
        Assert.False(ReopenSettingsMarker.Consume(settings));
    }

    [Fact]
    public async Task Dispose_StopsTheSchedule()
    {
        using var h = new UpdateHarness("1.0.0", Stable110);
        h.Service.Start();

        h.Service.Dispose();
        h.Time.Advance(TimeSpan.FromDays(2));
        await Task.Yield();

        Assert.Equal(0, h.FeedRequests);
    }
}
