using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trofeo.Extensions.Tests;

/// <summary>Un fichier joint à une version fabriquée pour les tests : son nom, son contenu et l'adresse où GitHub le servirait.</summary>
internal sealed record FakeAsset(string Name, string Url, long Size);

/// <summary>Une version de dépôt telle que l'API de GitHub la décrit.</summary>
internal sealed record FakeRelease(string Tag, bool Prerelease = false, string Body = "Notes de la version.", params FakeAsset[] Assets);

/// <summary>
/// Un faux GitHub : l'API des versions d'un dépôt et le téléchargement des fichiers, sans jamais toucher au réseau. Chaque requête est
/// consignée ; une adresse que le test n'a pas prévue répond 404 (et reste visible dans <see cref="Requests"/>).
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<Uri> _requests = [];
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public int ApiRequests => Requests.Count(uri => uri.Host == "api.github.com");

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>L'adresse standard d'un fichier joint à la version <paramref name="tag"/> de <paramref name="repository"/>.</summary>
    public static string AssetUrl(string repository, string tag, string name) => $"https://github.com/{repository}/releases/download/{tag}/{name}";

    public static FakeAsset Asset(string repository, string tag, string name, long size = 4096) => new(name, AssetUrl(repository, tag, name), size);

    /// <summary>Les deux fichiers d'un module pour une version : <c>&lt;id&gt;-&lt;version&gt;.avmod</c> et son <c>.sha256</c>.</summary>
    public static FakeAsset[] ModuleAssets(string repository, string id, string version, long size = 4096) =>
        [Asset(repository, "v" + version, $"{id}-{version}.avmod", size), Asset(repository, "v" + version, $"{id}-{version}.avmod.sha256", 100)];

    public void Api(string repository, params FakeRelease[] releases) =>
        Route($"https://api.github.com/repos/{repository}/releases?per_page=10", () => Json(ToJson(releases)));

    public void ApiRaw(string repository, Func<HttpResponseMessage> response) =>
        Route($"https://api.github.com/repos/{repository}/releases?per_page=10", response);

    public void File(string url, byte[] content) => Route(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

    /// <summary>Publie un paquet et son empreinte à leurs adresses standard ; <paramref name="checksum"/> remplace l'empreinte juste (pour simuler une altération).</summary>
    public void Package(string repository, string id, string version, byte[] content, string? checksum = null)
    {
        var tag = "v" + version;
        File(AssetUrl(repository, tag, $"{id}-{version}.avmod"), content);
        File(AssetUrl(repository, tag, $"{id}-{version}.avmod.sha256"), Encoding.UTF8.GetBytes((checksum ?? Hex(content)) + $"  {id}-{version}.avmod\n"));
    }

    public static string Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Route(string url, Func<HttpResponseMessage> response)
    {
        lock (_gate)
        {
            _routes[url] = response;
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string ToJson(IEnumerable<FakeRelease> releases) => JsonSerializer.Serialize(releases.Select(release => new
    {
        tag_name = release.Tag,
        draft = false,
        prerelease = release.Prerelease,
        body = release.Body,
        published_at = "2026-11-02T10:00:00Z",
        assets = release.Assets.Select(asset => new { name = asset.Name, size = asset.Size, browser_download_url = asset.Url }),
    }));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<HttpResponseMessage>? route;
        lock (_gate)
        {
            _requests.Add(request.RequestUri!);
            _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out route);
        }

        return Task.FromResult(route?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Un corps de réponse qui rend quelques octets puis ne dit plus rien : un serveur qui goutte à goutte, ou qui se tait.</summary>
internal sealed class StalledStream(byte[] beforeStall) : Stream
{
    private int _position;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < beforeStall.Length)
        {
            var n = Math.Min(buffer.Length, beforeStall.Length - _position);
            beforeStall.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
