using System.Diagnostics;
using Trofeo.Extensions;

namespace Trofeo.Display;

/// <summary>
/// Remet à neuf les cases posées d'un module complémentaire quand ce que son type exécute a réellement changé. Une case garde l'objet
/// que la fabrique de son type a créé ; or une validation, une installation ou une mise à jour remplace cette fabrique (et souvent le
/// superviseur qu'elle tenait) : une case « À valider » ne s'animerait jamais, une case dont le superviseur a été supprimé ne montrerait
/// plus rien de neuf. Chaque case du type est donc recréée par <see cref="DisplayLoop.RestartAsync"/>.
/// <para>
/// <b>Ce qui compte comme un changement</b> : le type apparaît, disparaît ou change d'<see cref="Kind"/> (de « tourne » à « ne tourne pas »,
/// ou l'inverse), de dossier (une autre version, ou le passage au mode développeur), ou de raison de ne pas tourner. Les permissions n'en
/// sont pas : le superviseur relance lui-même son hôte et la case reste branchée dessus.
/// </para>
/// <para>
/// <b>La mise de côté n'en est pas un</b> : <see cref="ICommunityModules.Changed"/> est aussi levé quand un module est mis de côté, et
/// une case déjà posée tient le superviseur, qui sait dire pourquoi il ne lance plus rien. La recréer n'apporterait rien et, comme
/// chaque examen lève <see cref="ICommunityModules.Changed"/>, un module qui retombe en panne ferait redémarrer ses cases en boucle.
/// Le retour de la mise de côté (<see cref="ICommunityModules.RelaunchAsync"/>) en est un : une case posée pendant la mise de côté tient
/// l'objet « mis de côté ».
/// </para>
/// Les redémarrages sont demandés sur le fil qui lève l'événement (la file de la boucle les exécute) ; rien n'est attendu ici.
/// </summary>
internal sealed class CommunityTypeWatcher : IDisposable
{
    private readonly ICommunityModules _community;
    private readonly DisplayLoop _loop;
    private readonly object _gate = new();
    private Dictionary<string, Kind> _last;
    private bool _disposed;

    public CommunityTypeWatcher(ICommunityModules community, DisplayLoop loop)
    {
        _community = community ?? throw new ArgumentNullException(nameof(community));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _last = Snapshot(community.Entries);
        community.Changed += OnChanged;
    }

    /// <summary>Ce que le type d'un module fait tourner, pour comparer deux examens.</summary>
    /// <param name="Runs">Le module est prêt : sa fabrique donne une case branchée sur son superviseur.</param>
    /// <param name="State">L'état du catalogue.</param>
    /// <param name="Reason">Pourquoi il ne tourne pas, quand la case le dit elle-même ; <c>null</c> sinon.</param>
    /// <param name="Directory">Le dossier du module (sa version, ou le dossier de développement).</param>
    /// <param name="Dev">Le module vient d'un dossier de développement.</param>
    internal sealed record Kind(bool Runs, CommunityState State, string? Reason, string Directory, bool Dev);

    private static Dictionary<string, Kind> Snapshot(IReadOnlyList<CommunityEntry> entries)
    {
        var kinds = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Manifest is null)
            {
                continue; // sans manifeste lisible, il n'y a pas de type : aucune case ne peut s'y rapporter
            }

            // Le premier gagne, comme dans le registre.
            kinds.TryAdd(entry.Id, new Kind(
                entry.State == CommunityState.Ready,
                entry.State,
                entry.State is CommunityState.Ready or CommunityState.SetAside ? null : entry.Message,
                entry.Directory,
                entry.FromDevFolder));
        }

        return kinds;
    }

    private static bool Differs(Kind? before, Kind? after)
    {
        if (before is null || after is null)
        {
            return before is not null || after is not null;
        }

        if (before.Runs && after.State == CommunityState.SetAside && before.Directory == after.Directory && before.Dev == after.Dev)
        {
            return false; // la mise de côté seule
        }

        return before != after;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        HashSet<string> changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = Snapshot(_community.Entries);
            changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in _last.Keys.Union(next.Keys))
            {
                if (Differs(_last.GetValueOrDefault(id), next.GetValueOrDefault(id)))
                {
                    changed.Add(id);
                }
            }

            _last = next;
        }

        if (changed.Count == 0)
        {
            return;
        }

        foreach (var placement in _loop.Layout.Placements.Where(p => changed.Contains(p.TypeId)).ToList())
        {
            Restart(placement.InstanceId, placement.TypeId);
        }
    }

    private void Restart(Guid instanceId, string typeId)
    {
        try
        {
            var task = _loop.RestartAsync(instanceId);
            _ = task.ContinueWith(
                t => Trace.TraceWarning($"Modules complémentaires : la case « {typeId} » ({instanceId}) n'a pas pu être remise à neuf : {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Modules complémentaires : la case « {typeId} » ({instanceId}) n'a pas pu être remise à neuf : {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _community.Changed -= OnChanged;
    }
}
