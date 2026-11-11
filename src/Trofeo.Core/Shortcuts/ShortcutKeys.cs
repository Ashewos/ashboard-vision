namespace Trofeo.Core.Shortcuts;

/// <summary>Lecture de la syntaxe des raccourcis (« Ctrl+Alt+Right ») en modificateurs et touche virtuelle Windows.</summary>
public static class ShortcutKeys
{
    private const uint Alt = 1;
    private const uint Ctrl = 2;
    private const uint Shift = 4;
    private const uint Win = 8;

    /// <summary>
    /// Des mots séparés par « + », sans tenir compte de la casse : au moins un modificateur (<c>Ctrl</c>, <c>Alt</c>,
    /// <c>Shift</c>, <c>Win</c>) puis une seule touche finale (<c>Left</c>, <c>Right</c>, <c>Up</c>, <c>Down</c>,
    /// <c>0</c> à <c>9</c>, <c>A</c> à <c>Z</c>, <c>F1</c> à <c>F24</c>). Renvoie <c>false</c> pour toute autre forme.
    /// </summary>
    /// <param name="modifiers">Les modificateurs au format de <c>RegisterHotKey</c> (Alt 1, Ctrl 2, Shift 4, Win 8).</param>
    /// <param name="virtualKey">Le code de touche virtuelle.</param>
    public static bool TryParse(string keys, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(keys)) return false;

        var words = keys.Split('+', StringSplitOptions.TrimEntries);
        if (words.Length < 2) return false; // au moins un modificateur et une touche

        uint mods = 0;
        for (var i = 0; i < words.Length - 1; i++)
        {
            var modifier = Modifier(words[i]);
            if (modifier == 0) return false;
            mods |= modifier;
        }

        var key = Key(words[^1]);
        if (key == 0) return false;

        modifiers = mods;
        virtualKey = key;
        return true;
    }

    /// <summary>Les deux écritures désignent la même combinaison (« alt+ctrl+right » et « Ctrl+Alt+Right »).</summary>
    public static bool Same(string a, string b) =>
        TryParse(a, out var modsA, out var keyA) && TryParse(b, out var modsB, out var keyB)
            ? modsA == modsB && keyA == keyB
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static uint Modifier(string word) => word.ToUpperInvariant() switch
    {
        "CTRL" => Ctrl,
        "ALT" => Alt,
        "SHIFT" => Shift,
        "WIN" => Win,
        _ => 0,
    };

    private static uint Key(string word)
    {
        var upper = word.ToUpperInvariant();
        switch (upper)
        {
            case "LEFT": return 0x25;
            case "UP": return 0x26;
            case "RIGHT": return 0x27;
            case "DOWN": return 0x28;
        }

        if (upper.Length == 1 && (char.IsAsciiDigit(upper[0]) || char.IsAsciiLetterUpper(upper[0]))) return upper[0];

        if (upper.Length is 2 or 3 && upper[0] == 'F' && upper.Skip(1).All(char.IsAsciiDigit)
            && int.Parse(upper.AsSpan(1)) is >= 1 and <= 24 and var n)
        {
            return (uint)(0x70 + n - 1);
        }

        return 0;
    }
}
