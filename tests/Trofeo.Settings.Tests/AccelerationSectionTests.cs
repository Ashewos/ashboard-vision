using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>La section « Accélération matérielle » de l'onglet Paramètres, sans fenêtre : faux lien, vrai fichier dans un dossier temporaire.</summary>
public sealed class AccelerationSectionTests : IDisposable
{
    private const string Problem = "La carte graphique a eu un problème (pilote réinitialisé) : dessin sur le processeur.";

    private static readonly GpuAdapterEntry Intel = new(0x8086, 0x7D55, 0x1234, "Intel(R) Graphics");
    private static readonly GpuAdapterEntry Nvidia = new(0x10DE, 0x2F04, 0x5678, "NVIDIA GeForce RTX 5070");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-acceleration-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public AccelerationSectionTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AccelerationInfo Working(
        bool enabled = true, string? card = "Intel(R) Graphics", bool drawing = true, bool video = true, string? problem = null,
        params GpuAdapterEntry[] adapters) =>
        new(Available: true, enabled, card, drawing, video, problem, adapters.Length > 0 ? adapters : [Intel, Nvidia]);

    /// <summary>Un fond fixe : la carte ne dessine pas, elle attend un GIF ou une vidéo (les vidéos, elles, peuvent déjà être sur la carte).</summary>
    private static AccelerationInfo WaitingForAnimation(string? card = "Intel(R) Graphics", bool video = true, string? problem = null) =>
        Working(card: card, drawing: false, video: video, problem: problem) with { DrawingWaitsForAnimation = true };

