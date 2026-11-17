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
        Assert.Equal(Integrated, _controller.VideoAdapter); // le dessin seul est en cause : les vidéos gardent leur carte
        Assert.Equal("La carte graphique n'a pas pu démarrer (pilote absent) : dessin sur le processeur.", _controller.Status.Problem);
        Assert.True(_controller.Status.Available);
        Assert.False(_controller.Status.Drawing);
        Assert.True(_controller.Status.VideoDecoding);
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
    public void VideoAdapterFor_NewSettings_ResolvesLikeApplyWithoutChangingAnything()
    {
        _controller.Apply(AccelerationSettings.Default);
        var device = _controller.DrawingDevice;
        var changes = _choiceChanges;
        var createdBefore = _factory.Created.Count;

        var wanted = _controller.VideoAdapterFor(new AccelerationSettings(Adapter: Discrete.Id));

        Assert.Equal(Discrete, wanted);
        Assert.Equal(Integrated, _controller.VideoAdapter);      // rien n'a changé
        Assert.Same(device, _controller.DrawingDevice);
        Assert.Equal(changes, _choiceChanges);                    // aucun événement
        Assert.Equal(createdBefore, _factory.Created.Count);      // aucun appareil ouvert
        Assert.Equal(Discrete, ApplyAndRead(new AccelerationSettings(Adapter: Discrete.Id))); // c'est bien ce qu'Apply prend ensuite
    }

    private GpuAdapterInfo? ApplyAndRead(AccelerationSettings settings)
    {
        _controller.Apply(settings);
        return _controller.VideoAdapter;
    }

    [Fact]
    public void VideoAdapterFor_OffOrNoVideoDecoding_IsNull()
    {
        _controller.Apply(AccelerationSettings.Default);

        Assert.Null(_controller.VideoAdapterFor(new AccelerationSettings(Enabled: false)));
        Assert.Null(_controller.VideoAdapterFor(new AccelerationSettings(VideoDecoding: false)));
        Assert.Equal(Integrated, _controller.VideoAdapterFor(new AccelerationSettings(Drawing: false))); // le dessin n'entre pas en compte
        Assert.Equal(Integrated, _controller.VideoAdapterFor(null));                                       // pas de réglages : les réglages par défaut
    }

    [Fact]
    public void VideoAdapterFor_CurrentSettings_FollowsTheFallbackAndSlowState()
    {
        _controller.Apply(AccelerationSettings.Default);
        Slow(100); // la première carte est trop lente : la suivante prend le relais
        _controller.Tick();
        Assert.Equal(Discrete, _controller.VideoAdapter);

        // Les mêmes réglages que ceux en place : la réponse est celle de l'état courant, pas celle d'un choix tout neuf.
        Assert.Equal(Discrete, _controller.VideoAdapterFor(AccelerationSettings.Default));

        _controller.ReportFailure("pilote planté");
        Assert.Equal(Discrete, _controller.VideoAdapter);
        Assert.Equal(Discrete, _controller.VideoAdapterFor(AccelerationSettings.Default));                // le dessin est en panne, pas le décodage
        Assert.Equal(Integrated, _controller.VideoAdapterFor(new AccelerationSettings(VideoDecoding: true, Drawing: false))); // d'autres réglages repartent de zéro
    }

    [Fact]
    public void VideoAdapterFor_SavedCardGone_IsTheAutomaticCardWithoutWarning()
    {
        var gone = new GpuAdapterId(0x10DE, 99, 1, "NVIDIA RTX 3080");

        var wanted = _controller.VideoAdapterFor(new AccelerationSettings(Adapter: gone));

        Assert.Equal(Integrated, wanted);
        Assert.Empty(_log); // l'avertissement viendra avec Apply : ici on ne fait que calculer
    }

    [Fact]
    public void VideoAdapterFor_WithoutFactory_IsNull()
    {
        using var controller = new AccelerationController(null, _time, _log.Add);

        Assert.Null(controller.VideoAdapterFor(AccelerationSettings.Default));
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
        Assert.Equal(Integrated, _controller.VideoAdapter);       // les vidéos ne suivent pas le dessin
        Assert.Equal(Integrated, _controller.Status.Adapter);
        Assert.False(_controller.Status.Drawing);
        Assert.True(_controller.Status.VideoDecoding);
        Assert.Equal("La carte graphique a eu un problème (carte retirée) : dessin sur le processeur.", _controller.Status.Problem);
        Assert.Equal(changes, _choiceChanges);                    // la carte des vidéos n'a pas changé : rien à rouvrir
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

        _controller.ReportFailure("une fois");
        _controller.ReportFailure("deux fois");

        Assert.Contains("(une fois)", _controller.Status.Problem);
        Assert.Single(_log, line => line.Contains("une fois"));
        Assert.DoesNotContain(_log, line => line.Contains("deux fois"));
    }

    [Fact]
    public void DrawingFailure_LeavesTheVideoItsCard()
    {
        _controller.Apply(AccelerationSettings.Default);

        _controller.ReportFailure("pilote planté");

        var status = _controller.Status;
        Assert.False(status.Drawing);
        Assert.True(status.VideoDecoding);
        Assert.Equal(Integrated, status.Adapter);
        Assert.Equal(Integrated, _controller.VideoAdapter);
        Assert.Equal(Integrated, _controller.VideoAdapterFor(AccelerationSettings.Default));
        Assert.Equal("La carte graphique a eu un problème (pilote planté) : dessin sur le processeur.", status.Problem);
    }

    [Fact]
    public void DrawingFailure_ThenRetry_KeepsTheVideoCardWithoutReopeningIt()
    {
        _controller.Apply(AccelerationSettings.Default);
        var changes = _choiceChanges;

        _controller.ReportFailure("pilote planté");
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_controller.Tick());

        Assert.NotNull(_controller.DrawingDevice);
        Assert.Equal(Integrated, _controller.VideoAdapter);
        Assert.Equal(changes, _choiceChanges);
    }

    [Fact]
    public void VideoOnCpu_StateLineSaysSo()
    {
        _controller.Apply(AccelerationSettings.Default);

        // Sans vidéo ouverte, la fenêtre montre ce qui est voulu : rien ne le contredit.
        Assert.True(_controller.Status.VideoDecoding);

        // Une vidéo est ouverte et la carte la décode : comme voulu.
        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: Integrated.Luid);
        Assert.True(_controller.Status.VideoDecoding);

        // La carte a refusé le fichier (ou le décodage a lâché en route) : la vidéo tourne sur le processeur.
        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: null);
        Assert.False(_controller.Status.VideoDecoding);
        Assert.True(_controller.Status.Drawing);
        Assert.Equal(Integrated, _controller.Status.Adapter);   // le dessin reste sur la carte
        Assert.Equal(Integrated, _controller.VideoAdapter);      // c'est toujours la carte voulue (la vidéo la retentera si elle change)

        // Plus de vidéo : on revient à l'état voulu.
        _controller.ReportVideoDecoder(videoOpen: false, decoderLuid: null);
        Assert.True(_controller.Status.VideoDecoding);
    }

    [Fact]
    public void VideoOnCpu_WithDrawingOff_ShowsNoCard()
    {
        _controller.Apply(new AccelerationSettings(Drawing: false));
        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: Integrated.Luid);
        Assert.True(_controller.Status.VideoDecoding);
        Assert.Equal(Integrated, _controller.Status.Adapter);

        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: null);

        Assert.False(_controller.Status.VideoDecoding);
        Assert.False(_controller.Status.Drawing);
        Assert.Null(_controller.Status.Adapter);
    }

    [Fact]
    public void VideoOnAnotherCard_IsNotShownOnTheWantedOne()
    {
        _controller.Apply(new AccelerationSettings(Adapter: Discrete.Id));

        // La carte voulue vient de changer : la vidéo ouverte se décode encore sur l'ancienne, jusqu'à sa réouverture.
        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: Integrated.Luid);
        Assert.False(_controller.Status.VideoDecoding);

        _controller.ReportVideoDecoder(videoOpen: true, decoderLuid: Discrete.Luid);
        Assert.True(_controller.Status.VideoDecoding);
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
    public void Failure_RetryFindsTheCardAgainUnderANewLuid()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("mise à jour du pilote");

        // Le pilote revient : même carte (mêmes identifiants), mais un autre LUID.
        var back = Integrated with { Luid = 999 };
        _factory.Adapters[0] = back;
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(_controller.Tick());
        Assert.Equal(back, _factory.Created[^1].Adapter);
        Assert.Same(_factory.Created[^1].Device, _controller.DrawingDevice);
        Assert.Equal(back, _controller.VideoAdapter);
        Assert.Equal(back, _controller.Status.Adapters[0]); // la liste montrée à la fenêtre est à jour
        Assert.Null(_controller.Status.Problem);
    }

    [Fact]
    public void Failure_RetryWithNoCardLeft_TriesAgainLater()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("carte retirée");
        _factory.Adapters.Clear();

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_controller.Tick());
        Assert.Null(_controller.DrawingDevice);
        Assert.StartsWith("La carte graphique n'a pas pu démarrer", _controller.Status.Problem);
        Assert.False(_controller.Status.Available);

        _factory.Adapters.Add(Integrated);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_controller.Tick());
        Assert.NotNull(_controller.DrawingDevice);
    }

    [Fact]
    public void Failure_AfterAnHourWithoutProblem_RetriesCountFromZeroAgain()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("première panne");
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_controller.Tick()); // le premier essai réussit
        Assert.Equal(2, _factory.Created.Count);

        // Une heure sans panne : la carte tourne, les Tick passent (ici toutes les minutes).
        for (var minute = 0; minute < 60; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            Assert.False(_controller.Tick());
        }

        // Une nouvelle panne a de nouveau ses trois essais.
        _controller.ReportFailure("seconde panne");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            Assert.True(_controller.Tick());
            Assert.Equal(2 + attempt, _factory.Created.Count);
            _controller.ReportFailure($"encore {attempt}");
        }

        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(_controller.Tick());
        Assert.Equal(5, _factory.Created.Count);
    }

    [Fact]
    public void Failure_BeforeAnHourWithoutProblem_KeepsCounting()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("première panne");
        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick();
        _time.Advance(TimeSpan.FromMinutes(59));
        _controller.Tick();

        _controller.ReportFailure("seconde panne"); // le compte continue : il reste deux essais
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            Assert.True(_controller.Tick());
            _controller.ReportFailure($"encore {attempt}");
        }

        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(_controller.Tick());
        Assert.Equal(4, _factory.Created.Count);
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
        Assert.Equal(Discrete, _controller.VideoAdapter); // trop lente pour dessiner : elle décode encore les vidéos
        Assert.True(_controller.Status.VideoDecoding);
        Assert.False(_controller.Status.Drawing);
        Assert.Equal("Aucune carte n'est assez rapide : dessin sur le processeur.", _controller.Status.Problem);
        Assert.Equal(changes + 1, _choiceChanges);
        Assert.Equal(AccelerationProblemKind.Slow, _controller.Status.ProblemKind);

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
    public void Failure_HasKindAndTime()
    {
        _controller.Apply(AccelerationSettings.Default);
        _time.Advance(TimeSpan.FromMinutes(3));
        var failedAt = _time.GetUtcNow();

        _controller.ReportFailure("pilote");

        Assert.Equal(AccelerationProblemKind.Failure, _controller.Status.ProblemKind);
        Assert.Equal(failedAt, _controller.Status.ProblemAt);
    }

    [Fact]
    public void CreateFails_IsAFailureDatedFromTheStart()
    {
        _factory.FailCreate = true;

        _controller.Apply(AccelerationSettings.Default);

        Assert.Equal(AccelerationProblemKind.Failure, _controller.Status.ProblemKind);
        Assert.Equal(_time.GetUtcNow(), _controller.Status.ProblemAt);
    }

    [Fact]
    public void FailedRetry_KeepsTheTime()
    {
        // Un essai qui échoue avant que l'appareil ne s'ouvre est la même carte morte, pas une nouvelle panne : la cloche ne resonne pas.
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");
        var first = _controller.Status.ProblemAt;
        _factory.FailCreate = true;

        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick(); // le nouvel essai échoue

        Assert.Equal(AccelerationProblemKind.Failure, _controller.Status.ProblemKind);
        Assert.Equal(first, _controller.Status.ProblemAt);

        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick(); // et encore
        Assert.Equal(first, _controller.Status.ProblemAt);
    }

    [Fact]
    public void FailureAfterASuccessfulRetry_GetsANewTime()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");
        var first = _controller.Status.ProblemAt;

        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick(); // le nouvel essai réussit : l'appareil est rouvert
        Assert.Null(_controller.Status.ProblemKind);

        _time.Advance(TimeSpan.FromMinutes(10));
        _controller.ReportFailure("pilote"); // une seconde panne : un nouvel événement

        Assert.Equal(AccelerationProblemKind.Failure, _controller.Status.ProblemKind);
        Assert.Equal(first + TimeSpan.FromMinutes(15), _controller.Status.ProblemAt);
    }

    [Fact]
    public void FailureAfterTheUserReappliesSettings_GetsANewTime()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");
        var first = _controller.Status.ProblemAt;
        _factory.FailCreate = true;

        _time.Advance(TimeSpan.FromMinutes(2));
        _controller.Apply(AccelerationSettings.Default with { VideoDecoding = false }); // d'autres réglages : on repart de zéro, et la carte ne démarre pas

        Assert.Equal(AccelerationProblemKind.Failure, _controller.Status.ProblemKind);
        Assert.Equal(first + TimeSpan.FromMinutes(2), _controller.Status.ProblemAt);
    }

    [Fact]
    public void Republish_KeepsTheTime()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");
        var at = _controller.Status.ProblemAt;

        _time.Advance(TimeSpan.FromMinutes(2));
        _controller.Tick();                       // rien à faire : republie sans changement
        _controller.ReportFrame(TimeSpan.FromMilliseconds(5));
        _controller.ReportVideoDecoder(false, null);

        Assert.Equal(at, _controller.Status.ProblemAt);
    }

    [Fact]
    public void Slow_Republish_KeepsTheTime()
    {
        _controller.Apply(new AccelerationSettings(Adapter: Discrete.Id));
        Slow(90);
        _controller.Tick();
        var at = _controller.Status.ProblemAt;
        Assert.NotNull(at);

        Slow(90);
        _controller.Tick(); // toujours lente : la même phrase

        Assert.Equal(AccelerationProblemKind.Slow, _controller.Status.ProblemKind);
        Assert.Equal(at, _controller.Status.ProblemAt);
    }

    [Fact]
    public void Slow_And_Gone_Kinds()
    {
        var gone = new GpuAdapterId(0x10DE, 99, 1, "NVIDIA RTX 3080");
        _controller.Apply(new AccelerationSettings(Adapter: gone));
        Assert.Equal(AccelerationProblemKind.Gone, _controller.Status.ProblemKind);
        Assert.Equal(_time.GetUtcNow(), _controller.Status.ProblemAt);

        _controller.Apply(new AccelerationSettings(Adapter: Discrete.Id));
        Slow(90);
        _controller.Tick();
        Assert.Equal(AccelerationProblemKind.Slow, _controller.Status.ProblemKind);
        Assert.NotNull(_controller.Status.ProblemAt);
    }

    [Fact]
    public void NoProblem_NullKindAndTime()
    {
        _controller.Apply(AccelerationSettings.Default);

        Assert.Null(_controller.Status.Problem);
        Assert.Null(_controller.Status.ProblemKind);
        Assert.Null(_controller.Status.ProblemAt);
    }

    [Fact]
    public void ProblemGone_ClearsKindAndTime()
    {
        _controller.Apply(AccelerationSettings.Default);
        _controller.ReportFailure("pilote");

        _time.Advance(TimeSpan.FromMinutes(5));
        _controller.Tick(); // le nouvel essai réussit

        Assert.Null(_controller.Status.Problem);
        Assert.Null(_controller.Status.ProblemKind);
        Assert.Null(_controller.Status.ProblemAt);
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
