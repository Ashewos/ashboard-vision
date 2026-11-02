using System.Net;
using System.Security.Cryptography;

namespace Trofeo.Distribution.Tests;

// Le téléchargement vérifié, tel que les mises à jour des modules s'en servent (celui du logiciel a ses propres tests).
public sealed class VerifiedDownloadTests
{
    private const string FileUrl = "https://github.com/alex/demo/releases/download/v1.0.0/alex.demo-1.0.0.avmod";
    private const string ChecksumUrl = FileUrl + ".sha256";
    private static readonly byte[] Content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 17)).ToArray();

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsChecksum(HttpRequestMessage request) =>
        request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal);

    private static FakeHandler Server(string checksumFile, byte[]? content = null) =>
        new(request => IsChecksum(request) ? FakeHandler.Text(checksumFile) : FakeHandler.Bytes(content ?? Content));

    private static Task<string> Run(FakeHandler handler, string destination, IProgress<int>? progress = null, long? maxBytes = null,
        string file = FileUrl, string checksum = ChecksumUrl, CancellationToken ct = default) =>
        maxBytes is { } max
            ? VerifiedDownload.DownloadAsync(handler.Client(), new Uri(file), new Uri(checksum), destination, progress, ct, max)
            : VerifiedDownload.DownloadAsync(handler.Client(), new Uri(file), new Uri(checksum), destination, progress, ct);

    [Fact]
    public async Task Download_ChecksumMatches_WritesTheFileAndReturnsTheHash()
    {
        using var dir = new TempDir();
        var destination = dir.Combine("alex.demo-1.0.0.avmod");

        var hash = await Run(Server(Hex(Content) + "  alex.demo-1.0.0.avmod"), destination);

        Assert.Equal(Hex(Content), hash, ignoreCase: true);
        Assert.Equal(Content, File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFileSystemEntries(dir.Path)); // pas de fichier partiel
    }

    [Fact]
    public async Task Download_CreatesTheDestinationFolder()
    {
        using var dir = new TempDir();
        var destination = dir.Combine("sous", "dossier", "a.avmod");

        await Run(Server(Hex(Content)), destination);

        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Download_ReportsPercentUpToOneHundred()
    {
        using var dir = new TempDir();
        var reports = new List<int>();

        await Run(Server(Hex(Content)), dir.Combine("a.avmod"), new IntProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task Download_ChecksumMismatch_ThrowsAndLeavesNothing()
    {
        using var dir = new TempDir();

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Run(Server(new string('0', 64)), dir.Combine("a.avmod")));

        Assert.Contains("empreinte", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("pas une empreinte")]
    [InlineData("")]
    public async Task Download_ChecksumUnreadable_ThrowsAndLeavesNothing(string checksumFile)
    {
        using var dir = new TempDir();

        await Assert.ThrowsAsync<UpdateException>(() => Run(Server(checksumFile), dir.Combine("a.avmod")));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("http://github.com/alex/demo/a.avmod")]
    [InlineData("https://evil.example/alex/demo/a.avmod")]
    [InlineData("https://github.com.evil.example/a.avmod")]
    [InlineData("ftp://github.com/a.avmod")]
    public async Task Download_NotGitHubHttps_ThrowsBeforeAnyRequest(string file)
    {
        using var dir = new TempDir();
        var handler = Server(Hex(Content));

        await Assert.ThrowsAsync<UpdateException>(() => Run(handler, dir.Combine("a.avmod"), file: file));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_ChecksumNotOnGitHub_ThrowsBeforeAnyRequest()
    {
        using var dir = new TempDir();
        var handler = Server(Hex(Content));

        await Assert.ThrowsAsync<UpdateException>(() => Run(handler, dir.Combine("a.avmod"), checksum: "https://evil.example/a.sha256"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_BiggerThanTheLimit_ThrowsAndLeavesNothing()
    {
        using var dir = new TempDir();

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Run(Server(Hex(Content)), dir.Combine("a.avmod"), maxBytes: Content.Length - 1));

        Assert.Contains("trop gros", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ExactlyTheLimit_IsAccepted()
    {
        using var dir = new TempDir();

        await Run(Server(Hex(Content)), dir.Combine("a.avmod"), maxBytes: Content.Length);

        Assert.True(File.Exists(dir.Combine("a.avmod")));
    }

    [Fact]
    public async Task Download_ServerError_Throws()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(request => IsChecksum(request) ? FakeHandler.Text(Hex(Content)) : FakeHandler.Text("non", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Run(handler, dir.Combine("a.avmod")));

        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_Interrupted_ThrowsAndLeavesNothing()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(request => IsChecksum(request)
            ? FakeHandler.Text(Hex(Content))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(Content[..50_000])) });

        await Assert.ThrowsAsync<UpdateException>(() => Run(handler, dir.Combine("a.avmod")));

        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_FailedVerification_NeverLeavesAFileUnderTheRealName()
    {
        using var dir = new TempDir();
        var destination = dir.Combine("a.avmod");

        await Assert.ThrowsAsync<UpdateException>(() => Run(Server(new string('0', 64)), destination));

        Assert.False(File.Exists(destination));
    }

    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
