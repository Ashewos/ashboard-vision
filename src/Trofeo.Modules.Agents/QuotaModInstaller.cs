using System.ComponentModel;
using System.Diagnostics;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents;

/// <summary>
/// Installe le mod <c>trofeo-quotas</c> dans Claude Code : ajoute le dossier <c>mods</c> comme marché de modules, puis installe
/// le mod. Essaie <c>claude</c> dans le PATH, puis le CLI natif ou embarqué dans Claude Desktop s'il est introuvable.
/// <paramref name="run"/> lance l'exécutable choisi et rend le code de retour et la sortie.
/// </summary>
public sealed class QuotaModInstaller(
    Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> run, string? modsDir,
    Func<IReadOnlyList<string>>? fallbackExecutables)
{
    public QuotaModInstaller(Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> run,
        string? modsDir) : this(run, modsDir, null) { }

    internal const string PluginName = "trofeo-quotas@trofeo-hb-dashboard";
    private const int MaxOutputLength = 300;
    private IReadOnlyList<string> FallbackExecutables => fallbackExecutables?.Invoke() ?? ClaudeCliLocator.FindCandidates();

    public async Task<ActionResult> InstallAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
            return new ActionResult(false, "Le dossier du mod est introuvable.");

        try
        {
            var executable = "claude";
            Queue<string>? fallbacks = null;
            foreach (var arguments in new[] { $"plugin marketplace add \"{modsDir}\"", $"plugin install {PluginName}" })
            {
                var (exitCode, output) = await RunAsync(arguments);
                if (exitCode != 0)
                {
                    var text = output.Trim();
                    if (text.Length > MaxOutputLength) text = text[..MaxOutputLength];
                    return new ActionResult(false, "L'installation a échoué : " + text);
                }
            }

            async Task<(int ExitCode, string Output)> RunAsync(string arguments)
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
            }
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, "Claude Code est introuvable sur ce PC.");
        }

        return new ActionResult(true, "Mod installé : il sera chargé par les nouvelles sessions de Claude Code.");
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
