using Trofeo.Control;
using Trofeo.Core.Shortcuts;

namespace Trofeo.Display;

/// <summary>
/// Ce qui pose les raccourcis clavier globaux : la fenêtre des raccourcis du service, sur le fil de l'interface. Un faux
/// la remplace dans les tests.
/// </summary>
public interface IHotkeyHost
{
    /// <summary>Remplace tous les raccourcis. Rend les raccourcis qui n'ont pas pu être posés, en français ; vide si tout va bien.</summary>
    Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<ShortcutBinding> bindings);

    /// <summary>Les touches que Windows a refusées au dernier enregistrement, écrites comme dans la disposition ; vide si tout est enregistré.</summary>
    IReadOnlyList<string> RefusedKeys { get; }

    /// <summary>Essaie ces touches auprès de Windows sur le fil de l'interface, puis les relâche (voir <see cref="ShortcutKeyProbe"/>).</summary>
    Task<ShortcutKeyState> TestAsync(string keys);
}
