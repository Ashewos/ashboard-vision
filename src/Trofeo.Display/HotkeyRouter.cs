using System.Diagnostics;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Display;

/// <summary>Ce qui enregistre les raccourcis auprès de Windows : <see cref="HotkeyWindow"/> en production, un faux dans les tests.</summary>
public interface IHotkeyRegistrar
{
    /// <summary>Enregistre un raccourci global ; rend faux si Windows le refuse (déjà pris par une autre application).</summary>
    bool Register(int id, uint modifiers, uint virtualKey);

    /// <summary>Retire un raccourci enregistré par <see cref="Register"/>.</summary>
    void Unregister(int id);
}

/// <summary>
/// Enregistre les raccourcis de la disposition et envoie la commande de chacun au module visé. À utiliser depuis
/// le thread de l'interface seulement.
/// </summary>
/// <param name="resolveTarget">Trouve le module visé (<c>null</c> : le premier qui accepte les commandes).</param>
public sealed class HotkeyRouter(
    IHotkeyRegistrar registrar, IReadOnlyList<ShortcutBinding> initialBindings, Func<Guid?, ICommandableModule?> resolveTarget) : IDisposable
{
    /// <summary>Évite que le maintien de la touche n'envoie la commande en rafale.</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly HashSet<int> _registered = [];
    private IReadOnlyList<ShortcutBinding> _bindings = initialBindings;
    private bool _disposed;

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
                continue;
            }

            _registered.Add(id);
        }

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
        // Les commandes de vue et de module complémentaire n'ont pas encore de destinataire ici.
        if (binding.Command.ToModuleCommand() is not { } kind) return;

        try
        {
            resolveTarget(binding.Target)?.HandleCommand(new ModuleCommand(kind, binding.Index));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Échec du raccourci « {binding.Keys} » : {ex}");
        }
    }

    /// <summary>Retire tous les raccourcis enregistrés.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
    }
}
