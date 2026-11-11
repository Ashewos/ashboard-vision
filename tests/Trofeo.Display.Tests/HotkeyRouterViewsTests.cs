using System.Diagnostics;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Display.Tests;

/// <summary>Les raccourcis de vue : ils changent la vue affichée, et ceux d'onglet ne visent que la vue affichée.</summary>
[Collection("Trace")]
public sealed class HotkeyRouterViewsTests
{
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<int> Registered { get; } = [];

        public bool Register(int id, uint modifiers, uint virtualKey)
        {
            Registered.Add(id);
            return true;
        }

        public void Unregister(int id) => Registered.Remove(id);
    }

    private sealed class FakeTarget : ICommandableModule
    {
        public List<ModuleCommand> Commands { get; } = [];

        public bool HandleCommand(ModuleCommand command)
        {
            Commands.Add(command);
            return true;
        }
    }

    /// <summary>Trois vues en boucle ; note ce qu'on lui demande et change vraiment de vue, comme la boucle d'affichage.</summary>
    private sealed class FakeViews(int count = 3, bool throws = false) : IViewSwitcher
    {
        public int Current { get; private set; }
        public List<string> Calls { get; } = [];

        public bool ShowAdjacentView(int step)
        {
            Calls.Add($"adjacent {step}");
            if (throws) throw new InvalidOperationException("vue refusée");
            if (count < 2) return false;
            Current = (((Current + step) % count) + count) % count;
            return true;
        }

        public bool ShowViewAt(int index)
        {
            Calls.Add($"at {index}");
            if (index < 0 || index >= count) return false;
            Current = index;
            return true;
        }
    }

    private static HotkeyRouter Router(
        IReadOnlyList<ShortcutBinding> bindings, IViewSwitcher? views, Func<Guid?, ICommandableModule?>? resolve = null)
    {
        var router = new HotkeyRouter(new FakeRegistrar(), bindings, resolve ?? (_ => null), views);
        router.RegisterAll();
        return router;
    }

    [Fact]
    public void NextView_CallsShowAdjacentPlusOne()
    {
        var views = new FakeViews();
        using var router = Router([new("Ctrl+Alt+Right", ShortcutCommand.NextView), new("Ctrl+Alt+Left", ShortcutCommand.PreviousView)], views);

        router.OnHotkey(1);
        router.OnHotkey(1);
        router.OnHotkey(1); // après la dernière, la première
        router.OnHotkey(2);

        Assert.Equal(["adjacent 1", "adjacent 1", "adjacent 1", "adjacent -1"], views.Calls);
        Assert.Equal(2, views.Current); // 0 → 1 → 2 → 0 → 2
    }

    [Fact]
    public void SelectView_CallsShowViewAt()
    {
        var views = new FakeViews();
        using var router = Router([new("Ctrl+Alt+F7", ShortcutCommand.SelectView, 2), new("Ctrl+Alt+F8", ShortcutCommand.SelectView, 9)], views);

        router.OnHotkey(1);
        router.OnHotkey(2); // pas de neuvième vue : la boucle répond faux, rien ne change

        Assert.Equal(["at 2", "at 9"], views.Calls);
        Assert.Equal(2, views.Current);
    }

    [Fact]
    public void TabCommand_GoesToTheTargetInTheCurrentViewOnly()
    {
        var inFirstView = new FakeTarget();
        var inSecondView = new FakeTarget();
        var views = new FakeViews();
        // Comme DisplayLoop.FindCommandTarget : seul le module de la vue affichée peut être visé.
        ICommandableModule? Resolve(Guid? _) => views.Current switch { 0 => inFirstView, 1 => inSecondView, _ => null };
        using var router = Router(
            [new("Ctrl+Alt+Down", ShortcutCommand.NextTab), new("Ctrl+Alt+Right", ShortcutCommand.NextView)], views, Resolve);

        router.OnHotkey(1);
        router.OnHotkey(2);
        router.OnHotkey(1);
        router.OnHotkey(2); // la troisième vue n'a aucun module commandable
        router.OnHotkey(1);

        var next = new ModuleCommand(ModuleCommandKind.NextTab);
        Assert.Equal([next], inFirstView.Commands);
        Assert.Equal([next], inSecondView.Commands);
    }

    [Fact]
    public void ViewShortcut_WorksWhenTheDisplayedViewHasNoCommandableCase()
    {
        var views = new FakeViews();
        using var router = Router(
            [new("Ctrl+Alt+Down", ShortcutCommand.NextTab), new("Ctrl+Alt+Right", ShortcutCommand.NextView)], views); // aucune cible

        router.OnHotkey(1); // une commande d'onglet sans cible ne fait rien et ne casse rien
        router.OnHotkey(2);

        Assert.Equal(["adjacent 1"], views.Calls);
        Assert.Equal(1, views.Current);
    }

    [Fact]
    public void ViewShortcut_WithoutASwitcher_DoesNothing()
    {
        using var router = Router([new("Ctrl+Alt+Right", ShortcutCommand.NextView), new("Ctrl+Alt+F7", ShortcutCommand.SelectView, 1)], views: null);

        router.OnHotkey(1);
        router.OnHotkey(2);
    }

    [Fact]
    public void ViewShortcut_SwitcherThrows_IsLoggedNotThrown()
    {
        var views = new FakeViews(throws: true);
        using var router = Router([new("Ctrl+Alt+Right", ShortcutCommand.NextView)], views);
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            router.OnHotkey(1);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains(listener.Lines, l => l.Contains("Ctrl+Alt+Right") && l.Contains("vue refusée"));
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public async Task UiHotkeyHost_RegistersEveryShortcut_EvenWithoutACommandableCase()
    {
        var registrar = new FakeRegistrar();
        using var router = new HotkeyRouter(registrar, [], _ => null, new FakeViews()); // aucun module commandable nulle part
        var host = new UiHotkeyHost(new InlineContext(), router);

        var failures = await host.ApplyAsync(ShortcutDefaults.All);

        Assert.Empty(failures);
        Assert.Equal(ShortcutDefaults.All.Count, registrar.Registered.Count); // les vues et les onglets : tout est posé
    }
}
