using Trofeo.Control;
using Trofeo.Core.Shortcuts;
using Trofeo.Extensions;

namespace Trofeo.Display.Tests;

/// <summary>
/// Les raccourcis déclarés par les modules complémentaires : le routeur les envoie aux cases du module dans la vue affichée, et à
/// personne d'autre. Il retient aussi les touches que Windows lui a refusées et sait dire si une touche est libre.
/// </summary>
[Collection("Trace")]
public sealed class HotkeyRouterCommunityTests
{
    private const string Module = "alex.demo";
    private static readonly Guid CaseInShownView = new("11110000-0000-0000-0000-000000000001");
    private static readonly Guid OtherCaseInShownView = new("11110000-0000-0000-0000-000000000002");
    private static readonly Guid CaseInHiddenView = new("22220000-0000-0000-0000-000000000003");

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<(int Id, uint Modifiers, uint VirtualKey)> Registered { get; } = [];
        public List<int> Unregistered { get; } = [];
        public HashSet<(uint Modifiers, uint VirtualKey)> TakenElsewhere { get; } = [];

        public bool Register(int id, uint modifiers, uint virtualKey)
        {
            if (TakenElsewhere.Contains((modifiers & ~0x4000u, virtualKey))) return false;
            Registered.Add((id, modifiers, virtualKey));
            return true;
        }

