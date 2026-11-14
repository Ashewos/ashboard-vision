using Trofeo.Control;
using Trofeo.Core.Shortcuts;

namespace Trofeo.Display.Tests;

/// <summary>Un faux Windows : il refuse les combinaisons qu'une « autre application » tient, et note ce qu'on enregistre et relâche.</summary>
internal sealed class FakeRegistrar : IHotkeyRegistrar
{
    private const uint ModNoRepeat = 0x4000;

    public HashSet<(uint Modifiers, uint VirtualKey)> TakenElsewhere { get; } = [];
    public List<(int Id, uint Modifiers, uint VirtualKey)> Registered { get; } = [];
    public List<int> Unregistered { get; } = [];

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (TakenElsewhere.Contains((modifiers & ~ModNoRepeat, virtualKey))) return false;
        Registered.Add((id, modifiers, virtualKey));
        return true;
    }

    public void Unregister(int id) => Unregistered.Add(id);
}

// La requête « cette touche est-elle libre ? » et les touches refusées dans l'état du service.
public sealed partial class ServiceControlHandlerTests
{
    private static (uint Modifiers, uint VirtualKey) Parse(string keys)
    {
        Assert.True(ShortcutKeys.TryParse(keys, out var modifiers, out var virtualKey));
        return (modifiers, virtualKey);
    }

    [Fact]
    public async Task TestShortcutKeys_KeyTakenElsewhere_ReportsIt()
    {
        using var rig = new Rig();
        rig.Hotkeys.Registrar.TakenElsewhere.Add(Parse("Ctrl+Alt+Shift+R"));

        var taken = await rig.Handler.TestShortcutKeysAsync("Ctrl+Alt+Shift+R", CancellationToken.None);
        var free = await rig.Handler.TestShortcutKeysAsync("Ctrl+Alt+Shift+T", CancellationToken.None);

        Assert.Equal(ShortcutKeyState.TakenByAnotherProgram, taken);
        Assert.Equal(ShortcutKeyState.Free, free);
        // L'essai libre est relâché aussitôt ; l'essai refusé n'a rien enregistré.
        Assert.Equal([ShortcutKeyProbe.ProbeId], rig.Hotkeys.Registrar.Registered.Select(r => r.Id));
        Assert.Equal([ShortcutKeyProbe.ProbeId], rig.Hotkeys.Registrar.Unregistered);
    }

    [Fact]
    public async Task TestShortcutKeys_UnreadableKeys_AreReportedWithoutTouchingWindows()
    {
        using var rig = new Rig();

        Assert.Equal(ShortcutKeyState.Unreadable, await rig.Handler.TestShortcutKeysAsync("Ctrl+Alt+Ouvre-toi", CancellationToken.None));
        Assert.Equal(ShortcutKeyState.Unreadable, await rig.Handler.TestShortcutKeysAsync("", CancellationToken.None));

        Assert.Empty(rig.Hotkeys.Registrar.Registered);
    }

    [Fact]
    public async Task TestShortcutKeys_ReadableButTooLong_IsRefusedAsUnreadable_AtTheLimitItIsRead()
    {
        using var rig = new Rig();
        // Lisible (les doublons de modificateurs passent par TryParse) : seul le plafond de longueur le refuse.
        var tooLong = string.Concat(Enumerable.Repeat("Ctrl+", 25)) + "R"; // 126 caractères
        var atTheLimit = string.Concat(Enumerable.Repeat("Ctrl+", 19)) + "Alt+R"; // exactement 100 caractères
        Assert.True(ShortcutKeys.TryParse(tooLong, out _, out _));
        Assert.Equal(ShortcutKeyProbe.MaxKeysLength, atTheLimit.Length);

        Assert.Equal(ShortcutKeyState.Unreadable, await rig.Handler.TestShortcutKeysAsync(tooLong, CancellationToken.None));
        Assert.Empty(rig.Hotkeys.Registrar.Registered); // Windows n'a pas été sollicité

        Assert.Equal(ShortcutKeyState.Free, await rig.Handler.TestShortcutKeysAsync(atTheLimit, CancellationToken.None));
        Assert.Equal(ShortcutKeyState.Free, await rig.Handler.TestShortcutKeysAsync("Ctrl+Alt+Shift+Win+F24", CancellationToken.None));
    }

    [Fact]
    public async Task TestShortcutKeys_KeyAlreadyRegisteredByTheService_IsFree()
    {
        using var rig = new Rig();
        rig.Hotkeys.OwnKeys.Add("Ctrl+Alt+Right");
        rig.Hotkeys.Registrar.TakenElsewhere.Add(Parse("Ctrl+Alt+Right")); // Windows la refuserait : c'est le service qui la tient

        var state = await rig.Handler.TestShortcutKeysAsync("alt+ctrl+right", CancellationToken.None);

        Assert.Equal(ShortcutKeyState.Free, state);
        Assert.Empty(rig.Hotkeys.Registrar.Registered);
    }

    [Fact]
    public async Task TestShortcutKeys_WithoutHotkeys_IsAnError()
    {
        using var rig = new Rig(withoutHotkeys: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Handler.TestShortcutKeysAsync("Ctrl+Alt+Shift+R", CancellationToken.None));

        Assert.Contains("raccourcis", error.Message);
    }

    [Fact]
    public void RefusedKeys_AreInTheStatus()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Handler.GetStatus().RefusedShortcutKeys);

        rig.Hotkeys.RefusedKeys = ["Ctrl+Alt+Right", "Ctrl+Alt+Shift+R"];

        Assert.Equal(["Ctrl+Alt+Right", "Ctrl+Alt+Shift+R"], rig.Handler.GetStatus().RefusedShortcutKeys);
    }

    [Fact]
    public void RefusedKeys_WithoutHotkeys_AreEmpty()
    {
        using var rig = new Rig(withoutHotkeys: true);

        Assert.Empty(rig.Handler.GetStatus().RefusedShortcutKeys);
    }
}
