using SkiaSharp;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk;

/// <summary>
/// La classe de base d'un module de la communauté. Un module est une DLL qui contient exactement une classe publique
/// dérivée de celle-ci, avec un constructeur sans paramètre. Le logiciel crée <b>une instance par case</b> posée sur le
/// damier (deux cases du même module = deux instances, chacune avec ses réglages), appelle <see cref="StartAsync"/>, puis
/// <see cref="Render"/> chaque fois qu'une image est nécessaire.
/// <para>
/// <b>Threads.</b> <see cref="Render"/> n'est jamais appelé en parallèle avec lui-même pour une même instance, mais l'état
/// qu'il lit est écrit par d'autres threads (minuterie, réseau) : il doit pouvoir être lu en concurrence.
/// <see cref="Invalidate"/> et <see cref="SetStatus"/> sont utilisables depuis n'importe quel thread.
/// </para>
/// <para>
/// <b>Erreurs.</b> Une exception qui sort d'une méthode du module met la case en erreur (« Erreur du module ») sans arrêter
/// le processus du module ni les autres cases. Après une exception de <see cref="Render"/>, la case se rétablit au prochain dessin
/// réussi (au plus un essai par seconde) ; après une exception du constructeur ou de <see cref="StartAsync"/>, l'instance reste en
/// erreur jusqu'à ce que la case soit refermée. Une <see cref="OutOfMemoryException"/> n'est jamais rattrapée : elle arrête le
/// processus du module. Une exception qui sort d'un thread créé par le module l'arrête aussi : le logiciel le relance, mais il
/// vaut mieux tout rattraper.
/// </para>
/// </summary>
public abstract class CommunityModule : IAsyncDisposable
{
    private ModuleServices? _services;
    private volatile bool _signalsOpen;

    /// <summary>
    /// Les services du logiciel pour cette instance (réglages, secrets, journal, capteurs…). Disponible dès
    /// <see cref="StartAsync"/> ; l'utiliser dans le constructeur est une erreur.
    /// </summary>
    protected ModuleServices Services =>
        _services ?? throw new InvalidOperationException("Services n'est disponible qu'à partir de StartAsync, pas dans le constructeur.");

    /// <summary>
    /// Vrai tant que le module veut être redessiné en continu (animation, caméra) : le logiciel redemande alors une image
    /// dès qu'il peut, dans la limite du plafond d'images par seconde de l'écran.
    /// </summary>
    public virtual bool IsAnimating => false;

    /// <summary>
    /// Démarre l'instance (lit les réglages, lance une minuterie ou une connexion). Reste rapide : la première image n'est
    /// dessinée qu'après, et elle l'est sans que le module ait à appeler <see cref="Invalidate"/>.
    /// </summary>
    public virtual Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Dessine dans <c>ctx.Bounds</c>. Le canevas est transparent : le logiciel pose l'image par-dessus le panneau de la case,
    /// déjà peint. Le module dessine toujours à l'endroit.
    /// </summary>
    public abstract void Render(SKCanvas canvas, ModuleRenderContext ctx);

    /// <summary>
    /// Propositions d'un champ de recherche des réglages pour la requête donnée. Appelée sur une instance à part, créée pour
    /// l'occasion : <see cref="StartAsync"/> n'a pas été appelé, <see cref="Services"/> est disponible (réglages vides,
    /// secrets, journal, données du service), et <see cref="Invalidate"/> et <see cref="SetStatus"/> n'ont aucun effet.
    /// </summary>
    public virtual Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LookupResult>>([]);

    /// <summary>
    /// Lance l'action d'un bouton des réglages. Appelée comme <see cref="LookupAsync"/> sur une instance à part, dont les
    /// <see cref="ModuleServices.Settings"/> sont les réglages courants de la fenêtre (pas forcément enregistrés).
    /// </summary>
    public virtual Task<ActionResult> RunActionAsync(string actionKey, CancellationToken ct) =>
        Task.FromResult(new ActionResult(false, "Action inconnue."));

    /// <summary>
    /// Un raccourci que le module a déclaré dans son manifeste (<c>shortcuts</c>) vient d'être déclenché par l'utilisateur ; <paramref name="shortcutId"/>
    /// est son <c>id</c>. Le module ne voit jamais le clavier : le logiciel l'appelle seulement quand le raccourci est utilisé et que la case est dans la vue
    /// affichée. Appelée sur l'instance de la case (pas sur une instance à part), après <see cref="StartAsync"/>, hors du fil de dessin et jamais deux fois en même temps
    /// pour une instance, dans l'ordre des appuis. Si le module est lent, les appuis en trop (une dizaine en attente) sont ignorés plutôt que mis en file sans fin ;
    /// reste donc rapide, ou lance le travail long à part. Une exception est écrite dans le journal du module sans mettre la case en erreur. Un
    /// <paramref name="shortcutId"/> que le module ne connaît pas doit être ignoré. Ajoutée au SDK 1.1.
    /// </summary>
    /// <param name="shortcutId">L'identifiant du raccourci, tel que le manifeste le déclare.</param>
    /// <param name="ct">Annulé quand la case est refermée.</param>
    public virtual Task OnShortcutAsync(string shortcutId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Libère l'instance (minuteries, connexions). Appelée à la fermeture de la case, après le dernier dessin.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Demande une nouvelle image. Utilisable depuis n'importe quel thread ; sans effet après <see cref="DisposeAsync"/>.</summary>
    protected void Invalidate()
    {
        var services = _services ?? throw NotStarted();
        if (_signalsOpen)
        {
            services.Backend.Invalidate();
        }
    }

    /// <summary>
    /// Change l'état de la case : <see cref="ModuleHealth.Online"/>, <see cref="ModuleHealth.Offline"/> ou
    /// <see cref="ModuleHealth.NotConfigured"/>, avec un message pour l'utilisateur. Utilisable depuis n'importe quel thread ;
    /// sans effet après <see cref="DisposeAsync"/>.
    /// </summary>
    protected void SetStatus(ModuleHealth health, string? message = null)
    {
        var services = _services ?? throw NotStarted();
        if (_signalsOpen)
        {
            services.Backend.SetStatus(health, message);
        }
    }

    /// <summary>Relie l'instance à ses services, juste après sa construction.</summary>
    internal void Bind(ModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _signalsOpen = true;
    }

    /// <summary>Coupe <see cref="Invalidate"/> et <see cref="SetStatus"/> (fermeture de l'instance) : un appel tardif d'une minuterie ne fait rien.</summary>
    internal void CloseSignals() => _signalsOpen = false;

    private static InvalidOperationException NotStarted() =>
        new("Cette méthode n'est utilisable qu'à partir de StartAsync, pas dans le constructeur.");
}
