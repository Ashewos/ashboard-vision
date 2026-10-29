using System.Security;
using Trofeo.Modules.Agents;

namespace Trofeo.Distribution;

/// <summary>
/// Le nettoyage propre à l'utilisateur, à la désinstallation : la valeur <c>Run</c> (la nouvelle et l'ancienne), le mod des quotas dans
/// Claude Code et, si l'utilisateur l'a demandé, le dossier des réglages. Chaque étape est tentée même si la précédente a échoué : un
/// désinstallateur ne doit pas s'arrêter sur un mod déjà retiré ou sur un Claude Code absent.
/// </summary>
public sealed class UserCleanup(IRunKey runKey, QuotaModInstaller mods)
{
    private const string LegacyRunValue = "trofeo-HB-dashboard";
    private const string RunValue = "ashboard-vision";

    public async Task RunAsync(string settingsDir, bool deleteSettings, CancellationToken ct)
    {
        foreach (var name in new[] { RunValue, LegacyRunValue })
        {
            try
            {
                runKey.Delete(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // La valeur reste : elle pointera sur un programme absent, sans autre conséquence.
            }
        }

        // Le résultat est ignoré : mod déjà absent ou Claude Code introuvable, il n'y a rien de plus à retirer.
        await mods.UninstallAsync(QuotaModInstaller.PluginName, QuotaModInstaller.Marketplace, ct);

        if (!deleteSettings)
        {
            return;
        }

        try
        {
            if (Directory.Exists(settingsDir))
            {
                Directory.Delete(settingsDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier encore ouvert : le dossier reste, l'utilisateur pourra le supprimer à la main.
        }
    }
}
