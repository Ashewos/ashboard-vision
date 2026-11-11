using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Modules.Agents;
using Trofeo.Modules.Clock;
using Trofeo.Modules.Monitoring;
using Trofeo.Modules.Network;
using Trofeo.Modules.NowPlaying;
using Trofeo.Sensors;

namespace Trofeo.ThemePreview;

/// <summary>
/// Les cinq vrais modules, démarrés sur de fausses sources qui donnent les données de la maquette (<c>themes-v3.html</c>),
/// ou des textes très longs pour le contrôle de débordement. L'horloge est simulée : samedi 24 octobre 2026, 14:32.
/// </summary>
internal sealed class PreviewScene : IDisposable
{
    /// <summary>L'instant affiché à l'écran.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 32, 0, TimeSpan.Zero);

    // Le temps simulé avance de cette durée, une seconde à la fois : assez pour que la courbe du réseau ait un historique.
    private const int WarmUpSeconds = 30;

    private readonly FakeTimeProvider _time = new(Now - TimeSpan.FromSeconds(WarmUpSeconds));
    private readonly FakeFeed _feed;
    private readonly ScriptedThroughput _network;
    private readonly List<IModule> _modules = [];

    /// <summary>
    /// Emplacement de Codex, qui n'est pas dans la disposition par défaut : sert aux captures des modules (<c>--modules</c>)
    /// et au README. Il occupe la place de Claude Code (colonnes 0 à 7).
    /// </summary>
    public static readonly ModulePlacement CodexPlacement = new(
        new Guid("7e0b1c52-3d4a-4f6e-8a19-5c2d9b3e6f40"),
        "codex", 0, 0, 8, 4, TextSize.Normal, JsonDocument.Parse("""{"view":"feed","autoFollow":true}""").RootElement.Clone());

    /// <summary>Les modules par identifiant d'emplacement de <see cref="DashboardLayout.Default"/> (et de <see cref="CodexPlacement"/>).</summary>
    public Dictionary<Guid, IModule> ByPlacement { get; } = [];

    /// <summary>Le nom de chaque module par identifiant d'emplacement (pour les messages).</summary>
    public Dictionary<Guid, string> Names { get; } = [];

    /// <summary>Les modules qui ne sont pas en ligne après l'attente de démarrage (vide si tout va bien).</summary>
    public List<string> NotOnline { get; } = [];

    private PreviewScene(bool longTexts)
    {
        _feed = new FakeFeed(Sensors());
        _network = new ScriptedThroughput(longTexts ? "Freebox Ultra, salon, étage du fond, accès invité" : "Freebox");
        LongTexts = longTexts;
    }

    public bool LongTexts { get; }

    public static async Task<PreviewScene> CreateAsync(bool longTexts)
    {
        var scene = new PreviewScene(longTexts);
        try
        {
            await scene.StartAsync().ConfigureAwait(false);
            return scene;
        }
        catch
        {
            scene.Dispose();
            throw;
        }
    }

