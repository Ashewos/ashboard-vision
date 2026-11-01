using System.Text;

namespace Trofeo.Extensions.Service;

/// <summary>Le texte que l'hôte écrit (journal, état, messages) est une entrée hostile : on le nettoie avant de l'afficher ou de le journaliser.</summary>
internal static class HostText
{
    /// <summary>
    /// Le texte sur une seule ligne, sans caractère de contrôle ni de changement de sens d'écriture (qui permettraient de maquiller
    /// un message), coupé à <paramref name="maxLength"/> caractères.
    /// </summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var clean = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (clean.Length >= maxLength)
            {
                break;
            }

            if (c is '\r' or '\n' or '\t')
            {
                clean.Append(' ');
            }
            else if (!char.IsControl(c) && c is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069')
                     and not ('\u200E' or '\u200F' or '\u061C'))
            {
                clean.Append(c);
            }
        }

        return clean.ToString().Trim();
    }
}
