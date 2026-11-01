using System.ComponentModel;
using System.Diagnostics;
using Trofeo.Distribution;

namespace Trofeo.Display;

/// <summary>
/// Lance l'installateur vérifié en silence. Il est lancé sans élévation : c'est lui qui demande l'autorisation d'administrateur à
/// Windows, et il garde ainsi l'utilisateur d'origine, sous lequel il relance le service (lancé déjà élevé, par <c>runas</c>, il
/// relançait le service en administrateur). L'installateur ferme le service, le remplace puis le relance : tant que ce service tourne,
/// il attend donc la fin de l'installateur, et une fin observée veut dire que rien n'a été installé.
/// </summary>
public sealed class WindowsInstallerLauncher : IInstallerLauncher
{
    /// <summary>Installation sans fenêtre ni redémarrage ; les applications ouvertes sur nos fichiers sont fermées.</summary>
    internal const string Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    // « L'opération a été annulée par l'utilisateur » : tu as refusé l'autorisation de Windows.
    private const int ErrorCancelled = 1223;

    // Code de sortie d'Inno Setup : annulé avant le début de l'installation (dont l'autorisation de Windows refusée).
    private const int SetupCancelledBeforeInstall = 2;

    // Au-delà, on cesse d'attendre : l'installateur tourne encore, il fera son travail sans nous.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    private readonly Func<ProcessStartInfo, int?> _run;

    public WindowsInstallerLauncher() : this(RunAndWait)
    {
    }

    /// <param name="run">Lance l'installateur et rend son code de sortie, ou <c>null</c> s'il tourne encore (ou n'a pas de processus à suivre).</param>
    internal WindowsInstallerLauncher(Func<ProcessStartInfo, int?> run) => _run = run;

    public InstallerLaunch Launch(string setupPath)
    {
        try
        {
            var exitCode = _run(new ProcessStartInfo(setupPath, Arguments) { UseShellExecute = true });
            switch (exitCode)
            {
                case null or 0:
                    return InstallerLaunch.Started;
                case SetupCancelledBeforeInstall:
                    Trace.TraceInformation("Mise à jour : l'installateur s'est arrêté avant d'installer (autorisation refusée).");
                    return InstallerLaunch.Cancelled;
                default:
                    Trace.TraceWarning($"Mise à jour : l'installateur s'est arrêté sans installer (code {exitCode}).");
                    return InstallerLaunch.Failed;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return InstallerLaunch.Cancelled;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning($"Mise à jour : l'installateur n'a pas démarré ({ex.NativeErrorCode}) : {ex.Message}");
            return InstallerLaunch.Failed;
        }
    }

    private static int? RunAndWait(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        if (process is null || !process.WaitForExit(MaxWait)) return null;
        return process.ExitCode;
    }
}
