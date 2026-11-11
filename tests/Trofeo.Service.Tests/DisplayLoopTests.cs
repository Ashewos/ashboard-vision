using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Device;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

[Collection(GlobalTraceCollection.Name)]
public sealed class DisplayLoopTests
{
    private static readonly Guid InstanceId = new("11111111-1111-1111-1111-111111111111");

    private sealed class FakeModuleFactory(string typeId, GridSize minSize, Func<IModule> make) : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new(typeId, typeId, minSize, new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context) => make();
    }

    private sealed class ThrowingModuleFactory : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new("boom", "boom", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context) => throw new InvalidOperationException("création impossible");
    }

    private sealed class DisposeCountingModule : IModule
    {
        public int Disposed { get; private set; }
        public ModuleDescriptor Descriptor { get; } = new("dup", "dup", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public ModuleStatus Status => new(ModuleHealth.Online);
        public bool IsAnimating => false;
        public event EventHandler? Invalidated { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public void Render(SkiaSharp.SKCanvas canvas, ModuleRenderContext ctx) { }
        public void Dispose() => Disposed++;
    }

    /// <summary>Un fond animé : une nouvelle image tous les <paramref name="frame"/>, comme un GIF à cadence fixe.</summary>
    private sealed class FakeAnimatedBackground(TimeSpan frame) : IBackground
    {
        private readonly ColorBackground _inner = new(SkiaSharp.SKColors.DarkSlateBlue);
        private long _index = -1;
        private TimeSpan _until = TimeSpan.Zero;

        public List<TimeSpan> AdvanceTimes { get; } = [];
        public bool Disposed { get; private set; }
        public bool IsAnimated => true;
        public SkiaSharp.SKImage Current => _inner.Current;
        public int Version => (int)_index;
        public TimeSpan? UntilNextFrame => _until;

        public bool Advance(TimeSpan elapsed)
        {
            AdvanceTimes.Add(elapsed);
            var index = elapsed.Ticks / frame.Ticks;
            _until = TimeSpan.FromTicks(frame.Ticks - elapsed.Ticks % frame.Ticks);
            if (index == _index) return false;
            _index = index;
            return true;
        }

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }
    }

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        public FakeModule Module { get; }
        public FakeUsbTransportFactory Usb { get; } = new();
        public ScreenDevice Screen { get; }
        public DisplayLoop Loop { get; }
        public FrameScheduler Scheduler { get; }

        public Rig(FakeModule? module = null, int cols = 4, int rows = 2, string typeId = "fake",
            IEnumerable<IModuleFactory>? extraFactories = null, IEnumerable<ModulePlacement>? extraPlacements = null,
            IBackground? background = null)
        {
            Module = module ?? new FakeModule();
            Screen = new ScreenDevice(Usb, Time, static () => []);
            Scheduler = new FrameScheduler(Time);
            var registry = new ModuleRegistry(
                [new FakeModuleFactory("fake", new GridSize(2, 1), () => Module), .. extraFactories ?? []],
                _ => new FakeSecretStore());
            var placements = new List<ModulePlacement>
            {
                new(InstanceId, typeId, 0, 0, cols, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone()),
            };
            placements.AddRange(extraPlacements ?? []);
            var layout = DashboardLayout.SingleView(180, placements);
            Loop = new DisplayLoop(layout, registry, Screen, Scheduler, Time, background: background);
        }

        /// <summary>Un écran sain, prêt à valider <paramref name="acks"/> images.</summary>
        public FakeUsbTransport AddHealthyScreen(int acks = 20)
        {
            var transport = new FakeUsbTransport().EnqueueReadTimeout().EnqueueHandshakeReply();
            for (var i = 0; i < acks; i++) transport.EnqueueAck();
            Usb.EnqueueTransport(transport);
            return transport;
        }

        public void Dispose()
        {
            Loop.Dispose();
            Screen.Dispose();
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void Tick_FirstTick_ConnectsAndSendsFirstFrame()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.Single(rig.Module.RenderCalls);
    }

    [Fact]
    public void Tick_Idle_ResendsLastFrameEverySecond_WithoutRerendering()
    {
        // L'écran repasse au logo Thermalright environ 3 s après la dernière image reçue (constaté sur l'écran
        // d'Alex) : sans changement, on renvoie la dernière image chaque seconde, sans la redessiner.
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        var firstFrame = rig.Screen.LastFrame;

        for (var i = 0; i < 500; i++) // 5 s simulées à 10 ms
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
            rig.Loop.Tick();
        }

        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.Equal(5, rig.Loop.KeepAlivesSent); // à 1, 2, 3, 4 et 5 s
        Assert.Single(rig.Module.RenderCalls);
        Assert.Equal(firstFrame, rig.Screen.LastFrame);
    }

    [Fact]
    public void Tick_FrameJustSent_NoKeepAliveBeforeOneSecond()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        rig.Module.RaiseInvalidated();
        rig.Loop.Tick(); // nouvelle image à 0,6 s : le compteur du renvoi repart de là

        rig.Time.Advance(TimeSpan.FromMilliseconds(990));
        rig.Loop.Tick();
        Assert.Equal(0, rig.Loop.KeepAlivesSent);

        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.KeepAlivesSent);
    }

    [Fact]
    public void Tick_Invalidated_SendsOneFrame()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        rig.Module.RaiseInvalidated();
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick(); // la demande est consommée : rien de plus

        Assert.Equal(2, rig.Loop.FramesSent);
    }

    [Fact]
    public void Tick_Animating_IsCappedAt12Fps()
    {
        using var rig = new Rig(new FakeModule { IsAnimating = true });
        rig.AddHealthyScreen(acks: 100);

        for (var i = 0; i < 100; i++) // 1 s simulée à 10 ms
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.InRange(rig.Loop.FramesSent, 11, 13);
    }

    [Fact]
    public void Loop_StaticBackground_KeepsOneFramePerSecond()
    {
        // Fond fixe (le fond du thème), aucun module animé : une image au départ, puis le renvoi de la dernière chaque seconde.
        using var rig = new Rig();
        rig.AddHealthyScreen(acks: 100);

        for (var i = 0; i < 1000; i++) // 10 s simulées à 10 ms
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        var sent = rig.Loop.FramesSent + rig.Loop.KeepAlivesSent;
        Assert.InRange(sent, 1, 10);
        Assert.Equal(1, rig.Loop.FramesSent);
    }

    [Fact]
    public void Loop_AnimatedBackground_FollowsFrames()
    {
        using var rig = new Rig(background: new FakeAnimatedBackground(TimeSpan.FromMilliseconds(100)));
        rig.AddHealthyScreen(acks: 200);

        for (var i = 0; i < 300; i++) // 3 s simulées à 10 ms
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        // Une image par changement du fond (toutes les 100 ms), jamais plus de 12 par seconde.
        Assert.InRange(rig.Loop.FramesSent, 28, 31);
        Assert.True(rig.Loop.FramesSent <= 12 * 3);
    }

    [Fact]
    public void Loop_AnimatedBackground_FasterThanCap_IsCappedAt12Fps()
    {
        using var rig = new Rig(background: new FakeAnimatedBackground(TimeSpan.FromMilliseconds(20)));
        rig.AddHealthyScreen(acks: 200);

        for (var i = 0; i < 200; i++) // 2 s simulées à 10 ms
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.InRange(rig.Loop.FramesSent, 22, 26);
    }

    [Fact]
    public void Loop_AnimatedBackground_DelayFollowsNextFrame()
    {
        using var rig = new Rig(background: new FakeAnimatedBackground(TimeSpan.FromMilliseconds(250)));
        rig.AddHealthyScreen(acks: 20);

        var delay = rig.Loop.Tick(); // image à 0 ms ; la suivante dans 250 ms, bien avant le renvoi d'1 s

        Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
    }

    [Fact]
    public void Loop_AnimatedBackground_AdvancesWithTimeSinceStart()
    {
        var background = new FakeAnimatedBackground(TimeSpan.FromMilliseconds(100));
        using var rig = new Rig(background: background);
        rig.AddHealthyScreen(acks: 20);

        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromMilliseconds(350));
        rig.Loop.Tick();

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(350)], background.AdvanceTimes);
    }

    /// <summary>Un fond animé dont <see cref="Advance"/> lève toujours, et dont le délai annoncé (périmé) reste de 83 ms.</summary>
    private sealed class ThrowingBackground : IBackground
    {
        private readonly ColorBackground _inner = new(SkiaSharp.SKColors.DarkSlateBlue);

        public int AdvanceCalls { get; private set; }
        public bool IsAnimated => true;
        public SkiaSharp.SKImage Current => _inner.Current;
        public int Version => 1;
        public TimeSpan? UntilNextFrame => TimeSpan.FromMilliseconds(83);

        public bool Advance(TimeSpan elapsed)
        {
            AdvanceCalls++;
            throw new InvalidOperationException("fond en panne");
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Fait tourner la boucle comme le fil d'affichage : un <see cref="DisplayLoop.Tick"/>, puis le sommeil demandé.</summary>
    private static int RunFor(Rig rig, TimeSpan duration)
    {
        int ticks = 0;
        var start = rig.Time.GetTimestamp();
        while (rig.Time.GetElapsedTime(start) < duration)
        {
            var delay = rig.Loop.Tick();
            ticks++;
            rig.Time.Advance(delay > TimeSpan.FromMilliseconds(1) ? delay : TimeSpan.FromMilliseconds(1));
        }

        return ticks;
    }

    [Fact]
    public void Loop_AnimatedBackground_SlowRender_StillReaches12Fps()
    {
        // Un rendu et un envoi de 50 ms ne doivent pas s'ajouter à l'intervalle de 83 ms entre deux images : l'échéance de la
        // suivante est absolue (6 à 7 images/s mesurées sur l'écran avant ce correctif, soit environ 15 en 2 s).
        Rig? rig = null;
        var module = new FakeModule((_, _) => rig!.Time.Advance(TimeSpan.FromMilliseconds(50)));
        rig = new Rig(module, background: new FakeAnimatedBackground(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 12)));
        using var _rig = rig;
        rig.AddHealthyScreen(acks: 200);

        RunFor(rig, TimeSpan.FromSeconds(2));

        Assert.InRange(module.RenderCalls.Count, 23, 25);
        Assert.Equal(module.RenderCalls.Count, rig.Loop.FramesSent);
    }

    [Fact]
    public void Loop_AnimatedBackground_RenderLongerThanTheFrame_RendersBackToBack()
    {
        // Un rendu de 120 ms dépasse l'intervalle : l'image suivante part tout de suite, sans attente en plus.
        Rig? rig = null;
        var module = new FakeModule((_, _) => rig!.Time.Advance(TimeSpan.FromMilliseconds(120)));
        rig = new Rig(module, background: new FakeAnimatedBackground(TimeSpan.FromMilliseconds(20)));
        using var _rig = rig;
        rig.AddHealthyScreen(acks: 200);

        RunFor(rig, TimeSpan.FromSeconds(2));

        Assert.InRange(module.RenderCalls.Count, 15, 17); // 2 s / 120 ms
    }

    [Fact]
    public void Loop_ScreenNotReady_DoesNotAdvanceTheBackground()
    {
        var background = new FakeAnimatedBackground(TimeSpan.FromMilliseconds(100));
        using var rig = new Rig(background: background); // aucun écran : la connexion échoue

        for (int i = 0; i < 5; i++)
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.NotEqual(ScreenState.Ready, rig.Screen.State);
        Assert.Empty(background.AdvanceTimes); // personne ne verrait le résultat : la vidéo ne décode pas dans le vide

        rig.AddHealthyScreen();
        rig.Loop.Tick();

        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.Single(background.AdvanceTimes);
    }

    [Fact]
    public void Loop_BackgroundAdvanceThrows_LogsOnce_KeepsRunning_AndDoesNotWakeAt12Fps()
    {
        var background = new ThrowingBackground();
        using var rig = new Rig(background: background);
        rig.AddHealthyScreen(acks: 100);
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        int ticks;
        try
        {
            ticks = RunFor(rig, TimeSpan.FromSeconds(10));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        // Journalisé une seule fois (le même message n'est pas répété), la boucle continue (image de départ, puis renvois).
        Assert.Single(listener.Lines, l => l.Contains("fond en panne") && l.Contains("avance du fond"));
        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.True(rig.Loop.KeepAlivesSent >= 8, $"{rig.Loop.KeepAlivesSent} renvois");
        // Pas de réveil à 12 par seconde pour un fond en échec (120 ticks en 10 s) : au plus un par seconde, et un essai.
        Assert.True(ticks <= 25, $"{ticks} réveils en 10 s");
        Assert.True(background.AdvanceCalls <= 12, $"{background.AdvanceCalls} appels à Advance en 10 s");
    }

    [Fact]
    public void Log_EveryHundredFrames_ShowsAverageRenderAndSendTimes()
    {
        // Les rendus durent 40 ms, puis 60 ms, en alternance : 50 ms en moyenne.
        Rig? rig = null;
        int calls = 0;
        var module = new FakeModule((_, _) => rig!.Time.Advance(TimeSpan.FromMilliseconds(++calls % 2 == 1 ? 40 : 60))) { IsAnimating = true };
        rig = new Rig(module);
        using var _rig = rig;
        rig.AddHealthyScreen(acks: 300);
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            while (rig.Loop.FramesSent < 200)
            {
                var delay = rig.Loop.Tick();
                rig.Time.Advance(delay > TimeSpan.FromMilliseconds(1) ? delay : TimeSpan.FromMilliseconds(1));
            }
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        var lines = listener.Lines.Where(l => l.StartsWith("Image ") && l.Contains(" envoyée (")).ToArray();
        Assert.Matches(@"^Image 1 envoyée \(\d+ octets\) ; rendu 40 ms, envoi 0 ms$", lines.First(l => l.StartsWith("Image 1 ")));
        Assert.Matches(@"^Image 100 envoyée \(\d+ octets\) ; rendu 50 ms, envoi 0 ms en moyenne$", lines.Single(l => l.StartsWith("Image 100 ")));
        Assert.Matches(@"^Image 200 envoyée \(\d+ octets\) ; rendu 50 ms, envoi 0 ms en moyenne$", lines.Single(l => l.StartsWith("Image 200 ")));
    }

    [Fact]
    public void Dispose_DisposesTheBackground()
    {
        var background = new FakeAnimatedBackground(TimeSpan.FromMilliseconds(100));
        var rig = new Rig(background: background);

        rig.Dispose();

        Assert.True(background.Disposed);
    }

    [Fact]
    public void Tick_Disconnected_RetriesEvery2s()
    {
        using var rig = new Rig(); // aucun écran : TryOpen renvoie null

        rig.Loop.Tick();
        Assert.Equal(1, rig.Usb.OpenCount); // tout de suite au premier passage

        for (var i = 0; i < 199; i++) // jusqu'à 1,99 s
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
            rig.Loop.Tick();
        }

        Assert.Equal(1, rig.Usb.OpenCount);

        rig.Time.Advance(TimeSpan.FromMilliseconds(10)); // 2,00 s
        rig.Loop.Tick();
        Assert.Equal(2, rig.Usb.OpenCount);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();
        Assert.Equal(2, rig.Usb.OpenCount);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();
        Assert.Equal(3, rig.Usb.OpenCount);
        Assert.Equal(0, rig.Loop.FramesSent);
    }

    [Fact]
    public void Tick_Idle_ReturnsTimeUntilKeepAlive()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();

        Assert.Equal(TimeSpan.FromSeconds(1), rig.Loop.Tick()); // juste après l'envoi : un renvoi dans 1 s

        rig.Time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(TimeSpan.FromMilliseconds(600), rig.Loop.Tick());
    }

    [Fact]
    public void Tick_Animating_ReturnsFrameInterval()
    {
        using var rig = new Rig(new FakeModule { IsAnimating = true });
        rig.AddHealthyScreen(acks: 100);

        var delay = rig.Loop.Tick();

        Assert.Equal(rig.Scheduler.MinInterval, delay);
        Assert.InRange(delay.TotalMilliseconds, 83, 84);
    }

    [Fact]
    public void Tick_Dirty_ReturnsTimeUntilNextSlot()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromMilliseconds(30));
        rig.Module.RaiseInvalidated();

        // 30 ms écoulés sur 83,3 ms : l'image attend le créneau, pas le renvoi dans 970 ms.
        Assert.Equal(rig.Scheduler.MinInterval - TimeSpan.FromMilliseconds(30), rig.Loop.Tick());
    }

    [Fact]
    public void Tick_Disconnected_ReturnsTimeUntilReconnect()
    {
        using var rig = new Rig(); // aucun écran : TryOpen renvoie null

        // Juste après l'échec : 2 s avant le prochain essai, ramenées à 1 s par le plafond global.
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Loop.Tick());

        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), rig.Loop.Tick()); // intervalle pas écoulé : le reste

        rig.Time.Advance(TimeSpan.FromMilliseconds(490));
        Assert.Equal(TimeSpan.FromMilliseconds(10), rig.Loop.Tick());
        Assert.Equal(1, rig.Usb.OpenCount);

        rig.Time.Advance(TimeSpan.FromMilliseconds(10)); // à l'échéance : essai, puis nouvelle attente
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Loop.Tick());
        Assert.Equal(2, rig.Usb.OpenCount);
    }

    [Fact]
    public void Tick_ConnectedButNothingToSend_NeverReturnsZeroForever()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        var delay = rig.Loop.Tick(); // présence et renvoi faits : le prochain est dans 1 s

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void RequestReconnect_SetsSignal()
    {
        using var rig = new Rig();
        rig.Scheduler.Signal.WaitOne(0); // vide le signal de l'invalidation initiale
        Assert.False(rig.Scheduler.Signal.WaitOne(0));

        rig.Loop.RequestReconnect();

        Assert.True(rig.Scheduler.Signal.WaitOne(0));
    }

    [Fact]
    public void Tick_SendFails_ReconnectsAndResendsWithoutNewInvalidation()
    {
        using var rig = new Rig();
        var first = rig.AddHealthyScreen(acks: 1);
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.FramesSent);

        first.FailNextWritesWithIOException(1); // débranchement pendant l'envoi suivant
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Module.RaiseInvalidated();
        rig.Loop.Tick();
        Assert.Equal(ScreenState.Disconnected, rig.Screen.State);
        Assert.Equal(1, rig.Loop.FramesSent);

        var second = rig.AddHealthyScreen();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Loop.Tick();

        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.True(rig.Loop.FramesSent >= 2);
        Assert.NotEmpty(second.Writes);
    }

    [Fact]
    public void Constructor_UnknownType_IsSkippedAndOthersStillRender()
    {
        using var rig = new Rig(typeId: "inconnu");
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.Empty(rig.Module.RenderCalls);
    }

    [Fact]
    public void Constructor_BelowMinSize_IsSkipped()
    {
        using var rig = new Rig(cols: 1, rows: 1); // le minimum du faux module est 2 × 1
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Empty(rig.Module.RenderCalls);
    }

    [Fact]
    public void Tick_ScreenRemovedWhileIdle_DetectedWithin1s()
    {
        using var rig = new Rig();
        var transport = rig.AddHealthyScreen();
        rig.Loop.Tick();
        Assert.Equal(ScreenState.Ready, rig.Screen.State);

        rig.Usb.Present = false;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();

        Assert.Equal(ScreenState.Disconnected, rig.Screen.State);
        Assert.Equal(ScreenDevice.AbsentReason, rig.Screen.Reason);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void Tick_ScreenPresent_ChecksPresenceAtMostOncePerSecond()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();

        for (var i = 0; i < 300; i++) // 3 s simulées à 10 ms
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
            rig.Loop.Tick();
        }

        Assert.InRange(rig.Usb.PresenceChecks, 2, 3);
        Assert.Equal(ScreenState.Ready, rig.Screen.State);
    }

    [Fact]
    public void Tick_Replug_ReadyWithin3s()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.FramesSent);

        rig.Usb.Present = false;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Loop.Tick();
        Assert.Equal(ScreenState.Disconnected, rig.Screen.State);

        rig.Usb.Present = true;
        var fresh = rig.AddHealthyScreen(); // un nouvel objet transport après le rebranchement
        var elapsed = TimeSpan.Zero;
        while (rig.Screen.State != ScreenState.Ready && elapsed < TimeSpan.FromSeconds(3))
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
            elapsed += TimeSpan.FromMilliseconds(10);
            rig.Loop.Tick();
        }

        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.True(elapsed <= TimeSpan.FromSeconds(3), $"Ready après {elapsed}");
        Assert.NotEmpty(fresh.Writes);
        Assert.True(rig.Loop.FramesSent >= 2); // l'image est renvoyée sans nouvelle invalidation
    }

    [Fact]
    public void Resume_ForcesReconnect()
    {
        using var rig = new Rig();
        var first = rig.AddHealthyScreen();
        rig.Loop.Tick();
        var second = rig.AddHealthyScreen();

        rig.Loop.RequestReconnect(); // reprise après veille, depuis un autre thread
        rig.Time.Advance(TimeSpan.FromMilliseconds(100)); // bien avant les 2 s de la reconnexion normale
        rig.Loop.Tick();

        Assert.True(first.IsDisposed);
        Assert.Equal(ScreenState.Ready, rig.Screen.State);
        Assert.Equal(2, rig.Usb.OpenCount);
        Assert.NotEmpty(second.Writes);
        Assert.True(rig.Loop.FramesSent >= 2);
    }

    [Fact]
    public void Resume_RecordsReason_WhenScreenIsNotBackYet()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        var reasons = new List<string?>();
        rig.Screen.StateChanged += (_, _) => reasons.Add(rig.Screen.Reason);

        rig.Loop.RequestReconnect();
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Loop.Tick(); // l'écran n'est pas encore ré-énuméré

        Assert.Equal("Reprise après veille", reasons[0]);
        Assert.Equal(ScreenState.Disconnected, rig.Screen.State);
    }

    [Fact]
    public void Constructor_PlacementThrows_OthersStillRender()
    {
        var good = new FakeModule();
        var bad = new ModulePlacement(
            Guid.NewGuid(), "boom", 8, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        using var rig = new Rig(good, extraFactories: [new ThrowingModuleFactory()], extraPlacements: [bad]);
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.Single(good.RenderCalls);
    }

    [Fact]
    public void Constructor_NullTypeId_DoesNotThrowAndOthersStillRender()
    {
        var good = new FakeModule();
        var bad = new ModulePlacement(
            Guid.NewGuid(), null!, 8, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        using var rig = new Rig(good, extraPlacements: [bad]);
        rig.AddHealthyScreen();

        rig.Loop.Tick();

        Assert.Single(good.RenderCalls);
    }

    [Fact]
    public void Constructor_DuplicateInstanceId_DisposesTheSecondModule()
    {
        var second = new DisposeCountingModule();
        var dup = new ModulePlacement(
            InstanceId, "dup", 8, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        using var rig = new Rig(
            extraFactories: [new FakeModuleFactory("dup", new GridSize(1, 1), () => second)], extraPlacements: [dup]);

        Assert.Equal(1, second.Disposed); // refusé et libéré : pas de minuteur orphelin
    }

    private sealed class CommandableModule(string typeId) : IModule, ICommandableModule
    {
        public ModuleDescriptor Descriptor { get; } = new(typeId, typeId, new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public ModuleStatus Status => new(ModuleHealth.Online);
        public bool IsAnimating => false;
        public event EventHandler? Invalidated { add { } remove { } }
        public event EventHandler? StatusChanged { add { } remove { } }
        public bool HandleCommand(ModuleCommand command) => true;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public void Render(SkiaSharp.SKCanvas canvas, ModuleRenderContext ctx) { }
        public void Dispose() { }
    }

    private static readonly Guid IdA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid IdB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid IdC = new("cccccccc-0000-0000-0000-000000000003");

    private static ModulePlacement Placed(Guid id, string typeId, int column, int row) =>
        new(id, typeId, column, row, 4, 1, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>Le module « fake » (non commandable) en (0, 0), plus des commandables posés dans l'ordre donné.</summary>
    private static Rig CommandRig(params (CommandableModule Module, ModulePlacement Placement)[] commandables) =>
        new(rows: 1,
            extraFactories: [.. commandables.Select(c => new FakeModuleFactory(c.Placement.TypeId, new GridSize(1, 1), () => c.Module))],
            extraPlacements: commandables.Select(c => c.Placement));

    [Fact]
    public void FindCommandTarget_FirstByColumn()
    {
        var a = new CommandableModule("cmd-a");
        var b = new CommandableModule("cmd-b");
        var c = new CommandableModule("cmd-c");
        // Listés dans le désordre : c est à la colonne la plus à gauche, bien que sur la rangée 1.
        using var rig = CommandRig((b, Placed(IdB, "cmd-b", 12, 0)), (a, Placed(IdA, "cmd-a", 8, 0)), (c, Placed(IdC, "cmd-c", 4, 1)));

        Assert.Same(c, rig.Loop.FindCommandTarget(null));
        Assert.Same(c, rig.Loop.FindCommandTarget(Guid.NewGuid())); // instance inconnue : le premier
        Assert.Same(c, rig.Loop.FindCommandTarget(InstanceId)); // le module « fake » n'accepte pas les commandes
    }

    [Fact]
    public void FindCommandTarget_SameColumn_FirstByRow()
    {
        var a = new CommandableModule("cmd-a");
        var b = new CommandableModule("cmd-b");
        using var rig = CommandRig((a, Placed(IdA, "cmd-a", 8, 1)), (b, Placed(IdB, "cmd-b", 8, 0)));

        Assert.Same(b, rig.Loop.FindCommandTarget(null));
    }

    [Fact]
    public void FindCommandTarget_ExplicitInstance()
    {
        var a = new CommandableModule("cmd-a");
        var b = new CommandableModule("cmd-b");
        using var rig = CommandRig((a, Placed(IdA, "cmd-a", 4, 0)), (b, Placed(IdB, "cmd-b", 8, 0)));

        Assert.Same(b, rig.Loop.FindCommandTarget(IdB));
        Assert.Same(a, rig.Loop.FindCommandTarget(IdA));
    }

    [Fact]
    public void FindCommandTarget_NoCommandableModule_Null()
    {
        using var rig = new Rig();

        Assert.Null(rig.Loop.FindCommandTarget(null));
        Assert.Null(rig.Loop.FindCommandTarget(InstanceId));
    }

    [Fact]
    public void StatusChanged_MarksDirtyAndLogs()
    {
        using var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            rig.Module.Status = new ModuleStatus(ModuleHealth.Offline, "Reconnectez le compte");
            rig.Module.RaiseStatusChanged();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        rig.Loop.Tick(); // une image au tick suivant

        Assert.Equal(2, rig.Loop.FramesSent);
        Assert.Contains(listener.Lines, l => l.Contains("[fake 11111111] État : Offline — Reconnectez le compte"));
    }

    [Fact]
    public void Dispose_UnsubscribesStatusChanged()
    {
        var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Loop.Dispose();

        rig.Module.RaiseStatusChanged(); // toléré après Dispose
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(rig.Scheduler.ShouldRender(false));
        rig.Screen.Dispose();
    }

    [Fact]
    public void Dispose_UnsubscribesInvalidated()
    {
        var rig = new Rig();
        rig.AddHealthyScreen();
        rig.Loop.Tick();
        rig.Loop.Dispose();

        rig.Module.RaiseInvalidated(); // toléré après Dispose
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // au-delà des 12 images/s : seule la désinscription empêche le redessin

        Assert.False(rig.Scheduler.ShouldRender(false));
        rig.Screen.Dispose();
    }
}

public sealed class ModuleRegistryTests
{
    /// <summary>Fournisseur de météo qui n'est jamais interrogé : les réglages de ces tests n'ont pas de lieu.</summary>
    private sealed class NoWeather : Trofeo.Modules.Clock.IWeatherProvider
    {
        public Task<Trofeo.Modules.Clock.WeatherNow> GetCurrentAsync(
            Trofeo.Modules.Clock.WeatherLocation where, Trofeo.Modules.Clock.TemperatureUnit unit, CancellationToken ct) =>
            throw new InvalidOperationException("Aucune météo attendue.");

        public Task<IReadOnlyList<Trofeo.Modules.Clock.CitySuggestion>> SearchCityAsync(string name, CancellationToken ct) =>
            throw new InvalidOperationException("Aucune recherche attendue.");
    }

    private sealed class CapturingFactory(Action<ModuleContext> capture) : IModuleFactory
    {
        public ModuleDescriptor Descriptor { get; } = new("fake", "fake", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());

        public IModule Create(ModuleContext context)
        {
            capture(context);
            return new FakeModule();
        }
    }

    private static ModulePlacement Placement(string typeId) => new(
        Guid.NewGuid(), typeId, 0, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    [Fact]
    public void Create_KnownType_ReturnsModule()
    {
        var registry = new ModuleRegistry([new Trofeo.Modules.Clock.ClockFactory(new NoWeather())], _ => new FakeSecretStore());

        using var module = registry.Create(Placement("clock"), TimeProvider.System);

        Assert.NotNull(module);
        Assert.True(registry.Descriptors.ContainsKey("clock"));
    }

    [Fact]
    public void Create_BuildsContextFromPlacement()
    {
        ModuleContext? captured = null;
        var scopes = new List<string>();
        var store = new FakeSecretStore();
        var registry = new ModuleRegistry(
            [new CapturingFactory(ctx => captured = ctx)],
            typeId => { scopes.Add(typeId); return store; });
        var settings = JsonDocument.Parse("{\"k\":42}").RootElement.Clone();
        var placement = Placement("fake") with { Settings = settings };
        var time = new FakeTimeProvider();
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            using var module = registry.Create(placement, time);

            Assert.NotNull(module);
            Assert.NotNull(captured);
            Assert.Equal(placement.InstanceId, captured.InstanceId);
            Assert.Equal(42, captured.Settings.GetProperty("k").GetInt32());
            Assert.Same(time, captured.Time);
            Assert.Equal(["fake"], scopes);
            Assert.Same(store, captured.Secrets);

            captured.Log.Info("bonjour");
            Assert.Contains(
                listener.Lines, l => l.Contains($"[fake {placement.InstanceId.ToString("N")[..8]}] bonjour"));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void Create_UnknownType_ReturnsNull()
    {
        var registry = new ModuleRegistry([new Trofeo.Modules.Clock.ClockFactory(new NoWeather())], _ => new FakeSecretStore());

        Assert.Null(registry.Create(Placement("meteo"), TimeProvider.System));
    }
}
