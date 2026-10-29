using System.Diagnostics;
using System.Runtime.InteropServices;
using Trofeo.Core.Logging;
using Trofeo.Sensors;

namespace Trofeo.SensorHelper;

/// <summary>
/// Assistant de capteurs. Sans argument, il sert les relevés sur le tube (lancé en SYSTEM par la tâche planifiée) ;
/// <c>install</c> et <c>uninstall</c> gèrent la tâche depuis un terminal administrateur.
/// Codes de sortie : 0 succès, 1 usage ou installeur de PawnIO introuvable, 2 pas élevé, 3 PawnIO manquant,
/// 4 échec de PawnIO, 5 erreur (programme impossible à lancer, fichier tenu, désinstallation depuis la copie installée…).
/// </summary>
internal static partial class Program
{
    private const string MutexName = @"Global\trofeo-HB-dashboard.sensors";
    private const int AttachParentProcess = -1;

    private const string Usage = """
        Usage :
          Trofeo.SensorHelper.exe install [--pawnio <PawnIO_setup.exe>]   installe l'assistant et sa tâche planifiée
          Trofeo.SensorHelper.exe uninstall                               supprime la tâche et les fichiers de l'assistant
          Trofeo.SensorHelper.exe                                         sert les capteurs (lancé par la tâche planifiée)

        Les commandes install et uninstall exigent un terminal administrateur.
        """;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    private static int Main(string[] args)
    {
        if (args.Length == 0) return Serve();

        string? pawnIoSetup = null;
        switch (args[0])
        {
            case "install":
                if (args.Length == 3 && args[1] == "--pawnio") pawnIoSetup = args[2];
                else if (args.Length != 1) return PrintUsage();
                break;
            case "uninstall":
                if (args.Length != 1) return PrintUsage();
                break;
            default:
                return PrintUsage();
        }

        // Exécutable graphique : sans cet attachement, rien de ce qui suit n'apparaît dans le terminal de l'administrateur.
        AttachConsole(AttachParentProcess);

        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Cette commande doit être lancée depuis un terminal administrateur.");
            return 2;
        }

        if (pawnIoSetup is not null)
        {
            pawnIoSetup = Path.GetFullPath(pawnIoSetup);
            if (!File.Exists(pawnIoSetup))
            {
                Console.Error.WriteLine($"L'installeur de PawnIO est introuvable : {pawnIoSetup}");
                return 1;
            }
        }

        var installer = new HelperInstaller(new ProcessCommandRunner(), new PawnIoDetector(), AppContext.BaseDirectory, HelperIdentity.InstallDir,
            legacyInstallDir: HelperIdentity.LegacyInstallDir);
        try
        {
            return args[0] == "install" ? Install(installer, pawnIoSetup) : Uninstall(installer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Échec : {ex.Message}");
            return 5;
        }
    }

    private static int PrintUsage()
    {
        AttachConsole(AttachParentProcess);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static int Install(HelperInstaller installer, string? pawnIoSetup)
    {
        switch (installer.Install(pawnIoSetup))
        {
            case InstallResult.Installed:
                Console.WriteLine("Assistant de capteurs installé et démarré.");
                return 0;
            case InstallResult.InstalledRebootRequired:
                Console.WriteLine("Assistant de capteurs installé et démarré. PawnIO demande un redémarrage de Windows pour être pleinement opérationnel.");
                return 0;
            case InstallResult.PawnIoMissing:
                Console.Error.WriteLine("Le pilote PawnIO n'est pas installé. Relance avec : install --pawnio <chemin de PawnIO_setup.exe>");
                return 3;
            default:
                Console.Error.WriteLine("L'installation de PawnIO a échoué : rien d'autre n'a été modifié.");
                return 4;
        }
    }

    private static int Uninstall(HelperInstaller installer)
    {
        installer.Uninstall();
        Console.WriteLine("Assistant de capteurs désinstallé. PawnIO est conservé : d'autres logiciels peuvent l'utiliser.");
        return 0;
    }

    /// <summary>Mode normal : publie les capteurs sur le tube jusqu'à l'arrêt du processus (la tâche planifiée le termine).</summary>
    private static int Serve()
    {
        if (!Environment.IsPrivilegedProcess) return 2;

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // l'instance précédente a été tuée : on reprend la main
        }

        if (!owned) return 0; // un autre assistant tourne déjà

        try
        {
            InitializeLog();
            Trace.TraceInformation("Démarrage de l'assistant de capteurs.");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            // Le gestionnaire est retiré avant la libération du jeton (en sortie du bloc) : un arrêt du processus
            // tardif n'appelle jamais Cancel sur un jeton libéré.
            EventHandler onExit = (_, _) =>
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Déjà libéré : l'arrêt est en cours.
                }
            };
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                using var reader = new LhmSensorReader();
                new SensorPipeServer(SensorPipe.Name, reader, TimeProvider.System).RunAsync(cts.Token).GetAwaiter().GetResult();
                return 0;
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= onExit;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Arrêt sur erreur : {ex}");
            return 5;
        }
        finally
        {
            Trace.TraceInformation("Arrêt de l'assistant de capteurs.");
            Trace.Flush();
            mutex.ReleaseMutex();
        }
    }

    private static void InitializeLog()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            Trace.Listeners.Add(new FileTraceListener(Path.Combine(dir, "sensors.log")));
            Trace.AutoFlush = true;
        }
        catch (Exception)
        {
            // Dossier inaccessible, disque plein : pas de journal, mais on sert quand même les capteurs.
        }
    }
}
