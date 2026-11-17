using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.TestDoubles;
using static Trofeo.Display.Tests.LoopRig;

namespace Trofeo.Display.Tests;

/// <summary>La boucle d'affichage et l'accélération matérielle : appareil perdu, appareil changé, état rendu (plan 10, tâche 5).</summary>
public sealed class DisplayLoopAccelerationTests
{
    private static readonly GpuAdapterInfo First = FakeGpuDeviceFactory.Card("Carte A", device: 1);
    private static readonly GpuAdapterInfo Second = FakeGpuDeviceFactory.Card("Carte B", device: 2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGpuDeviceFactory _factory = new();

    public DisplayLoopAccelerationTests() => _factory.Adapters.AddRange([First, Second]);

    private static LoopConfig Config(DashboardLayout layout, IBackground background) =>
        new(layout, Themes.ClassiqueDefinition, background);

    private static void NextFrame(LoopRig rig)
    {
        rig.Scheduler.MarkDirty();
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // laisse passer le plafond d'images
        rig.Loop.Tick();
    }

    [Fact]
    public void Status_ReportsTheAcceleration()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(acceleration: acceleration, time: _time);

        var status = rig.Loop.Status.Acceleration;

        Assert.NotNull(status);
        Assert.True(status.Drawing);
        Assert.Equal(First, status.Adapter);
        Assert.Equal(acceleration.Status, status);
    }

    [Fact]
    public void Status_WithoutController_HasNoAcceleration()
    {
        using var rig = new LoopRig();

        Assert.Null(rig.Loop.Status.Acceleration);
    }

