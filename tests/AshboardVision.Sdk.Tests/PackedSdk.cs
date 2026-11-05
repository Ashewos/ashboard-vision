using System.Diagnostics;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Fabrique le paquet une fois pour tous les tests qui le lisent : un vrai <c>dotnet pack</c> en Debug (jamais Release : voir
/// la règle du dépôt, et <c>dotnet pack</c> prend Release par défaut), dont la sortie ET les dossiers intermédiaires (bin, obj)
/// sont dans un dossier temporaire, pour ne jamais se heurter à une construction de la solution qui tourne en parallèle.
/// Rien n'est publié : le paquet reste un fichier local.
/// </summary>
public class PackedSdk : IDisposable
{
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ashboard-sdk-pack-" + Guid.NewGuid().ToString("N"));

    public PackedSdk() : this("AshboardVision.Sdk")
    {
    }

    /// <summary>
    /// Fabrique le paquet du projet <paramref name="projectName"/>, par exemple <c>AshboardVision.Sdk.Testing</c>, dans le dossier
    /// <paramref name="projectFolder"/> de la racine du dépôt (<c>src</c>, ou <c>tools</c> pour le banc d'essai).
    /// </summary>
    protected PackedSdk(string projectName, string projectFolder = "src")
    {
        try
        {
            var output = Path.Combine(_workDirectory, "out");
            var artifacts = Path.Combine(_workDirectory, "artifacts");
            Directory.CreateDirectory(output);

            var project = Path.Combine(RepositoryRoot.Find(), projectFolder, projectName, projectName + ".csproj");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "pack", project, "-c", "Debug", "-o", output, "--artifacts-path", artifacts, "-nodeReuse:false", "--nologo" })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet pack n'a pas démarré.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("dotnet pack a dépassé 5 minutes.");
            }

            Log = stdout.Result + stderr.Result;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("dotnet pack a échoué (code " + process.ExitCode + ") :\n" + Log);
            }

            var packages = Directory.GetFiles(output, "*.nupkg");
            PackagePath = packages.Length == 1
                ? packages[0]
                : throw new InvalidOperationException("Un seul .nupkg attendu dans " + output + ", trouvé : " + string.Join(", ", packages));
        }
        catch
        {
            // Le constructeur a échoué : xunit n'appellera pas Dispose, le dossier temporaire se supprime ici.
            Dispose();
            throw;
        }
    }

    /// <summary>La sortie de <c>dotnet pack</c> (pour les messages d'échec).</summary>
    public string Log { get; }

    /// <summary>Le chemin du fichier <c>.nupkg</c> fabriqué.</summary>
    public string PackagePath { get; }

    public virtual void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Un antivirus peut encore tenir un fichier ; le dossier temporaire sera nettoyé plus tard par le système.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Le paquet <c>AshboardVision.Sdk.Testing</c>, fabriqué comme celui du SDK (un seul <c>dotnet pack</c> pour tous les tests qui le lisent).</summary>
public sealed class PackedSdkTesting : PackedSdk
{
    public PackedSdkTesting() : base("AshboardVision.Sdk.Testing")
    {
    }
}
