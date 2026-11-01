using System.ComponentModel;
using System.Diagnostics;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Installe le mod <c>ashboard-quotas</c> dans Claude Code : ajoute le dossier <c>mods</c> comme marché de modules, puis installe
/// le mod ; sait aussi le retirer, avec l'ancien mod <c>trofeo-quotas</c>. <paramref name="run"/> lance <c>claude</c> avec les arguments donnés et rend le code de retour et la sortie ;
/// elle lève <see cref="Win32Exception"/> (ou <see cref="FileNotFoundException"/>) quand <c>claude</c> est introuvable.
/// Chaque commande essaie <c>claude</c> dans le PATH, puis, s'il est introuvable, les exécutables de
/// <paramref name="fallbackExecutables"/> (par défaut le CLI natif ou celui fourni par l'application Claude, voir <see cref="ClaudeCliLocator"/>).
/// </summary>
public sealed class QuotaModInstaller(
    Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> run, string? modsDir,
    Func<IReadOnlyList<string>>? fallbackExecutables = null)
{
    /// <summary>Le mod, sous la forme <c>mod@marché</c> que demande <c>claude plugin</c>.</summary>
    public const string PluginName = "ashboard-quotas@ashboard-vision";

    /// <summary>Le marché de modules qui le contient (le dossier <c>mods</c>).</summary>
    public const string Marketplace = "ashboard-vision";

    /// <summary>L'ancien mod, d'avant le changement de nom : à retirer.</summary>
    public const string LegacyPluginName = "trofeo-quotas@trofeo-hb-dashboard";

    /// <summary>L'ancien marché de modules, d'avant le changement de nom : à retirer.</summary>
    public const string LegacyMarketplace = "trofeo-hb-dashboard";

    /// <summary>Le message rendu quand <c>claude</c> n'est pas lançable : les appelants le reconnaissent pour sauter les étapes du mod.</summary>
    public const string ClaudeMissingMessage = "Claude Code est introuvable sur ce PC.";

    private const int MaxOutputLength = 300;
    private IReadOnlyList<string> FallbackExecutables => fallbackExecutables?.Invoke() ?? ClaudeCliLocator.FindCandidates();

    /// <summary>L'installateur réel, qui lance <c>claude</c> ; <paramref name="modsDir"/> n'est utile qu'à l'installation.</summary>
    public static QuotaModInstaller CreateProduction(string? modsDir) => new(RunProcessAsync, modsDir);

    public async Task<ActionResult> InstallAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
            return new ActionResult(false, "Le dossier du mod est introuvable.");

        try
        {
            var claude = ClaudeLauncher();
            foreach (var arguments in new[] { $"plugin marketplace add \"{modsDir}\"", $"plugin install {PluginName}" })
            {
                var (exitCode, output) = await claude(arguments, ct);
                if (exitCode != 0) return new ActionResult(false, "L'installation a échoué : " + Truncated(output));
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, ClaudeMissingMessage);
        }

        return new ActionResult(true, "Mod installé : il sera chargé par les nouvelles sessions de Claude Code.");
    }

    /// <summary>
    /// Lance <c>claude plugin list --json</c> : en cas de succès, le message est la sortie telle quelle (un tableau JSON dont chaque objet
    /// a un <c>id</c> de la forme <c>mod@marché</c>, deux fois s'il est installé aux portées user et project) ; sinon un échec, avec <see cref="ClaudeMissingMessage"/> quand <c>claude</c> est introuvable.
    /// </summary>
    public async Task<ActionResult> ListAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await ClaudeLauncher()("plugin list --json", ct);
            return exitCode == 0
                ? new ActionResult(true, output)
                : new ActionResult(false, "La liste des mods n'a pas pu être lue : " + Truncated(output));
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, ClaudeMissingMessage);
        }
    }

    /// <summary>
    /// Rafraîchit le mod déjà installé : met à jour le marché de modules (le dossier <c>mods</c> a pu changer avec la nouvelle version),
    /// puis le mod. Les deux commandes sont lancées même si la première échoue ; le résultat est un échec si l'une d'elles a échoué,
    /// avec la sortie de la première.
    /// </summary>
    public async Task<ActionResult> UpdateAsync(CancellationToken ct)
    {
        string? failure = null;
        try
        {
            var claude = ClaudeLauncher();
            foreach (var arguments in new[] { $"plugin marketplace update {Marketplace}", $"plugin update {PluginName}" })
            {
                var (exitCode, output) = await claude(arguments, ct);
                if (exitCode != 0) failure ??= Truncated(output);
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, ClaudeMissingMessage);
        }

        return failure is null
            ? new ActionResult(true, "Mod mis à jour : il sera rechargé par les nouvelles sessions de Claude Code.")
            : new ActionResult(false, "La mise à jour a échoué : " + failure);
    }

    /// <summary>
    /// Retire le mod <paramref name="plugin"/> (<c>mod@marché</c>) puis le marché <paramref name="marketplace"/>. Les deux commandes
    /// sont lancées même si la première échoue (le mod n'était peut-être déjà plus là) ; le résultat est un échec si l'une d'elles
    /// a échoué, avec la sortie de la première.
    /// </summary>
    public async Task<ActionResult> UninstallAsync(string plugin, string marketplace, CancellationToken ct)
    {
        string? failure = null;
        try
        {
            var claude = ClaudeLauncher();
            foreach (var arguments in new[] { $"plugin uninstall {plugin}", $"plugin marketplace remove {marketplace}" })
            {
                var (exitCode, output) = await claude(arguments, ct);
                if (exitCode != 0) failure ??= Truncated(output);
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, ClaudeMissingMessage);
        }

        return failure is null
            ? new ActionResult(true, "Mod retiré de Claude Code.")
            : new ActionResult(false, "Le retrait a échoué : " + failure);
    }

    /// <summary>
    /// Le lanceur d'une opération : essaie <c>claude</c> du PATH, puis, s'il est introuvable, chaque exécutable de repli dans l'ordre,
    /// et garde le premier qui se lance pour les commandes suivantes de la même opération. Quand aucun ne se lance, la dernière
    /// erreur remonte (l'appelant la rend en <see cref="ClaudeMissingMessage"/>).
    /// </summary>
    private Func<string, CancellationToken, Task<(int ExitCode, string Output)>> ClaudeLauncher()
    {
        var executable = "claude";
        Queue<string>? fallbacks = null;
        return async (arguments, ct) =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { return await run(executable, arguments, ct); }
                catch (Exception e) when (e is FileNotFoundException || e is Win32Exception { NativeErrorCode: 2 or 3 })
                {
                    fallbacks ??= new Queue<string>(FallbackExecutables);
                    if (!fallbacks.TryDequeue(out var next)) throw;
                    executable = next;
                }
            }
        };
    }

    private static string Truncated(string output)
    {
        var text = output.Trim();
        return text.Length > MaxOutputLength ? text[..MaxOutputLength] : text;
    }

    /// <summary>Lanceur réel sans shell, pour le PATH ou un chemin absolu ; 60 s au plus par commande.</summary>
    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.Start();

        // Lecture des deux flux en parallèle : un tampon plein bloquerait la commande.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Le processus s'est terminé entre-temps : rien à arrêter.
            }

            ct.ThrowIfCancellationRequested();
            return (-1, "La commande a dépassé 60 s.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
