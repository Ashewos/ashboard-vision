using System.Diagnostics;
using System.Text.Json;

namespace Trofeo.Display;

/// <summary>
/// Se souvient de la vue affichée dans un petit fichier à part (<c>vue-affichee.json</c>, <c>{"ViewId":"…"}</c>) : après un
/// redémarrage, l'écran reprend sur la même vue. Au plus une écriture par seconde : une rafale de changements ne garde que la
/// dernière valeur, écrite au plus tard une seconde après la première. Sûr entre fils. Une panne d'écriture est journalisée,
/// jamais levée.
/// </summary>
public sealed class DisplayedViewStore(string filePath, TimeProvider time)
{
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(1);

    private sealed record Stored(Guid ViewId);

    private readonly object _gate = new();
    private Guid? _pending;
    private long _lastWrite;
    private bool _hasWritten;
    private ITimer? _timer;

    /// <summary>La vue gardée ; <c>null</c> si le fichier manque, est illisible ou ne nomme rien.</summary>
    public Guid? Load()
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(filePath));
            return stored is { } s && s.ViewId != Guid.Empty ? s.ViewId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Vue affichée : fichier illisible, on repart de la première vue ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Note la vue affichée : écrite tout de suite si rien n'a été écrit depuis une seconde, sinon au plus tard une seconde après la dernière écriture.</summary>
    public void Save(Guid viewId)
    {
        lock (_gate)
        {
            _pending = viewId;
            var sinceLast = _hasWritten ? time.GetElapsedTime(_lastWrite) : WriteInterval;
            if (sinceLast >= WriteInterval)
            {
                WritePending();
            }
            else if (_timer is null)
            {
                _timer = time.CreateTimer(OnTimer, null, WriteInterval - sinceLast, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Écrit sans attendre la valeur en attente, s'il y en a une (à l'arrêt du service).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            WritePending();
        }
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            WritePending();
        }
    }

    private void WritePending()
    {
        if (_pending is not { } viewId) return;
        _pending = null;
        _lastWrite = time.GetTimestamp();
        _hasWritten = true;
        try
        {
            if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(viewId)));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Vue affichée : écriture impossible ({ex.Message}).");
        }
    }
}
