using System.Diagnostics;
using Trofeo.Control;
using Trofeo.Core.Shortcuts;

namespace Trofeo.Display;

/// <summary>
/// Pose les raccourcis sur le fil de l'interface (la fenêtre des raccourcis y vit) pour le compte du rechargement, qui
/// tourne sur un autre fil. Attend au plus <see cref="Timeout"/>. Tous les raccourcis sont posés, que la vue affichée ait ou
/// non un module qui accepte les commandes : les raccourcis de vue n'en ont pas besoin, et une commande d'onglet sans module
/// visé dans la vue affichée ne fait simplement rien.
/// </summary>
internal sealed class UiHotkeyHost(SynchronizationContext ui, HotkeyRouter router) : IHotkeyHost
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string Unavailable = "Les raccourcis ne sont pas disponibles pour l'instant.";

    public IReadOnlyList<string> RefusedKeys => router.RefusedKeys;

    /// <summary>
    /// Essaie ces touches sur le fil de l'interface (là où vit la fenêtre des raccourcis : <c>RegisterHotKey</c> est attaché à son fil).
    /// Lève <see cref="InvalidOperationException"/> si le service s'arrête, si la boucle d'interface est terminée ou si elle ne répond pas.
    /// </summary>
    public async Task<ShortcutKeyState> TestAsync(string keys)
    {
        var done = new TaskCompletionSource<ShortcutKeyState>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ui.Post(_ =>
            {
                try
                {
                    done.TrySetResult(router.TestKeys(keys));
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }, null);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(Unavailable); // la boucle d'interface est terminée
        }

        try
        {
            return await done.Task.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException)
        {
            throw new InvalidOperationException(Unavailable);
        }
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<ShortcutBinding> bindings)
    {
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ui.Post(_ =>
            {
                try
                {
                    done.TrySetResult(router.Replace(bindings));
                }
                catch (ObjectDisposedException)
                {
                    done.TrySetResult([]); // le service s'arrête : plus de raccourcis à poser
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Raccourcis non mis à jour : {ex.Message}");
                    done.TrySetResult([$"Les raccourcis n'ont pas pu être mis à jour : {ex.Message}"]);
                }
            }, null);
        }
        catch (InvalidOperationException)
        {
            return []; // la boucle d'interface est terminée
        }

        try
        {
            return await done.Task.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ["Les raccourcis n'ont pas pu être mis à jour : l'interface ne répond pas."];
        }
    }
}
