using System.Diagnostics;
using System.Text.RegularExpressions;
using Trofeo.Extensions;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// La passe « raccourcis » du banc d'essai : livre au module, l'un après l'autre, chaque raccourci que son manifeste déclare, par le chemin
/// du logiciel (le superviseur, le message <c>Shortcut</c>, l'hôte enfermé, <c>OnShortcutAsync</c> sur l'instance de la case).
/// <para>
/// Le logiciel garde une exception de <c>OnShortcutAsync</c> au journal sans mettre la case en erreur ; le banc, lui, la compte contre le module
/// (comme une exception de <c>Render</c>), tout comme un hôte qui s'arrête. Les appuis sont espacés de 300 ms : le service n'en transmet que 5
/// par seconde et par module, le banc reste sous ce plafond.
/// </para>
/// </summary>
internal static partial class CheckShortcuts
{
    /// <summary>L'écart entre deux appuis, pendant lequel on regarde si le module a levé une exception ou arrêté l'hôte.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    /// <summary>Le temps de regarder encore, après le dernier appui, avant de conclure que tout va bien.</summary>
    private static readonly TimeSpan LastObservation = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(
        ModuleManifest manifest, CheckSession session, CheckCase checkCase, CheckLog log, CheckReport report, CancellationToken ct)
    {
        if (manifest.Shortcuts.Count == 0)
        {
            return;
        }

        // Tous les raccourcis sont livrés d'abord, espacés de 300 ms ; on rend ensuite son verdict à chacun d'après le journal : la ligne
        // d'une exception dit de quel raccourci elle vient, donc elle ne peut pas être mise sur le compte du suivant, même si le module est lent.
        var start = log.Total;
        var ids = manifest.Shortcuts.Select(shortcut => shortcut.Id).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (!session.SendShortcut(id, checkCase))
            {
                report.Failure(
                    $"Raccourci « {id} » : le logiciel n'a pas pu le transmettre au module (hôte arrêté, ou case fermée).",
                    $"le raccourci « {id} » n'a pas pu être transmis au module");
                report.AddJournal(log.Lines);
                return;
            }

            if (await HostStoppedWithinAsync(session, i == ids.Count - 1 ? LastObservation : Pause, ct).ConfigureAwait(false) is { } code)
            {
                report.Failure(
                    $"Raccourci « {id} » : l'hôte du module s'arrête (code {code:X8}).",
                    $"le module s'arrête quand le raccourci « {id} » est déclenché");
                report.AddJournal(log.Lines);
                return;
            }
        }

        var thrown = ExceptionsByShortcut(log.Since(start));
        foreach (var id in ids.Where(thrown.ContainsKey))
        {
            report.Failure(
                $"Raccourci « {id} » : le module lève une exception dans OnShortcutAsync ({thrown[id]}). Rattrape-la dans le module.",
                $"le module lève une exception quand le raccourci « {id} » est déclenché");
        }

        if (thrown.Count > 0)
        {
            report.AddJournal(log.Lines);
            return;
        }

        report.Ok($"Raccourcis : {ids.Count} livré{(ids.Count > 1 ? "s" : "")} au module ({string.Join(", ", ids)}), sans erreur.");
    }

    /// <summary>Attend <paramref name="duration"/> en regardant si l'hôte s'arrête. Rend son code de sortie s'il l'a fait, <c>null</c> sinon.</summary>
    private static async Task<int?> HostStoppedWithinAsync(CheckSession session, TimeSpan duration, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (session.Recorder.UnexpectedExitCode is { } code)
            {
                return code;
            }

            if (clock.Elapsed >= duration)
            {
                return null;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Les exceptions de <c>OnShortcutAsync</c> que ces lignes du journal rapportent, par identifiant de raccourci (la première de chacun) :
    /// l'hôte écrit « Exception dans OnShortcutAsync (raccourci « id ») : » puis l'exception. Le début de l'exception suffit, sans la pile.
    /// </summary>
    private static Dictionary<string, string> ExceptionsByShortcut(IEnumerable<string> lines)
    {
        var thrown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (ExceptionLine().Match(line) is { Success: true } match)
            {
                thrown.TryAdd(match.Groups["id"].Value, match.Groups["detail"].Value.Trim());
            }
        }

        return thrown;
    }

    [GeneratedRegex(@"Exception dans OnShortcutAsync \(raccourci « (?<id>[^»]+?) »\) : (?<detail>[^\r\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();
}
