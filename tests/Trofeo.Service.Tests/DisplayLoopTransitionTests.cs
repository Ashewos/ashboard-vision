using System.Diagnostics;
using System.Text.RegularExpressions;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.TestDoubles;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>Le glissement d'une vue à l'autre : des images à la cadence de l'envoi pendant 300 ms, puis le repos.</summary>
[Collection(GlobalTraceCollection.Name)]
public sealed partial class DisplayLoopTransitionTests : IDisposable
{
    private static readonly Guid V1 = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid V2 = new("22220000-0000-0000-0000-000000000002");
    private static readonly Guid V3 = new("33330000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = new("11110000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("22220000-0000-0000-0000-0000000000a2");
    private static readonly Guid P3 = new("33330000-0000-0000-0000-0000000000a3");

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    /// <summary>Trois vues d'une case chacune, à des places différentes pour que leurs images diffèrent.</summary>
    private static DashboardLayout ThreeViews(bool animate = true) => new(
        180,
        [
            new LayoutView(V1, "Vue 1", [Place(P1, "fake", 0, 0, 4, 2)]),
            new LayoutView(V2, "Vue 2", [Place(P2, "fake", 2, 1, 6, 3)]),
            new LayoutView(V3, "Vue 3", [Place(P3, "fake", 6, 0, 8, 4)]),
        ],
        Appearance: animate ? null : new AppearanceSettings(AnimateViewSwitch: false));

    private LoopRig Rig(DashboardLayout? layout = null, int acks = 600)
    {
        var rig = new LoopRig(layout ?? ThreeViews());
        _disposables.Add(rig);
        rig.AddHealthyScreen(acks);
        return rig;
    }

    /// <summary>Fait tourner la boucle par pas de 10 ms pendant <paramref name="duration"/> ; rend les délais demandés par chaque Tick.</summary>
    private static List<TimeSpan> Run(LoopRig rig, TimeSpan duration)
    {
        var delays = new List<TimeSpan>();
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
        {
            delays.Add(rig.Loop.Tick());
            rig.Time.Advance(Step);
        }

        return delays;
    }

    /// <summary>L'image que montre un écran qui affiche seulement <paramref name="placement"/>, pour comparer à l'image finale.</summary>
    private static byte[] ReferenceFrame(ModulePlacement placement)
    {
        using var reference = new LoopRig(DashboardLayout.SingleView(180, [placement]));
        reference.AddHealthyScreen(5);
        reference.Loop.Tick();
        return reference.Screen.LastFrame!;
    }

    [GeneratedRegex(@"Passage de vue : (\d+) images en (\d+) ms\.")]
    private static partial Regex GlideLine();

    private static List<(int Frames, int Milliseconds)> GlideLines(CapturingTraceListener listener) =>
        [.. listener.Lines.Select(l => GlideLine().Match(l)).Where(m => m.Success)
            .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))];

    [Fact]
    public void Switch_SendsFramesAsFastAsPossibleThenRests()
    {
        var rig = Rig();
        Run(rig, TimeSpan.FromMilliseconds(100)); // la première image, puis le repos
        var before = rig.Loop.FramesSent;
        Assert.Equal(1, before);

        Assert.True(rig.Loop.ShowAdjacentView(+1));
        var delays = Run(rig, TimeSpan.FromMilliseconds(400));

        // Pendant les 300 ms : une image par réveil, bien au-delà des 12 par seconde du plafond (3 ou 4 en 300 ms).
        var glideFrames = rig.Loop.FramesSent - before;
        Assert.InRange(glideFrames, 25, 40);
        Assert.Equal(TimeSpan.Zero, delays[5]); // en plein glissement : la boucle ne dort pas

        // Après : retour au repos. Plus d'image, la boucle redort et seul le renvoi de maintien repart.
        var resting = rig.Loop.FramesSent;
        var restDelays = Run(rig, TimeSpan.FromSeconds(2));
        Assert.Equal(resting, rig.Loop.FramesSent);
        Assert.True(rig.Loop.KeepAlivesSent >= 1);
        Assert.All(restDelays, delay => Assert.True(delay > TimeSpan.Zero));
        Assert.Equal(V2, rig.Loop.CurrentViewId);
    }

    [Fact]
    public void Switch_ComposesTheArrivalViewOnce_AndEndsOnIt()
    {
        var rig = Rig();
        rig.Loop.Tick();
        rig.Loop.ShowAdjacentView(+1);

        Run(rig, TimeSpan.FromMilliseconds(500));

        // Les modules ne sont pas redessinés à chaque image du glissement : une fois pour l'ancienne vue, une fois pour la nouvelle.
        Assert.Single(rig.Of(P1).Single().Rects);
        Assert.Single(rig.Of(P2).Single().Rects);
        Assert.Equal(ReferenceFrame(Place(P2, "fake", 2, 1, 6, 3)), rig.Screen.LastFrame);
    }

    [Fact]
    public void SecondKeyDuringGlide_AbandonsItAndGoesOn()
    {
        var rig = Rig();
        Run(rig, TimeSpan.FromMilliseconds(100));
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            Assert.True(rig.Loop.ShowAdjacentView(+1));
            Run(rig, TimeSpan.FromMilliseconds(100));
            var midGlide = rig.Loop.FramesSent;
            Assert.True(rig.Loop.ShowAdjacentView(+1)); // deuxième appui Droite : la vue 1 est devenue la vue 3, via la 2
            Assert.Equal(V3, rig.Loop.CurrentViewId);

            Run(rig, TimeSpan.FromMilliseconds(100));
            Assert.True(rig.Loop.FramesSent > midGlide); // un nouveau glissement est parti tout de suite

            // 300 ms après le deuxième appui, l'écran est sur la vue 3 ; ensuite rien de plus : pas de troisième glissement en file.
            Run(rig, TimeSpan.FromMilliseconds(300));
            var settled = rig.Loop.FramesSent;
            Run(rig, TimeSpan.FromSeconds(2));

            Assert.Equal(settled, rig.Loop.FramesSent);
            Assert.Equal(V3, rig.Loop.CurrentViewId);
            Assert.Equal(ReferenceFrame(Place(P3, "fake", 6, 0, 8, 4)), rig.Screen.LastFrame);
            Assert.Equal(2, GlideLines(listener).Count); // le glissement abandonné, puis le nouveau mené à son terme
            Assert.Single(rig.Of(P2).Single().Rects); // la vue 2 n'a été composée qu'une fois, pour le glissement abandonné
            Assert.Single(rig.Of(P3).Single().Rects);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void TwoKeysBeforeTheNextFrame_GoStraightToTheThirdView_WithOneGlide()
    {
        var rig = Rig();
        Run(rig, TimeSpan.FromMilliseconds(100));
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            Assert.True(rig.Loop.ShowAdjacentView(+1));
            Assert.True(rig.Loop.ShowAdjacentView(+1));
            Assert.Equal(V3, rig.Loop.CurrentViewId);

            Run(rig, TimeSpan.FromSeconds(2));

            Assert.Single(GlideLines(listener));
            Assert.Equal(ReferenceFrame(Place(P3, "fake", 6, 0, 8, 4)), rig.Screen.LastFrame);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void ConcurrentKeys_NeverLoseAStep()
    {
        var rig = Rig(acks: 10);

        // Vingt appuis sur Droite depuis plusieurs fils : chacun part de la vue courante, sous le verrou de la boucle.
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => rig.Loop.ShowAdjacentView(+1));

        Assert.Equal(V3, rig.Loop.CurrentViewId); // 20 pas de +1 sur 3 vues : on finit sur la vue de rang 2 (20 mod 3)
        Assert.Equal(1, rig.Loop.LastViewDirection);
    }

    [Fact]
    public void ConcurrentSwitches_LastAnnouncedViewIsTheDisplayedOne()
    {
        var rig = Rig(acks: 10);
        var events = new List<Guid>();
        rig.Loop.ViewChanged += view => { lock (events) events.Add(view); };

        for (var round = 0; round < 20; round++)
        {
            Parallel.For(0, 30, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => rig.Loop.ShowViewAt(i % 3));

            // Quel que soit l'ordre dans lequel les fils ont fini, ce qui est annoncé en dernier est ce que l'écran montre.
            lock (events) Assert.Equal(rig.Loop.CurrentViewId, events.Count == 0 ? V1 : events[^1]);
        }
    }

    [Fact]
    public void AnimationOff_SwitchesInOneFrame()
    {
        var rig = Rig(ThreeViews(animate: false));
        Run(rig, TimeSpan.FromMilliseconds(100));
        var before = rig.Loop.FramesSent;
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            Assert.True(rig.Loop.ShowAdjacentView(+1));
            Run(rig, TimeSpan.FromSeconds(1));

            Assert.Equal(before + 1, rig.Loop.FramesSent);
            Assert.Equal(ReferenceFrame(Place(P2, "fake", 2, 1, 6, 3)), rig.Screen.LastFrame);
            Assert.Empty(GlideLines(listener));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void SwitchWithoutDirection_IsImmediate()
    {
        var rig = Rig();
        Run(rig, TimeSpan.FromMilliseconds(100));
        var before = rig.Loop.FramesSent;

        Assert.True(rig.Loop.ShowView(V3, 0));
        Run(rig, TimeSpan.FromSeconds(1));

        Assert.Equal(before + 1, rig.Loop.FramesSent);
    }

    [Fact]
    public void Switch_LogsTheFrameCount()
    {
        var rig = Rig();
        Run(rig, TimeSpan.FromMilliseconds(100));
        var before = rig.Loop.FramesSent;
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            rig.Loop.ShowAdjacentView(+1);
            Run(rig, TimeSpan.FromSeconds(1));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        var line = Assert.Single(GlideLines(listener));
        Assert.Equal(rig.Loop.FramesSent - before, line.Frames);
        Assert.InRange(line.Milliseconds, 300, 330);
        Assert.Contains(listener.Lines, l => l.Contains($"Passage de vue : {line.Frames} images en {line.Milliseconds} ms."));
    }

    [Fact]
    public void ChangeDuringGlide_IsDrawnRightAfterIt()
    {
        // Le même scénario avec et sans changement pendant le glissement : une image fraîche de plus, une seule, puis le repos.
        int FramesAfterSwitch(bool changeMidGlide)
        {
            var rig = Rig();
            Run(rig, TimeSpan.FromMilliseconds(100));
            rig.Loop.ShowAdjacentView(+1);
            Run(rig, TimeSpan.FromMilliseconds(100));
            if (changeMidGlide) rig.Scheduler.MarkDirty(); // un module de la vue d'arrivée change pendant le glissement
            Run(rig, TimeSpan.FromSeconds(2));
            return rig.Loop.FramesSent;
        }

        Assert.Equal(FramesAfterSwitch(changeMidGlide: false) + 1, FramesAfterSwitch(changeMidGlide: true));
    }

    [Fact]
    public void SingleView_KeepsNothingToGlideFrom()
    {
        var rig = Rig(DashboardLayout.SingleView(180, [Place(P1)]));
        Run(rig, TimeSpan.FromMilliseconds(100));

        Assert.False(rig.Loop.ShowAdjacentView(+1));
        Run(rig, TimeSpan.FromSeconds(1));

        Assert.Equal(1, rig.Loop.FramesSent);
    }
}
