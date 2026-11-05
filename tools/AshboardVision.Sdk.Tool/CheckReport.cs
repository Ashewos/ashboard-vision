using System.Globalization;
using Trofeo.Extensions;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// Ce que <c>ashboard-module check</c> écrit : une ligne par vérification (<c>✔</c> réussie, <c>!</c> à regarder, <c>✖</c> échouée) puis
/// la conclusion. Un <c>!</c> ne rend jamais le module incompatible ; un seul <c>✖</c> suffit à le dire « pas compatible ».
/// </summary>
internal sealed class CheckReport(TextWriter output)
{
    /// <summary>La culture des nombres affichés : le banc d'essai écrit en français quelle que soit la langue de Windows.</summary>
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly List<string> _failures = [];
    private readonly List<string> _journal = [];

    /// <summary>Faux dès qu'une vérification a échoué.</summary>
    public bool IsCompatible => _failures.Count == 0;

    /// <summary>Les raisons des échecs, dans l'ordre où elles sont arrivées (ce que dit la conclusion).</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>Une vérification réussie.</summary>
    public void Ok(string text) => output.WriteLine("✔ " + text);

    /// <summary>Une mesure : <c>✔</c> avec <paramref name="ok"/>, ou <c>!</c> avec <paramref name="warning"/> quand <paramref name="aboveMark"/> est vrai.</summary>
    public void Line(bool aboveMark, string ok, string warning)
    {
        if (aboveMark)
        {
            Warning(warning);
        }
        else
        {
            Ok(ok);
        }
    }

    /// <summary>Une vérification qui passe mais que le créateur doit regarder (consommation élevée, par exemple).</summary>
    public void Warning(string text) => output.WriteLine("! " + text);

    /// <summary>Une vérification échouée. <paramref name="reason"/> est la raison courte que la conclusion rappelle (par défaut, le texte de la ligne).</summary>
    public void Failure(string text, string? reason = null)
    {
        output.WriteLine("✖ " + text);
        _failures.Add(Trim(reason ?? text));
    }

    /// <summary>Une ligne d'explication sous la vérification précédente.</summary>
    public void Detail(string text) => output.WriteLine("    " + text);

    /// <summary>Ajoute des lignes du journal du module, qui seront montrées avec une conclusion négative.</summary>
    public void AddJournal(IEnumerable<string> lines) => _journal.AddRange(lines);

    /// <summary>Écrit la conclusion et rend le code de sortie : 0 si le module est compatible, 1 sinon.</summary>
    public int Conclude()
    {
        output.WriteLine();
        if (IsCompatible)
        {
            output.WriteLine($"Compatible avec Ashboard Vision (SDK {Compatibility.Current}).");
            return 0;
        }

        if (_journal.Count > 0)
        {
            output.WriteLine("Dernières lignes du journal du module :");
            foreach (var line in _journal.TakeLast(8))
            {
                output.WriteLine("    " + line);
            }

            output.WriteLine();
        }

        output.WriteLine("Pas compatible : " + string.Join(" ; ", _failures.Distinct()) + ".");
        return 1;
    }

    private static string Trim(string text) => text.TrimEnd('.', ' ');
}
