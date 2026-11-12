using System.Diagnostics;
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
internal static class CheckShortcuts
{
    /// <summary>Le texte que l'hôte écrit au journal quand <c>OnShortcutAsync</c> lève une exception.</summary>
    private const string ExceptionMarker = "Exception dans OnShortcutAsync";

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

        var delivered = new List<string>();
        var failed = false;
        for (var i = 0; i < manifest.Shortcuts.Count; i++)
        {
            var id = manifest.Shortcuts[i].Id;
            var before = log.Total;
            if (!session.SendShortcut(id, checkCase))
            {
                report.Failure(
                    $"Raccourci « {id} » : le logiciel n'a pas pu le transmettre au module (hôte arrêté, ou case fermée).",
                    $"le raccourci « {id} » n'a pas pu être transmis au module");
                failed = true;
                break;
            }

            var problem = await ObserveAsync(session, log, id, before, i == manifest.Shortcuts.Count - 1 ? LastObservation : Pause, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                report.Failure(problem.Value.Line, problem.Value.Reason);
                report.AddJournal(log.Lines);
                failed = true;
                break;
            }

            delivered.Add(id);
        }

        if (!failed)
        {
            report.Ok($"Raccourcis : {delivered.Count} livré{(delivered.Count > 1 ? "s" : "")} au module ({string.Join(", ", delivered)}), sans erreur.");
        }
    }

    /// <summary>
    /// Regarde pendant <paramref name="duration"/> si le raccourci qu'on vient d'envoyer a fait lever une exception au module (une ligne du
    /// journal, arrivée après que le journal comptait <paramref name="before"/> lignes) ou arrêté l'hôte. Rend la ligne et la raison de l'échec, ou <c>null</c>.
    /// </summary>
    private static async Task<(string Line, string Reason)?> ObserveAsync(
        CheckSession session, CheckLog log, string id, long before, TimeSpan duration, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (session.Recorder.UnexpectedExitCode is { } code)
            {
                return ($"Raccourci « {id} » : l'hôte du module s'arrête (code {code:X8}).", $"le module s'arrête quand le raccourci « {id} » est déclenché");
            }

            if (log.Since(before).FirstOrDefault(line => line.Contains(ExceptionMarker, StringComparison.Ordinal)) is { } line)
            {
                return ($"Raccourci « {id} » : le module lève une exception dans OnShortcutAsync ({Detail(line)}). Rattrape-la dans le module.",
                    $"le module lève une exception quand le raccourci « {id} » est déclenché");
            }

            if (clock.Elapsed >= duration)
            {
                return null;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Le début de l'exception, sans la pile : « InvalidOperationException: boom du raccourci ».</summary>
    private static string Detail(string line)
    {
        var start = line.IndexOf(':', line.IndexOf(ExceptionMarker, StringComparison.Ordinal));
        var text = start < 0 ? line : line[(start + 1)..].Trim();
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
