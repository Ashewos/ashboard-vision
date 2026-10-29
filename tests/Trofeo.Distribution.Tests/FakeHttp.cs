using System.Net;

namespace Trofeo.Distribution.Tests;

/// <summary>Un faux serveur : répond avec ce que <c>respond</c> décide, compte les requêtes, ne touche jamais au réseau.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public HttpClient Client() => new(this);

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text) };

    public static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}

/// <summary>Un flux qui rend ses octets puis échoue, comme une connexion coupée en plein téléchargement.</summary>
internal sealed class BrokenStream(byte[] beforeFailure) : Stream
{
    private int _position;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= beforeFailure.Length)
        {
            throw new IOException("Connexion coupée.");
        }

        var n = Math.Min(count, beforeFailure.Length - _position);
        Array.Copy(beforeFailure, _position, buffer, offset, n);
        _position += n;
        return n;
    }

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

/// <summary>Un dossier temporaire, supprimé à la fin du test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ashboard-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
