using Microsoft.Extensions.Time.Testing;
using Trofeo.Core.Rendering;

namespace Trofeo.Core.Tests;

public class FrameSchedulerTests
{
    /// <summary>
    /// Échantillonne ShouldRender à t = 0, 10, 20, … ms sur la fenêtre semi-ouverte [0 ms, 1000 ms)
    /// (100 échantillons) et compte les envois.
    /// </summary>
    private static int CountRendersOverOneSecond(FakeTimeProvider time, FrameScheduler scheduler)
    {
        int renders = 0;
        for (int i = 0; i < 100; i++)
        {
            if (scheduler.ShouldRender(anyAnimating: true))
            {
                renders++;
                scheduler.OnRendered();
            }
            time.Advance(TimeSpan.FromMilliseconds(10));
        }
        return renders;
    }

    [Fact]
    public void MarkDirty_SetsSignal()
    {
        using var scheduler = new FrameScheduler(new FakeTimeProvider());
        Assert.False(scheduler.Signal.WaitOne(0));

        scheduler.MarkDirty();

        Assert.True(scheduler.IsDirty);
        Assert.True(scheduler.Signal.WaitOne(0));
        Assert.False(scheduler.Signal.WaitOne(0)); // se réarme seul
    }

    [Fact]
    public void ShouldRender_IgnoringTheCap_RendersEveryTime_AndConsumesTheDirtyFlag()
    {
        var time = new FakeTimeProvider();
        using var scheduler = new FrameScheduler(time, maxFps: 10);
        Assert.True(scheduler.ShouldRender(anyAnimating: true));
        scheduler.OnRendered();
        time.Advance(TimeSpan.FromMilliseconds(5)); // bien avant l'intervalle de 100 ms
        scheduler.MarkDirty();

        Assert.False(scheduler.ShouldRender(anyAnimating: true));
        Assert.True(scheduler.ShouldRender(anyAnimating: false, ignoreCap: true));
        Assert.False(scheduler.IsDirty);
        scheduler.OnRendered();

        // Le plafond reprend ensuite : l'intervalle se compte depuis cette image.
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(scheduler.ShouldRender(anyAnimating: true));
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(scheduler.ShouldRender(anyAnimating: true));
    }

    [Fact]
    public void Wake_SetsSignal_WithoutMarkingDirty()
    {
        using var scheduler = new FrameScheduler(new FakeTimeProvider());

        scheduler.Wake();

        Assert.False(scheduler.IsDirty);
        Assert.True(scheduler.Signal.WaitOne(0));
    }

    [Fact]
    public void MarkDirty_AfterDispose_DoesNotThrow()
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider());
        scheduler.Dispose();

        scheduler.MarkDirty();
        scheduler.Wake();
    }

    [Fact]
    public void IsDirty_DoesNotConsume_ShouldRenderDoes()
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider());
        scheduler.MarkDirty();

        Assert.True(scheduler.IsDirty);
        Assert.True(scheduler.IsDirty);
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
        Assert.False(scheduler.IsDirty);
    }

    [Fact]
    public void UntilNextSlot_BeforeAnyRender_IsZero()
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider());

        Assert.Equal(TimeSpan.Zero, scheduler.UntilNextSlot());
    }

    [Fact]
    public void UntilNextSlot_AfterRender_IsRemainingInterval()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);
        scheduler.OnRendered();
        Assert.Equal(scheduler.MinInterval, scheduler.UntilNextSlot());

        time.Advance(TimeSpan.FromMilliseconds(30));
        Assert.Equal(scheduler.MinInterval - TimeSpan.FromMilliseconds(30), scheduler.UntilNextSlot());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, scheduler.UntilNextSlot());
    }

    [Fact]
    public void Idle_NeverRenders()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        Assert.False(scheduler.ShouldRender(anyAnimating: false));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(scheduler.ShouldRender(anyAnimating: false));
    }

    [Fact]
    public void Dirty_RendersOnce()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
        scheduler.OnRendered();

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(scheduler.ShouldRender(anyAnimating: false));
    }

    [Fact]
    public void Dirty_WaitsForMinInterval()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
        scheduler.OnRendered();

        scheduler.MarkDirty();
        time.Advance(TimeSpan.FromMilliseconds(80));
        Assert.False(scheduler.ShouldRender(anyAnimating: false));

        time.Advance(TimeSpan.FromMilliseconds(4));
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
    }

    [Fact]
    public void MarkDirty_DuringRender_IsNotLost()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
        scheduler.MarkDirty(); // un module demande un nouveau dessin pendant le rendu
        scheduler.OnRendered();

        time.Advance(scheduler.MinInterval);
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
    }

    [Fact]
    public void Interval_CountsFromTheStartOfTheRender_NotFromItsEnd()
    {
        // Échéance absolue : un rendu de 50 ms ne rallonge pas l'intervalle (83 ms) de 50 ms de plus.
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);
        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));

        time.Advance(TimeSpan.FromMilliseconds(50)); // le rendu et l'envoi
        scheduler.OnRendered();

        Assert.Equal(scheduler.MinInterval - TimeSpan.FromMilliseconds(50), scheduler.UntilNextSlot());
        time.Advance(scheduler.MinInterval - TimeSpan.FromMilliseconds(50));
        Assert.Equal(TimeSpan.Zero, scheduler.UntilNextSlot());
        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));
    }

    [Fact]
    public void Interval_RenderLongerThanTheInterval_NextRenderIsImmediate()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);
        scheduler.MarkDirty();
        Assert.True(scheduler.ShouldRender(anyAnimating: false));

        time.Advance(TimeSpan.FromMilliseconds(200));
        scheduler.OnRendered();

        Assert.Equal(TimeSpan.Zero, scheduler.UntilNextSlot());
    }

    [Fact]
    public void Animating_CappedAt12fps()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        Assert.Equal(12, CountRendersOverOneSecond(time, scheduler));
    }

    [Fact]
    public void MaxFps_Configurable()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time, maxFps: 6);

        Assert.Equal(6, CountRendersOverOneSecond(time, scheduler));
    }

    [Fact]
    public void MinInterval_IsOneOverMaxFps()
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider());

        Assert.Equal(1000.0 / 12, scheduler.MinInterval.TotalMilliseconds, precision: 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidMaxFps_Throws(double maxFps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameScheduler(new FakeTimeProvider(), maxFps));
    }

    [Fact]
    public void SetMaxFps_ChangesRate_AtRuntime()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FrameScheduler(time);

        scheduler.SetMaxFps(4);

        Assert.Equal(4, CountRendersOverOneSecond(time, scheduler));
        Assert.Equal(250, scheduler.MinInterval.TotalMilliseconds, precision: 3);
    }

    [Theory]
    [InlineData(100, 12)]
    [InlineData(12.5, 12)]
    [InlineData(0.2, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void SetMaxFps_IsBoundedToOneAndTwelve(double requested, double effective)
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider(), maxFps: 6);

        scheduler.SetMaxFps(requested);

        Assert.Equal(1000.0 / effective, scheduler.MinInterval.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void SetMaxFps_NaN_KeepsCurrentRate()
    {
        var scheduler = new FrameScheduler(new FakeTimeProvider(), maxFps: 6);

        scheduler.SetMaxFps(double.NaN);

        Assert.Equal(1000.0 / 6, scheduler.MinInterval.TotalMilliseconds, precision: 3);
    }
}
