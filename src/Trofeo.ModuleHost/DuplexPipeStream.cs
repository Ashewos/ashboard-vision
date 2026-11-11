namespace Trofeo.ModuleHost;

/// <summary>
/// Une connexion vers un service de ce PC, vue comme un seul flux : on lit d'un tube et on écrit dans l'autre (le service
/// relaie vers <c>127.0.0.1</c>). Fermer le flux ferme les deux tubes, donc la connexion.
/// </summary>
internal sealed class DuplexPipeStream : Stream
{
    private readonly Stream _reader;
    private readonly Stream _writer;

    public DuplexPipeStream(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _reader.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _writer.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _writer.WriteAsync(buffer, cancellationToken);

    // Les tubes anonymes n'ont rien à vider : chaque écriture part tout de suite.
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _writer.Dispose();
            }
            finally
            {
                _reader.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
