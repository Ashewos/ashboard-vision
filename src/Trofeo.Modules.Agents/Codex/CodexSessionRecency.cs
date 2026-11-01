using System.Text.Json;

namespace Trofeo.Modules.Agents;

/// <summary>Fraîcheur d'une transcription, même quand Windows conserve une ancienne date de modification.</summary>
internal sealed class CodexSessionRecency
{
    // Seule une petite fin de fichier est inspectée ; ni messages ni raisonnement ne sont conservés ici.
    private const int TailBytes = 64 * 1024;
    private sealed record Stamp(long Length, DateTimeOffset WrittenAt, DateTimeOffset? EventAt);
    private readonly Dictionary<string, Stamp> _cache = new(StringComparer.Ordinal);

    public DateTimeOffset? Read(FileInfo file)
    {
        try
        {
            // Length est lu sur le handle : les métadonnées de l'énumération peuvent rester périmées sur Windows.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var writtenAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            _cache.TryGetValue(file.FullName, out var previous);
            if (previous is not null && previous.Length == length && previous.WrittenAt == writtenAt)
            {
                return Latest(writtenAt, previous.EventAt);
            }

            var eventAt = ReadLatestTimestamp(stream, length);
            // Une ligne volumineuse ajoutée peut cacher le précédent événement : conserver sa date connue.
            // Une troncature/réécriture ne doit en revanche pas hériter de l'ancien fichier.
            if (previous is not null && length > previous.Length && previous.EventAt is { } old
                && (eventAt is null || old > eventAt))
            {
                eventAt = old;
            }
            _cache[file.FullName] = new Stamp(length, writtenAt, eventAt);
            return Latest(writtenAt, eventAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset Latest(DateTimeOffset writtenAt, DateTimeOffset? eventAt) =>
        eventAt is { } at && at > writtenAt ? at : writtenAt;

    private static DateTimeOffset? ReadLatestTimestamp(FileStream stream, long length)
    {
        var start = Math.Max(0, length - TailBytes);
        var discardFirst = false;
        if (start > 0)
        {
            stream.Seek(start - 1, SeekOrigin.Begin);
            discardFirst = stream.ReadByte() != '\n';
        }
        var buffer = new byte[(int)(length - start)];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var remaining = buffer.AsMemory(0, count);
        DateTimeOffset? latest = null;
        while (!remaining.IsEmpty)
        {
            var end = remaining.Span.IndexOf((byte)'\n');
            if (end < 0) break; // jamais utiliser une dernière ligne encore incomplète
            if (!discardFirst)
            {
                try
                {
                    using var document = JsonDocument.Parse(remaining[..end]);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
                        && value.TryGetDateTimeOffset(out var at) && (latest is null || at > latest))
                    {
                        latest = at;
                    }
                }
                catch (JsonException)
                {
                    // Une ligne invalide ne masque pas les horodatages lisibles autour d'elle.
                }
            }
            discardFirst = false;
            remaining = remaining[(end + 1)..];
        }
        return latest;
    }

    public void ForgetExcept(HashSet<string> paths)
    {
        foreach (var path in _cache.Keys.Where(path => !paths.Contains(path)).ToList()) _cache.Remove(path);
    }

    public void Clear() => _cache.Clear();
}
