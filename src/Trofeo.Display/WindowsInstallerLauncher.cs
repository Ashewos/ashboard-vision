using System.ComponentModel;
using System.Diagnostics;
using Trofeo.Distribution;

namespace Trofeo.Display;

/// <summary>
/// Lance l'installateur vérifié en silence, avec l'autorisation d'administrateur que Windows demande (<c>runas</c>). L'installateur
/// ferme le service, le remplace puis le relance.
/// </summary>
public sealed class WindowsInstallerLauncher : IInstallerLauncher
{
    /// <summary>Installation sans fenêtre ni redémarrage ; les applications ouvertes sur nos fichiers sont fermées.</summary>
    internal const string Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    // « L'opération a été annulée par l'utilisateur » : tu as refusé l'autorisation de Windows.
    private const int ErrorCancelled = 1223;

    private readonly Action<ProcessStartInfo> _start;

    public WindowsInstallerLauncher() : this(info => Process.Start(info)?.Dispose())
    {
    }

    internal WindowsInstallerLauncher(Action<ProcessStartInfo> start) => _start = start;

    public InstallerLaunch Launch(string setupPath)
    {
        try
        {
            _start(new ProcessStartInfo(setupPath, Arguments) { UseShellExecute = true, Verb = "runas" });
            return InstallerLaunch.Started;
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
}
