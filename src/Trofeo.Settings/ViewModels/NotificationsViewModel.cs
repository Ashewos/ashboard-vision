using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Control;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La liste de la cloche de l'en-tête : les mises à jour (le logiciel, les modules complémentaires) et les pannes (arrêt brusque ou mise de
/// côté d'un module, panne de la carte graphique). La fenêtre la compose elle-même, de trois sources : <see cref="IServiceLink.Updates"/>,
/// l'état du service (<see cref="ServiceStatus.Acceleration"/>) et la vue d'ensemble des modules complémentaires, relue à la connexion puis
/// toutes les 30 secondes tant que la liaison est connectée (et seulement alors : coupée, il n'y a ni requête ni minuteur).
/// <para>
/// Chaque notification a une clé ; la croix (<see cref="Dismiss"/>) la retient dans <see cref="EditorPreferences.DismissedNotifications"/>.
/// Une notification dont la clé change (une autre version, une nouvelle panne) revient. Les clés retenues dont la cause a disparu sont
/// élaguées pour que le fichier ne grossisse pas, mais jamais sans certitude : pas pendant une coupure, une lecture des modules en échec
/// ou une recherche de mise à jour en cours, sans quoi une croix serait oubliée et la notification reviendrait.
/// </para>
/// </summary>
public sealed class NotificationsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CommunityInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceLink _link;
    private readonly PreferencesStore _preferences;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _polling;
    private CommunityOverview? _overview;
    private bool _overviewOk;
    private int _reads;
    private bool _disposed;

    public NotificationsViewModel(IServiceLink link, PreferencesStore preferences, TimeProvider time)
    {
        _link = link;
        _preferences = preferences;
        _time = time;
        _link.Changed += OnLinkChanged;
        OnLinkChanged();
    }

    /// <summary>Les notifications visibles (sans celles que la croix a supprimées), dans l'ordre : les problèmes du plus récent au plus ancien, la mise à jour du logiciel, puis celles des modules par nom.</summary>
    public IReadOnlyList<NotificationItem> Items { get; private set; } = [];

    /// <summary>Le nombre de notifications visibles : le rond de la cloche.</summary>
    public int Count => Items.Count;

    /// <summary>Au moins un problème est visible : le rond de la cloche est rouge.</summary>
    public bool HasProblem => Items.Any(i => i.Kind == NotificationKind.Problem);

    /// <summary>Rien n'est visible : le panneau dit « Rien de nouveau. » et le rond est absent.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Un lien a été cliqué : l'onglet (et la section) à montrer. La fenêtre l'écoute.</summary>
    public event Action<NotificationTarget>? Navigate;

    /// <summary>Le lien d'une notification : demande à la fenêtre d'ouvrir l'onglet.</summary>
    public void Open(NotificationItem item) => Navigate?.Invoke(item.Target);

    /// <summary>La croix : la clé est retenue (elle reste dans le fichier des préférences) et la ligne disparaît.</summary>
    public void Dismiss(string key)
    {
        var current = _preferences.Current.DismissedNotifications ?? [];
        if (!current.Contains(key))
            _preferences.Update(p => p with { DismissedNotifications = [.. current, key] });
        Rebuild();
    }

    /// <summary>
    /// Relit la vue d'ensemble des modules complémentaires. Une réponse plus ancienne que la dernière demande est écartée ; une lecture
    /// qui échoue garde ce qu'on savait (rien n'est alors élagué).
    /// </summary>
    public async Task RefreshCommunityAsync()
    {
        var version = ++_reads;
        CommunityOverview overview;
        try
        {
            overview = await _link.GetCommunityAsync().ConfigureAwait(true);
        }
        catch (ControlException)
        {
            if (version == _reads && !_disposed)
            {
                _overviewOk = false;
                Rebuild();
            }

            return;
        }

        if (version != _reads || _disposed)
            return;
        _overview = overview;
        _overviewOk = true;
        Rebuild();
    }

    private void OnLinkChanged()
    {
        var connected = _link.State == LinkState.Connected;
        if (connected && _polling is null && !_disposed)
            StartPolling();
        else if (!connected && _polling is not null)
            StopPolling();

        Rebuild();
    }

    // Une lecture tout de suite, puis une toutes les 30 secondes, jusqu'à ce que la liaison soit coupée.
    private void StartPolling()
    {
        var polling = new CancellationTokenSource();
        _polling = polling;
        _ = PollAsync(polling.Token);
    }

    private void StopPolling()
    {
        var polling = _polling;
        _polling = null;
        _reads++; // une lecture en vol n'intéresse plus personne
        _overview = null;
        _overviewOk = false;
        polling?.Cancel();
        polling?.Dispose();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshCommunityAsync().ConfigureAwait(true);
                await Task.Delay(CommunityInterval, _time, ct).ConfigureAwait(true); // revient sur le fil de l'interface
            }
        }
        catch (OperationCanceledException)
        {
            // La liaison est coupée (ou la fenêtre se ferme) : la boucle s'arrête.
        }
    }

    // Recompose la liste, élague les croix périmées, et prévient les abonnés si la liste visible a changé.
    private void Rebuild()
    {
        var connected = _link.State == LinkState.Connected;
        var all = connected ? Compose() : [];
        if (connected)
            Prune(all);

        var dismissed = _preferences.Current.DismissedNotifications ?? [];
        var visible = all.Where(i => !dismissed.Contains(i.Key)).ToList();
        if (visible.SequenceEqual(Items))
            return;

        Items = visible;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // Toutes les notifications que les sources produisent, supprimées ou non, dans l'ordre d'affichage.
    private List<NotificationItem> Compose()
    {
        var problems = new List<NotificationItem>();
        var moduleUpdates = new List<(string Name, NotificationItem Item)>();
        NotificationItem? app = null;

        if (_link.Updates is { State: LinkUpdateState.Available, Version: { Length: > 0 } version })
        {
            app = new NotificationItem(
                $"app:{version}", NotificationKind.News, string.Format(Texts.NotificationAppUpdate, UntrustedText.Line(version)),
                Texts.NotificationLinkUpdate, NotificationTarget.Settings, null);
        }

        if (_link.Status?.Acceleration is { ProblemKind: LinkAccelerationProblem.Failure, Problem: { Length: > 0 } phrase } gpu)
        {
            problems.Add(new NotificationItem(
                $"gpu:{Stamp(gpu.ProblemAt)}", NotificationKind.Problem, phrase, Texts.NotificationLinkSettings,
                NotificationTarget.Settings, gpu.ProblemAt));
        }

        foreach (var module in _overview?.Modules ?? [])
        {
            // Le nom vient d'un module complémentaire : donnée non fiable, nettoyée ; l'identifiant le remplace s'il ne reste rien.
            var name = UntrustedText.Line(module.Name);
            if (name.Length == 0)
                name = UntrustedText.Line(module.Id);

            if (module.State == LinkCommunityState.SetAside)
            {
                problems.Add(new NotificationItem(
                    $"aside:{module.Id}:{Stamp(module.SetAsideAt)}", NotificationKind.Problem,
                    string.Format(Texts.NotificationModuleSetAside, name), Texts.NotificationLinkModule,
                    NotificationTarget.CommunityModules, module.SetAsideAt));
            }
            else if (module.StopCount > 0)
            {
                var text = module.StopCount >= 2
                    ? string.Format(Texts.NotificationModuleStoppedTimes, name, module.StopCount.ToString(CultureInfo.CurrentCulture))
                    : string.Format(Texts.NotificationModuleStopped, name);
                problems.Add(new NotificationItem(
                    $"stop:{module.Id}:{Stamp(module.LastStopAt)}", NotificationKind.Problem, text, Texts.NotificationLinkModule,
                    NotificationTarget.CommunityModules, module.LastStopAt));
            }

            if (module.UpdateVersion is { Length: > 0 } updateVersion)
            {
                moduleUpdates.Add((name, new NotificationItem(
                    $"update:{module.Id}:{updateVersion}", NotificationKind.News,
                    string.Format(Texts.NotificationModuleUpdate, name, UntrustedText.Line(updateVersion)), Texts.NotificationLinkModule,
                    NotificationTarget.CommunityModules, null)));
            }
        }

        var list = problems.OrderByDescending(i => i.At ?? DateTimeOffset.MinValue).ThenBy(i => i.Text, StringComparer.Ordinal).ToList();
        if (app is not null)
            list.Add(app);
        list.AddRange(moduleUpdates
            .OrderBy(u => u.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
            .ThenBy(u => u.Item.Key, StringComparer.Ordinal)
            .Select(u => u.Item));
        return list;
    }

    // N'élague que si tout est connu : liaison connectée, mises à jour lues, dernière lecture des modules réussie. Même alors, une source
    // qui n'est pas encore sûre garde ses croix : une recherche de mise à jour en cours ou ratée n'a pas retiré la proposition, et un
    // service qui ne dit pas l'accélération ne dit pas non plus qu'elle va bien.
    private void Prune(List<NotificationItem> produced)
    {
        var current = _preferences.Current.DismissedNotifications;
        if (current is null || current.Count == 0)
            return;
        if (_link.State != LinkState.Connected || _link.Updates is not { } updates || !_overviewOk)
            return;

        var appKnown = updates.State is not (LinkUpdateState.Unknown or LinkUpdateState.Checking or LinkUpdateState.Failed);
        var gpuKnown = _link.Status?.Acceleration is not null;
        var keys = produced.Select(i => i.Key).ToHashSet();

        var kept = current.Where(key =>
            keys.Contains(key)
            || key.StartsWith("app:", StringComparison.Ordinal) && !appKnown
            || key.StartsWith("gpu:", StringComparison.Ordinal) && !gpuKnown).ToList();
        if (kept.Count != current.Count)
            _preferences.Update(p => p with { DismissedNotifications = kept });
    }

    // L'heure d'un événement dans une clé : en UTC, au format « O » ; sans heure (une mise de côté d'avant l'enregistrement des heures), rien.
    private static string Stamp(DateTimeOffset? at) => at?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "";

    public void Dispose()
    {
        _disposed = true;
        _link.Changed -= OnLinkChanged;
        StopPolling();
    }
}
