namespace Trofeo.Distribution;

/// <summary>
/// Le déménagement des réglages d'avant le changement de nom (<c>%AppData%\trofeo-HB-dashboard</c>) vers
/// <c>%AppData%\ashboard-vision</c>. Il se fait <b>avant</b> l'ouverture du journal, qui vit dans le nouveau dossier :
/// il ne journalise donc rien lui-même et rend seulement son résultat, à journaliser par l'appelant.
/// </summary>
public static class LegacySettingsMove
{
    public enum MoveResult
    {
        /// <summary>Il n'y a pas d'ancien dossier.</summary>
        NothingToMove,

        /// <summary>L'ancien dossier est devenu le nouveau.</summary>
        Moved,

        /// <summary>Les deux dossiers existent : rien n'est déplacé et l'ancien est gardé, tel quel.</summary>
        AlreadyMoved,

        /// <summary>Le déménagement a échoué (fichier ouvert, droits) : l'ancien dossier est intact et le nouveau n'existe pas.</summary>
        Failed,
    }

    /// <summary>
    /// Renomme <paramref name="legacyDir"/> en <paramref name="newDir"/>. Un renommage est tout ou rien : s'il échoue,
    /// rien n'a bougé, et ce qui l'a empêché (le service encore lancé, par exemple) pourra être réglé avant un nouvel essai.
    /// </summary>
    public static MoveResult Run(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir))
        {
            return MoveResult.NothingToMove;
        }

        if (Directory.Exists(newDir))
        {
            return MoveResult.AlreadyMoved;
        }

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(newDir));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(legacyDir, newDir);
            return MoveResult.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MoveResult.Failed;
        }
    }
}