    private async Task StartAsync()
    {
        var log = new ConsoleLog();
        var secrets = new NoSecrets();
        var factories = new Dictionary<string, (IModuleFactory Factory, string Settings)>
        {
            ["claudecode"] = (new ClaudeCodeFactory(_ => new SampleAgentSource(LongTexts)), """{"view":"feed","autoFollow":true}"""),
            ["codex"] = (new CodexFactory(_ => new SampleAgentSource(LongTexts)), """{"view":"feed","autoFollow":true}"""),
            ["monitoring"] = (new MonitoringFactory(_feed), LongTexts
                ? """{"title":"Station de travail du bureau, Core Ultra 7 265K, RTX 5070 et 32 Go"}"""
                : "{}"),
            ["nowplaying"] = (new NowPlayingFactory(_ => new SampleMedia(LongTexts)), "{}"),
            ["clock"] = (new ClockFactory(new SampleWeather(LongTexts)),
                """{"location":{"name":"Lyon","latitude":45.76,"longitude":4.84}}"""),
            ["network"] = (new NetworkFactory((_, _) => _network), "{}"),
        };

        foreach (var placement in DashboardLayout.Default.Views[0].Placements.Append(CodexPlacement))
        {
            var (factory, settings) = factories[placement.TypeId];
            using var json = JsonDocument.Parse(settings);
            var module = factory.Create(new ModuleContext(placement.InstanceId, json.RootElement.Clone(), _time, log, secrets));
            _modules.Add(module);
            ByPlacement[placement.InstanceId] = module;
            Names[placement.InstanceId] = placement.TypeId;
        }

        foreach (var module in _modules)
        {
            await module.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Le temps simulé avance : le réseau lit son débit chaque seconde, l'agent relit son détail, la source de
        // capteurs publie (sinon Monitoring passerait hors ligne au bout de 5 s).
        for (var i = 0; i < WarmUpSeconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _feed.Publish();
        }

        // Les modules sont en ligne dès le départ, mais on laisse au plus 3 s aux lectures asynchrones pour finir.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_modules.Any(m => m.Status.Health != ModuleHealth.Online) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        await Task.Delay(200).ConfigureAwait(false); // laisse les continuations asynchrones se poser

        // Un module hors ligne montrerait des textes courts : l'aperçu et surtout --check ne vaudraient plus rien.
        foreach (var (id, name) in Names)
        {
            if (ByPlacement[id].Status.Health != ModuleHealth.Online)
            {
                NotOnline.Add(name);
                Console.Error.WriteLine(
                    $"Attention : le module « {name} » n'est pas en ligne après 3 s d'attente ; l'aperçu ne le montre pas comme prévu.");
            }
        }
    }

    /// <summary>La disposition par défaut, avec cette taille de texte partout.</summary>
    public static DashboardLayout Layout(TextSize size) => DashboardLayout.Default.WithPlacements(
        DashboardLayout.DefaultViewId, DashboardLayout.Default.Views[0].Placements.Select(p => p with { TextSize = size }).ToList());

    public void Dispose()
    {
        foreach (var module in _modules)
        {
            module.Dispose();
        }
    }

    private static SensorSnapshot Sensors() => new(
        "Intel(R) Core(TM) Ultra 7 265K",
        "NVIDIA GeForce RTX 5070",
        new Dictionary<string, double>
        {
            [SensorKeys.CpuLoad] = 34, [SensorKeys.CpuTemp] = 52,
            [SensorKeys.GpuLoad] = 61, [SensorKeys.GpuTemp] = 64,
            [SensorKeys.RamUsed] = 18.2, [SensorKeys.RamTotal] = 32,
        },
        Elevated: true);

    // --- Fausses sources ---

    private sealed class FakeFeed(SensorSnapshot snapshot) : ISensorFeed
    {
        private EventHandler? _updated;

        public SensorSnapshot? Latest { get; } = snapshot;

        public event EventHandler? Updated
        {
            add => _updated += value;
            remove => _updated -= value;
        }

        public IDisposable Acquire() => new Ticket();

        public void Publish() => _updated?.Invoke(this, EventArgs.Empty);

        private sealed class Ticket : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Claude Code : trois onglets, la tâche, les cinq lignes du fil, le contexte, le modèle et les quotas de la maquette.</summary>
    private sealed class SampleAgentSource(bool longTexts) : IAgentSource
    {
        public AgentSnapshot Poll(string? selectedSessionId, DateTimeOffset at)
        {
            var sessions = new List<AgentSessionSummary>
            {
                new("s1", longTexts ? "thermalright-dash-un-nom-de-projet-vraiment-beaucoup-trop-long" : "thermalright-dash",
                    AgentActivity.Working, Now.AddHours(-3), Now.AddSeconds(-2)),
                new("s2", longTexts ? "api-server-de-production-de-la-region-europe-ouest" : "api-server",
                    AgentActivity.Waiting, Now.AddHours(-2), Now.AddMinutes(-4)),
                new("s3", longTexts ? "blog-personnel-et-notes-de-lecture-de-toute-la-famille" : "blog",
                    AgentActivity.Idle, Now.AddHours(-1), Now.AddMinutes(-22)),
            };

            var feed = longTexts
                ?
                [
                    new FeedEntry("Read", "src/Trofeo.Modules.Weather/Providers/OpenMeteo/ForecastResponseMapper.cs"),
                    new FeedEntry("Read", "src/Trofeo.Modules.Weather/Providers/OpenMeteo/ForecastRequestBuilder.cs"),
                    new FeedEntry("Grep", "\"fetchForecastForTheNextSevenDaysWithRadar\" dans src/ et tests/, 3 résultats"),
                    new FeedEntry("Edit", "src/Trofeo.Modules.Weather/Providers/OpenMeteo/ForecastResponseMapper.cs", 124, 33),
                    new FeedEntry("Bash", "dotnet test tests/Trofeo.Modules.Weather.Tests --filter \"FullyQualifiedName~Forecast\", en cours…"),
                ]
                : new List<FeedEntry>
                {
                    new("Read", "src/config/modules.json"),
                    new("Read", "src/modules/weather.ts"),
                    new("Grep", "fetchForecast, 3 résultats"),
                    new("Edit", "src/modules/weather.ts", 24, 3),
                    new("Bash", "npm test, en cours…"),
                };
            var tasks = new List<AgentTask>
            {
                new("Lire la configuration des modules", AgentTaskState.Completed),
                new("Écrire le client météo", AgentTaskState.InProgress),
                new("Brancher le module sur l'écran", AgentTaskState.Pending),
            };

            var task = longTexts
                ? "Ajoute le module météo avec les prévisions sur sept jours, les alertes d'orage et la carte radar animée"
                : "Ajoute le module météo";
            var detail = new AgentSessionDetail(selectedSessionId ?? "s1", task, Now.AddMinutes(-6),
                feed, tasks, "claude-opus-5-5", 42);
            var quotas = new AgentQuotas(
                new QuotaWindow(38, Now.AddHours(2).AddMinutes(14)),
                new QuotaWindow(71, new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero)),
                Now);
            return new AgentSnapshot(true, sessions, detail, quotas);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Spotify, « VALHALLA », Yo Joshy, 1:24 sur 3:12, pochette en dégradé fabriqué.</summary>
    private sealed class SampleMedia : IMediaSource
    {
        public SampleMedia(bool longTexts)
        {
            Current = new MediaSnapshot(
                longTexts ? "VALHALLA (Extended Remastered Orchestral Version, Live at the Royal Albert Hall)" : "VALHALLA",
                longTexts ? "Yo Joshy featuring The Very Long Named Philharmonic Orchestra and Choir" : "Yo Joshy",
                "Spotify",
                TimeSpan.FromSeconds(84),
                TimeSpan.FromSeconds(192),
                Now, // la position est relevée à l'instant affiché : 1:24 exactement
                IsPlaying: true,
                Cover());
        }

        public MediaSnapshot? Current { get; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }

        /// <summary>Un carré de 300 px en dégradé diagonal violet, rose et orangé, avec un disque clair (PNG).</summary>
        private static byte[] Cover()
        {
            using var bitmap = new SKBitmap(300, 300);
            using (var canvas = new SKCanvas(bitmap))
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(300, 300),
                    [SKColor.Parse("#5B2A86"), SKColor.Parse("#C2437F"), SKColor.Parse("#FF9A5A")],
                    [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
                using var fill = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, 300, 300, fill);
                using var disc = new SKPaint { Color = SKColors.White.WithAlpha(60), IsAntialias = true };
                canvas.DrawCircle(210, 90, 60, disc);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    /// <summary>12 °, « Éclaircies » (ou « Orage et grêle » à −12 °, pour les textes longs).</summary>
    private sealed class SampleWeather(bool longTexts) : IWeatherProvider
    {
        public Task<WeatherNow> GetCurrentAsync(WeatherLocation where, TemperatureUnit unit, CancellationToken ct) =>
            Task.FromResult(longTexts ? new WeatherNow(-12.4, 96, true) : new WeatherNow(12, 2, true));

        public Task<IReadOnlyList<CitySuggestion>> SearchCityAsync(string name, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CitySuggestion>>([]);
    }

    /// <summary>
    /// Freebox : un débit qui suit la courbe de la maquette sur les trente dernières secondes et finit à 412 / 38 Mb/s.
    /// </summary>
    private sealed class ScriptedThroughput(string title) : IThroughputSource
    {
        // Les 17 points de la courbe de la maquette (hauteur, plus grand = plus de débit).
        private static readonly double[] Curve = [8, 10, 7, 14, 12, 22, 18, 28, 24, 31, 26, 20, 25, 16, 21, 13, 18];

        private int _reads;

        public string Title { get; } = title;

        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public Task<SourceReading> ReadAsync(CancellationToken ct)
        {
            var index = Math.Min(_reads++, WarmUpSeconds);
            var position = index * (Curve.Length - 1) / (double)WarmUpSeconds;
            var low = (int)Math.Floor(position);
            var high = Math.Min(low + 1, Curve.Length - 1);
            var height = Curve[low] + (Curve[high] - Curve[low]) * (position - low);
            var down = height * 22.9; // la dernière valeur (18) donne 412 Mb/s
            var up = 38d * (0.65 + 0.35 * Math.Sin(index * 0.9) * Math.Sin(index * 0.9));
            if (index >= WarmUpSeconds)
            {
                (down, up) = (412, 38);
            }

            // Mb/s en octets par seconde.
            var sample = new ThroughputSample(down * 125_000d, up * 125_000d);
            return Task.FromResult(new SourceReading(sample, new ModuleStatus(ModuleHealth.Online)));
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }
    }

    private sealed class ConsoleLog : IModuleLog
    {
        public void Info(string message)
        {
        }

        public void Warning(string message) => Console.Error.WriteLine("Avertissement : " + message);

        public void Error(string message) => Console.Error.WriteLine("Erreur : " + message);
    }
}
