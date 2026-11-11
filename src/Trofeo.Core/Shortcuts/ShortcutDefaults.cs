namespace Trofeo.Core.Shortcuts;

/// <summary>Les raccourcis pris quand la disposition n'en règle aucun.</summary>
public static class ShortcutDefaults
{
    /// <summary>
    /// Ctrl+Alt+Haut et Ctrl+Alt+Bas pour changer d'onglet, Ctrl+Alt+Gauche et Ctrl+Alt+Droite pour changer de vue,
    /// Ctrl+Alt+Maj+1 à 9 pour aller à l'onglet 1 à 9.
    /// </summary>
    public static IReadOnlyList<ShortcutBinding> All { get; } =
    [
        new("Ctrl+Alt+Up", ShortcutCommand.PreviousTab),
        new("Ctrl+Alt+Down", ShortcutCommand.NextTab),
        new("Ctrl+Alt+Left", ShortcutCommand.PreviousView),
        new("Ctrl+Alt+Right", ShortcutCommand.NextView),
        // Maj en plus : sur un clavier AZERTY, AltGr vaut Ctrl+Alt, et Ctrl+Alt+chiffre volerait ~ # { [ | ` \ ^.
        .. Enumerable.Range(1, 9).Select(n => new ShortcutBinding($"Ctrl+Alt+Shift+{n}", ShortcutCommand.SelectTab, n - 1)),
    ];
}
