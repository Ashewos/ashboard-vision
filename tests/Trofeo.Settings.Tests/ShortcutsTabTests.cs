using System.Text.Json;
using Avalonia.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Raccourcis sans fenêtre : un faux lien, un vrai fichier dans un dossier temporaire.</summary>
public sealed class ShortcutsTabTests : IDisposable
{
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid CodexId = new("c0de0000-0000-4000-8000-000000000001");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-shortcuts-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public ShortcutsTabTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModulePlacement Placement(Guid id, string typeId, int column, int columns, int rows) =>
        new(id, typeId, column, 0, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>Claude Code et Codex sur l'écran (les deux acceptent les commandes), plus l'Heure (qui ne les accepte pas).</summary>
    private ShortcutsTabViewModel Open(IReadOnlyList<ShortcutBinding>? shortcuts = null, bool secondClaude = false)
    {
        List<ModulePlacement> placements = [Placement(ClaudeId, "claudecode", 0, 8, 4)];
        placements.Add(secondClaude ? Placement(Guid.NewGuid(), "claudecode", 8, 6, 3) : Placement(CodexId, "codex", 8, 6, 3));
        placements.Add(Placement(new Guid("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01"), "clock", 14, 2, 1));
        _store.Save(DashboardLayout.SingleView(180, placements, shortcuts));
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new ShortcutsTabViewModel(_link, _session);
    }

    private IReadOnlyList<ShortcutBinding> Saved => _document.Current.Shortcuts ?? ShortcutDefaults.All;

    private IReadOnlyList<ShortcutBinding> OnDisk => _store.Load().Shortcuts ?? ShortcutDefaults.All;

    private const KeyModifiers CtrlAlt = KeyModifiers.Control | KeyModifiers.Alt;

    // ---- La liste ----

    [Fact]
    public void Rows_Default_ListsElevenShortcuts_ActionInWords_KeysAsPill()
    {
        var tab = Open();

        Assert.Equal(11, tab.Rows.Count);
        Assert.Equal(["Onglet précédent", "Onglet suivant", "Aller à l'onglet 1", "Aller à l'onglet 9"],
            new[] { tab.Rows[0], tab.Rows[1], tab.Rows[2], tab.Rows[10] }.Select(r => r.ActionText));
        Assert.Equal(["Ctrl + Alt + ←", "Ctrl + Alt + →", "Ctrl + Alt + Maj + 1"],
            tab.Rows.Take(3).Select(r => r.KeysText));
        Assert.All(tab.Rows, r => Assert.False(r.IsCapturing));
        Assert.False(tab.IsCapturing);
    }

    [Fact]
    public void Rows_Target_OffersOnlyModulesThatAcceptCommands()
    {
        var tab = Open();

        Assert.Equal(["Le premier qui accepte", "Claude Code", "Codex"], tab.Rows[0].Targets.Select(t => t.Name));
        Assert.Null(tab.Rows[0].SelectedTarget!.Id);
    }

    [Fact]
    public void Rows_TwoModulesOfTheSameType_AreTold_ApartByNumber()
    {
        var tab = Open(secondClaude: true);

        Assert.Equal(["Le premier qui accepte", "Claude Code", "Claude Code (2)"], tab.Rows[0].Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task Target_Change_Saves()
    {
        var tab = Open();

        await tab.SetTargetAsync(tab.Rows[1], tab.Rows[1].Targets.Single(t => t.Id == CodexId));

        Assert.Equal(CodexId, Saved[1].Target);
        Assert.Equal(CodexId, OnDisk[1].Target);
        Assert.Equal(ModuleCommandKind.NextTab, Saved[1].Command);
        Assert.Equal(CodexId, tab.Rows[1].SelectedTarget!.Id);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public void Rows_TargetNoLongerOnScreen_IsShownAsGone()
    {
        var gone = Guid.NewGuid();
        var tab = Open([new ShortcutBinding("Ctrl+Alt+Right", ModuleCommandKind.NextTab, 0, gone)]);

        var row = tab.Rows[0];
        Assert.Equal("Un module qui n'est plus là", row.SelectedTarget!.Name);
        Assert.Equal(gone, row.SelectedTarget.Id);
    }

    // ---- Capture ----

    [Fact]
    public async Task Capture_Edit_ReplacesKeys_AndSaves()
    {
        var tab = Open();

        tab.BeginCapture(tab.Rows[0]);
        Assert.True(tab.IsCapturing);
        Assert.True(tab.Rows[0].IsCapturing);
        Assert.Equal("Appuie sur la combinaison voulue (Échap pour annuler).", tab.Rows[0].Hint);

        var handled = await tab.CaptureAsync(Key.F5, KeyModifiers.Control);

        Assert.True(handled);
        Assert.False(tab.IsCapturing);
        Assert.Equal("Ctrl+F5", Saved[0].Keys);
        Assert.Equal(ModuleCommandKind.PreviousTab, Saved[0].Command);
        Assert.Equal("Ctrl+F5", OnDisk[0].Keys);
        Assert.Equal("Ctrl + F5", tab.Rows[0].KeysText);
        Assert.Equal(1, _link.ReloadCount);
    }

    [Fact]
    public async Task Capture_NotCapturing_LeavesTheKeyAlone()
    {
        var tab = Open();

        Assert.False(await tab.CaptureAsync(Key.Z, KeyModifiers.Control));
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Capture_WithoutModifier_KeepsWaiting()
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[0]);

        Assert.True(await tab.CaptureAsync(Key.A, KeyModifiers.None));
        Assert.True(await tab.CaptureAsync(Key.LeftCtrl, KeyModifiers.Control)); // un modificateur seul : on attend la touche

        Assert.True(tab.IsCapturing);
        Assert.Equal(0, _link.ReloadCount);
        Assert.Equal("Ctrl+Alt+Left", Saved[0].Keys);
    }

    [Fact]
    public async Task Capture_Escape_Cancels()
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[1]);

        Assert.True(await tab.CaptureAsync(Key.Escape, KeyModifiers.None));

        Assert.False(tab.IsCapturing);
        Assert.False(tab.Rows[1].IsCapturing);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Shortcut_Duplicate_IsRefusedWithMessage()
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[0]); // « Onglet précédent » veut les touches de « Onglet suivant »

        await tab.CaptureAsync(Key.Right, CtrlAlt);

        Assert.Equal("Ctrl+Alt+Right sert déjà à « Onglet suivant ».", tab.Rows[0].Refusal);
        Assert.True(tab.Rows[0].HasRefusal);
        Assert.Equal("Ctrl+Alt+Left", Saved[0].Keys);
        Assert.Equal(0, _link.ReloadCount); // rien n'est enregistré
        Assert.True(tab.IsCapturing); // on peut essayer une autre combinaison

        await tab.CaptureAsync(Key.F6, KeyModifiers.Control);
        Assert.Equal("Ctrl+F6", Saved[0].Keys);
        Assert.False(tab.Rows[0].HasRefusal);
    }

