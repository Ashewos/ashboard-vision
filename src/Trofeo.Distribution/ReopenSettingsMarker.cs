namespace Trofeo.Distribution;

/// <summary>
/// Le petit fichier <c>rouvrir-reglages</c> qu'on pose avant de lancer l'installateur quand la fenêtre de réglage
/// était ouverte : au prochain démarrage, le logiciel le trouve, le supprime et rouvre la fenêtre.
/// </summary>
public static class ReopenSettingsMarker
{
    private const string FileName = "rouvrir-reglages";

    public static void Create(string settingsDir)
    {
        Directory.CreateDirectory(settingsDir);
        File.WriteAllBytes(Path.Combine(settingsDir, FileName), []);
    }

    /// <summary>
    /// Vrai, et le repère est supprimé, s'il existait. Un repère qu'on n'arrive pas à supprimer ne compte pas :
    /// mieux vaut manquer une réouverture que rouvrir la fenêtre à chaque démarrage.
    /// </summary>
    public static bool Consume(string settingsDir)
    {
        var path = Path.Combine(settingsDir, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
