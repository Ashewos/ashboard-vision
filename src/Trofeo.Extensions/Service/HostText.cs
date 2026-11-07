using System.Globalization;
using System.Text;

namespace Trofeo.Extensions.Service;

/// <summary>Le texte que l'hôte écrit (journal, état, messages) est une entrée hostile : on le nettoie avant de l'afficher ou de le journaliser.</summary>
internal static class HostText
{
    /// <summary>
    /// Le texte sur une seule ligne, sans caractère de contrôle, de format (marques de sens d'écriture, espaces de largeur nulle…),
    /// sans séparateur de ligne ou de paragraphe Unicode (qui permettraient de maquiller un message), sans demi-paire de substitution,
    /// coupé à <paramref name="maxLength"/> caractères sans jamais séparer une paire de substitution.
    /// </summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var clean = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var rune in text.EnumerateRunes())
        {
            if (clean.Length + rune.Utf16SequenceLength > maxLength)
            {
                break;
            }

            if (rune.Value is '\r' or '\n' or '\t')
            {
                clean.Append(' ');
                continue;
            }

            if (rune == Rune.ReplacementChar)
            {
                continue; // une demi-paire de substitution devient ce caractère : on ne le garde pas
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
            {
                continue;
            }

            clean.Append(rune.ToString());
        }

        return clean.ToString().Trim();
    }
}