    [Fact]
    public async Task Shortcut_SameKeysOnTheSameRow_IsNotADuplicate()
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[1]);

        await tab.CaptureAsync(Key.Right, CtrlAlt); // les mêmes touches que celles de la ligne : rien à changer

        Assert.False(tab.Rows[1].HasRefusal);
        Assert.False(tab.IsCapturing);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Shortcut_DuplicateInAnotherSpelling_IsRefused()
    {
        var tab = Open([new ShortcutBinding("alt+ctrl+right", ModuleCommandKind.NextTab), new ShortcutBinding("Ctrl+Alt+Left", ModuleCommandKind.PreviousTab)]);
        tab.BeginCapture(tab.Rows[1]);

        await tab.CaptureAsync(Key.Right, CtrlAlt);

        Assert.Equal("Ctrl+Alt+Right sert déjà à « Onglet suivant ».", tab.Rows[1].Refusal);
    }

    [Theory]
    [InlineData(Key.C, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.D4, KeyModifiers.Control)]
    public async Task Capture_LetterWithoutAltOrWin_IsRefusedWithItsReason_AndKeepsWaiting(Key key, KeyModifiers modifiers)
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[2]);
        var before = Saved.Select(b => b.Keys).ToList();

        Assert.True(await tab.CaptureAsync(key, modifiers));

        Assert.Equal("Ajoute Alt ou Win : une lettre ou un chiffre seul avec Ctrl ou Maj gênerait la saisie partout.", tab.Rows[2].Refusal);
        Assert.True(tab.IsCapturing);                       // elle attend une autre combinaison
        Assert.Equal(before, Saved.Select(b => b.Keys).ToList());
        Assert.Equal(0, _link.ReloadCount);

        await tab.CaptureAsync(key, modifiers | KeyModifiers.Alt);   // avec Alt, c'est accepté

        Assert.False(tab.IsCapturing);
        Assert.Contains(Saved, b => b.Keys.StartsWith("Ctrl+Alt+") || b.Keys.StartsWith("Alt+"));
    }

    [Fact]
    public async Task Capture_ArrowAndFunctionKeys_KeepTheCurrentRule()
    {
        var tab = Open();
        tab.BeginCapture(tab.Rows[2]);

        await tab.CaptureAsync(Key.Up, KeyModifiers.Shift);

        Assert.False(tab.IsCapturing);
        Assert.Equal("Shift+Up", Saved[2].Keys);
    }

    // ---- Ajouter, supprimer, remettre ----

    [Fact]
    public async Task Add_DraftRow_CapturesThenAppends()
    {
        var tab = Open();

        tab.AddShortcut();

        Assert.Equal(12, tab.Rows.Count);
        Assert.True(tab.Rows[11].IsDraft);
        Assert.True(tab.Rows[11].IsCapturing);
        Assert.Equal(0, _link.ReloadCount); // le brouillon n'est pas enregistré

        tab.Rows[11].SelectedAction = tab.ActionChoices.Single(a => a.Command == ModuleCommandKind.SelectTab && a.Index == 2);
        await tab.CaptureAsync(Key.T, CtrlAlt);

        Assert.Equal(12, Saved.Count);
        Assert.Equal(new ShortcutBinding("Ctrl+Alt+T", ModuleCommandKind.SelectTab, 2), Saved[11]);
        Assert.Equal(12, OnDisk.Count);
        Assert.False(tab.Rows[11].IsDraft);
        Assert.Equal("Aller à l'onglet 3", tab.Rows[11].ActionText);
        Assert.False(tab.IsCapturing);
    }

    [Fact]
    public async Task Add_DraftRow_Escape_Discards()
    {
        var tab = Open();
        tab.AddShortcut();

        await tab.CaptureAsync(Key.Escape, KeyModifiers.None);

        Assert.Equal(11, tab.Rows.Count);
        Assert.False(tab.IsCapturing);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Add_DraftRow_Duplicate_IsRefused()
    {
        var tab = Open();
        tab.AddShortcut();

        await tab.CaptureAsync(Key.Left, CtrlAlt);

        Assert.Equal("Ctrl+Alt+Left sert déjà à « Onglet précédent ».", tab.Rows[11].Refusal);
        Assert.Equal(11, Saved.Count);
    }

    [Fact]
    public async Task Delete_RemovesTheShortcut_AndSaves()
    {
        var tab = Open();

        await tab.DeleteAsync(tab.Rows[0]);

        Assert.Equal(10, Saved.Count);
        Assert.Equal("Ctrl+Alt+Right", Saved[0].Keys);
        Assert.Equal(10, tab.Rows.Count);
        Assert.Equal(10, OnDisk.Count);
    }

    [Fact]
    public async Task Delete_Draft_DiscardsIt()
    {
        var tab = Open();
        tab.AddShortcut();

        await tab.DeleteAsync(tab.Rows[11]);

        Assert.Equal(11, tab.Rows.Count);
        Assert.False(tab.IsCapturing);
        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Shortcuts_ResetDefaults_Saves()
    {
        var tab = Open([new ShortcutBinding("Ctrl+F5", ModuleCommandKind.NextTab)]);
        Assert.Single(tab.Rows);

        await tab.ResetAsync();

        Assert.Equal(ShortcutDefaults.All, Saved);
        Assert.Equal(ShortcutDefaults.All, OnDisk);
        Assert.Equal(11, tab.Rows.Count);
    }

    [Fact]
    public async Task Shortcuts_ResetDefaults_WhenAlreadyDefault_WritesNothing()
    {
        var tab = Open();

        await tab.ResetAsync();

        Assert.Equal(0, _link.ReloadCount);
    }

    [Fact]
    public async Task Shortcuts_Undo_BringsBackTheDeletedOne()
    {
        var tab = Open();
        await tab.DeleteAsync(tab.Rows[0]);
        Assert.Equal(10, tab.Rows.Count);

        await _session.UndoAsync();

        Assert.Equal(11, tab.Rows.Count);
        Assert.Equal("Ctrl+Alt+Left", tab.Rows[0].Keys);
    }

    [Fact]
    public async Task Shortcuts_EmptyList_ShowsNoRow()
    {
        var tab = Open([]);

        Assert.Empty(tab.Rows);
        Assert.True(tab.IsEmpty);
        await tab.ResetAsync();
        Assert.False(tab.IsEmpty);
    }

    // ---- Ce que Windows refuse ----

    [Fact]
    public async Task Failure_FromReload_IsShownOnItsRow()
    {
        const string failure = "Raccourci « Ctrl+Alt+Right » indisponible : déjà pris par une autre application.";
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, [failure, "Fond d'écran introuvable : x.jpg. Fond du thème utilisé."]));
        var tab = Open();

        await tab.SetTargetAsync(tab.Rows[0], tab.Rows[0].Targets[1]);

        Assert.Equal(failure, tab.Rows[1].Failure);
        Assert.True(tab.Rows[1].HasFailure);
        Assert.False(tab.Rows[0].HasFailure);
        Assert.All(tab.Rows.Skip(2), r => Assert.False(r.HasFailure));
    }

    [Fact]
    public async Task Failure_IsClearedBySavingAgain()
    {
        const string failure = "Raccourci « Ctrl+Alt+Right » indisponible : touches illisibles.";
        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, [failure]));
        var tab = Open();
        await tab.SetTargetAsync(tab.Rows[0], tab.Rows[0].Targets[1]);
        Assert.True(tab.Rows[1].HasFailure);

        _link.OnReload = () => Task.FromResult(new Reloaded(true, null, []));
        await tab.SetTargetAsync(tab.Rows[0], tab.Rows[0].Targets[0]);

        Assert.False(tab.Rows[1].HasFailure);
    }
}
