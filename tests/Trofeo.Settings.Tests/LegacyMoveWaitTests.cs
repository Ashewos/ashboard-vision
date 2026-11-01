using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

public sealed class LegacyMoveWaitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trofeo-settings-move-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacy;
    private readonly string _new;

    public LegacyMoveWaitTests()
    {
        _legacy = Path.Combine(_root, "trofeo-HB-dashboard");
        _new = Path.Combine(_root, "ashboard-vision");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true, false, true)]   // l'ancien dossier attend son déménagement : il faut attendre le service
    [InlineData(true, true, false)]   // les deux existent : le service a déjà tranché, rien à attendre
    [InlineData(false, true, false)]  // déjà déménagé
    [InlineData(false, false, false)] // première installation
    public void MustWait_OnlyWhenTheOldFolderIsStillAloneInPlace(bool legacyExists, bool newExists, bool expected) =>
        Assert.Equal(expected, LegacyMoveWait.MustWait(legacyExists, newExists));

    [Fact]
    public void Run_NothingToMove_ProceedsWithoutStartingTheService()
    {
        var started = 0;

        var outcome = LegacyMoveWait.Run(_legacy, _new, () => { started++; return true; }, _ => { });

        Assert.Equal(LegacyMoveOutcome.Proceed, outcome);
        Assert.Equal(0, started);
        Assert.False(Directory.Exists(_new)); // la décision ne crée rien
    }

    [Fact]
    public void Run_BothFoldersExist_ProceedsWithoutStartingTheService()
    {
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(_new);
        var started = 0;

        var outcome = LegacyMoveWait.Run(_legacy, _new, () => { started++; return true; }, _ => { });

        Assert.Equal(LegacyMoveOutcome.Proceed, outcome);
        Assert.Equal(0, started);
    }

    [Fact]
    public void Run_OldFolderOnly_StartsTheServiceAndWaitsForTheNewFolder()
    {
        Directory.CreateDirectory(_legacy);
        var started = 0;
        var sleeps = new List<TimeSpan>();

        var outcome = LegacyMoveWait.Run(_legacy, _new, () => { started++; return true; }, delay =>
        {
            sleeps.Add(delay);
            if (sleeps.Count == 3) Directory.Move(_legacy, _new); // le service déménage au troisième sondage
        });

        Assert.Equal(LegacyMoveOutcome.Proceed, outcome);
        Assert.Equal(1, started);
        Assert.Equal(3, sleeps.Count);
        Assert.All(sleeps, d => Assert.Equal(TimeSpan.FromMilliseconds(250), d));
    }

    [Fact]
    public void Run_NewFolderNeverAppears_GivesUpAfter15Seconds_AndCreatesNothing()
    {
        Directory.CreateDirectory(_legacy);
        var sleeps = new List<TimeSpan>();

        var outcome = LegacyMoveWait.Run(_legacy, _new, () => true, sleeps.Add);

        Assert.Equal(LegacyMoveOutcome.NotMoved, outcome);
        Assert.Equal(TimeSpan.FromSeconds(15), TimeSpan.FromTicks(sleeps.Sum(d => d.Ticks)));
        Assert.False(Directory.Exists(_new));
        Assert.True(Directory.Exists(_legacy)); // l'ancien dossier reste intact
    }

    [Fact]
    public void Run_ServiceCannotStart_DoesNotWait()
    {
        Directory.CreateDirectory(_legacy);
        var sleeps = 0;

        var outcome = LegacyMoveWait.Run(_legacy, _new, () => false, _ => sleeps++);

        Assert.Equal(LegacyMoveOutcome.ServiceMissing, outcome);
        Assert.Equal(0, sleeps);
        Assert.False(Directory.Exists(_new));
    }

    [Fact]
    public void MessageFor_SaysWhatToDo_AndTutoies()
    {
        Assert.Null(LegacyMoveWait.MessageFor(LegacyMoveOutcome.Proceed));
        Assert.Equal(Texts.MoveInProgress, LegacyMoveWait.MessageFor(LegacyMoveOutcome.NotMoved));
        Assert.Equal(Texts.MoveServiceMissing, LegacyMoveWait.MessageFor(LegacyMoveOutcome.ServiceMissing));
        Assert.Contains("Tes réglages", Texts.MoveInProgress);
        Assert.Contains("Relance", Texts.MoveInProgress);
    }
}
