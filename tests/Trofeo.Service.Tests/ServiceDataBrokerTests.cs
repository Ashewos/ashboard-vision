using AshboardVision.Sdk;
using Microsoft.Extensions.Time.Testing;
using Trofeo.Modules.NowPlaying;
using Trofeo.Sensors;

namespace Trofeo.Display.Tests;

public sealed class ServiceDataBrokerTests
{
    private sealed class FakeFeed : ISensorFeed
    {
        public SensorSnapshot? Latest { get; set; }

        public int Acquired { get; private set; }

        public int Released { get; private set; }

        public event EventHandler? Updated;

        public IDisposable Acquire()
        {
            Acquired++;
            return new Lease(this);
        }

        public void Publish(SensorSnapshot snapshot)
        {
            Latest = snapshot;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        private sealed class Lease(FakeFeed feed) : IDisposable
        {
            public void Dispose() => feed.Released++;
        }
    }

    private sealed class FakeMedia : IMediaSource
    {
        public MediaSnapshot? Current { get; set; }

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public Func<Task>? OnStart { get; set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public async Task StartAsync(CancellationToken ct)
        {
            Started = true;
            if (OnStart is not null)
            {
                await OnStart();
            }
        }

        public void Dispose() => Disposed = true;
    }

    private static readonly SensorSnapshot Reading = new("CPU test", "GPU test", new Dictionary<string, double> { ["cpu.load"] = 12.5 }, Elevated: false);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFeed _feed = new();
    private readonly List<FakeMedia> _created = [];
    private FakeMedia? _next;

    private ServiceDataBroker Broker()
    {
        return new ServiceDataBroker(_feed, () =>
        {
            var media = _next ?? new FakeMedia();
            _created.Add(media);
            return media;
        }, _time);
    }

    [Fact]
    public void Nothing_IsAcquiredOrCreated_UntilAModuleAsks()
    {
        using var broker = Broker();

        Assert.Equal(0, _feed.Acquired);
        Assert.Empty(_created);
    }

    [Fact]
    public void Sensors_FirstCall_AcquiresTheFeedOnceAndCopiesTheReading()
    {
        _feed.Latest = Reading;
        using var broker = Broker();

        var first = broker.Sensors();
        var second = broker.Sensors();

        Assert.Equal(DataStatus.Ok, first.Status);
        Assert.Equal(("CPU test", "GPU test", 12.5), (first.Value!.CpuName, first.Value.GpuName, first.Value.Values["cpu.load"]));
        Assert.Equal(DataStatus.Ok, second.Status);
        Assert.Equal(1, _feed.Acquired);
    }

    [Fact]
    public void Sensors_NoReadingYet_WaitsForTheFirstOne()
    {
        using var broker = Broker();

        // Un fil à lui, pas la réserve de fils : sur une machine chargée (tests en parallèle qui bloquent des fils), un Task.Run et
        // son Task.Delay pouvaient dépasser l'attente de 2 s du courtier, et le test échouait sans que le courtier y soit pour rien.
        var publisher = new Thread(() =>
        {
            Thread.Sleep(100);
            _feed.Publish(Reading);
        }) { IsBackground = true };
        publisher.Start();

        var result = broker.Sensors();
        publisher.Join();

        Assert.Equal(DataStatus.Ok, result.Status);
        Assert.Equal(12.5, result.Value!.Values["cpu.load"]);
    }

    [Fact]
    public void NowPlaying_NothingPlaying_IsUnavailable_AndTheSourceIsCreatedOnce()
    {
        using var broker = Broker();

        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);
        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);

        var media = Assert.Single(_created);
        Assert.True(media.Started);
    }

    [Fact]
    public void NowPlaying_Playing_ExtrapolatesThePosition_AndStopsAtTheDuration()
    {
        var media = new FakeMedia();
        _next = media;
        using var broker = Broker();
        var cover = new byte[] { 1, 2, 3 };
        media.Current = new MediaSnapshot("Titre", "Artiste", "Spotify", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(200), _time.GetUtcNow(), true, cover);
        _time.Advance(TimeSpan.FromSeconds(10));

        var playing = broker.NowPlaying();
        _time.Advance(TimeSpan.FromSeconds(500));
        var late = broker.NowPlaying();

        Assert.Equal(DataStatus.Ok, playing.Status);
        Assert.Equal(("Titre", "Artiste", "Spotify", TimeSpan.FromSeconds(40), true), (playing.Value!.Title, playing.Value.Artist, playing.Value.AppName, playing.Value.Position, playing.Value.IsPlaying));
        Assert.Same(cover, playing.Value.CoverPng);
        Assert.Equal(TimeSpan.FromSeconds(200), late.Value!.Position);
    }

    [Fact]
    public void NowPlaying_Paused_KeepsThePosition()
    {
        var media = new FakeMedia();
        _next = media;
        using var broker = Broker();
        media.Current = new MediaSnapshot("Titre", "Artiste", "Spotify", TimeSpan.FromSeconds(30), TimeSpan.Zero, _time.GetUtcNow(), false, null);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(30), broker.NowPlaying().Value!.Position);
    }

    [Fact]
    public void NowPlaying_SourceFailsToStart_IsUnavailable_AndRetriesOnlyLater()
    {
        _next = new FakeMedia { OnStart = () => throw new InvalidOperationException("pas de contrôles média") };
        using var broker = Broker();

        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);
        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);

        Assert.Single(_created);
        Assert.True(_created[0].Disposed);

        _time.Advance(ServiceDataBroker.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);
        Assert.Equal(2, _created.Count);
    }

    [Fact]
    public void Idle_ReleasesTheFeedAndTheSource_AfterThirtySeconds()
    {
        _feed.Latest = Reading;
        var media = new FakeMedia();
        _next = media;
        using var broker = Broker();
        broker.Sensors();
        broker.NowPlaying();

        _time.Advance(TimeSpan.FromSeconds(20));
        broker.Sensors(); // une demande remet le compteur à zéro
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, _feed.Released);
        Assert.False(media.Disposed);

        _time.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(1, _feed.Released);
        Assert.True(media.Disposed);
        broker.Sensors(); // la demande suivante reprend un ticket
        Assert.Equal(2, _feed.Acquired);
    }

    [Fact]
    public void Dispose_ReleasesEverything_AndLaterCallsAreUnavailable()
    {
        _feed.Latest = Reading;
        var media = new FakeMedia();
        _next = media;
        var broker = Broker();
        broker.Sensors();
        broker.NowPlaying();

        broker.Dispose();

        Assert.Equal(1, _feed.Released);
        Assert.True(media.Disposed);
        Assert.Equal(DataStatus.Unavailable, broker.Sensors().Status);
        Assert.Equal(DataStatus.Unavailable, broker.NowPlaying().Status);
        Assert.Equal(1, _feed.Acquired);
    }
}
