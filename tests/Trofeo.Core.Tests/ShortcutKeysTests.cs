using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public sealed class ShortcutKeysTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Right", 3u, 0x27u)]
    [InlineData("ctrl+shift+F5", 6u, 0x74u)]
    [InlineData("Alt+1", 1u, 0x31u)]
    [InlineData("Win+Shift+a", 12u, 0x41u)]
    [InlineData("Ctrl+F24", 2u, 0x87u)]
    [InlineData("Ctrl+Alt+Left", 3u, 0x25u)]
    [InlineData("Ctrl+Alt+Shift+1", 7u, 0x31u)]
    [InlineData("Ctrl+Up", 2u, 0x26u)]
    [InlineData("Ctrl+Down", 2u, 0x28u)]
    public void TryParse_Table(string keys, uint modifiers, uint virtualKey)
    {
        Assert.True(ShortcutKeys.TryParse(keys, out var mods, out var vk));
        Assert.Equal((modifiers, virtualKey), (mods, vk));
    }

    [Theory]
    [InlineData("Right")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+Foo")]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("A+Ctrl")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+A+B")]
    public void TryParse_Invalid_ReturnsFalse(string keys)
    {
        Assert.False(ShortcutKeys.TryParse(keys, out var mods, out var vk));
        Assert.Equal((0u, 0u), (mods, vk));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Shift+R\n", "Ctrl+Alt+Shift+R")]
    [InlineData(" alt + ctrl + r ", "Ctrl+Alt+R")]
    [InlineData("Win+Shift+ctrl+a", "Ctrl+Shift+Win+A")]
    [InlineData("ctrl+ctrl+f5", "Ctrl+F5")]
    [InlineData("Ctrl+Alt+right", "Ctrl+Alt+Right")]
    [InlineData("Ctrl+Alt+UP", "Ctrl+Alt+Up")]
    [InlineData("ctrl+alt+f24", "Ctrl+Alt+F24")]
    [InlineData("Alt+1", "Alt+1")]
    public void Normalize_GivesTheCanonicalForm(string keys, string expected)
    {
        Assert.Equal(expected, ShortcutKeys.Normalize(keys));
        Assert.True(ShortcutKeys.Same(keys, expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Right")]
    [InlineData("Ctrl+Alt+Foo")]
    public void Normalize_Unreadable_IsNull(string keys) => Assert.Null(ShortcutKeys.Normalize(keys));

    [Fact]
    public void Defaults_ThirteenBindings()
    {
        var all = ShortcutDefaults.All;

        Assert.Equal(13, all.Count);
        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Up", ShortcutCommand.PreviousTab), all[0]);
        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Down", ShortcutCommand.NextTab), all[1]);
        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Left", ShortcutCommand.PreviousView), all[2]);
        Assert.Equal(new ShortcutBinding("Ctrl+Alt+Right", ShortcutCommand.NextView), all[3]);
        for (var n = 1; n <= 9; n++)
            Assert.Equal(new ShortcutBinding($"Ctrl+Alt+Shift+{n}", ShortcutCommand.SelectTab, n - 1), all[n + 3]);
        Assert.All(all, b => Assert.Null(b.Target));
        Assert.All(all, b => Assert.True(ShortcutKeys.TryParse(b.Keys, out _, out _)));
    }

    [Fact]
    public void Same_IgnoresCaseAndModifierOrder()
    {
        Assert.True(ShortcutKeys.Same("alt+ctrl+right", "Ctrl+Alt+Right"));
        Assert.False(ShortcutKeys.Same("Ctrl+Alt+Left", "Ctrl+Alt+Right"));
        Assert.True(ShortcutKeys.Same(" foo ", "FOO"));
    }
}
