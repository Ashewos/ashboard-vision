using Trofeo.Extensions.Protocol;

namespace Trofeo.Extensions.Service;

// Les raccourcis que le service transmet aux cases du module. Le module ne voit jamais le clavier : le service a déjà choisi les
// cases (celles de la vue affichée) et il ne transmet qu'un identifiant déclaré par le manifeste, au plus 5 appuis par seconde.
public sealed partial class ModuleHostSupervisor
{
    /// <summary>Le nombre d'appuis transmis par seconde à ce module, au plus : un maintien de touche ou un module bavard ne le noie pas.</summary>
    public const int MaxShortcutsPerSecond = 5;

    private static readonly TimeSpan ShortcutWindow = TimeSpan.FromSeconds(1);

    // Les instants des appuis transmis dans la dernière seconde ; sous _gate.
    private readonly Queue<DateTimeOffset> _shortcutTimes = new();

    /// <summary>
    /// Transmet un raccourci aux cases données (leurs identifiants d'instance dans la disposition), qui doivent déjà être celles de la
    /// vue affichée. Rend faux, sans rien envoyer, si l'hôte ne tourne pas, si le module est mis de côté, si le manifeste ne déclare
    /// pas <paramref name="shortcutId"/> (comparé tel quel), si aucune de ces cases n'est ouverte dans l'hôte, ou si 5 appuis ont déjà
    /// été transmis dans la dernière seconde. Le décompte est par appui, pas par case.
    /// </summary>
    public bool SendShortcut(string shortcutId, IReadOnlyCollection<Guid> instances)
    {
        ArgumentNullException.ThrowIfNull(shortcutId);
        ArgumentNullException.ThrowIfNull(instances);
        lock (_gate)
        {
            if (_disposed || _session is not { } session || _setAsideReason is not null
                || !_target.Manifest.Shortcuts.Any(s => string.Equals(s.Id, shortcutId, StringComparison.Ordinal)))
            {
                return false;
            }

            var slots = _instances
                .Where(m => instances.Contains(m.Context.InstanceId))
                .Select(m => m.Slot)
                .OfType<InstanceSlot>()
                .ToList();
            if (slots.Count == 0)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            while (_shortcutTimes.Count > 0 && now - _shortcutTimes.Peek() >= ShortcutWindow)
            {
                _shortcutTimes.Dequeue();
            }

            if (_shortcutTimes.Count >= MaxShortcutsPerSecond)
            {
                return false;
            }

            _shortcutTimes.Enqueue(now);
            foreach (var slot in slots)
            {
                session.Post(new Shortcut(slot.Id, shortcutId));
            }

            return true;
        }
    }
}
