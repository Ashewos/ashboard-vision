namespace Trofeo.Core.Shortcuts;

/// <summary>
/// Migration, une seule fois, des raccourcis d'un <c>layout.json</c> d'avant les vues (1.1.0 et avant) : les onglets
/// passent de Gauche / Droite à Haut / Bas, et Gauche / Droite deviennent « vue précédente / suivante ». Seules les
/// anciennes valeurs par défaut, exactement, sont touchées : une liaison que l'utilisateur a changée reste telle quelle.
/// </summary>
internal static class ShortcutMigration
{
    private const string OldPreviousTabKeys = "Ctrl+Alt+Left";
    private const string OldNextTabKeys = "Ctrl+Alt+Right";
    private const string NewPreviousTabKeys = "Ctrl+Alt+Up";
    private const string NewNextTabKeys = "Ctrl+Alt+Down";

    /// <summary>
    /// Rend les raccourcis migrés. <c>null</c> (aucun raccourci écrit : les valeurs par défaut s'appliquent) reste
    /// <c>null</c>. Une ancienne valeur par défaut n'est déplacée que si sa nouvelle touche est libre ; la commande de
    /// vue n'est donnée à la touche libérée que si elle est encore libre, et seulement quand l'ancienne liaison par
    /// défaut y était (une liste volontairement vidée ou allégée n'est pas remplie).
    /// </summary>
    internal static IReadOnlyList<ShortcutBinding>? Migrate(IReadOnlyList<ShortcutBinding>? shortcuts)
    {
        if (shortcuts is null) return null;

        var result = shortcuts.ToList();
        var previous = MoveTab(result, OldPreviousTabKeys, ShortcutCommand.PreviousTab, NewPreviousTabKeys, ShortcutCommand.PreviousView);
        var next = MoveTab(result, OldNextTabKeys, ShortcutCommand.NextTab, NewNextTabKeys, ShortcutCommand.NextView);

        // Les commandes de vue prennent la place juste derrière la dernière liaison d'onglet déplacée.
        var insertAt = Math.Max(previous?.TabIndex ?? -1, next?.TabIndex ?? -1) + 1;
        foreach (var view in new[] { previous?.View, next?.View })
        {
            if (view is null || result.Any(b => ShortcutKeys.Same(b.Keys, view.Keys))) continue;

            result.Insert(insertAt++, view);
        }

        return result;
    }

    /// <summary>Déplace l'ancienne liaison par défaut d'onglet vers sa nouvelle touche ; rend où elle est et la liaison de vue à proposer.</summary>
    private static Moved? MoveTab(
        List<ShortcutBinding> bindings, string oldKeys, ShortcutCommand command, string newKeys, ShortcutCommand viewCommand)
    {
        var index = bindings.FindIndex(b =>
            b.Command == command && b.Index == 0 && b.Target is null && b.ModuleId is null && b.ShortcutId is null &&
            ShortcutKeys.Same(b.Keys, oldKeys));
        if (index < 0) return null;
        if (bindings.Any(b => ShortcutKeys.Same(b.Keys, newKeys))) return null; // la nouvelle touche est déjà à l'utilisateur : rien ne bouge

        bindings[index] = bindings[index] with { Keys = newKeys };
        return new Moved(index, new ShortcutBinding(oldKeys, viewCommand));
    }

    private sealed record Moved(int TabIndex, ShortcutBinding View);
}
