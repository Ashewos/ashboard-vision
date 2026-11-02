using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AshboardVision.Sdk;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Un processus d'hôte de module en train de tourner, vu par le superviseur. En production c'est un
/// <see cref="SandboxedProcess"/> rangé dans son <see cref="ModuleJob"/> ; les tests en fournissent d'autres.
/// </summary>
public interface IHostProcess : IDisposable
{
    /// <summary>Se termine, avec le code de sortie, quand le processus s'arrête (annulée si le processus est libéré avant).</summary>
    Task<int> Exited { get; }

    /// <summary>Le dossier privé du module (<see cref="AshboardVision.Sdk.ModuleServices"/> : son dossier de données).</summary>
    string DataDirectory { get; }

    /// <summary>Duplique une poignée de ce processus-ci dans l'hôte et renvoie sa valeur là-bas.</summary>
    long DuplicateInto(SafeHandle source);

    /// <summary>Arrête le processus (sans effet s'il est déjà terminé).</summary>
    void Kill();

    /// <summary>Ce que le processus a consommé depuis son lancement ; <c>null</c> s'il ne se mesure pas (ou plus).</summary>
    JobUsage? ReadUsage();

    /// <summary>La mémoire privée du processus en octets ; <c>null</c> si elle ne se mesure pas (ou plus). Sans réponse par défaut : les faux processus des tests n'ont pas à la fournir.</summary>
    long? ReadPrivateBytes() => null;
}

/// <summary>Ce qu'il faut pour lancer un hôte de module.</summary>
/// <param name="Executable">Chemin complet de <c>Trofeo.ModuleHost.exe</c>.</param>
/// <param name="Arguments">Ligne de commande : <c>--in &lt;poignée&gt; --out &lt;poignée&gt;</c>.</param>
/// <param name="InheritedHandles">Les deux bouts « client » des tubes (lecture, puis écriture, côté hôte) : les seules poignées que l'hôte reçoit. Créées non héritables ; c'est la fabrique qui les rend héritables le temps du lancement.</param>
/// <param name="ModuleId">L'identifiant du module (nom de son AppContainer).</param>
/// <param name="ModuleDirectory">Le dossier du module, à rendre lisible à l'hôte.</param>
/// <param name="Granted">Les permissions accordées.</param>
public sealed record HostStart(
    string Executable,
    string Arguments,
    IReadOnlyList<SafeHandle> InheritedHandles,
    string ModuleId,
    string ModuleDirectory,
    IReadOnlyList<ModulePermission> Granted);

/// <summary>Lance les hôtes de module.</summary>
public interface IHostProcessFactory
{
    /// <summary>Lance l'hôte. L'appelant ferme ses copies des bouts « client » une fois que cette méthode a rendu la main.</summary>
    IHostProcess Start(HostStart start);
}

/// <summary>
/// La fabrique de production : l'hôte tourne dans l'AppContainer du module (lecture du dossier du module accordée à ce
/// conteneur seul) et dans un job object (mémoire, processeur, aucun autre processus).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SandboxedHostProcessFactory : IHostProcessFactory
{
    /// <inheritdoc />
    public IHostProcess Start(HostStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var profile = AppContainerProfile.Ensure(start.ModuleId);
        profile.GrantRead(start.ModuleDirectory);
        var job = new ModuleJob();
        try
        {
            var process = SandboxedProcess.Start(new SandboxStart(start.Executable, start.Arguments, profile, start.Granted, start.InheritedHandles, job));
            return new SandboxedHostProcess(process, job, profile.DataDirectory);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private sealed class SandboxedHostProcess(SandboxedProcess process, ModuleJob job, string dataDirectory) : IHostProcess
    {
        private int _disposed;

        public Task<int> Exited => process.Exited;

        public string DataDirectory { get; } = dataDirectory;

        public long DuplicateInto(SafeHandle source) => process.DuplicateInto(source).ToInt64();

        public void Kill()
        {
            if (Volatile.Read(ref _disposed) == 0 && !process.Exited.IsCompleted)
            {
                try
                {
                    process.Kill();
                }
                catch (ObjectDisposedException)
                {
                    // libéré en même temps : il n'y a plus rien à arrêter
                }
            }
        }

        public JobUsage? ReadUsage()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            try
            {
                return job.Read();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                return null;
            }
        }

        public long? ReadPrivateBytes() => Volatile.Read(ref _disposed) != 0 ? null : process.ReadPrivateBytes();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            process.Dispose();
            job.Dispose(); // Windows arrête ce qui y tourne encore
        }
    }
}
