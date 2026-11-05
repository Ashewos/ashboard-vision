using Trofeo.Extensions.Sandbox;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// Supprime le conteneur d'essai à la fin, ou à la sortie du processus (Ctrl+C, fermeture de la console) si on n'y est pas arrivé. Rien ne sort
/// de <see cref="Dispose"/> ni du gestionnaire de sortie : un échec de suppression (accès refusé, partage refusé, erreur système) est dit
/// dans une ligne <c>!</c>, et c'est tout.
/// </summary>
internal sealed class ContainerCleanup : IDisposable
{
    private readonly string _id;
    private readonly TextWriter _writer;
    private readonly Action<string> _delete;
    private int _done;

    /// <param name="id">L'identifiant du module essayé.</param>
    /// <param name="writer">Où écrire le message d'échec.</param>
    /// <param name="delete">La suppression ; par défaut celle du profil d'essai (les tests en injectent une qui échoue).</param>
    public ContainerCleanup(string id, TextWriter writer, Action<string>? delete = null)
    {
        _id = id;
        _writer = writer;
        _delete = delete ?? AppContainerProfile.DeleteForCheck;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        Run();
    }

    private void OnExit(object? sender, EventArgs e) => Run();

    private void Run()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
        {
            return;
        }

        try
        {
            _delete(_id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Marshal.ThrowExceptionForHR rend selon l'erreur un COMException, un UnauthorizedAccessException ou un IOException, et la
            // suppression du dossier peut en lever d'autres : toutes se disent de la même façon.
            try
            {
                _writer.WriteLine($"! Nettoyage : le conteneur d'essai « {AppContainerProfile.CheckNamePrefix}{_id} » n'a pas pu être supprimé ({ex.Message}).");
            }
            catch (Exception writeError) when (writeError is IOException or ObjectDisposedException)
            {
                // la console est déjà fermée : rien d'autre à faire
            }
        }
    }
}
