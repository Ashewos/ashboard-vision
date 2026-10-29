using System.Net;
using System.Security.Cryptography;
using Trofeo.Core;

namespace Trofeo.Distribution.Tests;

public sealed class UpdateDownloaderTests
{
    private const string SetupUrl = "https://github.com/Ashewos/ashboard-vision/releases/download/v1.0.0/Ashboard-Vision-Setup-1.0.0.exe";
    private const string ChecksumUrl = SetupUrl + ".sha256";
    private static readonly byte[] Setup = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static GitHubRelease Release(long? size = null, string? setupUrl = null, string? checksumUrl = ChecksumUrl) =>
        new(new AppVersion(1, 0, 0), false, setupUrl ?? SetupUrl, size ?? Setup.Length, checksumUrl, DateTimeOffset.UnixEpoch);

    private static bool IsChecksum(HttpRequestMessage request) =>
        request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal);

    private static FakeHandler Server(string checksumFile, byte[]? setup = null) =>
        new(request => IsChecksum(request) ? FakeHandler.Text(checksumFile) : FakeHandler.Bytes(setup ?? Setup));

    private static string SetupPath(TempDir dir) => dir.Combine(ProductIdentity.SetupFileName("1.0.0"));

    [Theory]
    [InlineData("{0}  Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("{0} *Ashboard-Vision-Setup-1.0.0.exe\n")]
    [InlineData("{0}")]
    [InlineData("{0}\r\n")]
    public async Task Download_ChecksumMatches_ReturnsVerifiedPath(string format)
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(string.Format(format, Hex(Setup).ToLowerInvariant())).Client());

        var path = await downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None);

        Assert.Equal(SetupPath(dir), path);
        Assert.Equal(Setup, File.ReadAllBytes(path));
        // Ni fichier partiel ni autre chose dans le dossier.
        Assert.Equal([path], Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumInUppercase_IsAccepted()
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(Hex(Setup).ToUpperInvariant() + "  Setup.exe").Client());

        var path = await downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Download_ReportsProgressUpToOne()
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(Hex(Setup)).Client());
        var reports = new List<double>();

        await downloader.DownloadAsync(Release(), dir.Path, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task Download_ChecksumMismatch_ThrowsAndDeletesFile()
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(new string('0', 64)).Client());

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None));

        Assert.Contains("empreinte", ex.Message);
        Assert.False(File.Exists(SetupPath(dir)));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumMissing_Throws()
    {
        using var dir = new TempDir();
        var handler = Server(Hex(Setup));
        var downloader = new UpdateDownloader(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(checksumUrl: null), dir.Path, null, CancellationToken.None));

        // Rien n'est même téléchargé.
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("ceci n'est pas une empreinte")]
    [InlineData("")]
    [InlineData("abcd")]
    public async Task Download_ChecksumFileUnreadable_Throws(string content)
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(content).Client());

        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_Interrupted_ThrowsAndDeletesPartial()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(request => IsChecksum(request)
            ? FakeHandler.Text(Hex(Setup))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(Setup[..100_000])) });
        var downloader = new UpdateDownloader(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ServerError_Throws()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(request => IsChecksum(request)
            ? FakeHandler.Text(Hex(Setup))
            : FakeHandler.Text("oups", HttpStatusCode.NotFound));
        var downloader = new UpdateDownloader(handler.Client());

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(), dir.Path, null, CancellationToken.None));

        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Download_SizeDiffersFromRelease_Throws(long difference)
    {
        using var dir = new TempDir();
        var downloader = new UpdateDownloader(Server(Hex(Setup)).Client());

        // Plus petit comme plus grand que l'annonce : l'empreinte serait bonne, mais ce n'est pas le fichier publié.
        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(size: Setup.Length + difference), dir.Path, null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("http://github.com/Ashewos/ashboard-vision/releases/download/v1.0.0/Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("https://evil.example/Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("https://github.com.evil.example/Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("https://evil.example/github.com/Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("ftp://github.com/Ashboard-Vision-Setup-1.0.0.exe")]
    [InlineData("pas une adresse")]
    public async Task Download_NonGitHubUrl_Throws(string setupUrl)
    {
        using var dir = new TempDir();
        var handler = Server(Hex(Setup));
        var downloader = new UpdateDownloader(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(setupUrl: setupUrl), dir.Path, null, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_NonGitHubChecksumUrl_Throws()
    {
        using var dir = new TempDir();
        var handler = Server(Hex(Setup));
        var downloader = new UpdateDownloader(handler.Client());

        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release(checksumUrl: "https://evil.example/empreinte.sha256"), dir.Path, null, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_EmptiesTargetDirFilesOnly()
    {
        using var dir = new TempDir();
        var target = dir.Combine("mise-a-jour");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "ancien-setup.exe"), "vieux");
        File.WriteAllText(Path.Combine(target, "reste.part"), "vieux");
        File.WriteAllText(dir.Combine("voisin.txt"), "intact");
        var downloader = new UpdateDownloader(Server(Hex(Setup)).Client());

        var path = await downloader.DownloadAsync(Release(), target, null, CancellationToken.None);

        Assert.Equal([path], Directory.GetFileSystemEntries(target));
        Assert.Equal("intact", File.ReadAllText(dir.Combine("voisin.txt")));
    }

    [Fact]
    public async Task Download_Cancelled_Throws_AndLeavesNothing()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var downloader = new UpdateDownloader(Server(Hex(Setup)).Client());
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Release(), dir.Path, null, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }
}

/// <summary>Un <see cref="IProgress{T}"/> qui répond tout de suite, sans passer par un contexte de synchronisation.</summary>
internal sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
