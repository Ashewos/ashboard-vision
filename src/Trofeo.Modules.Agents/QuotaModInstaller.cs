using System.ComponentModel;
using System.Diagnostics;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Installe le mod <c>ashboard-quotas</c> dans Claude Code : ajoute le dossier <c>mods</c> comme marché de modules, puis installe
/// le mod ; sait aussi le retirer, avec l'ancien mod <c>trofeo-quotas</c>. <paramref name="run"/> lance <c>claude</c> avec les arguments donnés et rend le code de retour et la sortie ;
/// elle lève <see cref="Win32Exception"/> (ou <see cref="FileNotFoundException"/>) quand <c>claude</c> est introuvable.
/// </summary>
public sealed class QuotaModInstaller(
    Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> run, string? modsDir)
{
    /// <summary>Le mod, sous la forme <c>mod@marché</c> que demande <c>claude plugin</c>.</summary>
    public const string PluginName = "ashboard-quotas@ashboard-vision";

    /// <summary>Le marché de modules qui le contient (le dossier <c>mods</c>).</summary>
    public const string Marketplace = "ashboard-vision";

    /// <summary>L'ancien mod, d'avant le changement de nom : à retirer.</summary>
    public const string LegacyPluginName = "trofeo-quotas@trofeo-hb-dashboard";

    /// <summary>L'ancien marché de modules, d'avant le changement de nom : à retirer.</summary>
    public const string LegacyMarketplace = "trofeo-hb-dashboard";

    private const int MaxOutputLength = 300;

    public async Task<ActionResult> InstallAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
            return new ActionResult(false, "Le dossier du mod est introuvable.");

        try
        {
            foreach (var arguments in new[] { $"plugin marketplace add \"{modsDir}\"", $"plugin install {PluginName}" })
            {
                var (exitCode, output) = await run("claude", arguments, ct);
                if (exitCode != 0) return new ActionResult(false, "L'installation a échoué : " + Truncated(output));
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, "Claude Code est introuvable sur ce PC.");
        }

        return new ActionResult(true, "Mod installé : il sera chargé par les nouvelles sessions de Claude Code.");
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
            foreach (var arguments in new[] { $"plugin uninstall {plugin}", $"plugin marketplace remove {marketplace}" })
            {
                var (exitCode, output) = await run("claude", arguments, ct);
                if (exitCode != 0) failure ??= Truncated(output);
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, "Claude Code est introuvable sur ce PC.");
        }

        return failure is null
            ? new ActionResult(true, "Mod retiré de Claude Code.")
            : new ActionResult(false, "Le retrait a échoué : " + failure);
    }

    private static string Truncated(string output)
    {
        var text = output.Trim();
        return text.Length > MaxOutputLength ? text[..MaxOutputLength] : text;
    }

    /// <summary>Lanceur réel : <c>claude</c> est résolu par le <c>PATH</c> ; 60 s au plus par commande.</summary>
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
