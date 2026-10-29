using System.Diagnostics;
using Microsoft.Win32;
using Trofeo.Control;
using Trofeo.Core;
using Trofeo.Core.Layout;
using Trofeo.Core.Logging;
using Trofeo.Core.Rendering;
using Trofeo.Core.Secrets;
using Trofeo.Core.Shortcuts;
using Trofeo.Device;
using Trofeo.Display.Video;
using Trofeo.Modules.Agents;
using Trofeo.Modules.Clock;
using Trofeo.Modules.Monitoring;
using Trofeo.Modules.Network;
using Trofeo.Modules.NowPlaying;
using Trofeo.Sensors;

namespace Trofeo.Display;

static class Program
{
    private const string HelperExeName = "Trofeo.SensorHelper.exe";
    private const string MutexName = @"Local\trofeo-HB-dashboard.display";
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMilliseconds(100);

    [STAThread]
    static int Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew) return 0; // une seule instance

        try
        {
            InitializeLog();
            Trace.TraceInformation("Démarrage du service d'affichage.");
            return Run();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Arrêt sur erreur : {ex}");
            return 1;
        }
        finally
        {
            Trace.TraceInformation("Arrêt du service d'affichage.");
            Trace.Flush();
            mutex.ReleaseMutex();
        }
    }

    private static int Run()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Trace.TraceError($"Exception de l'interface : {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace.TraceError($"Exception non gérée : {e.ExceptionObject}");

        var store = new LayoutStore(Path.Combine(AppPaths.SettingsDir, "layout.json"), Warn);
        var layout = LoadLayout(store);
        var vault = new DpapiSecretVault(Path.Combine(AppPaths.SettingsDir, "secrets.dat"));
        // Tout ce qui suit est déclaré avant la boucle : libéré après elle, dans l'ordre inverse de la création.
        // Pas de redirection suivie : la session Freebox ne doit jamais partir vers un autre hôte.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductIdentity.Id}/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"}");

        // Capteurs : l'assistant élevé s'il tourne, sinon une lecture locale. Aucune panne ici n'arrête le service.
        using var sensorPipe = new SensorPipeClient(SensorPipe.Name, HelperIdentity.IsTrusted, TimeProvider.System);
        StartSensorPipe(sensorPipe);
        using var sensorHub = new SensorHub(() => new LhmSensorReader(), sensorPipe, TimeProvider.System);

        var registry = new ModuleRegistry(
            [
                new ClockFactory(new OpenMeteoClient(http)),
                new MonitoringFactory(sensorHub),
                new NowPlayingFactory(ctx => new SmtcMediaSource(ctx.Log)),
                NetworkFactory.CreateProduction(http),
                ClaudeCodeFactory.CreateProduction(AppLocator.FindDirectory("mods")),
                CodexFactory.CreateProduction(),
            ],
            vault.Scope);
        var screen = new ScreenDevice(new WinUsbTransportFactory(), TimeProvider.System);
        screen.StateChanged += (_, _) =>
        {
            var status = screen.Status;
            Trace.TraceInformation($"Écran : {status.State} ({status.Reason ?? "ok"}).");
        };

        using var scheduler = new FrameScheduler(TimeProvider.System); // déclaré avant la boucle : libéré après elle
        // Apparence et fond : ce que la disposition demande, au démarrage comme à chaque rechargement. Les avertissements
        // vont au journal et au rechargement en cours.
        var buildWarnings = new WarningSink();
        Action<string> warnAndCollect = message =>
        {
            Warn(message);
            buildWarnings.Warn(message);
        };
        LoopConfig BuildConfig(DashboardLayout next, LoopConfig? current) => LoopConfigFactory.Build(
            next, new SkiaSystemFonts(), path => MediaFoundationBackground.TryOpen(path, warnAndCollect), warnAndCollect, current);

        var config = BuildConfig(layout, null);
        var theme = config.Theme;
        Trace.TraceInformation(
            $"Apparence : thème « {theme.Theme.Id} », fond {(layout.Appearance?.Background?.Kind ?? BackgroundKind.Theme)}"
            + $"{(theme.LegibilityGuard ? "" : ", garde de lisibilité coupée")}.");

        // La boucle devient propriétaire du fond et le libère après elle.
        using var loop = new DisplayLoop(layout, registry, screen, scheduler, TimeProvider.System, theme, config.Background);
        using var cts = new CancellationTokenSource();
        loop.Start(cts.Token);

        // Après une veille, l'écran a pu être réinitialisé sans que le lien le dise : on repart d'une connexion neuve.
        PowerModeChangedEventHandler onPowerChanged = (_, e) =>
        {
            if (e.Mode != PowerModes.Resume) return;
            Trace.TraceInformation("Reprise après veille : reconnexion de l'écran.");
            loop.RequestReconnect();
        };
        SystemEvents.PowerModeChanged += onPowerChanged;

        var thread = new Thread(() => TickLoop(loop, scheduler, cts.Token)) { IsBackground = true, Name = "Trofeo.Display.Tick" };
        thread.Start();

        try
        {
            // Sur le thread de l'interface, avant la pompe de messages. Déclarés après la boucle et dans ce bloc :
            // le serveur de contrôle, le routeur (qui retire les raccourcis) puis la fenêtre sont libérés avant elle,
            // plus rien ne peut atteindre une boucle libérée.
            using var tray = new TrayIcon(screen); // d'abord : installe le contexte de synchronisation de l'interface
            using var hotkeys = StartHotkeys(layout, loop, SynchronizationContext.Current);

            var meter = new ProcessMeter(SampleProcess, TimeProvider.System, Environment.ProcessorCount);
            var handler = new ServiceControlHandler(
                store, loop, registry, () => ServiceControlHandler.ToScreenInfo(screen.Status), () => ReadSensors(sensorPipe),
                meter, BuildConfig, hotkeys?.Host, buildWarnings, config);
            using var control = StartControl(handler);

            Application.Run(tray);
        }
        finally
        {
            SystemEvents.PowerModeChanged -= onPowerChanged; // sinon le gestionnaire fuit
            cts.Cancel();
            if (!thread.Join(TimeSpan.FromSeconds(15)))
            {
                Trace.TraceError("La boucle d'affichage ne s'est pas arrêtée à temps.");
                Trace.Flush();
                Environment.Exit(1); // le thread est de fond : on évite de libérer ce qu'il utilise encore
            }

            screen.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// Crée la fenêtre des raccourcis (toujours : un rechargement peut ajouter un module commandable) et enregistre ceux de
    /// la disposition si un module accepte les commandes. Une panne ici n'arrête pas le service : il tourne sans
    /// raccourcis (<c>null</c>). Libérer le résultat retire les raccourcis puis détruit la fenêtre.
    /// </summary>
    private static HotkeyScope? StartHotkeys(DashboardLayout layout, DisplayLoop loop, SynchronizationContext? ui)
    {
        HotkeyWindow? window = null;
        HotkeyRouter? router = null;
        try
        {
            // Sans module commandable, un raccourci ne servirait à rien et prendrait une combinaison à d'autres applications.
            var commandable = loop.FindCommandTarget(null) is not null;
            if (!commandable) Trace.TraceInformation("Aucun module de la disposition n'accepte les commandes : pas de raccourcis clavier pour l'instant.");

            window = new HotkeyWindow();
            router = new HotkeyRouter(window, commandable ? layout.Shortcuts ?? ShortcutDefaults.All : [], loop.FindCommandTarget);
            window.Hotkey += router.OnHotkey;
            router.RegisterAll();

            IHotkeyHost? host = null;
            if (ui is null) Trace.TraceWarning("Pas de contexte d'interface : les raccourcis ne suivront pas les rechargements.");
            else host = new UiHotkeyHost(ui, router, loop);
            return new HotkeyScope(router, window, host);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Raccourcis clavier indisponibles, le service tourne sans : {ex.Message}");
            router?.Dispose();
            window?.Dispose();
            return null;
        }
    }

    /// <summary>Libère le routeur (qui retire les raccourcis) avant la fenêtre qui les portait.</summary>
    private sealed class HotkeyScope(HotkeyRouter router, HotkeyWindow window, IHotkeyHost? host) : IDisposable
    {
        public IHotkeyHost? Host { get; } = host;

        public void Dispose()
        {
            router.Dispose();
            window.Dispose();
        }
    }

    /// <summary>
    /// Ouvre le canal de contrôle de la fenêtre de réglage. Une panne ici n'arrête pas le service : il tourne sans
    /// (<c>null</c>), les réglages se feront en éditant le fichier.
    /// </summary>
    private static ControlServer? StartControl(ServiceControlHandler handler)
    {
        ControlServer? server = null;
        try
        {
            server = new ControlServer(ControlPipe.Name, handler);
            server.Start();
            return server;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Canal de contrôle indisponible, le service tourne sans : {ex.Message}");
            server?.Dispose();
            return null;
        }
    }

    /// <summary>Temps CPU, mémoire privée et mémoire de travail du processus.</summary>
    private static (TimeSpan Cpu, long PrivateBytes, long WorkingSet) SampleProcess()
    {
        using var process = Process.GetCurrentProcess();
        return (process.TotalProcessorTime, process.PrivateMemorySize64, process.WorkingSet64);
    }

    /// <summary>PawnIO, assistant de capteurs installé (son exécutable est là) et tube relié ; un échec de lecture vaut « non ».</summary>
    private static SensorInfo ReadSensors(SensorPipeClient pipe)
    {
        var pawnIo = false;
        var helper = false;
        try
        {
            pawnIo = new PawnIoDetector().IsInstalled();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Détection de PawnIO impossible : {ex.Message}");
        }

        try
        {
            helper = File.Exists(Path.Combine(HelperIdentity.InstallDir, HelperExeName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Détection de l'assistant des capteurs impossible : {ex.Message}");
        }

        return new SensorInfo(pawnIo, helper, pipe.IsConnected);
    }

    /// <summary>Lance la boucle de connexion au tube en tâche de fond ; <see cref="SensorPipeClient.Dispose"/> l'arrête.</summary>
    private static void StartSensorPipe(SensorPipeClient client) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await client.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Liaison avec l'assistant des capteurs interrompue : {ex}");
            }
        });

    private static void TickLoop(DisplayLoop loop, FrameScheduler scheduler, CancellationToken ct)
    {
        WaitHandle[] handles = [ct.WaitHandle, scheduler.Signal];
        var errors = new RateLimitedErrorLog(TimeProvider.System); // une panne qui se répète à chaque tour ne remplit pas le journal
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = loop.Tick();
            }
            catch (Exception ex)
            {
                errors.Report($"Erreur dans la boucle d'affichage : {ex}");
                ct.WaitHandle.WaitOne(ErrorRetryDelay); // pas de boucle serrée si l'erreur se répète
                continue;
            }

            WaitHandle.WaitAny(handles, delay); // dort jusqu'à l'échéance, un signal ou l'arrêt
        }
    }

    // Trace.TraceWarning est conditionnelle (TRACE) : elle ne peut pas servir de délégué, d'où ce relais.
    private static void Warn(string message) => Trace.TraceWarning(message);

    private static DashboardLayout LoadLayout(LayoutStore store)
    {
        var path = Path.Combine(AppPaths.SettingsDir, "layout.json");
        if (!File.Exists(path))
        {
            try
            {
                store.Save(DashboardLayout.Default);
                Trace.TraceInformation($"Disposition par défaut enregistrée : {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Impossible d'enregistrer la disposition par défaut : {ex.Message}");
            }
        }

        var layout = store.Load();
        Trace.TraceInformation($"Disposition chargée : {layout.Placements.Count} module(s), rotation {layout.Rotation}°.");
        return layout;
    }

    /// <summary>Le journal est un confort : s'il ne peut pas s'ouvrir, le service tourne sans.</summary>
    private static void InitializeLog()
    {
        try
        {
            OpenLog();
        }
        catch (Exception)
        {
            // Dossier inaccessible, disque plein, droits : pas de journal, mais on démarre.
        }
    }

    private static void OpenLog()
    {
        var dir = Path.Combine(AppPaths.SettingsDir, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "display.log");
        Trace.Listeners.Add(new FileTraceListener(path));
        Trace.AutoFlush = true;
    }
}
