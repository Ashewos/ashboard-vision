using System.Diagnostics;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Consumption;

/// <summary>Ce que la mesure d'un hôte rend à un instant donné.</summary>
/// <param name="Usage">Ce que son job object a consommé depuis son lancement.</param>
/// <param name="PrivateBytes">Sa mémoire privée, en octets.</param>
/// <param name="DisplayName">Le nom du module à afficher ; <c>null</c> : on garde celui d'avant (ou l'identifiant).</param>
public readonly record struct HostReading(JobUsage Usage, long PrivateBytes, string? DisplayName = null);

/// <summary>
/// Fait les relevés de <see cref="ConsumptionMeter"/>, et seulement quand ils servent. Le rythme :
/// <list type="bullet">
/// <item><b>Aucun hôte en cours : rien.</b> Pas de minuteur, pas de relevé, quoi que la fenêtre demande.</item>
/// <item><b>Un hôte, la fenêtre ne lit pas :</b> un relevé par minute, de quoi tenir le repère « consommation élevée ».</item>
/// <item><b>La fenêtre lit</b> (<see cref="Touch"/>, à chaque requête de consommation) : un relevé tout de suite, puis toutes les
/// 5 s. Sans nouvelle requête depuis <see cref="ReadingTimeout"/>, le rythme d'une minute revient tout seul au relevé suivant.</item>
/// </list>
/// Un seul minuteur sert tous les hôtes ; il naît avec le premier et disparaît avec le dernier. Les relevés se font hors de tout
/// verrou de cette classe : <paramref name="read"/> peut prendre ceux du service.
/// </summary>
/// <param name="time">La source de temps et de minuteurs.</param>
/// <param name="meter">Reçoit les relevés.</param>
/// <param name="read">Mesure l'hôte de ce module ; <c>null</c> s'il est parti entre-temps.</param>
public sealed class ConsumptionSampler(TimeProvider time, ConsumptionMeter meter, Func<string, HostReading?> read) : IDisposable
{
    /// <summary>L'intervalle des relevés quand la fenêtre ne lit pas.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

    /// <summary>L'intervalle des relevés pendant que la fenêtre lit.</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(5);

    /// <summary>Le temps sans requête de consommation au bout duquel la fenêtre n'est plus censée lire.</summary>
    public static readonly TimeSpan ReadingTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _hosts = new(StringComparer.Ordinal); // identifiant -> hôtes en cours (deux un instant, quand un superviseur en remplace un autre)
    private ITimer? _timer;
    private bool _active;
    private DateTimeOffset _lastTouch;
    private bool _disposed;

    /// <summary>
    /// Un hôte vient de partir. Réveille le minuteur s'il dormait. Ne mesure rien et n'appelle personne : on peut l'appeler sous le
    /// verrou du superviseur.
    /// </summary>
    public void HostStarted(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var count = _hosts.GetValueOrDefault(id);
            _hosts[id] = count + 1;
            if (count > 0)
            {
                return;
            }

            meter.Begin(id);
            if (_timer is null)
            {
                _timer = time.CreateTimer(_ => OnTick(), null, CurrentInterval, CurrentInterval);
            }
        }
    }

    /// <summary>Un hôte est arrêté. Le dernier arrêté libère le minuteur.</summary>
    public void HostStopped(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (!_hosts.TryGetValue(id, out var count))
            {
                return;
            }

            if (count > 1)
            {
                _hosts[id] = count - 1;
                return;
            }

            _hosts.Remove(id);
            meter.End(id);
            if (_hosts.Count == 0)
            {
                StopTimerLocked();
            }
        }
    }

    /// <summary>
    /// La fenêtre de réglage lit la consommation. Si elle ne lisait pas, un relevé est fait tout de suite (elle doit voir des chiffres
    /// frais) et le rythme passe à 5 s. Sans hôte en cours, ne fait rien.
    /// </summary>
    public void Touch()
    {
        bool immediate;
        lock (_gate)
        {
            if (_disposed || _hosts.Count == 0)
            {
                return;
            }

            _lastTouch = time.GetUtcNow();
            immediate = !_active;
            if (immediate)
            {
                _active = true;
                _timer?.Change(ActiveInterval, ActiveInterval);
            }
        }

        if (immediate)
        {
            SampleAll();
        }
    }

    /// <summary>Libère le minuteur ; plus aucun relevé ensuite.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _hosts.Clear();
            StopTimerLocked();
        }
    }

    private TimeSpan CurrentInterval => _active ? ActiveInterval : IdleInterval;

    private void StopTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _active = false;
    }

    private void OnTick()
    {
        lock (_gate)
        {
            if (_disposed || _hosts.Count == 0)
            {
                return;
            }

            if (_active && time.GetUtcNow() - _lastTouch > ReadingTimeout)
            {
                // La fenêtre ne demande plus rien : retour à un relevé par minute (après celui-ci).
                _active = false;
                _timer?.Change(IdleInterval, IdleInterval);
            }
        }

        SampleAll();
    }

    private void SampleAll()
    {
        string[] ids;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ids = [.. _hosts.Keys];
        }

        foreach (var id in ids)
        {
            try
            {
                if (read(id) is { } reading)
                {
                    lock (_gate)
                    {
                        // L'hôte a pu s'arrêter pendant la mesure : on ne ressuscite pas sa ligne.
                        if (_hosts.ContainsKey(id))
                        {
                            meter.Sample(id, reading.Usage, reading.PrivateBytes, reading.DisplayName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning($"Consommation de « {id} » : relevé impossible : {ex.Message}");
            }
        }
    }
}
