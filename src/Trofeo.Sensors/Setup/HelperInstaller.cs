using System.Diagnostics;
using System.Text;

namespace Trofeo.Sensors;

/// <summary>Résultat de <see cref="HelperInstaller.Install"/>.</summary>
public enum InstallResult
{
    /// <summary>Assistant copié, tâche créée et démarrée.</summary>
    Installed,

    /// <summary>Comme <see cref="Installed"/>, mais l'installeur de PawnIO demande un redémarrage pour finir.</summary>
    InstalledRebootRequired,

    /// <summary>PawnIO n'est pas installé et aucun installeur n'a été fourni : rien n'a été fait.</summary>
    PawnIoMissing,

    /// <summary>L'installeur de PawnIO a échoué : rien d'autre n'a été fait.</summary>
    PawnIoFailed,
}

/// <summary>
/// Installe l'assistant de capteurs (PawnIO, copie dans <paramref name="installDir"/>, tâche planifiée SYSTEM) et le
/// désinstalle. Doit tourner élevé ; toutes les commandes passent par <paramref name="runner"/>.
/// </summary>
/// <param name="sourceDir">Dossier d'où copier l'assistant (celui de l'exécutable en cours).</param>
/// <param name="installDir">Dossier d'installation (voir <see cref="HelperIdentity.InstallDir"/>).</param>
/// <param name="sleep">Attente entre deux essais quand un fichier est encore tenu par l'ancien assistant ; sert aux tests à ne pas dormir.</param>
/// <param name="legacyInstallDir">
/// Dossier d'installation d'avant le changement de nom (voir <see cref="HelperIdentity.LegacyInstallDir"/>), supprimé à
/// l'installation. <c>null</c> : aucun dossier à supprimer (l'ancienne tâche, elle, est toujours retirée).
/// </param>
public sealed class HelperInstaller(
    ICommandRunner runner, IPawnIoDetector pawnIo, string sourceDir, string installDir, Action<TimeSpan>? sleep = null,
    string? legacyInstallDir = null)
{
    private const string ExeName = "Trofeo.SensorHelper.exe";
    private const string StagedXmlName = "task.xml";
    private const int PawnIoRebootRequired = 3010;
    private static readonly string QuotedTask = "\"" + TaskDefinition.TaskName + "\"";
    private static readonly string QuotedLegacyTask = "\"" + TaskDefinition.LegacyTaskName + "\"";

    // Attentes successives (environ 10 s au total) pour laisser l'ancien assistant, arrêté par « schtasks /End », relâcher ses fichiers.
    private static readonly TimeSpan[] RetryDelays =
        [.. new[] { 250, 500, 1000, 2000, 3000, 3000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;

    /// <param name="pawnIoSetupPath">Installeur de PawnIO, utilisé seulement si PawnIO est absent.</param>
    public InstallResult Install(string? pawnIoSetupPath)
    {
        var rebootRequired = false;
        if (!pawnIo.IsInstalled())
        {
            if (string.IsNullOrWhiteSpace(pawnIoSetupPath)) return InstallResult.PawnIoMissing;
            switch (runner.Run(pawnIoSetupPath, "-install -silent"))
            {
                case 0: break;
                case PawnIoRebootRequired: rebootRequired = true; break;
                default: return InstallResult.PawnIoFailed;
            }
        }

        RemoveLegacy();

        runner.Run("schtasks", $"/End /TN {QuotedTask}"); // ancienne version éventuelle ; absente ou arrêtée : sans importance

        if (!SameDirectory(sourceDir, installDir)) CopyDirectory(sourceDir, installDir);

        CreateTask();

        Check(runner.Run("schtasks", $"/Run /TN {QuotedTask}"), "démarrage de la tâche planifiée");
        return rebootRequired ? InstallResult.InstalledRebootRequired : InstallResult.Installed;
    }

    /// <summary>
    /// Retire l'installation d'avant le changement de nom : la tâche (arrêtée d'abord, pour que l'ancien assistant relâche ses
    /// fichiers), puis son dossier. Une tâche absente n'est pas une erreur : les codes de sortie sont ignorés, rien n'est signalé.
    /// Le dossier n'est pas supprimé si c'est celui d'où l'on s'exécute ou celui où l'on installe. S'il reste tenu après les
    /// attentes, il est laissé en place avec un avertissement dans la trace : la nouvelle installation n'en dépend pas.
    /// </summary>
    private void RemoveLegacy()
    {
        runner.Run("schtasks", $"/End /TN {QuotedLegacyTask}");
        runner.Run("schtasks", $"/Delete /TN {QuotedLegacyTask} /F");
        if (legacyInstallDir is null || SameDirectory(sourceDir, legacyInstallDir) || SameDirectory(installDir, legacyInstallDir)) return;
        if (!Directory.Exists(legacyInstallDir)) return;
        try
        {
            Retry(() => Directory.Delete(legacyInstallDir, recursive: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Ancien dossier de l'assistant de capteurs non supprimé ({legacyInstallDir}) : {ex.Message}");
        }
    }

    /// <summary>
    /// Arrête et supprime la tâche, puis le dossier d'installation. PawnIO reste en place : d'autres logiciels peuvent s'en servir.
    /// Refuse, avant de rien changer, d'être lancée depuis la copie installée : son propre exécutable ne peut pas se supprimer.
    /// </summary>
    public void Uninstall()
    {
        if (SameDirectory(sourceDir, installDir))
            throw new InvalidOperationException(
                "La désinstallation ne peut pas se lancer depuis la copie installée : lance-la depuis le dossier de l'application.");

        runner.Run("schtasks", $"/End /TN {QuotedTask}");
        runner.Run("schtasks", $"/Delete /TN {QuotedTask} /F");
        if (Directory.Exists(installDir)) Retry(() => Directory.Delete(installDir, recursive: true));
    }

    /// <summary>
    /// Écrit le XML dans le dossier d'installation (réservé aux administrateurs : un processus ordinaire ne peut pas
    /// y substituer une autre commande entre l'écriture et la lecture par schtasks), puis le garde ouvert en lecture
    /// seule, sans partage d'écriture, pendant que schtasks le lit.
    /// </summary>
    private void CreateTask()
    {
        Directory.CreateDirectory(installDir);
        var xmlPath = Path.Combine(installDir, StagedXmlName);
        try
        {
            using (var writer = new FileStream(xmlPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(TaskDefinition.Xml(Path.Combine(installDir, ExeName)))).ToArray();
                writer.Write(bytes); // UTF-16 avec BOM
            }

            using var guard = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Check(runner.Run("schtasks", $"/Create /TN {QuotedTask} /XML \"{xmlPath}\" /F"), "création de la tâche planifiée");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private static void Check(int exitCode, string step)
    {
        if (exitCode != 0) throw new InvalidOperationException($"Échec de la {step} (code {exitCode}).");
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Exécute l'action, en la retentant après chaque attente de <see cref="RetryDelays"/> tant qu'un fichier est tenu ; la dernière erreur est relancée.</summary>
    private void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < RetryDelays.Length)
            {
                _sleep(RetryDelays[attempt]);
            }
        }
    }

    /// <summary>
    /// Copie récursive qui écrase les fichiers existants et ne supprime rien. Les fichiers créés héritent des droits du dossier
    /// de destination. Les jonctions et liens symboliques sont ignorés : la copie est élevée et ne doit pas suivre un lien vers
    /// un autre dossier (ni tourner en boucle sur un lien vers un parent).
    /// </summary>
    private void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (IsReparsePoint(entry))
            {
                Trace.TraceWarning($"Lien ignoré lors de la copie : {entry.FullName}");
                continue;
            }

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo) CopyDirectory(entry.FullName, target);
            else Retry(() => File.Copy(entry.FullName, target, overwrite: true));
        }
    }

    private static bool IsReparsePoint(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
