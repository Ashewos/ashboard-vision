using System.Globalization;

namespace Trofeo.Extensions;

/// <summary>
/// Une version du SDK des modules : <c>MAJEUR.MINEUR</c> (« 1.0 »). Le deuxième chiffre augmente quand le SDK gagne
/// quelque chose ; rien n'est retiré tant que le premier ne change pas.
/// </summary>
public readonly record struct SdkVersion(int Major, int Minor) : IComparable<SdkVersion>
{
    /// <summary>Lit la forme <c>M.m</c> seulement : deux nombres entiers positifs séparés par un point, sans <c>v</c> ni espace.</summary>
    public static bool TryParse(string? text, out SdkVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
        {
            return false;
        }

        var major = text.AsSpan(0, dot);
        var minor = text.AsSpan(dot + 1);
        if (!IsDigits(major) || !IsDigits(minor)
            || !int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var maj)
            || !int.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out var min))
        {
            return false;
        }

        version = new SdkVersion(maj, min);
        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SdkVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
    }

    /// <summary>Par exemple <c>1.0</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