    private AccelerationSectionViewModel Open(AccelerationInfo? info, AccelerationSettings? saved = null)
    {
        _store.Save(DashboardLayout.Default with { Acceleration = saved });
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { Acceleration = info });
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new AccelerationSectionViewModel(_link, _session);
    }

    private void Push(AccelerationInfo? info) =>
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status() with { Acceleration = info });

    // ---- La liste des cartes ----

    [Fact]
    public void Entries_AutoFirstThenEachCard()
    {
        var section = Open(Working());

        Assert.Equal(["Automatique (Intel(R) Graphics)", "Intel(R) Graphics", "NVIDIA GeForce RTX 5070"], section.Entries.Select(e => e.Label));
        Assert.Null(section.Entries[0].Id);
        Assert.Equal(new GpuAdapterId(0x10DE, 0x2F04, 0x5678, "NVIDIA GeForce RTX 5070"), section.Entries[2].Id);
        Assert.Same(section.Entries[0], section.SelectedAdapter); // rien d'enregistré : Automatique
    }

    [Fact]
    public void Entries_SavedCard_IsTheSelectedOne()
    {
        var section = Open(Working(), new AccelerationSettings(Adapter: new GpuAdapterId(0x10DE, 0x2F04, 0x5678, "NVIDIA GeForce RTX 5070")));

        Assert.Equal("NVIDIA GeForce RTX 5070", section.SelectedAdapter?.Label);
    }

    [Fact]
    public void Entries_SavedCardIsGone_AutomaticIsSelected()
    {
        // La carte enregistrée n'est plus dans la liste (carte retirée, disposition copiée d'un autre PC) : le service le dit dans Problem.
        const string gone = "La carte GeForce GTX 1080 n'est plus là : la carte automatique est utilisée.";
        var section = Open(Working(problem: gone), new AccelerationSettings(Adapter: new GpuAdapterId(0x10DE, 0x1B80, 0x1, "GeForce GTX 1080")));

        Assert.Same(section.Entries[0], section.SelectedAdapter);
        Assert.Equal(gone, section.Problem);
    }

    [Fact]
    public void Entries_NewListFromTheService_IsRebuiltWithTheSelectionKept()
    {
        var saved = new AccelerationSettings(Adapter: new GpuAdapterId(0x10DE, 0x2F04, 0x5678, "NVIDIA GeForce RTX 5070"));
        var section = Open(Working(), saved);
        var seen = new List<string?>();
        section.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        Push(Working(adapters: [Nvidia]));

        Assert.Equal(["Automatique (NVIDIA GeForce RTX 5070)", "NVIDIA GeForce RTX 5070"], section.Entries.Select(e => e.Label));
        Assert.Equal("NVIDIA GeForce RTX 5070", section.SelectedAdapter?.Label);
        Assert.Contains(nameof(section.Entries), seen);
    }

    [Fact]
    public void Entries_SameListAgain_KeepsTheSameItems()
    {
        var section = Open(Working());
        var before = section.Entries;

        Push(Working());

        Assert.Same(before, section.Entries); // la liste déroulante n'est pas refaite chaque seconde
    }

    // ---- L'interrupteur ----

    [Fact]
    public void SwitchOn_EverythingIsEnabled()
    {
        var section = Open(Working());

        Assert.True(section.Enabled);
        Assert.True(section.CanSwitch);
        Assert.True(section.CanChooseDetails);
        Assert.True(section.Drawing);
        Assert.True(section.VideoDecoding);
    }

    [Fact]
    public void SwitchOff_GraysOutTheRest()
    {
        var section = Open(Working(enabled: false, card: null, drawing: false, video: false), new AccelerationSettings(Enabled: false));

        Assert.False(section.Enabled);
        Assert.True(section.CanSwitch);            // on peut le rallumer
        Assert.False(section.CanChooseDetails);    // la liste et les deux cases sont grisées
        Assert.Equal("Tout se calcule sur le processeur.", section.StateText);
    }

    [Fact]
    public void NoCard_SwitchDisabledWithMessage()
    {
        var section = Open(new AccelerationInfo(Available: false, Enabled: true, AdapterName: null, Drawing: false, VideoDecoding: false, Problem: null, Adapters: []));

        Assert.False(section.CanSwitch);
        Assert.False(section.CanChooseDetails);
        Assert.Equal("Aucune carte graphique compatible : tout se calcule sur le processeur.", section.StateText);
        Assert.Equal(["Automatique"], section.Entries.Select(e => e.Label));
    }

    [Fact]
    public void OlderService_SectionIsDisabledWithTheSavedValuesAndNoStateLine()
    {
        var section = Open(null, new AccelerationSettings(Drawing: false));

        Assert.False(section.CanSwitch);
        Assert.False(section.CanChooseDetails);
        Assert.True(section.Enabled);
        Assert.False(section.Drawing);
        Assert.True(section.VideoDecoding);
        Assert.False(section.HasStateText);
        Assert.False(section.HasProblem);
    }

    // ---- La ligne d'état ----

    [Theory]
    [InlineData(true, true, "Sur Intel(R) Graphics : dessin et vidéos.")]
    [InlineData(true, false, "Sur Intel(R) Graphics : dessin.")]
    [InlineData(false, true, "Sur Intel(R) Graphics : vidéos.")]
    [InlineData(false, false, "Tout se calcule sur le processeur.")]
    public void StateLine(bool drawing, bool video, string expected)
    {
        var section = Open(Working(drawing: drawing, video: video));

        Assert.Equal(expected, section.StateText);
        Assert.True(section.HasStateText);
    }

    [Fact]
    public void StateLine_NoCardInUse_SaysTheProcessorDoesEverything()
    {
        var section = Open(Working(card: null, drawing: false, video: false, problem: Problem));

        Assert.Equal("Tout se calcule sur le processeur.", section.StateText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StateLine_FixedBackground_SaysTheProcessorDrawsAndTheCardTakesOverOnAnAnimation(bool video)
    {
        var section = Open(WaitingForAnimation(video: video));

        Assert.Equal("Fond fixe : l'image se dessine sur le processeur. La carte prend le relais sur un GIF ou une vidéo.", section.StateText);
        Assert.True(section.HasStateText);
        Assert.False(section.HasProblem); // un fond fixe n'est pas un problème
    }

    [Fact]
    public void StateLine_FixedBackgroundWithAFailure_KeepsTheFailureSentence()
    {
        // La phrase de la panne garde la priorité : la ligne d'état ne la contredit pas.
        var section = Open(WaitingForAnimation(card: null, video: false, problem: Problem));

        Assert.Equal(Problem, section.Problem);
        Assert.True(section.HasProblem);
        Assert.Equal("Tout se calcule sur le processeur.", section.StateText);
    }

    [Fact]
    public void StateLine_FollowsTheBackgroundAsTheServiceTellsIt()
    {
        var section = Open(WaitingForAnimation());
        var seen = new List<string?>();
        section.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        Push(Working()); // le fond devient animé : la carte dessine

        Assert.Equal("Sur Intel(R) Graphics : dessin et vidéos.", section.StateText);
        Assert.Contains(nameof(section.StateText), seen);

        seen.Clear();
        Push(WaitingForAnimation());

        Assert.Equal("Fond fixe : l'image se dessine sur le processeur. La carte prend le relais sur un GIF ou une vidéo.", section.StateText);
        Assert.Contains(nameof(section.StateText), seen);
    }

    [Fact]
    public void StateLine_FollowsTheServiceWhenItChangesItsMind()
    {
        var section = Open(Working());
        var seen = new List<string?>();
        section.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        Push(Working(card: null, drawing: false, video: false, problem: Problem));

        Assert.Equal("Tout se calcule sur le processeur.", section.StateText);
        Assert.Contains(nameof(section.StateText), seen);
        Assert.Contains(nameof(section.Problem), seen);
    }

    [Fact]
    public void Problem_ShownAsWarning()
    {
        var section = Open(Working(card: null, drawing: false, video: false, problem: Problem));

        Assert.Equal(Problem, section.Problem); // tel quel
        Assert.True(section.HasProblem);
    }

    [Fact]
    public void NoProblem_NothingIsShown()
    {
        var section = Open(Working());

        Assert.Null(section.Problem);
        Assert.False(section.HasProblem);
    }

    // ---- Les changements ----

    [Fact]
    public async Task Change_SavesAccelerationAndReloads()
    {
        var section = Open(Working());

        await section.SetEnabledAsync(false);
        Assert.Equal(new AccelerationSettings(Enabled: false), _document.Current.Acceleration);
        Assert.Equal(new AccelerationSettings(Enabled: false), _store.Load().Acceleration);
        Assert.Equal(1, _link.ReloadCount);
        Assert.False(section.Enabled);
        Assert.False(section.CanChooseDetails);

        await section.SetEnabledAsync(true);
        Assert.Null(_document.Current.Acceleration); // les réglages d'origine ne s'écrivent pas : le fichier n'en parle plus
        Assert.Equal(2, _link.ReloadCount);

        await section.SetDrawingAsync(false);
        Assert.Equal(new AccelerationSettings(Drawing: false), _store.Load().Acceleration);
        Assert.False(section.Drawing);
        Assert.Equal(3, _link.ReloadCount);

        await section.SetVideoDecodingAsync(false);
        Assert.Equal(new AccelerationSettings(Drawing: false, VideoDecoding: false), _store.Load().Acceleration);
        Assert.False(section.VideoDecoding);
        Assert.Equal(4, _link.ReloadCount);
    }

    [Fact]
    public async Task Change_ChoosingACard_SavesItsIdentity_AndAutomaticForgetsIt()
    {
        var section = Open(Working());

        await section.SetAdapterAsync(section.Entries[2]);

        var nvidia = new GpuAdapterId(0x10DE, 0x2F04, 0x5678, "NVIDIA GeForce RTX 5070");
        Assert.Equal(new AccelerationSettings(Adapter: nvidia), _store.Load().Acceleration);
        Assert.Equal(1, _link.ReloadCount);
        Assert.Same(section.Entries[2], section.SelectedAdapter);

        await section.SetAdapterAsync(section.Entries[0]);

        Assert.Null(_store.Load().Acceleration); // Automatique : plus de carte enregistrée
        Assert.Equal(2, _link.ReloadCount);
        Assert.Same(section.Entries[0], section.SelectedAdapter);
    }

    [Fact]
    public async Task Change_SelectionProperty_SavesToo()
    {
        var section = Open(Working());

        section.SelectedAdapter = section.Entries[1];
        await WaitUntilAsync(() => _store.Load().Acceleration is not null); // la liste déroulante n'attend pas l'enregistrement

        Assert.Equal(new AccelerationSettings(Adapter: new GpuAdapterId(0x8086, 0x7D55, 0x1234, "Intel(R) Graphics")), _store.Load().Acceleration);
    }

    [Fact]
    public async Task Change_NothingNew_WritesNothing()
    {
        var section = Open(Working());

        await section.SetEnabledAsync(true);
        await section.SetAdapterAsync(section.Entries[0]);

        Assert.Equal(0, _link.ReloadCount);
        Assert.Null(_store.Load().Acceleration);
    }

    [Fact]
    public async Task Change_IsUndoable()
    {
        var section = Open(Working());
        await section.SetEnabledAsync(false);

        await _session.UndoAsync();

        Assert.True(section.Enabled);
        Assert.Null(_document.Current.Acceleration);
    }

    [Fact]
    public async Task Change_KeepsTheOtherSettingsOfTheFile()
    {
        var section = Open(Working(), new AccelerationSettings(Drawing: false));

        await section.SetVideoDecodingAsync(false);

        Assert.Equal(new AccelerationSettings(Drawing: false, VideoDecoding: false), _store.Load().Acceleration);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
