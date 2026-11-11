using System.Text.Json;
using Avalonia.Input;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels;

namespace Trofeo.Settings.Tests;

/// <summary>L'onglet Raccourcis et les vues : les actions « Vue précédente » à « Vue n° 9 », et les cases de toutes les vues comme modules visés.</summary>
public sealed class ShortcutsTabViewsTests : IDisposable
{
    private static readonly Guid ClaudeId = new("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17");
    private static readonly Guid CodexId = new("c0de0000-0000-4000-8000-000000000001");
    private static readonly Guid HiddenClaudeId = new("c0de0000-0000-4000-8000-000000000002");
    private static readonly Guid ClockId = new("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-settings-shortcut-views-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceLink _link = new();
    private readonly LayoutStore _store;
    private LayoutDocument _document = null!;
    private EditSession _session = null!;

    public ShortcutsTabViewsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LayoutStore(Path.Combine(_dir, "layout.json"));
        _link.Set(LinkState.Connected, SampleData.Welcome(), SampleData.Status());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModulePlacement Placement(Guid id, string typeId, int column, string? name = null) =>
        new(id, typeId, column, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone(), name);

    private ShortcutsTabViewModel Open(DashboardLayout layout)
    {
        _store.Save(layout);
        _document = new LayoutDocument(_store, _link);
        _document.Load();
        _session = new EditSession(_document);
        return new ShortcutsTabViewModel(_link, _session);
    }

    private ShortcutsTabViewModel OpenDefault() =>
        Open(DashboardLayout.SingleView(180, [Placement(ClaudeId, "claudecode", 0)]));

    /// <summary>Deux vues : « Salon » (Claude Code nommé, Codex, Heure) et « Bureau » (un second Claude Code sans nom).</summary>
    private ShortcutsTabViewModel OpenTwoViews() =>
        Open(new DashboardLayout(180,
        [
            new LayoutView(Guid.NewGuid(), "Salon",
            [
                Placement(ClaudeId, "claudecode", 0, "Mon Claude"),
                Placement(CodexId, "codex", 4),
                Placement(ClockId, "clock", 8),
            ]),
            new LayoutView(Guid.NewGuid(), "Bureau", [Placement(HiddenClaudeId, "claudecode", 0)]),
        ]));

    private IReadOnlyList<ShortcutBinding> Saved => _document.Current.Shortcuts ?? ShortcutDefaults.All;

    private const KeyModifiers CtrlAlt = KeyModifiers.Control | KeyModifiers.Alt;

    [Fact]
    public void Choices_IncludePreviousNextAndNineViews()
    {
        var tab = OpenDefault();

        var views = tab.ActionChoices.Where(c => c.Command is ShortcutCommand.PreviousView or ShortcutCommand.NextView or ShortcutCommand.SelectView).ToList();

        Assert.Equal(
            ["Vue précédente", "Vue suivante", "Vue n° 1", "Vue n° 2", "Vue n° 3", "Vue n° 4", "Vue n° 5", "Vue n° 6", "Vue n° 7", "Vue n° 8", "Vue n° 9"],
            views.Select(c => c.Text));
        Assert.Equal(ShortcutCommand.PreviousView, views[0].Command);
        Assert.Equal(ShortcutCommand.NextView, views[1].Command);
        Assert.All(views.Skip(2), c => Assert.Equal(ShortcutCommand.SelectView, c.Command));
        Assert.Equal(Enumerable.Range(0, 9), views.Skip(2).Select(c => c.Index));
        // les onglets restent proposés
        Assert.Contains(tab.ActionChoices, c => c.Command == ShortcutCommand.PreviousTab && c.Text == "Onglet précédent");
        Assert.Contains(tab.ActionChoices, c => c.Command == ShortcutCommand.SelectTab && c.Index == 8 && c.Text == "Aller à l'onglet 9");
    }

    [Fact]
    public void DefaultRows_HaveFrenchTexts_ForEveryCommand()
    {
        var tab = OpenDefault();

        Assert.Equal("Vue précédente", tab.Rows.Single(r => r.Binding.Command == ShortcutCommand.PreviousView).ActionText);
        Assert.Equal("Vue suivante", tab.Rows.Single(r => r.Binding.Command == ShortcutCommand.NextView).ActionText);

        var withSelect = Open(DashboardLayout.SingleView(180, [Placement(ClaudeId, "claudecode", 0)],
            [new ShortcutBinding("Ctrl+Alt+F4", ShortcutCommand.SelectView, 3)]));
        Assert.Equal("Vue n° 4", withSelect.Rows[0].ActionText);
        Assert.DoesNotContain(tab.Rows, r => r.ActionText is "PreviousView" or "NextView" or "SelectView");
    }

    [Fact]
    public void TargetList_ShowsCaseNamesOrTypeNames()
    {
        var tab = OpenTwoViews();

        Assert.Equal(
            ["Le premier qui accepte", "Mon Claude — Salon", "Codex — Salon", "Claude Code — Bureau"],
            tab.Rows[0].Targets.Select(t => t.Name));
        Assert.Equal([null, ClaudeId, CodexId, HiddenClaudeId], tab.Rows[0].Targets.Select(t => t.Id));
    }

    [Fact]
    public void TargetList_SingleView_HasNoViewSuffix()
    {
        var tab = Open(DashboardLayout.SingleView(180,
        [
            Placement(ClaudeId, "claudecode", 0, "Mon Claude"),
            Placement(CodexId, "codex", 4),
            Placement(ClockId, "clock", 8),
        ]));

        Assert.Equal(["Le premier qui accepte", "Mon Claude", "Codex"], tab.Rows[0].Targets.Select(t => t.Name));
    }

    [Fact]
    public void TargetList_SameLabelTwice_IsNumbered()
    {
        var tab = Open(new DashboardLayout(180,
        [
            new LayoutView(Guid.NewGuid(), "Salon", [Placement(ClaudeId, "claudecode", 0), Placement(HiddenClaudeId, "claudecode", 4)]),
            new LayoutView(Guid.NewGuid(), "Bureau", [Placement(CodexId, "codex", 0)]),
        ]));

        Assert.Equal(["Le premier qui accepte", "Claude Code — Salon", "Claude Code — Salon (2)", "Codex — Bureau"], tab.Rows[0].Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task TargetList_CaseOfAnotherView_CanBeChosen_AndStaysValid()
    {
        var tab = OpenTwoViews();

        await tab.SetTargetAsync(tab.Rows[0], tab.Rows[0].Targets.Single(t => t.Id == HiddenClaudeId));

        Assert.Equal(HiddenClaudeId, Saved[0].Target);
        Assert.Equal("Claude Code — Bureau", tab.Rows[0].SelectedTarget!.Name); // la vue n'est pas affichée : le raccourci reste valable, il n'agit que si elle l'est
    }

    [Fact]
    public async Task AddingATakenKey_IsRefusedWithTheHolderName()
    {
        var tab = OpenDefault();
        tab.AddShortcut();
        tab.Rows[^1].SelectedAction = tab.ActionChoices.Single(a => a.Command == ShortcutCommand.PreviousView);

        await tab.CaptureAsync(Key.Right, CtrlAlt); // Ctrl+Alt+Right est « Vue suivante »

        Assert.Equal("Déjà pris par « Vue suivante ».", tab.Rows[^1].Refusal);
        Assert.Equal(13, Saved.Count);
        Assert.True(tab.IsCapturing);

        await tab.CaptureAsync(Key.V, CtrlAlt);

        Assert.Equal(new ShortcutBinding("Ctrl+Alt+V", ShortcutCommand.PreviousView), Saved[^1]);
        Assert.Equal("Vue précédente", tab.Rows[^1].ActionText);
    }

    [Fact]
    public void ViewCommands_HaveNoTargetChoice()
    {
        var tab = OpenTwoViews();

        var previous = tab.Rows.Single(r => r.Binding.Command == ShortcutCommand.PreviousView);
        var next = tab.Rows.Single(r => r.Binding.Command == ShortcutCommand.NextView);
        var previousTab = tab.Rows.Single(r => r.Binding.Command == ShortcutCommand.PreviousTab);
        Assert.False(previous.HasTarget);
        Assert.False(next.HasTarget);
        Assert.True(previousTab.HasTarget); // les onglets, eux, visent un module

        // un brouillon qui choisit une action de vue : pas de module visé, et l'action choisie est bien dans la liste
        tab.AddShortcut();
        var draft = tab.Rows[^1];
        Assert.True(draft.HasTarget);
        draft.SelectedAction = tab.ActionChoices.Single(a => a.Command == ShortcutCommand.SelectView && a.Index == 2);
        draft = tab.Rows[^1];
        Assert.False(draft.HasTarget);
        Assert.NotNull(draft.SelectedAction);
        Assert.Contains(draft.SelectedAction, tab.ActionChoices);
        Assert.Equal(ShortcutCommand.SelectView, draft.SelectedAction.Command);
        Assert.Equal(2, draft.SelectedAction.Index);
    }

    [Fact]
    public async Task ViewCommand_DraftThatHadATarget_IsSavedWithoutTarget()
    {
        var tab = OpenTwoViews();
        tab.AddShortcut();
        await tab.SetTargetAsync(tab.Rows[^1], tab.Rows[^1].Targets.Single(t => t.Id == CodexId)); // le brouillon vise Codex
        tab.Rows[^1].SelectedAction = tab.ActionChoices.Single(a => a.Command == ShortcutCommand.SelectView && a.Index == 1);

        await tab.CaptureAsync(Key.W, CtrlAlt);

        Assert.Equal(new ShortcutBinding("Ctrl+Alt+W", ShortcutCommand.SelectView, 1), Saved[^1]);
        Assert.Null(Saved[^1].Target);
    }
}
