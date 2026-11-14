using System.Diagnostics;
using Trofeo.Control;
using Trofeo.Core.Shortcuts;
using Trofeo.Extensions;
using Trofeo.ModuleKit;

namespace Trofeo.Display;

/// <summary>
/// Enregistre les raccourcis de la disposition et envoie la commande de chacun : un raccourci d'onglet va au module visé
/// (dans la vue affichée seulement), un raccourci de vue change la vue affichée, un raccourci d'un module complémentaire part vers
/// ses cases de la vue affichée (le module ne voit jamais le clavier). À utiliser depuis le thread de l'interface seulement.
/// </summary>
/// <param name="resolveTarget">
/// Trouve le module visé par une commande d'onglet (<c>null</c> : le premier de la vue affichée qui accepte les commandes) ;
/// il rend <c>null</c> si la vue affichée n'en a pas, et la commande ne fait alors rien.
/// </param>
/// <param name="views">Change la vue affichée ; <c>null</c> : les raccourcis de vue ne font rien.</param>
/// <param name="community">Transmet un raccourci aux cases d'un module complémentaire ; <c>null</c> : ces raccourcis ne font rien.</param>
/// <param name="findCommunityCases">
/// Les cases de la vue affichée qui reçoivent un raccourci de module complémentaire : celles du type de module donné et, si une case est
/// visée (second argument), elle seule, et seulement si elle est dans la vue affichée. Vide : rien n'est envoyé.
/// </param>
public sealed class HotkeyRouter(
    IHotkeyRegistrar registrar, IReadOnlyList<ShortcutBinding> initialBindings, Func<Guid?, ICommandableModule?> resolveTarget,
    IViewSwitcher? views = null, ICommunityShortcuts? community = null,
    Func<string, Guid?, IReadOnlyCollection<Guid>>? findCommunityCases = null) : IDisposable
{
    /// <summary>Évite que le maintien de la touche n'envoie la commande en rafale.</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly HashSet<int> _registered = [];
    private IReadOnlyList<ShortcutBinding> _bindings = initialBindings;
    private volatile IReadOnlyList<string> _refusedKeys = [];
    private bool _disposed;

    /// <summary>
    /// Les touches que Windows a refusées au dernier enregistrement (déjà prises par une autre application), écrites comme dans la
    /// disposition. Lisible depuis n'importe quel fil.
    /// </summary>
    public IReadOnlyList<string> RefusedKeys => _refusedKeys;

    /// <summary>
    /// Essaie ces touches auprès de Windows (puis les relâche), sans toucher aux raccourcis posés ; les touches que le routeur a lui-même
    /// enregistrées rendent <see cref="ShortcutKeyState.Free"/>. À appeler depuis le fil de l'interface, qui possède la fenêtre.
    /// </summary>
    public ShortcutKeyState TestKeys(string keys)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var own = _registered.Select(id => _bindings[id - 1].Keys);
        return ShortcutKeyProbe.Test(registrar, keys, own);
    }

    /// <summary>
    /// Enregistre les liaisons sous les identifiants 1 à N, dans l'ordre. Une touche illisible ou refusée par Windows
    /// est journalisée et laissée de côté : les autres sont enregistrées quand même.
    /// </summary>
    public void RegisterAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Register();
    }

    /// <summary>
    /// Retire tous les raccourcis, puis pose ceux de <paramref name="newBindings"/> (même règle que
    /// <see cref="RegisterAll"/>). Rend ceux qui n'ont pas pu être posés, en français (vide si tout va bien).
    /// </summary>
    public IReadOnlyList<string> Replace(IReadOnlyList<ShortcutBinding> newBindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        UnregisterAll();
        _bindings = newBindings;
        return Register();
    }

    private List<string> Register()
    {
        var failures = new List<string>();
        var refused = new List<string>();
        for (var i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            var id = i + 1;
            if (!ShortcutKeys.TryParse(binding.Keys, out var modifiers, out var virtualKey))
            {
                failures.Add($"Raccourci « {binding.Keys} » indisponible : touches illisibles.");
                continue;
            }

            if (!registrar.Register(id, modifiers | ModNoRepeat, virtualKey))
            {
                failures.Add($"Raccourci « {binding.Keys} » indisponible : déjà pris par une autre application.");
                if (!refused.Contains(binding.Keys, StringComparer.OrdinalIgnoreCase)) refused.Add(binding.Keys);
                continue;
            }

            _registered.Add(id);
        }

        _refusedKeys = refused;
        foreach (var failure in failures) Trace.TraceWarning(failure);
        return failures;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) registrar.Unregister(id);
        _registered.Clear();
    }

    /// <summary>Un raccourci a été pressé : envoie sa commande au module visé. Aucune exception n'en sort.</summary>
    public void OnHotkey(int id)
    {
        if (_disposed || id < 1 || id > _bindings.Count) return;

        var binding = _bindings[id - 1];

        try
        {
            switch (binding.Command)
            {
                case ShortcutCommand.PreviousView:
                    views?.ShowAdjacentView(-1);
                    break;
                case ShortcutCommand.NextView:
                    views?.ShowAdjacentView(+1);
                    break;
                case ShortcutCommand.SelectView:
                    views?.ShowViewAt(binding.Index);
                    break;
                case ShortcutCommand.Community:
                    SendToCommunityCases(binding);
                    break;
                default:
                    if (binding.Command.ToModuleCommand() is { } kind)
                        resolveTarget(binding.Target)?.HandleCommand(new ModuleCommand(kind, binding.Index));
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec du raccourci « {binding.Keys} » : {ex}");
        }
    }

    /// <summary>
    /// Un raccourci d'un module complémentaire : il va aux cases de ce module dans la vue affichée (ou à la case visée, si elle y est),
    /// et à personne d'autre. Sans case à l'écran, rien n'est envoyé.
    /// </summary>
    private void SendToCommunityCases(ShortcutBinding binding)
    {
        if (community is null || findCommunityCases is null || binding.ModuleId is not { } moduleId || binding.ShortcutId is not { } shortcutId)
            return;

        var cases = findCommunityCases(moduleId, binding.Target);
        if (cases.Count > 0) community.SendShortcut(moduleId, shortcutId, cases);
    }

    /// <summary>Retire tous les raccourcis enregistrés.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
    }
}
