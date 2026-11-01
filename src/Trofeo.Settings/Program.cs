using System.Diagnostics;
using Avalonia;
using Trofeo.Core;
using Trofeo.Settings.Services;

namespace Trofeo.Settings;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Une seule fenêtre : un second lancement ramène la première au premier plan, puis quitte.
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
            return 0;

        // Avant tout accès au dossier des réglages (le journal y crée « logs ») : si le service n'a pas encore déménagé les réglages
        // d'avant le changement de nom, on le lui demande et on l'attend, au lieu de leur faire concurrence.
        var moved = LegacyMoveWait.Run(AppPaths.LegacySettingsDir, AppPaths.SettingsDir, ServiceStarter.Default.TryStart, Thread.Sleep);
        if (LegacyMoveWait.MessageFor(moved) is { } message)
        {
            LegacyMoveWait.Show(message);
            return 0;
        }

        // Ouvrir la fenêtre, c'est lancer Ashboard Vision (menu Démarrer, bureau, fin de l'installation) : le service démarre s'il ne
        // tourne pas. S'il ne démarre pas, la fenêtre le dit et garde son bouton « Démarrer le service ».
        ServiceStarter.Default.TryStartIfStopped(() => ServiceStarter.IsServiceRunning());

        // Le journal et le filet d'abord : un plantage de la fenêtre laisse une trace dans logs\settings.log.
        var log = SettingsLog.TryOpen(SettingsLog.DefaultPath);
        using var guard = new CrashGuard();
        App.Guard = guard;
        guard.InstallProcessHandlers();
        Trace.TraceInformation("Démarrage de la fenêtre de réglage.");

        try
        {
            App.Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Trace.TraceInformation("Arrêt de la fenêtre de réglage.");
            Trace.Flush();
            if (log is not null)
                Trace.Listeners.Remove(log);
        }
    }

    // Rendu logiciel : avec la carte graphique (Direct3D par ANGLE), le pilote réserve environ 150 Mo dans le processus
    // (189 à 215 Mo de mémoire propre contre 48 à 52 Mo, mesurés sur le PC d'Alex), pour 0,05 point de processeur de plus.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
}