        public void Unregister(int id) => Unregistered.Add(id);
    }

    private sealed class FakeCommunity : ICommunityShortcuts
    {
        public List<(string ModuleId, string ShortcutId, Guid[] Instances)> Sent { get; } = [];

        public bool SendShortcut(string moduleId, string shortcutId, IReadOnlyCollection<Guid> instances)
        {
            Sent.Add((moduleId, shortcutId, [.. instances]));
            return true;
        }
    }

    /// <summary>
    /// Une disposition de deux vues : la vue affichée a deux cases du module, l'autre une troisième. Comme
    /// <c>DisplayLoop.FindCommunityCases</c>, seules les cases de la vue affichée sont rendues, et la case visée doit en être.
    /// </summary>
    private static IReadOnlyCollection<Guid> CasesOfShownView(string moduleId, Guid? target)
    {
        if (moduleId != Module) return [];
        Guid[] shown = [CaseInShownView, OtherCaseInShownView];
        return target is { } id ? shown.Where(c => c == id).ToArray() : shown;
    }

    private static HotkeyRouter Router(
        IReadOnlyList<ShortcutBinding> bindings, FakeCommunity community, FakeRegistrar? registrar = null,
        Func<string, Guid?, IReadOnlyCollection<Guid>>? cases = null)
    {
        var router = new HotkeyRouter(registrar ?? new FakeRegistrar(), bindings, _ => null, null, community, cases ?? CasesOfShownView);
        router.RegisterAll();
        return router;
    }

    private static ShortcutBinding Refresh(Guid? target = null, string keys = "Ctrl+Alt+Shift+R") =>
        new(keys, ShortcutCommand.Community, 0, target, Module, "refresh");

    [Fact]
    public void CommunityBinding_SendsToTheModuleCasesOfTheCurrentViewOnly()
    {
        var community = new FakeCommunity();
        using var router = Router([Refresh()], community);

        router.OnHotkey(1);

        var sent = Assert.Single(community.Sent);
        Assert.Equal((Module, "refresh"), (sent.ModuleId, sent.ShortcutId));
        Assert.Equal([CaseInShownView, OtherCaseInShownView], sent.Instances);
        Assert.DoesNotContain(CaseInHiddenView, sent.Instances);
    }

    [Fact]
    public void CommunityBinding_WithTarget_SendsToThatCaseOnly_IfShown()
    {
        var community = new FakeCommunity();
        using var router = Router([Refresh(OtherCaseInShownView), Refresh(CaseInHiddenView, "Ctrl+Alt+Shift+T")], community);

        router.OnHotkey(1);
        router.OnHotkey(2); // la case visée est dans une vue cachée : rien

        var sent = Assert.Single(community.Sent);
        Assert.Equal([OtherCaseInShownView], sent.Instances);
    }

    [Fact]
    public void CommunityBinding_NoCaseShown_SendsNothing()
    {
        var community = new FakeCommunity();
        using var router = Router([Refresh()], community, cases: (_, _) => []);

        router.OnHotkey(1);

        Assert.Empty(community.Sent);
    }

    [Fact]
    public void CommunityBinding_OfAnotherModule_FindsNoCase_AndSendsNothing()
    {
        var community = new FakeCommunity();
        using var router = Router([new ShortcutBinding("Ctrl+Alt+Shift+R", ShortcutCommand.Community, 0, null, "other.module", "refresh")], community);

        router.OnHotkey(1);

        Assert.Empty(community.Sent);
    }

    [Fact]
    public void CommunityBinding_WithoutASink_OrWithoutIds_DoesNothing()
    {
        var community = new FakeCommunity();
        using var withoutSink = new HotkeyRouter(new FakeRegistrar(), [Refresh()], _ => null);
        withoutSink.RegisterAll();
        using var withoutIds = Router([new ShortcutBinding("Ctrl+Alt+Shift+R", ShortcutCommand.Community)], community);

        withoutSink.OnHotkey(1);
        withoutIds.OnHotkey(1);

        Assert.Empty(community.Sent);
    }

    [Fact]
    public void CommunityBinding_NeverReachesTabCommandsOrViewCommands()
    {
        var community = new FakeCommunity();
        using var router = Router(
            [new("Ctrl+Alt+Down", ShortcutCommand.NextTab), new("Ctrl+Alt+Right", ShortcutCommand.NextView), Refresh()], community);

        router.OnHotkey(1);
        router.OnHotkey(2);

        Assert.Empty(community.Sent); // un module ne reçoit jamais les commandes d'onglet ni de vue
    }

    [Fact]
    public void RefusedKeys_AreTheOnesWindowsRefused()
    {
        var registrar = new FakeRegistrar();
        registrar.TakenElsewhere.Add((0x1 | 0x2 | 0x4, (uint)'R'));
        using var router = Router([Refresh(), Refresh(keys: "Ctrl+Alt+Shift+T")], new FakeCommunity(), registrar);

        Assert.Equal(["Ctrl+Alt+Shift+R"], router.RefusedKeys);

        var failures = router.Replace([Refresh(keys: "Ctrl+Alt+Shift+T")]);
        Assert.Empty(failures);
        Assert.Empty(router.RefusedKeys);
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class ClosedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("boucle terminée");
    }

    [Fact]
    public async Task UiHotkeyHost_TestsKeysOnTheUiThread_AndExposesTheRefusedKeys()
    {
        var registrar = new FakeRegistrar();
        registrar.TakenElsewhere.Add((0x1 | 0x2 | 0x4, (uint)'R'));
        using var router = Router([Refresh()], new FakeCommunity(), registrar);
        var host = new UiHotkeyHost(new InlineContext(), router);

        // Windows a refusé R au routeur : elle n'est pas à lui, une autre application la tient.
        Assert.Equal(ShortcutKeyState.TakenByAnotherProgram, await host.TestAsync("Ctrl+Alt+Shift+R"));
        Assert.Equal(ShortcutKeyState.Free, await host.TestAsync("Ctrl+Alt+Shift+Z"));
        Assert.Equal(["Ctrl+Alt+Shift+R"], host.RefusedKeys);
    }

    [Fact]
    public async Task UiHotkeyHost_TestWhenTheUiLoopIsGone_IsAnError()
    {
        using var router = Router([], new FakeCommunity());
        var host = new UiHotkeyHost(new ClosedContext(), router);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.TestAsync("Ctrl+Alt+Shift+Z"));
    }

    [Fact]
    public async Task UiHotkeyHost_TestWhenTheRouterIsDisposed_IsAnError()
    {
        var router = Router([], new FakeCommunity());
        var host = new UiHotkeyHost(new InlineContext(), router);
        router.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.TestAsync("Ctrl+Alt+Shift+Z"));
    }

    [Fact]
    public void TestKeys_ReleasesAtOnce_AndOwnKeysAreFree()
    {
        var registrar = new FakeRegistrar();
        registrar.TakenElsewhere.Add((0x1 | 0x2 | 0x4, (uint)'Q'));
        using var router = Router([Refresh()], new FakeCommunity(), registrar);
        var registeredBefore = registrar.Registered.Count;

        Assert.Equal(ShortcutKeyState.Free, router.TestKeys("Ctrl+Alt+Shift+Z"));
        Assert.Equal(ShortcutKeyState.TakenByAnotherProgram, router.TestKeys("Ctrl+Alt+Shift+Q"));
        Assert.Equal(ShortcutKeyState.Unreadable, router.TestKeys("n'importe quoi"));
        Assert.Equal(ShortcutKeyState.Free, router.TestKeys("alt+ctrl+shift+r")); // déjà posée par le routeur

        Assert.Equal(registeredBefore + 1, registrar.Registered.Count); // seul l'essai de Z a abouti
        Assert.Equal([ShortcutKeyProbe.ProbeId], registrar.Unregistered); // et il a été relâché aussitôt
    }
}
