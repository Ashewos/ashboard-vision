using Microsoft.Extensions.Time.Testing;
using Trofeo.Control;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>La liste des notifications de la cloche : composée par la fenêtre à partir de la liaison, sans vue.</summary>
public sealed class NotificationsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-notifications-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<NotificationsViewModel> _models = [];

    public NotificationsTests()
    {
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        _link.Updates = Updates(LinkUpdateState.UpToDate, null);
    }

    public void Dispose()
    {
        foreach (var model in _models)
            model.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private PreferencesStore Store(IReadOnlyList<string>? dismissed = null, string? path = null) =>
        new(new EditorPreferences(DismissedNotifications: dismissed), path);

    private NotificationsViewModel Open(PreferencesStore? store = null)
    {
        var model = new NotificationsViewModel(_link, store ?? Store(), _time);
        _models.Add(model);
        return model;
    }

    private static UpdateInfo Updates(LinkUpdateState state, string? version) =>
        new(state, version, false, null, null, LinkUpdateChannel.Stable, false);

    private static CommunityModuleInfo Module(
        string id, string name = "Météo", string? updateVersion = null, LinkCommunityState state = LinkCommunityState.Ready,
        int stops = 0, DateTimeOffset? lastStop = null, DateTimeOffset? setAside = null) =>
        new(id, name, "Alex", "Un module.", "1.0", state, null, [], [], false, updateVersion, null,
            StopCount: stops, LastStopAt: lastStop, SetAsideAt: setAside);

    private void SetModules(params CommunityModuleInfo[] modules) =>
        _link.Community = new CommunityOverview(modules, @"C:\Modules", false, [], false);

    private void SetGpu(LinkAccelerationProblem? kind, string? problem = null, DateTimeOffset? at = null) =>
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with
        {
            Acceleration = new AccelerationInfo(true, true, "Carte", true, false, problem, [], kind, at),
        });

    private int Reads => _link.ModuleCalls.Count(c => c == "community");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "L'état attendu n'est pas arrivé.");
    }

    // ---- Le contenu des notifications ----

    [Fact]
    public void AppUpdate_Notified()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        var model = Open();

        var item = Assert.Single(model.Items);
        Assert.Equal("app:1.2.0", item.Key);
        Assert.Equal(NotificationKind.News, item.Kind);
        Assert.Equal("Ashboard Vision 1.2.0 est disponible.", item.Text);
        Assert.Equal("Voir la mise à jour", item.LinkText);
        Assert.Equal(NotificationTarget.Settings, item.Target);
        Assert.Null(item.At);
    }

    [Fact]
    public void AppUpdate_NotAvailable_NotNotified()
    {
        foreach (var state in new[] { LinkUpdateState.UpToDate, LinkUpdateState.Checking, LinkUpdateState.NoStableYet, LinkUpdateState.Failed })
        {
            _link.SetUpdates(Updates(state, "1.2.0"));
            Assert.Empty(Open().Items);
        }
    }

    [Fact]
    public void ModuleUpdate_Notified()
    {
        SetModules(Module("alex.meteo", updateVersion: "1.3"));
        var model = Open();

        var item = Assert.Single(model.Items);
        Assert.Equal("update:alex.meteo:1.3", item.Key);
        Assert.Equal(NotificationKind.News, item.Kind);
        Assert.Equal("Météo 1.3 est disponible.", item.Text);
        Assert.Equal("Voir le module", item.LinkText);
        Assert.Equal(NotificationTarget.CommunityModules, item.Target);
    }

    [Fact]
    public async Task ModuleStopped_OnceThenCount()
    {
        SetModules(Module("alex.meteo", stops: 1, lastStop: T0));
        var model = Open();

        var first = Assert.Single(model.Items);
        Assert.Equal($"stop:alex.meteo:{T0.UtcDateTime:O}", first.Key);
        Assert.Equal(NotificationKind.Problem, first.Kind);
        Assert.Equal("Météo s'est arrêté brusquement et a été relancé.", first.Text);
        Assert.Equal("Voir le module", first.LinkText);
        Assert.Equal(NotificationTarget.CommunityModules, first.Target);
        Assert.Equal(T0, first.At);

        SetModules(Module("alex.meteo", stops: 2, lastStop: T0.AddMinutes(5)));
        await model.RefreshCommunityAsync();

        var second = Assert.Single(model.Items);
        Assert.Equal("Météo s'est arrêté brusquement et a été relancé (2 fois).", second.Text);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void ModuleStopped_KeyIsUtc()
    {
        // Une même heure donne la même clé, quel que soit le décalage horaire avec lequel le service l'écrit.
        SetModules(Module("a", stops: 1, lastStop: new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("stop:a:2026-11-17T10:00:00.0000000Z", Assert.Single(Open().Items).Key);
    }

    [Fact]
    public void SetAside_ReplacesStop()
    {
        SetModules(Module("alex.meteo", state: LinkCommunityState.SetAside, stops: 3, lastStop: T0, setAside: T0.AddMinutes(1)));
        var model = Open();

        var item = Assert.Single(model.Items);
        Assert.Equal($"aside:alex.meteo:{T0.AddMinutes(1).UtcDateTime:O}", item.Key);
        Assert.Equal(NotificationKind.Problem, item.Kind);
        Assert.Equal("Météo a été mis de côté après des arrêts répétés.", item.Text);
        Assert.Equal(NotificationTarget.CommunityModules, item.Target);
        Assert.Equal(T0.AddMinutes(1), item.At);
    }

    [Fact]
    public void SetAside_WithoutTime_HasKeyWithoutTime()
    {
        SetModules(Module("alex.meteo", state: LinkCommunityState.SetAside));

        var item = Assert.Single(Open().Items);
        Assert.Equal("aside:alex.meteo:", item.Key);
        Assert.Null(item.At);
    }

    [Fact]
    public void GpuFailure_Notified_SlowAndGoneNot()
    {
        SetGpu(LinkAccelerationProblem.Failure, "La carte graphique a eu un souci.", T0);
        var model = Open();

        var item = Assert.Single(model.Items);
        Assert.Equal($"gpu:{T0.UtcDateTime:O}", item.Key);
        Assert.Equal(NotificationKind.Problem, item.Kind);
        Assert.Equal("La carte graphique a eu un souci.", item.Text);
        Assert.Equal("Voir les réglages", item.LinkText);
        Assert.Equal(NotificationTarget.Settings, item.Target);

        SetGpu(LinkAccelerationProblem.Slow, "Trop lente.", T0);
        Assert.Empty(model.Items);
        SetGpu(LinkAccelerationProblem.Gone, "Absente.", T0);
        Assert.Empty(model.Items);
        SetGpu(null);
        Assert.Empty(model.Items);
    }

    // ---- La croix ----

    [Fact]
    public void Dismiss_HidesAndPersists()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        var path = Path.Combine(_dir, "settings-app.json");
        var store = Store(path: path);
        var model = Open(store);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        model.Dismiss("app:1.2.0");

        Assert.Empty(model.Items);
        Assert.Equal(0, model.Count);
        Assert.True(model.IsEmpty);
        Assert.Contains("app:1.2.0", store.Current.DismissedNotifications!);
        Assert.Contains(nameof(NotificationsViewModel.Items), changes);
        Assert.Contains("app:1.2.0", EditorPreferences.Load(path).DismissedNotifications!);
    }

    [Fact]
    public void Dismissed_StaysHiddenWhileTheKeyIsTheSame()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        var model = Open(Store(["app:1.2.0"]));
        Assert.Empty(model.Items);

        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task Dismissed_ComesBackWhenTheKeyChanges()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(Module("alex.meteo", stops: 1, lastStop: T0));
        var store = Store();
        var model = Open(store);
        model.Dismiss("app:1.2.0");
        model.Dismiss($"stop:alex.meteo:{T0.UtcDateTime:O}");
        Assert.Empty(model.Items);

        // Une autre version sort.
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.3.0"));
        Assert.Equal("app:1.3.0", Assert.Single(model.Items).Key);

        // Un nouvel arrêt.
        SetModules(Module("alex.meteo", stops: 2, lastStop: T0.AddMinutes(10)));
        await model.RefreshCommunityAsync();
        Assert.Equal(2, model.Items.Count);
        Assert.Contains(model.Items, i => i.Text.EndsWith("(2 fois)."));
    }

    [Fact]
    public async Task Resolved_Disappears_AndKeyIsPruned()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(
            Module("a", state: LinkCommunityState.SetAside, setAside: T0),
            Module("b", updateVersion: "2.0"));
        SetGpu(LinkAccelerationProblem.Failure, "Panne.", T0);
        _link.Updates = Updates(LinkUpdateState.Available, "1.2.0");
        var store = Store(["app:1.2.0", $"aside:a:{T0.UtcDateTime:O}", "update:b:2.0", $"gpu:{T0.UtcDateTime:O}"]);
        var model = Open(store);
        Assert.Empty(model.Items);
        Assert.Equal(4, store.Current.DismissedNotifications!.Count);

        // La mise à jour est installée, le module est relancé, la mise à jour du module est faite, la carte va mieux.
        SetModules(Module("a"), Module("b"));
        await model.RefreshCommunityAsync();
        _link.Updates = Updates(LinkUpdateState.UpToDate, null);
        SetGpu(null);

        Assert.Empty(model.Items);
        Assert.Empty(store.Current.DismissedNotifications!);

        // Elles reviennent plus tard : ce sont des événements nouveaux, rien n'est resté supprimé.
        SetModules(Module("a", state: LinkCommunityState.SetAside, setAside: T0));
        await model.RefreshCommunityAsync();
        Assert.Single(model.Items);
    }

    [Fact]
    public async Task Prune_KeepsOnlyKeysStillProduced()
    {
        SetModules(Module("a", stops: 1, lastStop: T0));
        var current = $"stop:a:{T0.UtcDateTime:O}";
        var store = Store([current, "app:0.9.0"]);
        var model = Open(store);
        await model.RefreshCommunityAsync();

        Assert.Equal([current], store.Current.DismissedNotifications);
    }

    // ---- L'ordre et la cloche ----

    [Fact]
    public void Order_ProblemsFirstNewestFirst_ThenApp_ThenModulesByName()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(
            Module("z", name: "Zèbre", updateVersion: "1.0"),
            Module("a", name: "Agenda", updateVersion: "2.0"),
            Module("old", name: "Ancien", stops: 1, lastStop: T0),
            Module("new", name: "Récent", stops: 1, lastStop: T0.AddHours(2)),
            Module("aside", name: "Côté", state: LinkCommunityState.SetAside, setAside: T0.AddHours(1)));
        SetGpu(LinkAccelerationProblem.Failure, "Carte en panne.", T0.AddHours(3));
        var model = Open();

        Assert.Equal(
            [
                "Carte en panne.",
                "Récent s'est arrêté brusquement et a été relancé.",
                "Côté a été mis de côté après des arrêts répétés.",
                "Ancien s'est arrêté brusquement et a été relancé.",
                "Ashboard Vision 1.2.0 est disponible.",
                "Agenda 2.0 est disponible.",
                "Zèbre 1.0 est disponible.",
            ],
            model.Items.Select(i => i.Text));
    }

    [Fact]
    public void Badge_CountAndHasProblem()
    {
        var model = Open();
        Assert.Equal(0, model.Count);
        Assert.True(model.IsEmpty);
        Assert.False(model.HasProblem);

        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        Assert.Equal(1, model.Count);
        Assert.False(model.IsEmpty);
        Assert.False(model.HasProblem);          // une nouveauté : la pastille est de la couleur d'accent

        SetGpu(LinkAccelerationProblem.Failure, "Panne.", T0);
        Assert.Equal(2, model.Count);
        Assert.True(model.HasProblem);           // un problème : la pastille est rouge

        model.Dismiss($"gpu:{T0.UtcDateTime:O}");
        Assert.Equal(1, model.Count);
        Assert.False(model.HasProblem);          // un problème supprimé ne rougit plus la cloche
    }

    [Fact]
    public void Open_RaisesNavigate()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(Module("a", updateVersion: "1.3"));
        var model = Open();
        var targets = new List<NotificationTarget>();
        model.Navigate += targets.Add;

        foreach (var item in model.Items.ToList())
            model.Open(item);

        Assert.Equal([NotificationTarget.Settings, NotificationTarget.CommunityModules], targets.Order());
    }

    // ---- La relecture des modules ----

    [Fact]
    public async Task Community_ReadAtConnectThenEvery30s()
    {
        _link.Set(LinkState.Connecting);
        var model = Open();
        Assert.Equal(0, Reads);                  // pas connecté : pas de lecture

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        Assert.Equal(1, Reads);                  // une lecture à la connexion

        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(1, Reads);                  // aucune à 29 s

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Reads == 2);  // une autre à 30 s

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => Reads == 3);
        GC.KeepAlive(model);
    }

    [Fact]
    public async Task Community_StopsReadingWhenTheLinkIsLost()
    {
        var model = Open();
        Assert.Equal(1, Reads);

        _link.Set(LinkState.ServiceStopped);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, Reads);

        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        Assert.Equal(2, Reads);                  // relue à la reconnexion
        GC.KeepAlive(model);
    }

    [Fact]
    public void Disconnected_NoItems()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(Module("a", updateVersion: "1.3"));
        var model = Open();
        Assert.Equal(2, model.Items.Count);

        _link.Set(LinkState.ServiceStopped);

        Assert.Empty(model.Items);
        Assert.True(model.IsEmpty);
        Assert.Equal(0, model.Count);
        Assert.False(model.HasProblem);

        // Un modèle ouvert sans connexion n'a rien non plus, même si le faux lien garde des données.
        Assert.Empty(Open().Items);
    }

    // ---- Les points de vigilance ----

    [Fact]
    public async Task Prune_SkippedWhileDisconnectedOrOverviewFailed()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        SetModules(Module("a", stops: 1, lastStop: T0));
        SetGpu(LinkAccelerationProblem.Failure, "Panne.", T0);
        var keys = new[] { "app:1.2.0", $"stop:a:{T0.UtcDateTime:O}", $"gpu:{T0.UtcDateTime:O}" };
        var store = Store(keys);
        var model = Open(store);
        Assert.Empty(model.Items);
        Assert.Equal(keys, store.Current.DismissedNotifications);

        // La liaison est coupée : rien n'est produit, rien n'est élagué.
        _link.Set(LinkState.ServiceStopped);
        Assert.Equal(keys, store.Current.DismissedNotifications);

        // Reconnexion, mais la lecture des modules échoue : les notifications des modules ne sont pas jugées disparues.
        _link.CommunityFailure = new ControlException("Le service ne répond pas.");
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        await model.RefreshCommunityAsync();
        Assert.Equal(keys, store.Current.DismissedNotifications);

        // Les mises à jour ne sont pas encore connues : rien n'est élagué non plus.
        _link.CommunityFailure = null;
        _link.Updates = null;
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        await model.RefreshCommunityAsync();
        Assert.Equal(keys, store.Current.DismissedNotifications);

        // Tout est lu : la panne de la carte graphique est finie, donc sa clé est élaguée, et seulement elle.
        _link.Updates = Updates(LinkUpdateState.Available, "1.2.0");
        SetGpu(null);
        Assert.Equal(keys.Take(2), store.Current.DismissedNotifications);
    }

    [Fact]
    public void Prune_KeepsAppKeyWhileTheUpdateCheckIsRunning()
    {
        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        var store = Store(["app:1.2.0"]);
        var model = Open(store);

        // Une recherche en cours (ou ratée) ne dit pas que la mise à jour a disparu.
        _link.SetUpdates(Updates(LinkUpdateState.Checking, null));
        _link.SetUpdates(Updates(LinkUpdateState.Failed, null));
        Assert.Equal(["app:1.2.0"], store.Current.DismissedNotifications);

        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        Assert.Empty(model.Items);               // la croix a tenu
    }

    [Fact]
    public void Preferences_LegacyFile_HasNoDismissed()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings-app.json");
        File.WriteAllText(path, """{"firstRunDone": true, "realTimePreview": false, "followView": true}""");

        var preferences = EditorPreferences.Load(path);
        Assert.True(preferences.FirstRunDone);
        Assert.Null(preferences.DismissedNotifications);

        _link.SetUpdates(Updates(LinkUpdateState.Available, "1.2.0"));
        var model = Open(new PreferencesStore(preferences, path));
        Assert.Single(model.Items);              // la liste des croix est vide : rien n'est caché, rien ne plante
        model.Dismiss("app:1.2.0");
        Assert.Equal(["app:1.2.0"], EditorPreferences.Load(path).DismissedNotifications);
    }

    [Fact]
    public void ModuleName_IsCleaned()
    {
        SetModules(
            Module("a", name: "Mé\ntéo\u202E\u0007\r évil", stops: 1, lastStop: T0),
            Module("b", name: "  \n\u200B ", updateVersion: "1.\n3"));
        var model = Open();

        Assert.Contains(model.Items, i => i.Text == "Mé téo évil s'est arrêté brusquement et a été relancé.");
        // Un nom vide une fois nettoyé : l'identifiant le remplace ; la version est nettoyée elle aussi.
        Assert.Contains(model.Items, i => i.Text == "b 1. 3 est disponible.");
        Assert.All(model.Items, i => Assert.DoesNotContain('\n', i.Text));
    }

    [Fact]
    public async Task UninstalledModule_NotificationDisappears()
    {
        SetModules(
            Module("a", stops: 1, lastStop: T0),
            Module("b", name: "Agenda", updateVersion: "2.0"));
        var stopKey = $"stop:a:{T0.UtcDateTime:O}";
        var store = Store([stopKey, "update:b:2.0"]);
        var model = Open(store);
        Assert.Empty(model.Items);

        SetModules(Module("c", name: "Autre", updateVersion: "1.0"));
        await model.RefreshCommunityAsync();

        Assert.Equal("update:c:1.0", Assert.Single(model.Items).Key);
        Assert.Empty(store.Current.DismissedNotifications!);

        // Et sans croix : une notification d'arrêt disparaît avec son module.
        SetModules(Module("a", stops: 1, lastStop: T0));
        await model.RefreshCommunityAsync();
        Assert.Contains(model.Items, i => i.Key == stopKey);
        SetModules();
        await model.RefreshCommunityAsync();
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task StaleCommunityRead_IsDropped()
    {
        var slow = new TaskCompletionSource<CommunityOverview>();
        var calls = 0;
        _link.OnCommunity = () => calls++ == 0 ? slow.Task : Task.FromResult(_link.Community);
        SetModules(Module("a", updateVersion: "1.3"));

        // La première lecture (celle de la connexion) traîne ; une seconde, plus récente, répond avant elle.
        var model = Open();
        Assert.Empty(model.Items);
        await model.RefreshCommunityAsync();
        Assert.Single(model.Items);
        slow.SetResult(new CommunityOverview([], @"C:\Modules", false, [], false));
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.Single(model.Items);              // la réponse ancienne n'a pas écrasé la récente
    }
}
