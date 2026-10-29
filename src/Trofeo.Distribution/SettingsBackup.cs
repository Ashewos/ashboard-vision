using System.Globalization;

namespace Trofeo.Distribution;

/// <summary>La copie des réglages qu'on fait avant un retour en arrière (de la bêta à la stable).</summary>
public static class SettingsBackup
{
    /// <summary>
    /// Copie tout le dossier des réglages, sauf <c>logs\</c>, dans <c>backupsRoot\aaaa-MM-jj-HHmmss-version</c>.
    /// Deux copies dans la même seconde ne s'écrasent pas : la seconde reçoit le suffixe <c>-2</c>, la troisième <c>-3</c>.
    /// Rend le dossier de la copie. Si la copie échoue, ce qui en reste est supprimé et l'exception remonte :
    /// une copie à moitié faite ne doit pas passer pour une bonne.
    /// </summary>
    /// <param name="from">La version qui est installée (celle dont viennent les réglages).</param>
    public static string Create(string settingsDir, string backupsRoot, AppVersion from, DateTimeOffset now)
    {
        var name = $"{now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}-{from}";
        var destination = Path.Combine(backupsRoot, name);
        for (var n = 2; Directory.Exists(destination); n++)
        {
            destination = Path.Combine(backupsRoot, $"{name}-{n}");
        }

        Directory.CreateDirectory(destination);
        try
        {
            if (Directory.Exists(settingsDir))
            {
                var settingsRoot = Path.GetFullPath(settingsDir);
                CopyDirectory(settingsRoot, destination, settingsRoot, Path.GetFullPath(backupsRoot));
            }
        }
        catch
        {
            try
            {
                Directory.Delete(destination, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Le nettoyage est un bonus : c'est l'erreur de la copie qui compte.
            }

            throw;
        }

        return destination;
    }

    private static void CopyDirectory(string source, string destination, string settingsRoot, string backupsRoot)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var full = Path.GetFullPath(directory);
            var isLogs = string.Equals(Path.GetDirectoryName(full), settingsRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(full), "logs", StringComparison.OrdinalIgnoreCase);
            // Les journaux ne font pas partie des réglages. Le dossier des copies, s'il est rangé dans celui des réglages,
            // ne se copie pas dans lui-même ; un lien (jonction) n'est pas suivi, pour ne pas tourner en rond.
            if (isLogs
                || string.Equals(full, backupsRoot, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(full));
            Directory.CreateDirectory(target);
            CopyDirectory(full, target, settingsRoot, backupsRoot);
        }
    }
}
