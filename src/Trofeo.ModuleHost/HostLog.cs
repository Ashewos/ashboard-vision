using System.Diagnostics;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>
/// Le journal d'une instance : chaque message part au service (préfixé de l'instance) dans la limite de 20 par seconde.
/// Au-delà, les messages sont perdus et un seul avertissement dit combien, au message suivant d'une nouvelle seconde.
/// Un même message d'erreur n'est écrit en entier qu'une fois par minute.
/// </summary>
internal sealed class HostLog : IModuleLog
{
    public const int MaxPerSecond = 20;

    private static readonly long Second = Stopwatch.Frequency;
    private static readonly long Minute = 60 * Stopwatch.Frequency;

    private readonly HostLink _link;
    private readonly string _prefix;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _errors = [];
    private long _windowStart;
    private bool _windowOpen;
    private int _count;
    private int _dropped;

    public HostLog(HostLink link, string prefix)
    {
        _link = link;
        _prefix = prefix;
    }

    public void Info(string message) => Write("info", message);

    public void Warning(string message) => Write("warn", message);

    public void Error(string message) => Write("error", message);

    /// <summary>Un message de l'hôte lui-même (pas du module), qui ne compte pas dans la limite.</summary>
    public void Host(string level, string message) => _link.Post(new Log(level, HostLink.Clip(_prefix + message)));

    private void Write(string level, string message)
    {
        message ??= "";
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (level == "error" && !AllowError(message, now))
            {
                return;
            }

            if (!_windowOpen || now - _windowStart >= Second)
            {
                if (_dropped > 0)
                {
                    _link.Post(new Log("warn", $"{_prefix}{_dropped} message(s) du module ignoré(s) : {MaxPerSecond} par seconde au plus."));
                }

                _windowOpen = true;
                _windowStart = now;
                _count = 0;
                _dropped = 0;
            }

            if (_count >= MaxPerSecond)
            {
                _dropped++;
                return;
            }

            _count++;
            _link.Post(new Log(level, HostLink.Clip(_prefix + message)));
        }
    }

    private bool AllowError(string message, long now)
    {
        if (_errors.TryGetValue(message, out var last) && now - last < Minute)
        {
            return false;
        }

        if (_errors.Count >= 100)
        {
            _errors.Clear();
        }

        _errors[message] = now;
        return true;
    }
}
