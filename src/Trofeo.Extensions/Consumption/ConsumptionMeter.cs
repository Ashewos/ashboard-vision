using Trofeo.Control;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Consumption;

/// <summary>
/// Ce que consomment les modules complémentaires : processeur et mémoire de chaque hôte, en moyenne sur la dernière minute, et le
/// repère « consommation élevée » (plus de 2 % du processeur de la machine, ou plus de 150 Mio de mémoire privée, en moyenne sur
/// 60 s). Ce n'est qu'un calcul sur des relevés qu'on lui remet (<see cref="Sample"/>) : le rythme des relevés est celui de
/// <see cref="ConsumptionSampler"/>, qui n'en fait aucun sans hôte en cours.
/// <para>
/// <b>Pourquoi un relevé par minute suffit.</b> Le temps processeur du job object est cumulé depuis le lancement de l'hôte : la
/// différence entre deux relevés donne la moyenne exacte sur l'intervalle, pic compris. Seule la mémoire est un instantané, et sa
/// moyenne est celle des relevés de la minute.
/// </para>
/// <para>
/// Un module n'est « élevé » qu'après une minute entière d'observation (à cinq secondes près) : un hôte qui vient de partir et
/// s'emballe dix secondes n'est pas signalé. Un compteur qui recule veut dire que l'hôte a été relancé : l'historique repart de zéro.
/// </para>
/// </summary>
/// <param name="time">La source de temps (les relevés sont datés avec elle).</param>
/// <param name="processorCount">Le nombre de processeurs logiques de la machine ; <see cref="Environment.ProcessorCount"/> par défaut.</param>
public sealed class ConsumptionMeter(TimeProvider time, int? processorCount = null)
{
    /// <summary>L'identifiant de la ligne des modules d'origine : sans point, il ne peut être celui d'aucun module de la communauté.</summary>
    public const string OriginalModulesId = "origine";

    /// <summary>Le nom affiché de la ligne des modules d'origine.</summary>
    public const string OriginalModulesName = "Modules d'origine (dans le service)";

    /// <summary>La durée sur laquelle on moyenne.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>La part du processeur de la machine au-delà de laquelle un module est signalé, en pour cent.</summary>
    public const double HighCpuPercent = 2.0;

    /// <summary>La mémoire privée au-delà de laquelle un module est signalé, en octets (150 Mio).</summary>
    public const long HighMemoryBytes = 150L * 1024 * 1024;

    // Les relevés ne tombent jamais pile à 60 s l'un de l'autre : cinq secondes de tolérance sur la fenêtre.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    // Plus qu'il n'en faut pour la fenêtre à un relevé toutes les 5 s (une douzaine) : une borne contre une horloge qui n'avance pas.
    private const int MaxPoints = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly int _processors = Math.Max(1, processorCount ?? Environment.ProcessorCount);

    /// <summary>Un relevé : l'instant, le temps processeur cumulé du job, et la mémoire privée (<c>null</c> pour le point de départ).</summary>
    private readonly record struct Point(DateTimeOffset At, TimeSpan Cpu, long? Memory);

    private sealed class Series
    {
        public string? Name { get; set; }

        public List<Point> Points { get; } = [];
    }

    /// <summary>
    /// Un hôte vient de partir : son temps processeur est à zéro, c'est le point de départ de la première minute. Remet à neuf
    /// l'historique de ce module s'il en avait un.
    /// </summary>
    public void Begin(string id, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            var series = new Series { Name = displayName };
            series.Points.Add(new Point(time.GetUtcNow(), TimeSpan.Zero, null));
            _series[id] = series;
        }
    }

    /// <summary>Remet un relevé de l'hôte de <paramref name="id"/>.</summary>
    /// <param name="usage">Ce que le job a consommé depuis le lancement de l'hôte (<see cref="JobUsage.CpuTime"/> est cumulé).</param>
    /// <param name="privateBytes">La mémoire privée de l'hôte, en octets.</param>
    /// <param name="displayName">Le nom à afficher (l'identifiant sinon).</param>
    public void Sample(string id, JobUsage usage, long privateBytes, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (!_series.TryGetValue(id, out var series))
            {
                series = new Series();
                _series[id] = series;
            }

            if (displayName is not null)
            {
                series.Name = displayName;
            }

            var now = time.GetUtcNow();
            var points = series.Points;
            if (points.Count > 0 && usage.CpuTime < points[^1].Cpu)
            {
                points.Clear(); // le compteur a reculé : c'est un autre hôte
            }

            points.Add(new Point(now, usage.CpuTime, Math.Max(0, privateBytes)));
            Prune(points, now);
        }
    }

    /// <summary>L'hôte de <paramref name="id"/> est arrêté : il n'a plus de ligne.</summary>
    public void End(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            _series.Remove(id);
        }
    }

    /// <summary>
    /// Les lignes à montrer : d'abord les modules d'origine (le processus du service, pour comparer), puis chaque module de la
    /// communauté dont l'hôte tourne, par nom.
    /// </summary>
    /// <param name="service">Ce que le service consomme en tout (processeur de la machine, mémoire privée).</param>
    public IReadOnlyList<ModuleConsumption> Read(ProcessInfo service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var rows = new List<ModuleConsumption>
        {
            new(OriginalModulesId, OriginalModulesName, false, service.CpuPercent, service.PrivateBytes, false),
        };

        lock (_gate)
        {
            foreach (var (id, series) in _series.OrderBy(p => p.Value.Name ?? p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                if (series.Points.Count > 0)
                {
                    rows.Add(Summarize(id, series));
                }
            }
        }

        return rows;
    }

    /// <summary>Le nombre de relevés gardés pour ce module (pour les tests : l'historique reste borné).</summary>
    internal int PointCount(string id)
    {
        lock (_gate)
        {
            return _series.TryGetValue(id, out var series) ? series.Points.Count : 0;
        }
    }

    /// <summary>Garde le relevé qui sert de départ à la fenêtre (le plus récent d'au moins 55 s) et ce qui le suit.</summary>
    private static void Prune(List<Point> points, DateTimeOffset now)
    {
        var start = BaselineIndex(points, now);
        if (start > 0)
        {
            points.RemoveRange(0, start);
        }

        if (points.Count > MaxPoints)
        {
            points.RemoveRange(0, points.Count - MaxPoints);
        }
    }

    private static int BaselineIndex(List<Point> points, DateTimeOffset newest)
    {
        var limit = newest - (Window - Slack);
        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].At <= limit)
            {
                return i;
            }
        }

        return 0;
    }

    private ModuleConsumption Summarize(string id, Series series)
    {
        var points = series.Points;
        var newest = points[^1];
        var start = BaselineIndex(points, newest.At);
        var baseline = points[start];
        var span = newest.At - baseline.At;

        var cpuPercent = 0.0;
        if (span > TimeSpan.Zero)
        {
            cpuPercent = Math.Clamp((newest.Cpu - baseline.Cpu).TotalSeconds / (span.TotalSeconds * _processors) * 100, 0, 100);
        }

        long memorySum = 0;
        var memoryCount = 0;
        for (var i = start; i < points.Count; i++)
        {
            if (points[i].Memory is { } bytes)
            {
                memorySum += bytes;
                memoryCount++;
            }
        }

        var memory = memoryCount > 0 ? memorySum / memoryCount : 0;
        var observedLongEnough = span >= Window - Slack;
        var high = observedLongEnough && (cpuPercent > HighCpuPercent || memory > HighMemoryBytes);
        return new ModuleConsumption(id, series.Name ?? id, true, cpuPercent, memory, high);
    }
}
