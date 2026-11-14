using SkiaSharp;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Device;
using Trofeo.ModuleKit;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>
/// L'aperçu d'une vue qui n'est pas affichée : composée seulement tant qu'un abonné la demande, jamais envoyée à l'écran, sans
/// effet sur son rythme ; ses cases de module complémentaire sont visibles pour leur hôte le temps de l'aperçu.
/// </summary>
public sealed class DisplayLoopHiddenPreviewTests : IDisposable
{
    private static readonly Guid V1 = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid V2 = new("22220000-0000-0000-0000-000000000002");
    private static readonly Guid P1 = new("11110000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("22220000-0000-0000-0000-0000000000a2");

    /// <summary>Un module cachable : compte ses dessins et note ce qu'on lui annonce.</summary>
    private sealed class Probe(Guid instanceId) : IModule, IHideableModule
    {
        private int _renders;

        public Guid InstanceId { get; } = instanceId;
        public int Renders => Volatile.Read(ref _renders);
        public List<bool> Visibility { get; } = [];
        public bool IsAnimating { get; set; }
        public ModuleStatus Status => new(ModuleHealth.Online);
        public ModuleDescriptor Descriptor { get; } = new("view", "view", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public event EventHandler? Invalidated { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public void Render(SKCanvas canvas, ModuleRenderContext ctx) => Interlocked.Increment(ref _renders);
        public void SetVisible(bool visible, ModuleRenderContext place) => Visibility.Add(visible);
        public void Dispose()
        {
        }
    }

    private sealed class ProbeFactory : IModuleFactory
    {
        public Dictionary<Guid, Probe> Created { get; } = [];
        public ModuleDescriptor Descriptor { get; } = new("view", "view", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context)
        {
            var probe = new Probe(context.InstanceId);
            lock (Created) Created[context.InstanceId] = probe;
            return probe;
        }
    }

    private readonly ProbeFactory _factory = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    private static DashboardLayout TwoViews() => new(
        180,
        [
            new LayoutView(V1, "Vue 1", [Place(P1, "view")]),
            new LayoutView(V2, "Vue 2", [Place(P2, "view", columns: 6, rows: 3, column: 2, row: 1)]),
        ]);

    private LoopRig Rig(bool screen = true)
    {
        var rig = new LoopRig(TwoViews(), extraFactories: [_factory]);
        _disposables.Add(rig);
        if (screen) rig.AddHealthyScreen(acks: 400);
        return rig;
    }

    private static List<PreviewFrame> Subscribe(LoopRig rig, Guid? view, out IDisposable subscription, int fps = 4)
    {
        var frames = new List<PreviewFrame>();
        subscription = rig.Loop.SubscribePreview(fps, view, f => { lock (frames) frames.Add(f); });
        return frames;
    }

    private static void Run(LoopRig rig, int milliseconds, int stepMs = 10)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += stepMs)
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(stepMs));
        }
    }

    [Fact]
    public void HiddenView_IsComposedOnlyWhileSubscribed()
    {
        var rig = Rig();
        rig.Loop.Tick();
        Assert.Equal(0, _factory.Created[P2].Renders); // personne ne regarde la vue cachée

        var frames = Subscribe(rig, V2, out var subscription);
        Run(rig, 2000);

        Assert.True(_factory.Created[P2].Renders >= 4, "la vue cachée est recomposée au rythme de l'aperçu");
        Assert.NotEmpty(frames);
        Assert.Equal(0xFF, frames[0].Jpeg[0]);
        Assert.Equal(180, frames[0].Rotation);

        subscription.Dispose();
        var rendersAtTheEnd = _factory.Created[P2].Renders;
        var framesAtTheEnd = frames.Count;
        Run(rig, 2000);

        Assert.Equal(rendersAtTheEnd, _factory.Created[P2].Renders); // plus personne : plus rien n'est composé
        Assert.Equal(framesAtTheEnd, frames.Count);
    }

    [Fact]
    public void HiddenView_FrameRateOfTheHiddenPreviewFollowsTheSubscriber()
    {
        var rig = Rig();
        var frames = Subscribe(rig, V2, out var subscription, fps: 2);
        using var _ = subscription;

        Run(rig, 5000);

        Assert.InRange(frames.Count, 9, 11); // 2 images par seconde pendant 5 s
    }

    [Fact]
    public void HiddenView_NeverReachesTheScreen_AndDoesNotChangeItsFrameRate()
    {
        static int FramesSent(LoopRig rig, ProbeFactory factory, bool hiddenPreview)
        {
            IDisposable? subscription = null;
            if (hiddenPreview)
            {
                factory.Created[P2].IsAnimating = true; // une vue cachée animée ne réveille pas l'écran
                Subscribe(rig, V2, out subscription, fps: 12);
            }

            Run(rig, 10_000);
            subscription?.Dispose();
            return rig.Loop.FramesSent;
        }

        var alone = FramesSent(Rig(), _factory, hiddenPreview: false);

        var factory = new ProbeFactory();
        var rig = new LoopRig(TwoViews(), extraFactories: [factory]);
        _disposables.Add(rig);
        rig.AddHealthyScreen(acks: 400);
        var withPreview = FramesSent(rig, factory, hiddenPreview: true);

        Assert.Equal(alone, withPreview);
        Assert.Equal(1, factory.Created[P1].Renders); // la vue affichée n'est pas redessinée pour autant
    }

    [Fact]
    public void HiddenView_NoScreen_IsComposedAloneWithoutDrawingTheDisplayedView()
    {
        var rig = Rig(screen: false);
        var frames = Subscribe(rig, V2, out var subscription);
        using var _ = subscription;

        Run(rig, 2000);

        Assert.NotEmpty(frames);
        Assert.True(_factory.Created[P2].Renders >= 1);
        Assert.Equal(0, _factory.Created[P1].Renders); // personne ne regarde la vue affichée : elle n'est pas dessinée
        Assert.Equal(0, rig.Loop.FramesSent);
        Assert.Null(rig.Screen.LastFrame);
    }

    [Fact]
    public void MissingView_GivesNoFrames_AndDoesNotSpinTheLoop()
    {
        var rig = Rig();
        var frames = Subscribe(rig, Guid.NewGuid(), out var subscription);
        using var _ = subscription;
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));

        var delay = rig.Loop.Tick();

        Assert.Empty(frames);
        Assert.True(delay > TimeSpan.Zero, "une vue inconnue ne fait pas tourner la boucle à vide");
    }

    [Fact]
    public void CommunityCases_OfAPreviewedHiddenView_AreVisibleOnlyWhileSubscribed()
    {
        var rig = Rig();
        var hidden = _factory.Created[P2];
        Assert.Equal([false], hidden.Visibility);

        var subscription = rig.Loop.SubscribePreview(4, V2, _ => { });
        Assert.Equal([false, true], hidden.Visibility);
        Assert.Equal([false], _factory.Created[P1].Visibility); // la vue affichée n'a rien reçu de nouveau (ni écran, ni témoin)

        subscription.Dispose();
        Assert.Equal([false, true, false], hidden.Visibility);
    }

    // ---- Les cases de la vue affichée : visibles seulement si quelqu'un peut les voir (écran prêt ou aperçu) ----

    [Fact]
    public void DisplayedView_NoScreenNoViewer_CommunityCasesStayHidden()
    {
        var rig = Rig(screen: false);
        var shown = _factory.Created[P1];

        Run(rig, 3000);

        Assert.Equal([false], shown.Visibility); // pas d'écran, personne ne regarde : l'hôte n'a pas à dessiner
        Assert.Equal([false], _factory.Created[P2].Visibility);
        Assert.Equal(0, shown.Renders);
    }

    [Fact]
    public void DisplayedView_NoScreen_ShowingAnotherViewKeepsTheCasesHidden()
    {
        var rig = Rig(screen: false);

        Assert.True(rig.Loop.ShowView(V2, +1));

        Assert.Equal([false], _factory.Created[P1].Visibility);
        Assert.Equal([false], _factory.Created[P2].Visibility); // affichée, mais ni écran ni témoin
    }

    [Fact]
    public void DisplayedView_ScreenBecomesReady_CommunityCasesBecomeVisible()
    {
        var rig = Rig();
        var shown = _factory.Created[P1];
        Assert.Equal([false], shown.Visibility); // l'écran n'est pas encore connecté

        rig.Loop.Tick(); // connexion de l'écran
        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.Equal([false, true], shown.Visibility);

        Run(rig, 2000);
        Assert.Equal([false, true], shown.Visibility); // rien ne bouge tant que l'écran reste là
        Assert.Equal([false], _factory.Created[P2].Visibility);

        rig.Screen.MarkLost("test");
        rig.Loop.Tick();
        Assert.Equal([false, true, false], shown.Visibility); // écran perdu et personne ne regarde : l'hôte se tait
    }

    [Fact]
    public void DisplayedView_PreviewSubscribed_CommunityCasesBecomeVisible_AndHiddenAgainOnUnsubscribe()
    {
        var rig = Rig(screen: false);
        var shown = _factory.Created[P1];

        var subscription = rig.Loop.SubscribePreview(4, null, _ => { }); // la fenêtre suit la vue affichée
        Assert.Equal([false, true], shown.Visibility);

        subscription.Dispose();
        Assert.Equal([false, true, false], shown.Visibility);

        var pinned = rig.Loop.SubscribePreview(4, V1, _ => { }); // ... ou la demande par son numéro
        Assert.Equal([false, true, false, true], shown.Visibility);

        pinned.Dispose();
        Assert.Equal([false, true, false, true, false], shown.Visibility);
    }

    [Fact]
    public void DisplayedView_HiddenViewPreviewOnly_DoesNotWakeTheDisplayedCases()
    {
        var rig = Rig(screen: false);

        var subscription = rig.Loop.SubscribePreview(4, V2, _ => { });
        using var _ = subscription;

        Assert.Equal([false], _factory.Created[P1].Visibility);
        Assert.Equal([false, true], _factory.Created[P2].Visibility); // seule la vue regardée s'ouvre
    }

    [Fact]
    public void DisplayedView_ScreenReady_SwitchingViewsAndReloadingNeverFlickers()
    {
        var rig = Rig();
        rig.Loop.Tick();
        var first = _factory.Created[P1];
        var second = _factory.Created[P2];

        Assert.True(rig.Loop.ShowView(V2, +1));
        Run(rig, 500);
        Assert.True(rig.Loop.ShowView(V1, -1));
        Run(rig, 500);

        Assert.Equal([false, true, false, true], first.Visibility);
        Assert.Equal([false, true, false], second.Visibility);
    }

    [Fact]
    public void CommunityCases_TwoSubscribersOfTheSameView_HideWhenTheLastOneLeaves()
    {
        var rig = Rig();
        var hidden = _factory.Created[P2];

        var first = rig.Loop.SubscribePreview(4, V2, _ => { });
        var second = rig.Loop.SubscribePreview(4, V2, _ => { });
        first.Dispose();
        Assert.Equal([false, true], hidden.Visibility); // il reste un abonné

        second.Dispose();
        Assert.Equal([false, true, false], hidden.Visibility);
    }

    [Fact]
    public void CommunityCases_OfAViewThatBecomesDisplayed_StayVisible()
    {
        var rig = Rig();
        rig.Loop.Tick(); // l'écran est prêt : la vue affichée est visible
        var hidden = _factory.Created[P2];
        var subscription = rig.Loop.SubscribePreview(4, V2, _ => { });

        Assert.True(rig.Loop.ShowView(V2, +1));
        subscription.Dispose();

        Assert.Equal([false, true], hidden.Visibility); // affichée : visible avant, pendant et après l'aperçu

        Assert.True(rig.Loop.ShowView(V1, -1));
        Assert.Equal([false, true, false], hidden.Visibility); // la vue repart : ses cases se cachent
    }

    [Fact]
    public void CommunityCases_PreviewedViewSwitchedAwayFromTheScreen_StaysVisibleWhileSubscribed()
    {
        var rig = Rig();
        var shown = _factory.Created[P1];
        var subscription = rig.Loop.SubscribePreview(4, V1, _ => { }); // la fenêtre regarde la vue qui est affichée
        Assert.Equal([false, true], shown.Visibility);

        Assert.True(rig.Loop.ShowView(V2, +1)); // le clavier la quitte
        Assert.Equal([false, true], shown.Visibility);

        subscription.Dispose();
        Assert.Equal([false, true, false], shown.Visibility);
    }

    [Fact]
    public void PreviewPinnedToTheDisplayedView_FollowsTheScreen_ThenBecomesAHiddenPreview()
    {
        var rig = Rig();
        var frames = Subscribe(rig, V1, out var subscription);
        using var _ = subscription;
        rig.Loop.Tick();

        Assert.Single(frames);
        Assert.Equal(rig.Screen.LastFrame, frames[0].Jpeg); // les octets qui sont partis vers l'écran

        Assert.True(rig.Loop.ShowView(V2, +1));
        var rendersBefore = _factory.Created[P1].Renders;
        Run(rig, 2000);

        Assert.True(_factory.Created[P1].Renders > rendersBefore); // sa vue n'est plus affichée : composée à part pour l'aperçu
        Assert.True(frames.Count > 1);
        Assert.NotEqual(rig.Screen.LastFrame, frames[^1].Jpeg);
    }

    [Fact]
    public void PreviewWithoutAView_StillFollowsTheDisplayedOne()
    {
        var rig = Rig();
        var frames = Subscribe(rig, null, out var subscription);
        using var _ = subscription;
        rig.Loop.Tick();
        Assert.True(rig.Loop.ShowView(V2, +1));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.Equal(2, frames.Count);
        Assert.Equal(rig.Screen.LastFrame, frames[^1].Jpeg);
        Assert.Equal(1, _factory.Created[P1].Renders); // une seule composition de la première vue : celle de l'écran
    }

    [Fact]
    public void DisposingTheLoop_WhileAHiddenPreviewIsOpen_DoesNotThrow()
    {
        var rig = Rig();
        var subscription = rig.Loop.SubscribePreview(4, V2, _ => { });
        Run(rig, 500);

        rig.Loop.Dispose();

        subscription.Dispose(); // le service s'arrête : la session de contrôle se ferme après la boucle
    }
}
