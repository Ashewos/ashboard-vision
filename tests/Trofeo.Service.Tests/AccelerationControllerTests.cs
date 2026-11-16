using Microsoft.Extensions.Time.Testing;
using Trofeo.Core.Layout;

namespace Trofeo.Display.Tests;

/// <summary>Le choix de la carte, le repli sur le processeur et les nouveaux essais (plan 10, tâche 5).</summary>
public sealed class AccelerationControllerTests : IDisposable
{
    private static readonly GpuAdapterInfo Integrated = FakeGpuDeviceFactory.Card("Intel UHD", vendor: 0x8086, device: 10);
    private static readonly GpuAdapterInfo Discrete = FakeGpuDeviceFactory.Card("NVIDIA RTX", vendor: 0x10DE, device: 20);

    private readonly FakeGpuDeviceFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _log = [];
    private readonly AccelerationController _controller;
    private int _choiceChanges;

    public AccelerationControllerTests()
    {
        _factory.Adapters.AddRange([Integrated, Discrete]);
        _controller = new AccelerationController(_factory, _time, _log.Add);
        _controller.EffectiveChoiceChanged += () => _choiceChanges++;
    }

    public void Dispose() => _controller.Dispose();

    private void Slow(double ms, int seconds = 11)
    {
        for (var i = 0; i < seconds; i++)
        {
            _controller.ReportFrame(TimeSpan.FromMilliseconds(ms));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Auto_TakesTheFirstAdapter()
    {
        _controller.Apply(AccelerationSettings.Default);

        Assert.Same(_factory.Created[0].Device, _controller.DrawingDevice);
        Assert.Equal(Integrated, _factory.Created[0].Adapter);
        Assert.Equal(Integrated, _controller.VideoAdapter);
        var status = _controller.Status;
        Assert.True(status.Available);
        Assert.True(status.Enabled);
        Assert.True(status.Drawing);
        Assert.True(status.VideoDecoding);
        Assert.Equal(Integrated, status.Adapter);
        Assert.Equal([Integrated, Discrete], status.Adapters);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void Manual_FindsTheSavedCard()
    {
        _controller.Apply(new AccelerationSettings(Adapter: Discrete.Id));

        Assert.Equal(Discrete, _factory.Created[0].Adapter);
        Assert.Equal(Discrete, _controller.VideoAdapter);
        Assert.Null(_controller.Status.Problem);
    }

    [Fact]
    public void Manual_FindsTheSavedCardByNameWhenTheIdsMoved()
    {
        // Le pilote a changé le sous-système : les identifiants ne correspondent plus, le nom si.
        var saved = new GpuAdapterId(Discrete.Id.VendorId, Discrete.Id.DeviceId, 999, Discrete.Id.Name);

        _controller.Apply(new AccelerationSettings(Adapter: saved));

        Assert.Equal(Discrete, _controller.VideoAdapter);
        Assert.Empty(_log);
    }

    [Fact]
    public void SavedCardGone_FallsBackToAutoWithWarning()
    {
        var gone = new GpuAdapterId(0x10DE, 99, 1, "NVIDIA RTX 3080");

        _controller.Apply(new AccelerationSettings(Adapter: gone));

        const string sentence = "La carte NVIDIA RTX 3080 n'est plus là : la carte automatique est utilisée.";
        Assert.Equal(Integrated, _controller.VideoAdapter);
        Assert.Equal(sentence, _controller.Status.Problem);
        Assert.Contains(sentence, _log);
        Assert.NotNull(_controller.DrawingDevice);
    }

    [Fact]
    public void NoAdapter_StaysOnCpu()
    {
        _factory.Adapters.Clear();

        _controller.Apply(AccelerationSettings.Default);

        Assert.Null(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        var status = _controller.Status;
        Assert.False(status.Available);
        Assert.Empty(status.Adapters);
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public void NoFactory_StaysOnCpu()
    {
        using var controller = new AccelerationController(null, _time, _log.Add);

        controller.Apply(AccelerationSettings.Default);

        Assert.Null(controller.DrawingDevice);
        Assert.False(controller.Status.Available);
    }

    [Fact]
    public void CreateFails_StaysOnCpuWithProblem()
    {
        _factory.FailCreate = true;

        _controller.Apply(AccelerationSettings.Default);

        Assert.Null(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        Assert.Equal("La carte graphique n'a pas pu démarrer (pilote absent) : dessin sur le processeur.", _controller.Status.Problem);
        Assert.True(_controller.Status.Available);
    }

    [Fact]
    public void Disabled_OrDrawingOff_HasNoDrawingDevice()
    {
        _controller.Apply(new AccelerationSettings(Enabled: false));
        Assert.Null(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        Assert.False(_controller.Status.Enabled);
        Assert.Empty(_factory.Created);

        _controller.Apply(new AccelerationSettings(Drawing: false));
        Assert.Null(_controller.DrawingDevice);
        Assert.Equal(Integrated, _controller.VideoAdapter); // les vidéos peuvent rester sur la carte
        Assert.False(_controller.Status.Drawing);
        Assert.True(_controller.Status.VideoDecoding);
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public void VideoDecodingOff_HasNoVideoAdapter()
    {
        _controller.Apply(new AccelerationSettings(VideoDecoding: false));

        Assert.NotNull(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        Assert.False(_controller.Status.VideoDecoding);
        Assert.True(_controller.Status.Drawing);
    }

    [Fact]
    public void Apply_SameSettings_KeepsTheDevice()
    {
        _controller.Apply(AccelerationSettings.Default);
        var device = _controller.DrawingDevice;

        _controller.Apply(new AccelerationSettings());
        _controller.Apply(null); // une disposition sans réglages vaut les réglages par défaut

        Assert.Same(device, _controller.DrawingDevice);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public void Apply_VideoToggle_KeepsTheDrawingDeviceAndSignalsTheVideo()
    {
        _controller.Apply(AccelerationSettings.Default);
        var device = _controller.DrawingDevice;
        var changes = _choiceChanges;

        _controller.Apply(new AccelerationSettings(VideoDecoding: false));

        Assert.Same(device, _controller.DrawingDevice);
        Assert.Single(_factory.Created);
        Assert.Equal(changes + 1, _choiceChanges);
    }

    [Fact]
    public void ReportFailure_FallsBackToCpuAndKeepsTheDeviceUntilReleased()
    {
        _controller.Apply(AccelerationSettings.Default);
        var device = _factory.Created[0].Device;
        var changes = _choiceChanges;

        _controller.ReportFailure("carte retirée");

        Assert.Null(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        Assert.Null(_controller.Status.Adapter);
        Assert.Equal("La carte graphique a eu un problème (carte retirée) : dessin sur le processeur.", _controller.Status.Problem);
        Assert.Equal(changes + 1, _choiceChanges);
        Assert.False(device.IsDisposed); // des compositeurs s'en servent peut-être encore : la boucle les libère d'abord

        _controller.ReleaseRetired();
        Assert.Equal(1, device.DisposeCount);

        _controller.ReleaseRetired();
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public void ReportFailure_Twice_IsReportedOnce()
    {
        _controller.Apply(AccelerationSettings.Default);
        var changes = _choiceChanges;

        _controller.ReportFailure("une fois");
        _controller.ReportFailure("deux fois");

        Assert.Contains("(une fois)", _controller.Status.Problem);
        Assert.Equal(changes + 1, _choiceChanges);
    }

    [Fact]
    public void Failure_RetriesEveryFiveMinutes_ThreeTimesAtMost()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");

        _time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.False(_controller.Tick());
        Assert.Null(_controller.DrawingDevice);

        _time.Advance(TimeSpan.FromSeconds(1)); // 5 min
        Assert.True(_controller.Tick());
        Assert.NotNull(_controller.DrawingDevice);
        Assert.Null(_controller.Status.Problem);
        Assert.Equal(2, _factory.Created.Count);

        _controller.ReportFailure("encore"); // et il échoue encore
        _time.Advance(TimeSpan.FromMinutes(5)); // 10 min
        Assert.True(_controller.Tick());
        Assert.Equal(3, _factory.Created.Count);

        _controller.ReportFailure("toujours");
        _time.Advance(TimeSpan.FromMinutes(5)); // 15 min
        Assert.True(_controller.Tick());
        Assert.Equal(4, _factory.Created.Count);

        _controller.ReportFailure("pour de bon"); // le troisième essai a échoué : fini
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(_controller.Tick());
        Assert.Null(_controller.DrawingDevice);
        Assert.Equal(4, _factory.Created.Count);
        Assert.Contains("(pour de bon)", _controller.Status.Problem);

        // D'autres réglages remettent le compte à zéro.
        _controller.Apply(new AccelerationSettings(VideoDecoding: false));
        Assert.NotNull(_controller.DrawingDevice);
        _controller.ReportFailure("encore une fois");
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_controller.Tick());
        Assert.NotNull(_controller.DrawingDevice);
    }

    [Fact]
    public void Failure_RetryThatCannotCreate_TriesAgainFiveMinutesLater()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");
        _factory.FailCreate = true;

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_controller.Tick()); // pas d'appareil : rien n'a changé
        Assert.StartsWith("La carte graphique n'a pas pu démarrer (pilote absent)", _controller.Status.Problem);

        _factory.FailCreate = false;
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(_controller.Tick());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_controller.Tick());
        Assert.NotNull(_controller.DrawingDevice);
        Assert.Null(_controller.Status.Problem);
    }

    [Fact]
    public void Auto_TooSlow_TriesNextCardThenCpu()
    {
        _controller.Apply(AccelerationSettings.Default);
        var changes = _choiceChanges;

        Slow(90);
        Assert.True(_controller.Tick());

        Assert.Equal(Discrete, _factory.Created[1].Adapter);
        Assert.Same(_factory.Created[1].Device, _controller.DrawingDevice);
        Assert.Equal(Discrete, _controller.VideoAdapter);
        Assert.Equal(changes + 1, _choiceChanges);
        Assert.Null(_controller.Status.Problem);

        Slow(90); // la fenêtre repart de zéro avec la nouvelle carte
        Assert.True(_controller.Tick());

        Assert.Null(_controller.DrawingDevice);
        Assert.Null(_controller.VideoAdapter);
        Assert.Equal("Aucune carte n'est assez rapide : dessin sur le processeur.", _controller.Status.Problem);
        Assert.Equal(changes + 2, _choiceChanges);

        _time.Advance(TimeSpan.FromHours(1)); // pas de nouvel essai : ce n'est pas une panne
        Assert.False(_controller.Tick());
        Assert.Equal(2, _factory.Created.Count);
    }

    [Fact]
    public void Auto_FastEnough_StaysPut()
    {
        _controller.Apply(AccelerationSettings.Default);

        Slow(40);

        Assert.False(_controller.Tick());
        Assert.Same(_factory.Created[0].Device, _controller.DrawingDevice);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public void Auto_FewFrames_AreNotJudged()
    {
        _controller.Apply(AccelerationSettings.Default);

        _controller.ReportFrame(TimeSpan.FromMilliseconds(400)); // une image de démarrage lente ne condamne pas la carte
        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.False(_controller.Tick());
        Assert.Single(_factory.Created);
    }

    [Fact]
    public void Manual_TooSlow_StaysButSaysSo()
    {
        _controller.Apply(new AccelerationSettings(Adapter: Discrete.Id));
        var device = _controller.DrawingDevice;

        Slow(90);

        Assert.False(_controller.Tick());
        Assert.Same(device, _controller.DrawingDevice);
        Assert.Equal("NVIDIA RTX est trop lente pour tenir le rythme.", _controller.Status.Problem);

        Slow(40); // elle a repris son rythme : la mention disparaît
        _controller.Tick();
        Assert.Null(_controller.Status.Problem);
    }

    [Fact]
    public void TwentyToggles_LeaveOneDeviceAtMost()
    {
        for (var i = 0; i < 20; i++)
        {
            _controller.Apply(new AccelerationSettings(Enabled: false));
            _controller.ReleaseRetired(); // ce que fait la boucle une fois ses compositeurs refaits
            Assert.Equal(0, _factory.Alive);

            _controller.Apply(AccelerationSettings.Default);
            _controller.ReleaseRetired();
            Assert.Equal(1, _factory.Alive);
        }

        Assert.Equal(20, _factory.Created.Count);
        Assert.Same(_factory.Created[^1].Device, _controller.DrawingDevice);
        Assert.All(_factory.Created.Take(19), c => Assert.Equal(1, c.Device.DisposeCount));
    }

    [Fact]
    public void Dispose_ReleasesTheCurrentAndRetiredDevices()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("panne"); // l'appareil est retiré, pas encore libéré
        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick(); // un second appareil

        _controller.Dispose();

        Assert.Equal(0, _factory.Alive);
        Assert.All(_factory.Created, c => Assert.Equal(1, c.Device.DisposeCount));
        Assert.Null(_controller.DrawingDevice);
    }
}
