using System.Security;
using Trofeo.Modules.Agents;

namespace Trofeo.Distribution;

/// <summary>
/// Le nettoyage propre à l'utilisateur, à la désinstallation : la valeur <c>Run</c> (la nouvelle et l'ancienne), le mod des quotas dans
/// Claude Code, le marqueur de préparation <c>installation.json</c> (toujours : une réinstallation doit refaire la préparation et
/// appliquer de nouveau le choix de l'installateur), le nettoyage des modules complémentaires et, si l'utilisateur l'a demandé, le dossier des réglages avec les dossiers
/// qui contiennent des copies de ses données. Chaque étape est tentée même si la précédente a échoué : un
/// désinstallateur ne doit pas s'arrêter sur un mod déjà retiré ou sur un Claude Code absent.
/// </summary>
/// <param name="runKey">La valeur <c>Run</c> de l'utilisateur.</param>
/// <param name="mods">Le mod des quotas dans Claude Code.</param>
/// <param name="modulesCleanup">
/// Le nettoyage des modules complémentaires (conteneurs, secrets et, si les réglages sont effacés, la liste et le dossier des modules).
/// Il vit dans <c>Trofeo.Extensions</c>, qui dépend de cette bibliothèque : l'appelant le fournit. Il reçoit « effacer mes réglages »
/// et est appelé avant l'effacement du dossier des réglages, où est la liste scellée qu'il relit. <c>null</c> : rien à nettoyer.
/// </param>
public sealed class UserCleanup(IRunKey runKey, QuotaModInstaller mods, Func<bool, CancellationToken, Task>? modulesCleanup = null)
{
    private const string LegacyRunValue = "trofeo-HB-dashboard";
    private const string RunValue = "ashboard-vision";

    /// <param name="settingsDir">Le dossier des réglages (<c>%AppData%\ashboard-vision</c>).</param>
    /// <param name="deleteSettings">Vrai pour effacer aussi les réglages et <paramref name="leftovers"/>.</param>
    /// <param name="leftovers">
    /// Les autres dossiers qui gardent des copies des réglages, dont <c>secrets.dat</c> : les sauvegardes d'avant un retour en arrière,
    /// l'ancien dossier orphelin, le téléchargement de la mise à jour. Des chemins fixes donnés par l'appelant, jamais lus ailleurs.
    /// Ils ne sont effacés qu'avec <paramref name="deleteSettings"/>.
    /// </param>
    public async Task RunAsync(string settingsDir, bool deleteSettings, CancellationToken ct, IReadOnlyList<string>? leftovers = null)
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

        // Même en gardant les réglages : sans lui, la réinstallation sauterait la préparation (même version) ou ignorerait la case de l'installateur.
        TryDelete(() => File.Delete(Path.Combine(settingsDir, UserSetup.StateFileName)));

        // Avant l'effacement des réglages : il relit la liste scellée, qui y est. Les conteneurs et les secrets des modules partent
        // même quand les réglages sont gardés ; le reste suit le choix.
        if (modulesCleanup is not null)
        {
            try
            {
                await modulesCleanup(deleteSettings, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Ce qui reste (un fichier ouvert…) sera laissé à l'utilisateur : le reste du nettoyage continue.
            }
        }

        if (!deleteSettings)
        {
            return;
        }

        foreach (var folder in (string[])[settingsDir, .. leftovers ?? []])
        {
            // Un fichier encore ouvert : le dossier reste, l'utilisateur pourra le supprimer à la main.
            TryDelete(() =>
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            });
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Voir l'appelant : un fichier qui résiste est laissé en place, sans arrêter le nettoyage.
        }
    }
}
