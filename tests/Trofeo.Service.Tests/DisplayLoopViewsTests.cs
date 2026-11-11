using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>La boucle crée les cases de toutes les vues mais ne dessine que la vue affichée.</summary>
public sealed class DisplayLoopViewsTests : IDisposable
{
    private static readonly Guid V1 = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid V2 = new("22220000-0000-0000-0000-000000000002");
    private static readonly Guid V3 = new("33330000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = new("11110000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("22220000-0000-0000-0000-0000000000a2");
    private static readonly Guid P3 = new("33330000-0000-0000-0000-0000000000a3");

    /// <summary>Un module cachable et commandable qui note ce qu'on lui fait.</summary>
    private sealed class ViewProbe(Guid instanceId) : IModule, IHideableModule, ICommandableModule
    {
        private int _renders;
        private int _starts;
        private int _disposed;

        public Guid InstanceId { get; } = instanceId;
        public int Renders => Volatile.Read(ref _renders);
        public int Starts => Volatile.Read(ref _starts);
        public int Disposed => Volatile.Read(ref _disposed);
        public List<(bool Visible, ModuleRenderContext Place)> Announcements { get; } = [];
        public bool IsAnimating { get; set; }
        public ModuleStatus Status => new(ModuleHealth.Online);
        public ModuleDescriptor Descriptor { get; } = new("view", "view", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public event EventHandler? Invalidated;
        public event EventHandler? StatusChanged;

        public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);
        public void ChangeStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

        public Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            return Task.CompletedTask;
        }

        public void Render(SKCanvas canvas, ModuleRenderContext ctx) => Interlocked.Increment(ref _renders);
        public void SetVisible(bool visible, ModuleRenderContext place) => Announcements.Add((visible, place));
        public bool HandleCommand(ModuleCommand command) => true;
        public void Dispose() => Interlocked.Increment(ref _disposed);
    }

    private sealed class ViewProbeFactory : IModuleFactory
    {
        public Dictionary<Guid, ViewProbe> Created { get; } = [];
        public ModuleDescriptor Descriptor { get; } = new("view", "view", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context)
        {
            var probe = new ViewProbe(context.InstanceId);
            lock (Created) Created[context.InstanceId] = probe;
            return probe;
        }
    }

