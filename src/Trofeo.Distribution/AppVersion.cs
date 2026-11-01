using System.Text.RegularExpressions;

namespace Trofeo.Distribution;

/// <summary>
/// Une version SemVer 2.0 : <c>majeure.mineure.correctif</c>, avec une partie de pré-version facultative
/// (<c>1.0.0-beta.2</c>). Les métadonnées de construction (<c>+abc123</c>) sont ignorées, comme SemVer le demande
/// pour l'ordre.
/// </summary>
public readonly partial record struct AppVersion(int Major, int Minor, int Patch, string? Prerelease = null)
    : IComparable<AppVersion>
{
    [GeneratedRegex(
        @"^v?(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)" +
        @"(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>Vrai pour une bêta ou toute autre pré-version : elle passe avant la stable du même numéro.</summary>
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    /// <summary>Lit une version, avec ou sans <c>v</c> devant et avec ou sans <c>+métadonnées</c> derrière.</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var match = VersionPattern().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["maj"].ValueSpan, out var major)
            || !int.TryParse(match.Groups["min"].ValueSpan, out var minor)
            || !int.TryParse(match.Groups["pat"].ValueSpan, out var patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    /// <summary>L'ordre de priorité de SemVer 2.0 : une pré-version précède sa stable.</summary>
    public int CompareTo(AppVersion other)
    {
        var parNumero = Major.CompareTo(other.Major);
        if (parNumero == 0) parNumero = Minor.CompareTo(other.Minor);
        if (parNumero == 0) parNumero = Patch.CompareTo(other.Patch);
        if (parNumero != 0)
        {
            return parNumero;
        }

        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        return IsPrerelease ? ComparePrerelease(Prerelease!, other.Prerelease!) : 0;
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var c = CompareIdentifier(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // Tous les identifiants communs sont égaux : le plus court précède.
        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumber = left.All(char.IsAsciiDigit);
        var rightIsNumber = right.All(char.IsAsciiDigit);
        if (leftIsNumber && rightIsNumber)
        {
            // Comparés comme des nombres, sans risque de débordement : moins de chiffres, plus petit.
            var a = left.TrimStart('0');
            var b = right.TrimStart('0');
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        if (leftIsNumber != rightIsNumber)
        {
            // Un identifiant numérique précède un identifiant texte.
            return leftIsNumber ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    /// <summary>La version sans le <c>v</c> ni les métadonnées, par exemple <c>1.0.0-beta.2</c>.</summary>
    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