    [Fact]
    public void Frames_AreDrawnOnTheDevice()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(acceleration: acceleration, time: _time);
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);

        rig.Loop.Tick();

        Assert.Equal(1, rig.Loop.FramesSent);
        Assert.True(_factory.Created[0].Device.Submits > 0);
    }

    [Fact]
    public void DeviceLostMidRun_NextFrameIsSent()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(acceleration: acceleration, time: _time);
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var device = _factory.Created[0].Device;
        Assert.Equal(1, rig.Loop.FramesSent);

        device.Lose("pilote planté");
        NextFrame(rig); // la carte tombe pendant cette image : le processeur la dessine quand même

        Assert.Equal(2, rig.Loop.FramesSent);
        Assert.Null(acceleration.DrawingDevice);
        var status = rig.Loop.Status.Acceleration!;
        Assert.False(status.Drawing);
        Assert.Equal("La carte graphique a eu un problème (pilote planté) : dessin sur le processeur.", status.Problem);

        NextFrame(rig); // les compositeurs sont refaits sur le processeur, puis la carte perdue est libérée
        Assert.Equal(3, rig.Loop.FramesSent);
        Assert.True(device.IsDisposed);
        Assert.Single(_factory.Created);

        _time.Advance(TimeSpan.FromMinutes(5)); // nouvel essai : la carte revient
        NextFrame(rig);
        Assert.Equal(4, rig.Loop.FramesSent);
        Assert.Equal(2, _factory.Created.Count);
        Assert.True(_factory.Created[1].Device.Submits > 0);
        Assert.True(rig.Loop.Status.Acceleration!.Drawing);
        Assert.Null(rig.Loop.Status.Acceleration!.Problem);
    }

    [Fact]
    public void DeviceLostMidRun_PreviewKeepsGettingFrames()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(acceleration: acceleration, time: _time);
        var frames = 0;
        using var subscription = rig.Loop.SubscribePreview(12, _ => frames++);
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick(); // sans écran : image émulée
        Assert.Equal(1, frames);

        _factory.Created[0].Device.Lose();
        NextFrame(rig);

        Assert.Equal(2, frames);
    }

    [Fact]
    public async Task DeviceChange_RebuildsComposers()
    {
        var background = new TrackedBackground(SKColors.Black);
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(background: background, acceleration: acceleration, time: _time);
        rig.AddHealthyScreen();
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var (_, firstDevice) = _factory.Created[0];
        var submitsBefore = firstDevice.Submits;

        // Une autre carte : la boucle dessine sur la nouvelle, puis libère l'ancienne.
        var task = rig.Loop.ApplyAsync(Config(rig.Loop.Layout with { Acceleration = new AccelerationSettings(Adapter: Second.Id) }, background));
        NextFrame(rig);
        Assert.Empty(await task);

        Assert.Equal(2, _factory.Created.Count);
        Assert.Equal(Second, _factory.Created[1].Adapter);
        Assert.True(firstDevice.IsDisposed);
        Assert.Equal(submitsBefore, firstDevice.Submits);
        Assert.True(_factory.Created[1].Device.Submits > 0);
        Assert.Equal(2, rig.Loop.FramesSent);
        Assert.Equal(Second, rig.Loop.Status.Acceleration!.Adapter);

        // Coupée : tout repasse sur le processeur, le dernier appareil est libéré.
        var secondDevice = _factory.Created[1].Device;
        var secondSubmits = secondDevice.Submits;
        task = rig.Loop.ApplyAsync(Config(rig.Loop.Layout with { Acceleration = new AccelerationSettings(Enabled: false) }, background));
        NextFrame(rig);
        Assert.Empty(await task);

        Assert.True(secondDevice.IsDisposed);
        Assert.Equal(secondSubmits, secondDevice.Submits);
        Assert.Equal(3, rig.Loop.FramesSent);
        Assert.Equal(2, _factory.Created.Count);
        Assert.False(rig.Loop.Status.Acceleration!.Enabled);
        Assert.False(background.Disposed);
    }

    [Fact]
    public void SlowFrames_ReachTheController()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(acceleration: acceleration, time: _time);
        rig.AddHealthyScreen(acks: 400);
        rig.Loop.Start(CancellationToken.None);
        _time.AutoAdvanceAmount = TimeSpan.FromMilliseconds(100); // chaque lecture de l'horloge coûte 100 ms : la composition paraît lente

        for (var i = 0; i < 6; i++) NextFrame(rig);
        _time.AutoAdvanceAmount = TimeSpan.Zero;
        _time.Advance(TimeSpan.FromSeconds(11));
        rig.Loop.Tick();

        Assert.Equal(Second, _factory.Created[^1].Adapter); // la carte suivante a pris le relais
        Assert.Equal(Second, rig.Loop.Status.Acceleration!.Adapter);
    }

    private static DashboardLayout TwoViews() => new(
        180,
        [
            new LayoutView(new Guid("11110000-0000-0000-0000-000000000001"), "Vue 1", [Place(new Guid("11110000-0000-0000-0000-0000000000a1"), "fake", 0, 0, 4, 2)]),
            new LayoutView(new Guid("22220000-0000-0000-0000-000000000002"), "Vue 2", [Place(new Guid("22220000-0000-0000-0000-0000000000a2"), "fake", 2, 1, 6, 3)]),
        ]);

    /// <summary>Fait tourner la boucle par pas de 10 ms pendant <paramref name="duration"/>.</summary>
    private static void Run(LoopRig rig, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromMilliseconds(10))
        {
            rig.Loop.Tick();
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public void ViewGlide_IsDrawnOnTheDevice()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(TwoViews(), acceleration: acceleration, time: _time);
        rig.AddHealthyScreen(acks: 600);
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick();
        var device = _factory.Created[0].Device;
        var surfacesBefore = device.SurfacesCreated;

        Assert.True(rig.Loop.ShowAdjacentView(+1));
        Run(rig, TimeSpan.FromMilliseconds(400));

        // Une surface de la carte par image du glissement (la composition de l'écran garde la sienne, la même d'une image à l'autre).
        Assert.True(device.SurfacesCreated - surfacesBefore >= 10, $"surfaces créées pendant le glissement : {device.SurfacesCreated - surfacesBefore}");
        Assert.True(rig.Loop.Status.Acceleration!.Drawing);
        Assert.Null(rig.Loop.Status.Acceleration.Problem);
    }

    [Fact]
    public void ViewGlide_OnADeviceThatRefusesTheSurface_IsReportedToTheController()
    {
        using var acceleration = new AccelerationController(_factory, _time, _ => { });
        using var rig = new LoopRig(TwoViews(), acceleration: acceleration, time: _time);
        rig.AddHealthyScreen(acks: 600);
        rig.Loop.Start(CancellationToken.None);
        rig.Loop.Tick(); // l'écran a sa surface : seul le glissement en demandera une autre
        var device = _factory.Created[0].Device;
        device.FailCreate = true;

        Assert.True(rig.Loop.ShowAdjacentView(+1));
        Run(rig, TimeSpan.FromMilliseconds(400));

        Assert.False(device.IsLost); // refusée, pas perdue : sans signalement le glissement resterait sur le processeur sans rien dire
        Assert.Null(acceleration.DrawingDevice);
        var status = rig.Loop.Status.Acceleration!;
        Assert.False(status.Drawing);
        Assert.Contains("a eu un problème", status.Problem);
        Assert.Contains("refusée", status.Problem);
    }
}
