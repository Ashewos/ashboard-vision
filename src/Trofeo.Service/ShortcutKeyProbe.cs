using Trofeo.Control;
using Trofeo.Core.Shortcuts;

namespace Trofeo.Display;

/// <summary>
/// Répond à « cette combinaison de touches est-elle libre ? » : le service essaie de l'enregistrer auprès de Windows, puis la relâche
/// aussitôt. À appeler depuis le fil qui possède la fenêtre des raccourcis (l'enregistrement y est attaché).
/// </summary>
public static class ShortcutKeyProbe
{
    /// <summary>L'identifiant de l'essai : hors de ceux des raccourcis du routeur (1 à N) et dans la plage permise aux applications (0 à 0xBFFF).</summary>
    public const int ProbeId = 0xBF00;

    /// <summary>Évite que le maintien de la touche n'envoie la commande en rafale ; sans effet sur l'essai, mais c'est la forme des vrais enregistrements.</summary>
    private const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// La longueur au-delà de laquelle des touches ne sont plus lues : la plus longue combinaison permise (« Ctrl+Alt+Shift+Win+F24 ») tient
    /// en 22 caractères. La requête vient du canal de contrôle : un texte énorme est refusé avant d'être découpé.
    /// </summary>
    public const int MaxKeysLength = 100;

    /// <param name="registrar">La fenêtre des raccourcis.</param>
    /// <param name="keys">Les touches à essayer.</param>
    /// <param name="ownKeys">
    /// Les touches que le service a déjà enregistrées lui-même : Windows les refuserait pour cette raison, alors qu'aucune autre
    /// application ne les tient. Les conflits entre raccourcis du logiciel sont vérifiés par la fenêtre de réglage.
    /// </param>
    public static ShortcutKeyState Test(IHotkeyRegistrar registrar, string keys, IEnumerable<string> ownKeys)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(ownKeys);
        if (keys is null || keys.Length > MaxKeysLength) return ShortcutKeyState.Unreadable;
        if (!ShortcutKeys.TryParse(keys, out var modifiers, out var virtualKey)) return ShortcutKeyState.Unreadable;
        if (ownKeys.Any(own => ShortcutKeys.Same(own, keys))) return ShortcutKeyState.Free;

        if (!registrar.Register(ProbeId, modifiers | ModNoRepeat, virtualKey)) return ShortcutKeyState.TakenByAnotherProgram;

        registrar.Unregister(ProbeId);
        return ShortcutKeyState.Free;
    }
}
