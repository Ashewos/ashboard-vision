using System.Text.Json;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>L'historique et la file des modifications, partagés par tous les onglets.</summary>
public sealed class EditSessionTests : IDisposable
{
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-session-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private readonly LayoutDocument _document;
    private readonly EditSession _session;

    public EditSessionTests()
    {
        Directory.CreateDirectory(_dir);
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _store.Save(Base);
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModulePlacement Placement(Guid id, string typeId, int column, int row, int columns, int rows) =>
        new(id, typeId, column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private static readonly DashboardLayout Base = DashboardLayout.SingleView(180, [Placement(ClaudeId, "claudecode", 0, 0, 8, 4), Placement(ClockId, "clock", 12, 0, 4, 2)]);

    private static DashboardLayout WithRotation(DashboardLayout layout, int rotation) => layout with { Rotation = rotation };

    private static DashboardLayout WithText(DashboardLayout layout, TextSize size) => layout.WithPlacements(
        layout.Views[0].Id, [.. layout.Views[0].Placements.Select(p => p.InstanceId == ClockId ? p with { TextSize = size } : p)]);

    private TextSize ClockText => _document.Current.Views[0].Placements.Single(p => p.InstanceId == ClockId).TextSize;

    [Fact]
    public async Task Apply_Change_SavesAndFillsTheHistory()
    {
        var outcome = await _session.ApplyAsync(layout => WithRotation(layout, 0));

        Assert.True(outcome!.Saved);
        Assert.Equal(0, _document.Current.Rotation);
        Assert.Equal(1, _link.ReloadCount);
        Assert.True(_session.CanUndo);
        Assert.False(_session.CanRedo);
    }

    [Fact]
    public async Task Apply_NullOrUnchanged_SavesNothingAndKeepsTheHistory()
    {
        Assert.Null(await _session.ApplyAsync(_ => null));
        // Une disposition égale mais neuve (comme en rend le moteur d'édition) ne compte pas.
        Assert.Null(await _session.ApplyAsync(layout => layout.WithPlacements(layout.Views[0].Id, [.. layout.Views[0].Placements])));

        Assert.Equal(0, _link.ReloadCount);
        Assert.False(_session.CanUndo);
    }

    [Fact]
    public async Task Undo_ThenRedo_SaveEachTime()
    {
        await _session.ApplyAsync(layout => WithRotation(layout, 0));

        await _session.UndoAsync();
        Assert.Equal(180, _document.Current.Rotation);
        Assert.Equal(180, _store.Load().Rotation);
        Assert.False(_session.CanUndo);
        Assert.True(_session.CanRedo);

        await _session.RedoAsync();
        Assert.Equal(0, _document.Current.Rotation);
        Assert.Equal(3, _link.ReloadCount);
    }

    [Fact]
    public async Task Undo_RevertsLastEditWhicheverTabMadeIt()
    {
        var tab = new LayoutTabViewModel(_link, _session);
        tab.Select(ClockId);
        await tab.SetTextSizeAsync(TextSize.Large);          // un changement de l'onglet Disposition
        await _session.ApplyAsync(layout => WithRotation(layout, 0));  // puis un changement d'un autre onglet

        await _session.UndoAsync();

        Assert.Equal(180, _document.Current.Rotation);       // c'est celui de l'autre onglet qui est défait...
        Assert.Equal(TextSize.Large, ClockText);             // ... pas celui de Disposition
        Assert.True(tab.IsTextLarge);

        await _session.UndoAsync();
        Assert.Equal(TextSize.Normal, ClockText);
    }

    [Fact]
    public async Task Undo_ThenNewEdit_ClearsRedo()
    {
        await _session.ApplyAsync(layout => WithText(layout, TextSize.Large));
        await _session.UndoAsync();
        Assert.True(_session.CanRedo);

        await _session.ApplyAsync(layout => WithText(layout, TextSize.Small));

        Assert.False(_session.CanRedo);
    }

    [Fact]
    public async Task Undo_NothingToUndo_DoesNothing()
    {
        Assert.Null(await _session.UndoAsync());
        Assert.Null(await _session.RedoAsync());
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Undo_AfterExternalChange_ReloadsInsteadOfApplying()
    {
        await _session.ApplyAsync(layout => WithText(layout, TextSize.Large));

        // Quelqu'un d'autre réécrit le fichier : l'annulation est écartée et la fenêtre relit le fichier.
        _store.Save(WithRotation(Base, 0));
        var outcome = await _session.UndoAsync();

        Assert.False(outcome!.Saved);
        Assert.Equal(Texts.LayoutChangedOutside, outcome.Message);
        Assert.Equal(TextSize.Normal, ClockText);  // le fichier relu, pas l'annulation
        Assert.Equal(0, _document.Current.Rotation);
        Assert.False(_session.CanUndo);            // l'historique est vidé par le changement extérieur
        Assert.False(_session.CanRedo);
    }

    [Fact]
    public async Task Replaced_ClearsTheHistory_AndRaisesChanged()
    {
        await _session.ApplyAsync(layout => WithText(layout, TextSize.Large));
        var raised = 0;
        _session.Changed += () => raised++;

        _store.Save(WithRotation(Base, 0));
        await _session.ApplyAsync(layout => WithText(layout, TextSize.Small));  // le document voit le changement extérieur

        Assert.False(_session.CanUndo);
        Assert.True(raised > 0);
    }

    [Fact]
    public async Task WhileTheServiceReloads_ChangedAnnouncesTheNewLayout_AndTheSessionIsBusy()
    {
        var release = new TaskCompletionSource<Reloaded>();
        _link.OnReload = () => release.Task;
        var seenWhileBusy = new List<(int Rotation, bool Busy)>();
        _session.Changed += () => seenWhileBusy.Add((_document.Current.Rotation, _session.IsBusy));

        var pending = _session.ApplyAsync(layout => WithRotation(layout, 0));

        Assert.False(pending.IsCompleted);
        Assert.True(_session.IsBusy);
        Assert.Contains((0, true), seenWhileBusy);  // la nouvelle disposition est annoncée avant la réponse du service

        release.SetResult(new Reloaded(true, null, []));
        await pending;

        Assert.False(_session.IsBusy);
        Assert.True(_session.CanUndo);
    }

    [Fact]
    public async Task Edits_RunOneAtATime_EachOnTheLayoutLeftByThePrevious()
    {
        var release = new TaskCompletionSource<Reloaded>();
        _link.OnReload = () => release.Task;

        var first = _session.ApplyAsync(layout => WithRotation(layout, 0));
        var second = _session.ApplyAsync(layout => WithText(layout, TextSize.Large));  // attend son tour
        Assert.Equal(1, _link.ReloadCount);
        Assert.Equal(TextSize.Normal, ClockText);

        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        release.SetResult(new Reloaded(true, null, []));
        await first;
        await second;

        Assert.Equal(0, _document.Current.Rotation);   // la seconde modification part de la première
        Assert.Equal(TextSize.Large, ClockText);
        Assert.Equal(2, _link.ReloadCount);
        await _session.UndoAsync();
        Assert.Equal(TextSize.Normal, ClockText);
        await _session.UndoAsync();
        Assert.Equal(180, _document.Current.Rotation);
    }

    // ---- Le message et les avertissements du dernier enregistrement (montrés par la fenêtre, quel que soit l'onglet) ----

    [Fact]
    public async Task Message_OfARefusedSave_IsKept_AndClearedByTheNextEdit()
    {
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Disposition invalide : trop de modules.", []));
        var changes = 0;
        _session.Changed += () => changes++;

        await _session.ApplyAsync(layout => WithRotation(layout, 0));

        Assert.Equal("Disposition invalide : trop de modules.", _session.Message);
        Assert.True(changes > 0);

        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        await _session.ApplyAsync(layout => WithRotation(layout, 90));
        Assert.Null(_session.Message);
    }

    [Fact]
    public async Task Message_OfAnExternalChange_IsKept()
    {
        _store.Save(Base with { Rotation = 0 });

        await _session.ApplyAsync(layout => WithRotation(layout, 90));

        Assert.Equal(Texts.LayoutChangedOutside, _session.Message);
    }

    [Fact]
    public async Task Message_ClearedByUndo_AndByClearMessage()
    {
        await _session.ApplyAsync(layout => WithRotation(layout, 0));
        _link.OnReload = () => Task.FromResult(new Reloaded(false, "Refusé.", []));
        await _session.UndoAsync();
        Assert.Equal("Refusé.", _session.Message);

        _session.ClearMessage();
        Assert.Null(_session.Message);
    }

    [Fact]
    public async Task Warnings_AppearanceOnesStayUntilTheAppearanceChanges_OtherOnesFollowEachReload()
    {
        const string Missing = "Fond d'écran introuvable : x.png. Fond du thème utilisé.";
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, [Missing, "Raccourci ignoré."]));
        await _session.ApplyAsync(layout => layout with { Appearance = new Trofeo.Core.Rendering.AppearanceSettings("pixel") });
        Assert.Equal([Missing], _session.AppearanceWarnings.Background);
        Assert.Equal(["Raccourci ignoré."], _session.OtherWarnings);

        // Un rechargement sans changement d'apparence : le service ne répète pas l'avertissement du fond, la fenêtre le garde.
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        await _session.ApplyAsync(layout => WithRotation(layout, 0));
        Assert.Equal([Missing], _session.AppearanceWarnings.Background);
        Assert.Empty(_session.OtherWarnings);

        await _session.ApplyAsync(layout => layout with { Appearance = new Trofeo.Core.Rendering.AppearanceSettings("classique") });
        Assert.Empty(_session.AppearanceWarnings.Background);
    }

    // ---- layout.json illisible à l'ouverture ----

    private const string Garbage = "{ ceci n'est pas du JSON";

    private (EditSession Session, LayoutDocument Document, string File) OpenUnreadable()
    {
        var file = Path.Combine(_dir, "illisible.json");
        File.WriteAllText(file, Garbage);
        var document = new LayoutDocument(new LayoutStore(file), _link);
        document.Load();
        return (new EditSession(document), document, file);
    }

    [Fact]
    public async Task Apply_WhileTheFileIsUnreadable_RefusesWithoutEditingOrWriting()
    {
        var (session, document, file) = OpenUnreadable();
        var called = false;

        var outcome = await session.ApplyAsync(layout => { called = true; return WithRotation(layout, 0); });

        Assert.False(called);
        Assert.False(outcome!.Saved);
        Assert.StartsWith("layout.json est illisible : ", outcome.Message);
        Assert.Equal(outcome.Message, session.Message);
        Assert.Equal(Garbage, File.ReadAllText(file));
        Assert.NotNull(document.LoadError);
        Assert.False(session.CanUndo);
        session.Dispose();
    }

    [Fact]
    public async Task Undo_WhileTheFileIsUnreadable_RefusesToo()
    {
        var (session, _, file) = OpenUnreadable();

        var outcome = await session.UndoAsync();

        Assert.False(outcome!.Saved);
        Assert.Equal(Garbage, File.ReadAllText(file));
        session.Dispose();
    }

    [Fact]
    public async Task Apply_TheFileBecameReadable_ShowsItAndRefusesTheStaleEdit()
    {
        var (session, document, file) = OpenUnreadable();
        new LayoutStore(file).Save(Base with { Rotation = 0 });
        var changes = 0;
        session.Changed += () => changes++;

        var outcome = await session.ApplyAsync(layout => WithRotation(layout, 180));

        Assert.False(outcome!.Saved);
        Assert.Equal(Texts.LayoutChangedOutside, outcome.Message);
        Assert.Null(document.LoadError);
        Assert.Equal(0, document.Current.Rotation);
        Assert.True(changes > 0);
        Assert.Equal(0, new LayoutStore(file).Load().Rotation); // la modification périmée n'est pas écrite
        session.Dispose();
    }

    [Fact]
    public async Task ResetToDefault_KeepsTheOldFile_WritesTheDefault_AndClearsTheMessage()
    {
        var (session, document, file) = OpenUnreadable();
        await session.ApplyAsync(layout => layout); // le refus laisse un message
        Assert.NotNull(session.Message);

        var outcome = await session.ResetToDefaultAsync();

        Assert.True(outcome!.Saved);
        Assert.Null(session.Message);
        Assert.Null(document.LoadError);
        Assert.Equal(Garbage, File.ReadAllText(file + ".avant-reglages"));
        Assert.Equal(DashboardLayout.Default.Views[0].Placements.Count, new LayoutStore(file).Load().Views[0].Placements.Count);
        Assert.Equal(1, _link.ReloadCount);

        // Les modifications suivantes passent.
        Assert.True((await session.ApplyAsync(layout => WithRotation(layout, 0)))!.Saved);
        session.Dispose();
    }
}
