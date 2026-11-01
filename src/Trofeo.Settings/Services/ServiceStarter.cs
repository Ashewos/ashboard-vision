using System.Diagnostics;
using Trofeo.Core;

namespace Trofeo.Settings.Services;

/// <summary>Lance le service d'affichage (Trofeo.Display.exe) quand il ne tourne pas.</summary>
public sealed class ServiceStarter(Func<string?> locate, Func<string, bool> launch)
{
    public const string ExecutableName = "Trofeo.Display.exe";

    /// <summary>Cherche l'exécutable à côté des réglages (ou dans les sorties du dépôt) et le lance par le shell.</summary>
    public static ServiceStarter Default { get; } = new(() => AppLocator.Find(ExecutableName), Launch);

    /// <summary>Vrai si le service a été lancé ; faux s'il est introuvable ou refuse de démarrer. Ne lève jamais.</summary>
    public bool TryStart()
    {
        try
        {
            return locate() is { } path && launch(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                                      or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Vrai si le service tourne déjà ou vient d'être lancé ; faux s'il est arrêté et ne démarre pas. <paramref name="isRunning"/> :
    /// <see cref="IsServiceRunning(string)"/> en production.
    /// </summary>
    public bool TryStartIfStopped(Func<bool> isRunning) => isRunning() || TryStart();

    /// <summary>Vrai tant qu'un service tient son mutex (<see cref="ProductIdentity.ServiceMutexName"/>), dans cette session Windows.</summary>
    public static bool IsServiceRunning(string mutexName = ProductIdentity.ServiceMutexName)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    private static bool Launch(string path)
    {
        // Le shell lance le service comme si Alex avait double-cliqué : il vit sa vie, sans dépendre de cette fenêtre.
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        });
        return process is not null;
    }
}
