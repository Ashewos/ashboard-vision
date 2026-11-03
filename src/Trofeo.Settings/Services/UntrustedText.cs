using System.Globalization;
using System.Text;

namespace Trofeo.Settings.Services;

/// <summary>
/// Le texte qui vient d'un module complémentaire (nom, auteur, description, notes, message) est une donnée non fiable : la fenêtre le
/// montre comme du texte brut (jamais interprété) et en retire d'abord ce qui peut tromper l'œil, notamment dans la boîte qui
/// avertit de l'installation : les caractères de contrôle, les marques de sens d'écriture (qui font lire un texte à l'envers), les
/// caractères de largeur nulle et les séparateurs de ligne ou de paragraphe Unicode.
/// </summary>
public static class UntrustedText
{
    /// <summary>
    /// Le texte nettoyé, sur une seule ligne (les retours à la ligne deviennent des espaces). <c>null</c> donne un texte vide.
    /// </summary>
    public static string Line(string? text) => Clean(text, keepLineBreaks: false);

    /// <summary>Le texte nettoyé, retours à la ligne gardés (les notes d'une version). <c>null</c> donne un texte vide.</summary>
    public static string Block(string? text) => Clean(text, keepLineBreaks: true);

    private static string Clean(string? text, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    result.Append(keepLineBreaks ? '\n' : ' ');
                    break;
                case '\r':
                    break;
                case '\t':
                    result.Append(' ');
                    break;
                default:
                    var category = char.GetUnicodeCategory(c);
                    // Cc : contrôle ; Cf : format (sens d'écriture, largeur nulle, trait d'union conditionnel) ; Zl, Zp : séparateurs de ligne.
                    if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator)
                        break;
                    result.Append(c);
                    break;
            }
        }

        return result.ToString().Trim();
    }
}