    private readonly ViewProbeFactory _factory = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashboard-views-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // un fichier encore ouvert ne fait pas échouer le test
        }
    }

    private static ModulePlacement ViewPlace(Guid id, int columns = 4, int rows = 2, int column = 0, int row = 0) =>
        Place(id, "view", column, row, columns, rows);

    /// <summary>Trois vues d'une case chacune ; la deuxième est plus large pour qu'on la reconnaisse.</summary>
    private static DashboardLayout ThreeViews() => new(
        180,
        [
            new LayoutView(V1, "Vue 1", [ViewPlace(P1)]),
            new LayoutView(V2, "Vue 2", [ViewPlace(P2, columns: 6, rows: 3, column: 2, row: 1)]),
            new LayoutView(V3, "Vue 3", [ViewPlace(P3)]),
        ]);

    private LoopRig Rig(DashboardLayout? layout = null, DisplayedViewStore? store = null)
    {
        var rig = new LoopRig(layout ?? ThreeViews(), extraFactories: [_factory], viewStore: store);
        _disposables.Add(rig);
        return rig;
    }

    private DisplayedViewStore Store(out string path)
    {
        Directory.CreateDirectory(_directory);
        path = Path.Combine(_directory, "vue-affichee.json");
        return new DisplayedViewStore(path, new FakeTimeProvider());
    }

    private static void TickAfter(LoopRig rig, TimeSpan wait)
    {
        rig.Time.Advance(wait); // laisse passer le plafond d'images
        rig.Loop.Tick();
    }

    private static LoopConfig Config(DashboardLayout layout) =>
        new(layout, Themes.ClassiqueDefinition, new ColorBackground(SKColors.Black));

    [Fact]
    public void AllCasesOfAllViews_AreStarted()
    {
        var rig = Rig();

        rig.Loop.Start(CancellationToken.None);

        Assert.Equal(3, _factory.Created.Count);
        Assert.All(_factory.Created.Values, probe => Assert.Equal(1, probe.Starts));
        Assert.Equal(3, rig.Loop.Status.Modules.Count);
    }

    [Fact]
    public void OnlyTheCurrentView_IsComposed()
    {
        var rig = Rig();
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Equal(1, _factory.Created[P1].Renders);
        Assert.Equal(0, _factory.Created[P2].Renders);
        Assert.Equal(0, _factory.Created[P3].Renders);
        Assert.Equal(V1, rig.Loop.CurrentViewId);
    }

    [Fact]
    public void HiddenAnimatingModule_DoesNotRaiseTheFrameRate()
    {
        static int FramesOver10Seconds(LoopRig rig, Action? beforeStart = null)
        {
            rig.AddHealthyScreen(acks: 400);
            beforeStart?.Invoke();
            for (var i = 0; i < 1000; i++) // 10 s simulées à 10 ms
            {
                rig.Loop.Tick();
                rig.Time.Advance(TimeSpan.FromMilliseconds(10));
            }

            return rig.Loop.FramesSent;
        }

        var alone = FramesOver10Seconds(Rig(DashboardLayout.SingleView(180, [ViewPlace(P1)])));

        var withHidden = Rig();
        var frames = FramesOver10Seconds(withHidden, () => _factory.Created[P2].IsAnimating = true);

        Assert.Equal(alone, frames);
        Assert.Equal(0, _factory.Created[P2].Renders);
    }

    [Fact]
    public void HiddenModuleInvalidated_RedrawsOnlyWhenItsViewIsShown()
    {
        var rig = Rig();
        rig.AddHealthyScreen(acks: 50);
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.FramesSent);

        _factory.Created[P2].Invalidate();
        _factory.Created[P2].ChangeStatus();
        TickAfter(rig, TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, rig.Loop.FramesSent); // rien à redessiner : l'écran n'a pas été réveillé
        Assert.Equal(0, _factory.Created[P2].Renders);

        Assert.True(rig.Loop.ShowView(V2, +1));
        TickAfter(rig, TimeSpan.FromMilliseconds(500));

        Assert.Equal(2, rig.Loop.FramesSent);
        Assert.Equal(1, _factory.Created[P2].Renders);
        Assert.Equal(1, _factory.Created[P1].Renders);
    }

    [Fact]
    public void ShowAdjacentView_Wraps()
    {
        var rig = Rig();

        Assert.True(rig.Loop.ShowAdjacentView(+1));
        Assert.Equal(V2, rig.Loop.CurrentViewId);
        Assert.True(rig.Loop.ShowAdjacentView(+1));
        Assert.Equal(V3, rig.Loop.CurrentViewId);
        Assert.True(rig.Loop.ShowAdjacentView(+1));
        Assert.Equal(V1, rig.Loop.CurrentViewId); // après la dernière, la première
        Assert.True(rig.Loop.ShowAdjacentView(-1));
        Assert.Equal(V3, rig.Loop.CurrentViewId); // avant la première, la dernière
        Assert.Equal(-1, rig.Loop.LastViewDirection);
    }

    [Fact]
    public void ShowAdjacentView_SingleView_DoesNothing()
    {
        var rig = Rig(DashboardLayout.SingleView(180, [ViewPlace(P1)]));
        var only = rig.Loop.CurrentViewId;

        Assert.False(rig.Loop.ShowAdjacentView(+1));
        Assert.Equal(only, rig.Loop.CurrentViewId);
    }

    [Fact]
    public void ShowViewAt_MissingIndex_DoesNothing()
    {
        var rig = Rig();
        var changes = new List<Guid>();
        rig.Loop.ViewChanged += changes.Add;

        Assert.False(rig.Loop.ShowViewAt(3));
        Assert.False(rig.Loop.ShowViewAt(-1));
        Assert.Equal(V1, rig.Loop.CurrentViewId);
        Assert.Empty(changes);

        Assert.True(rig.Loop.ShowViewAt(2));
        Assert.Equal(V3, rig.Loop.CurrentViewId);
        Assert.Equal([V3], changes);
    }

    [Fact]
    public void ShowView_UnknownOrCurrent_ReportsWithoutChanging()
    {
        var rig = Rig();
        var changes = new List<Guid>();
        rig.Loop.ViewChanged += changes.Add;

        Assert.False(rig.Loop.ShowView(Guid.NewGuid(), 0));
        Assert.True(rig.Loop.ShowView(V1, 0)); // déjà affichée
        Assert.Empty(changes);
        Assert.Equal(V1, rig.Loop.CurrentViewId);
    }

    [Fact]
    public async Task ReloadWithoutTheCurrentView_ShowsTheNeighbourView()
    {
        var rig = Rig();
        rig.AddHealthyScreen(acks: 50);
        var changes = new List<Guid>();
        rig.Loop.ViewChanged += changes.Add;
        Assert.True(rig.Loop.ShowView(V2, +1));

        // La vue affichée disparaît : la suivante prend sa place.
        var withoutSecond = new DashboardLayout(180, [.. ThreeViews().Views.Where(v => v.Id != V2)]);
        var task = rig.Loop.ApplyAsync(Config(withoutSecond));
        TickAfter(rig, TimeSpan.FromSeconds(1));
        Assert.Empty(await task);

        Assert.Equal(V3, rig.Loop.CurrentViewId);
        Assert.Equal([V2, V3], changes);
        Assert.Equal(1, _factory.Created[P2].Disposed);

        // La dernière vue disparaît à son tour : la précédente.
        var withoutLast = new DashboardLayout(180, [withoutSecond.Views[0]]);
        task = rig.Loop.ApplyAsync(Config(withoutLast));
        TickAfter(rig, TimeSpan.FromSeconds(1));
        Assert.Empty(await task);

        Assert.Equal(V1, rig.Loop.CurrentViewId);
        Assert.Equal([V2, V3, V1], changes);
    }

    [Fact]
    public async Task ReloadKeepingTheCurrentView_StaysOnIt()
    {
        var rig = Rig();
        rig.AddHealthyScreen(acks: 50);
        Assert.True(rig.Loop.ShowView(V3, +1));
        var changes = new List<Guid>();
        rig.Loop.ViewChanged += changes.Add;

        var task = rig.Loop.ApplyAsync(Config(ThreeViews()));
        TickAfter(rig, TimeSpan.FromSeconds(1));
        await task;

        Assert.Equal(V3, rig.Loop.CurrentViewId);
        Assert.Empty(changes);
    }

    [Fact]
    public void StartWithStoredViewMissing_ShowsTheFirstView()
    {
        var store = Store(out var path);
        File.WriteAllText(path, $$"""{"ViewId":"{{Guid.NewGuid()}}"}""");
        var rig = Rig(store: store);
        var changes = new List<Guid>();
        rig.Loop.ViewChanged += changes.Add;

        Assert.Equal(V1, rig.Loop.CurrentViewId);
        Assert.Empty(changes);
    }

    [Fact]
    public void StartWithStoredView_ShowsIt_WithoutAnnouncingAChange()
    {
        var store = Store(out var path);
        File.WriteAllText(path, $$"""{"ViewId":"{{V3}}"}""");
        var rig = Rig(store: store);
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Equal(V3, rig.Loop.CurrentViewId);
        Assert.Equal(1, _factory.Created[P3].Renders);
        Assert.Equal(0, _factory.Created[P1].Renders);
    }

    [Fact]
    public void ShowView_SavesTheViewInTheStore_AndDisposeFlushesIt()
    {
        var store = Store(out var path);
        var rig = Rig(store: store);

        Assert.True(rig.Loop.ShowView(V2, +1));
        Assert.Equal(V2, store.Load()); // la première écriture est immédiate

        Assert.True(rig.Loop.ShowView(V3, +1)); // dans la même seconde : en attente
        Assert.Equal(V2, store.Load());
        rig.Loop.Dispose(); // l'arrêt écrit la dernière valeur

        Assert.Equal(V3, store.Load());
    }

    [Fact]
    public void FindCommandTarget_IgnoresCasesOfHiddenViews()
    {
        var rig = Rig();

        // Une cible nommée dans une vue cachée rend null : le raccourci n'agit pas (pas de repli sur la vue affichée).
        Assert.Null(rig.Loop.FindCommandTarget(P2));
        Assert.Same(_factory.Created[P1], rig.Loop.FindCommandTarget(P1));
        Assert.Same(_factory.Created[P1], rig.Loop.FindCommandTarget(null));
        Assert.Same(_factory.Created[P1], rig.Loop.FindCommandTarget(Guid.NewGuid())); // identifiant inconnu : le repli reste

        rig.Loop.ShowView(V2, +1);

        Assert.Null(rig.Loop.FindCommandTarget(P1));
        Assert.Same(_factory.Created[P2], rig.Loop.FindCommandTarget(P2));
        Assert.Same(_factory.Created[P2], rig.Loop.FindCommandTarget(null));
    }

    [Fact]
    public void FindCommandTarget_OnlyHiddenViewsCommandable_ReturnsNull()
    {
        var layout = new DashboardLayout(
            180,
            [
                new LayoutView(V1, "Vue 1", [Place(A, "plain")]),
                new LayoutView(V2, "Vue 2", [ViewPlace(P2)]),
            ]);
        var rig = new LoopRig(layout, extraFactories: [_factory, new PlainFactory()]);
        _disposables.Add(rig);

        Assert.Null(rig.Loop.FindCommandTarget(null));
        Assert.Null(rig.Loop.FindCommandTarget(P2));
    }

    [Fact]
    public void HideableModule_IsToldItsVisibilityAndPlace()
    {
        var rig = Rig();
        var hidden = _factory.Created[P2];
        var shown = _factory.Created[P1];

        // À la création : la case de la vue cachée sait qu'elle l'est, et où elle serait.
        var (visible, place) = Assert.Single(hidden.Announcements);
        Assert.False(visible);
        Assert.Equal(new GridSize(6, 3), place.Size);
        var grid = Themes.ClassiqueDefinition.Grid;
        var expected = GridGeometry.ModuleRect(2, 1, 6, 3, grid);
        Assert.Equal(SKRect.Create(expected.Left, expected.Top, expected.Width, expected.Height), place.Bounds);
        Assert.Equal(Themes.ClassiqueDefinition.Theme, place.Theme);
        Assert.True(Assert.Single(shown.Announcements).Visible);

        // Au changement de vue : l'une se cache, l'autre apparaît.
        rig.Loop.ShowView(V2, +1);

        Assert.True(hidden.Announcements.Last().Visible);
        Assert.Equal(2, hidden.Announcements.Count);
        Assert.False(shown.Announcements.Last().Visible);
        Assert.Equal(new GridSize(4, 2), shown.Announcements.Last().Place.Size);
        Assert.Single(_factory.Created[P3].Announcements); // la troisième n'a pas changé
    }

    [Fact]
    public async Task HideableModule_UnchangedByReload_IsNotToldAgain()
    {
        var rig = Rig();
        rig.AddHealthyScreen(acks: 50);

        var task = rig.Loop.ApplyAsync(Config(ThreeViews()));
        TickAfter(rig, TimeSpan.FromSeconds(1));
        await task;

        Assert.All(_factory.Created.Values, probe => Assert.Single(probe.Announcements));
    }

    private sealed class PlainFactory : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new("plain", "plain", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context) => new PlainModule();
    }

    /// <summary>Un module qui n'accepte pas les commandes.</summary>
    private sealed class PlainModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("plain", "plain", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public ModuleStatus Status => new(ModuleHealth.Online);
        public bool IsAnimating => false;
        public event EventHandler? Invalidated { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public void Render(SKCanvas canvas, ModuleRenderContext ctx) { }
        public void Dispose() { }
    }
}
