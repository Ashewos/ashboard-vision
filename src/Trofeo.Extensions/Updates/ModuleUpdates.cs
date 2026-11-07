using System.Diagnostics;
using System.Security.Cryptography;
using AshboardVision.Sdk;
using Trofeo.Control;
using Trofeo.Distribution;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Updates;

/// <summary>Ce que la fenêtre de réglage montre d'une mise à jour proposée pour un module : ce que GitHub en dit, rien de plus.</summary>
/// <param name="Version">La version proposée.</param>
/// <param name="Notes">Les notes de la version (nettoyées, bornées).</param>
public sealed record ModuleUpdateOffer(string Version, string Notes);

/// <summary>
/// Les mises à jour des modules : quand les chercher, ce qu'on en retient, et leur installation à la demande de l'utilisateur.
/// <para>
/// <b>Rythme.</b> Une vérification 60 s après qu'un module portant un dépôt (<c>repository</c>) est apparu, puis toutes les 24 h, avec
/// un seul minuteur qui n'existe que tant qu'il y a un tel module : aucun minuteur et aucune requête sans lui. L'utilisateur peut
/// aussi lancer une vérification (<see cref="CheckNowAsync"/>). <b>Une vérification n'interroge que GitHub</b> : elle ne télécharge aucun
/// paquet et ne connaît que la version et les notes.
/// </para>
/// <para>
/// <b>Le paquet se télécharge au clic</b> (décision d'Alex, 03/11/2026). Le clic sur « Mettre à jour » est la <see cref="PrepareAsync">préparation</see> :
/// le service télécharge le paquet, vérifie son empreinte, ouvre le fichier en lecture seule (il ne peut plus changer) et lit son manifeste,
/// qui doit porter l'identifiant et le dépôt du module concerné et la version annoncée. Il rend ce qu'il faut pour afficher les permissions
/// nouvelles à valider. La validation (<see cref="InstallAsync"/>) installe <i>ce fichier-là</i>, sans rien retélécharger, par
/// <see cref="ICommunityModules.InstallAsync"/> et avec les seules permissions que l'utilisateur a cochées. L'abandon
/// (<see cref="CancelUpdate"/>) supprime le fichier.
/// </para>
/// <para>
/// Un seul fichier est préparé pour tout le service : une nouvelle préparation annule celle en cours et supprime le fichier déjà prêt, même
/// d'un autre module. Un fichier prêt expire au bout de <see cref="PreparedLifetime"/> (un minuteur, qui n'existe que tant qu'un fichier
/// est prêt). Il disparaît aussi avec son module, ou quand une version égale ou plus récente est installée, et à l'arrêt. Une
/// vérification et une préparation ne s'attendent pas.
/// </para>
/// </summary>
public sealed class ModuleUpdates : IDisposable
{
    /// <summary>Le délai de la première vérification, comme pour le logiciel.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);

    /// <summary>L'intervalle des vérifications suivantes.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Le temps pendant lequel un fichier préparé attend la validation de l'utilisateur.</summary>
    public static readonly TimeSpan PreparedLifetime = TimeSpan.FromMinutes(15);

    /// <summary>La réponse quand aucune mise à jour n'est proposée pour le module demandé.</summary>
    public const string NoUpdateMessage = "Aucune mise à jour proposée pour ce module.";

    /// <summary>La réponse d'une installation sans fichier préparé (jamais préparé, abandonné, expiré, remplacé, ou déjà installé).</summary>
    public const string PrepareFirstMessage = "Prépare d'abord cette mise à jour.";

    private const string WrongPackageMessage = "Le paquet téléchargé ne correspond pas à la mise à jour annoncée de ce module : il n'est pas installé.";

    /// <summary>
    /// Le temps total laissé au téléchargement d'un paquet (10 minutes) : le silence est déjà borné à 60 s, mais un serveur qui goutte
    /// à goutte ne doit pas retenir la préparation indéfiniment. Réglable pour les tests.
    /// </summary>
    internal TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private readonly ICommunityModules _community;
    private readonly ModuleUpdateChecker _checker;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string _tempRoot;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _checks = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Dictionary<string, Held> _offers = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private Slot? _slot;
    private ITimer? _expiry;
    private int _orphansCleared;
    private bool _disposed;

    /// <summary>Une proposition : ce que GitHub a annoncé (avec les adresses du paquet) et ce que la fenêtre en voit.</summary>
    private sealed record Held(ModuleUpdate Update, ModuleUpdateOffer Offer);

    /// <summary>
    /// La préparation en cours ou prête (une seule pour tout le service) : son dossier temporaire, son fichier gardé ouvert, et de quoi
    /// l'annuler. <see cref="Ready"/> ne change que sous le verrou du moteur, tant que la préparation est la courante.
    /// </summary>
    private sealed class Slot(string id, string folder, ModuleUpdate update, CancellationTokenSource cts) : IDisposable
    {
        private int _disposed;

        public string Id { get; } = id;

        public ModuleUpdate Update { get; } = update;

        public CancellationTokenSource Cts { get; } = cts;

        public string? File { get; set; }

        public FileStream? Lock { get; set; }

        public volatile bool Ready;

        /// <summary>Annule le téléchargement s'il court encore (son propriétaire nettoie alors lui-même).</summary>
        public void Abandon()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // déjà libérée
            }
        }

        /// <summary>Libère le fichier et supprime le dossier. À faire par le propriétaire d'une préparation inachevée, ou par qui retire une préparation prête.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Lock?.Dispose();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // déjà parti
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Mises à jour des modules : {folder} n'a pas pu être supprimé : {ex.Message}");
            }

            Cts.Dispose();
        }
    }

    /// <param name="community">Les modules (leur liste, l'inspection d'un paquet, l'installation) ; ses changements sont suivis.</param>
    /// <param name="checker">Cherche les versions sur GitHub.</param>
    /// <param name="http">Télécharge les paquets : suit les redirections de GitHub, a un délai d'attente.</param>
    /// <param name="time">La source de temps et de minuteurs.</param>
    /// <param name="tempRoot">Le dossier où poser les téléchargements ; un dossier du dossier temporaire de l'utilisateur par défaut.</param>
    public ModuleUpdates(ICommunityModules community, ModuleUpdateChecker checker, HttpClient http, TimeProvider time, string? tempRoot = null)
    {
        _community = community ?? throw new ArgumentNullException(nameof(community));
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "ashboard-vision-mises-a-jour");
        community.Changed += OnChanged;
        Evaluate();
    }

    /// <summary>La mise à jour proposée pour ce module, ou <c>null</c>.</summary>
    public ModuleUpdateOffer? OfferFor(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return _offers.TryGetValue(id, out var held) ? held.Offer : null;
        }
    }

    /// <summary>
    /// Cherche les mises à jour maintenant, auprès de GitHub seulement. Attend son tour si une vérification est en cours (jamais une
    /// préparation). Ne lève jamais pour un échec réseau ou une réponse douteuse : il n'y a simplement rien de nouveau (et ce qui était
    /// proposé le reste si GitHub n'a pas répondu).
    /// </summary>
    public async Task CheckNowAsync(CancellationToken ct)
    {
        await _checks.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = _community.Entries;
            var sources = SourcesOf(entries);
            if (sources.Count == 0)
            {
                Replace(new Dictionary<string, Held>(StringComparer.Ordinal), new HashSet<string>()); // aucun module avec un dépôt : aucune requête
                return;
            }

            var check = await _checker.CheckWithStatusAsync(entries, ct).ConfigureAwait(false);
            var next = new Dictionary<string, Held>(StringComparer.Ordinal);
            foreach (var (id, update) in check.Found)
            {
                if (sources.ContainsKey(id))
                {
                    next[id] = new Held(update, new ModuleUpdateOffer(update.Version, update.Notes));
                }
            }

            Replace(next, check.Unknown);
        }
        finally
        {
            _checks.Release();
        }
    }

    /// <summary>
    /// Prépare la mise à jour proposée pour <paramref name="id"/> (le clic sur « Mettre à jour ») : télécharge le paquet, vérifie son empreinte,
    /// garde le fichier ouvert et lit son manifeste. Sans offre pour ce module : refus (<see cref="NoUpdateMessage"/>). Un échec (réseau,
    /// empreinte, paquet qui n'est pas celui qui est annoncé, délai dépassé) se dit dans la réponse, sans exception et sans rien laisser sur
    /// le disque ; l'offre reste, on peut réessayer. Une annulation (<paramref name="ct"/>, <see cref="CancelUpdate"/>, une préparation
    /// plus récente, l'arrêt) lève <see cref="OperationCanceledException"/> et supprime tout aussi.
    /// </summary>
    public async Task<ModuleUpdatePreparation> PrepareAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        Held? held;
        lock (_gate)
        {
            if (_disposed)
            {
                return Refused(NoUpdateMessage);
            }

            _offers.TryGetValue(id, out held);
        }

        var entry = _community.Entries.FirstOrDefault(e => e.Id == id);
        if (held is null || entry is null || ModuleUpdateChecker.UpdateSource.Of(entry) is not { } source)
        {
            return Refused(NoUpdateMessage);
        }

        var folder = NewTempFolder(out var problem);
        if (folder is null)
        {
            return Refused(problem!);
        }

        var slot = new Slot(id, folder, held.Update, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
        Slot? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                slot.Dispose();
                return Refused(NoUpdateMessage);
            }

            previous = _slot;
            _slot = slot;
            StopExpiryLocked();
        }

        Release(previous); // une seule préparation à la fois : la précédente (même d'un autre module) est annulée et son fichier supprimé
        try
        {
            var token = slot.Cts.Token;
            var file = Path.Combine(folder, $"{id}-{held.Update.Version}.avmod");
            var (failure, hash) = await DownloadAsync(held.Update, file, token).ConfigureAwait(false);
            if (failure is not null)
            {
                return Refused(failure);
            }

            slot.File = file;
            try
            {
                // Ouvert sans partage en écriture ni en suppression, puis vérifié sur ce qu'on tient : ce fichier ne change plus jusqu'à l'installation.
                slot.Lock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(slot.Lock, token).ConfigureAwait(false)), hash, StringComparison.OrdinalIgnoreCase))
                {
                    return Refused(WrongPackageMessage);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refused($"Le téléchargement a échoué ({ex.Message.TrimEnd('.')}).");
            }

            if (Inspect(file, source, held.Update) is not { } manifest)
            {
                Trace.TraceInformation($"Mises à jour des modules : le paquet de « {id} » {held.Update.Version} n'est pas celui qui est annoncé.");
                return Refused(WrongPackageMessage);
            }

            var installed = entry.Manifest?.Permissions ?? entry.Approval?.Granted ?? [];
            var preparation = new ModuleUpdatePreparation(
                true, null, held.Update.Version, held.Update.Notes, [.. manifest.Permissions], [.. manifest.Permissions.Where(p => !installed.Contains(p))]);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(_slot, slot))
                {
                    throw new OperationCanceledException(token); // remplacée entre-temps
                }

                slot.Ready = true;
                _expiry = _time.CreateTimer(_ => Expire(slot), null, PreparedLifetime, Timeout.InfiniteTimeSpan);
            }

            return preparation;
        }
        finally
        {
            if (!slot.Ready)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_slot, slot))
                    {
                        _slot = null;
                    }
                }

                slot.Dispose();
            }
        }
    }

    /// <summary>
    /// Abandonne la préparation de <paramref name="id"/> : annule son téléchargement s'il court, supprime son fichier. Sans effet s'il n'y
    /// a pas de préparation pour ce module.
    /// </summary>
    public void CancelUpdate(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Slot? slot = null;
        lock (_gate)
        {
            if (_slot is { } current && current.Id == id)
            {
                slot = current;
                _slot = null;
                StopExpiryLocked();
            }
        }

        Release(slot);
    }

    /// <summary>
    /// Installe le fichier préparé pour <paramref name="id"/>, avec les seules permissions de <paramref name="granted"/>, par
    /// <see cref="ICommunityModules.InstallAsync"/> et sans aucune requête réseau. Sans fichier prêt pour ce module : refus, rien n'est
    /// téléchargé (<see cref="PrepareFirstMessage"/>, ou <see cref="NoUpdateMessage"/> s'il n'y a même pas d'offre). Le fichier est supprimé
    /// dans tous les cas : après un échec, un nouvel essai repasse par une préparation. L'offre disparaît après un succès.
    /// </summary>
    public async Task<ActionResult> InstallAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(granted);
        Slot? slot = null;
        bool offered;
        lock (_gate)
        {
            offered = _offers.ContainsKey(id);
            if (_slot is { Ready: true } current && current.Id == id)
            {
                slot = current;
                _slot = null; // retirée : ni l'expiration ni un changement de module ne la supprimeront pendant l'installation
                StopExpiryLocked();
            }
        }

        if (slot is null)
        {
            return new ActionResult(false, offered ? PrepareFirstMessage : NoUpdateMessage);
        }

        try
        {
            var entry = _community.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null || ModuleUpdateChecker.UpdateSource.Of(entry) is null)
            {
                return new ActionResult(false, NoUpdateMessage);
            }

            var result = await _community.InstallAsync(slot.File!, granted, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                lock (_gate)
                {
                    if (_offers.TryGetValue(id, out var held) && held.Update.Version == slot.Update.Version)
                    {
                        _offers.Remove(id);
                    }
                }
            }

            return result;
        }
        finally
        {
            slot.Dispose();
        }
    }

    /// <summary>Arrête le minuteur, annule ce qui se prépare et supprime le fichier préparé. Une vérification en cours est annulée.</summary>
    public void Dispose()
    {
        Slot? slot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            StopExpiryLocked();
            slot = _slot;
            _slot = null;
        }

        _community.Changed -= OnChanged;
        _lifetime.Cancel();
        Release(slot);
    }

    // --- Suivi des modules ---------------------------------------------------------------------------------------------------------

    private void OnChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>Les modules qui disent d'où viennent leurs mises à jour, par identifiant.</summary>
    private static Dictionary<string, ModuleUpdateChecker.UpdateSource> SourcesOf(IReadOnlyList<CommunityEntry> entries)
    {
        var sources = new Dictionary<string, ModuleUpdateChecker.UpdateSource>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (ModuleUpdateChecker.UpdateSource.Of(entry) is { } source)
            {
                sources[source.Id] = source;
            }
        }

        return sources;
    }

    /// <summary>
    /// Met le minuteur d'accord avec les modules : un seul tant qu'il y en a un qui porte un dépôt, aucun sinon ; et retire les
    /// propositions et le fichier préparé qui n'ont plus de sens (module parti, version égale ou plus récente installée entre-temps).
    /// </summary>
    private void Evaluate()
    {
        var sources = SourcesOf(_community.Entries);
        Slot? stale = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (sources.Count > 0 && _timer is null)
            {
                _timer = _time.CreateTimer(_ => _ = RunScheduledAsync(), null, FirstCheckDelay, CheckInterval);
            }
            else if (sources.Count == 0 && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
            }

            foreach (var (id, held) in _offers.ToList())
            {
                if (!Outdated(held.Update.Version, id, sources))
                {
                    continue;
                }

                _offers.Remove(id);
            }

            if (_slot is { } slot && Outdated(slot.Update.Version, slot.Id, sources))
            {
                stale = slot;
                _slot = null;
                StopExpiryLocked();
            }
        }

        Release(stale);
    }

    /// <summary>Vrai si la version proposée n'a plus de sens : le module n'est plus là, ou une version égale ou plus récente est installée.</summary>
    private static bool Outdated(string offered, string id, Dictionary<string, ModuleUpdateChecker.UpdateSource> sources) =>
        !sources.TryGetValue(id, out var source) || !AppVersion.TryParse(offered, out var version) || version.CompareTo(source.Installed) <= 0;

    private async Task RunScheduledAsync()
    {
        try
        {
            await CheckNowAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // le service s'arrête
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Mises à jour des modules : la vérification a échoué : {ex}");
        }
    }

    /// <summary>Remplace les propositions par <paramref name="next"/>, en gardant celles des modules dont la réponse est restée inconnue.</summary>
    private void Replace(Dictionary<string, Held> next, IReadOnlySet<string> unknown)
    {
        lock (_gate)
        {
            foreach (var id in unknown)
            {
                if (_offers.TryGetValue(id, out var kept) && !next.ContainsKey(id))
                {
                    next[id] = kept;
                }
            }

            _offers = next;
        }

        Evaluate(); // une proposition qui ne tient plus (installée entre-temps) est retirée tout de suite
    }

    // --- La préparation ------------------------------------------------------------------------------------------------------------

    private static ModuleUpdatePreparation Refused(string message) => new(false, message, null, null, [], []);

    /// <summary>La préparation retirée de la place : son téléchargement est annulé, et son fichier supprimé s'il était prêt (sinon c'est son propriétaire qui nettoie).</summary>
    private static void Release(Slot? slot)
    {
        if (slot is null)
        {
            return;
        }

        slot.Abandon();
        if (slot.Ready)
        {
            slot.Dispose();
        }
    }

    /// <summary>Le fichier prêt a trop attendu : supprimé, sauf s'il a été retiré entre-temps.</summary>
    private void Expire(Slot slot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_slot, slot))
            {
                return;
            }

            _slot = null;
            StopExpiryLocked();
        }

        slot.Dispose();
    }

    private void StopExpiryLocked()
    {
        _expiry?.Dispose();
        _expiry = null;
    }

    /// <summary>Télécharge et vérifie (empreinte, taille) ; rend la raison de l'échec en français, ou l'empreinte vérifiée si le fichier est là.</summary>
    private async Task<(string? Failure, string? Hash)> DownloadAsync(ModuleUpdate update, string file, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);
        try
        {
            var hash = await VerifiedDownload.DownloadAsync(
                _http, update.Package, update.Sha256, file, null, deadline.Token, ModuleUpdateChecker.MaxPackageBytes).ConfigureAwait(false);
            return (null, hash);
        }
        catch (UpdateException ex)
        {
            return (ex.Message, null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ("Le téléchargement a pris trop de temps.", null);
        }
    }

    /// <summary>Le manifeste du paquet s'il est bien celui de la mise à jour attendue et s'il peut s'installer ; <c>null</c> sinon.</summary>
    private ModuleManifest? Inspect(string file, ModuleUpdateChecker.UpdateSource source, ModuleUpdate update)
    {
        PackageInspection inspection;
        try
        {
            inspection = _community.Inspect(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (inspection.Manifest is not { } manifest
            || inspection.Errors.Count > 0
            || inspection.Conflict is not null
            || manifest.Id != source.Id
            || !string.Equals(manifest.Repository, source.Repository, StringComparison.OrdinalIgnoreCase)
            || !AppVersion.TryParse(manifest.Version, out var version)
            || !AppVersion.TryParse(update.Version, out var announced)
            || version.CompareTo(announced) != 0)
        {
            return null;
        }

        return manifest;
    }

    // --- Dossier temporaire --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Un dossier neuf sous <c>tempRoot</c> ; <c>null</c> (et la raison) s'il ne se crée pas. À la première demande du processus, ce qu'un
    /// service tué en plein téléchargement a pu laisser dans <c>tempRoot</c> est supprimé, sans bruit : rien n'est fait au démarrage.
    /// </summary>
    private string? NewTempFolder(out string? problem)
    {
        if (Interlocked.Exchange(ref _orphansCleared, 1) == 0)
        {
            RemoveOrphans();
        }

        try
        {
            var folder = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder);
            problem = null;
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Le téléchargement a échoué ({ex.Message.TrimEnd('.')}).";
            return null;
        }
    }

    private void RemoveOrphans()
    {
        try
        {
            if (!Directory.Exists(_tempRoot))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempRoot))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // un reste qu'on ne peut pas supprimer ne change rien à ce qui suit
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // le dossier n'est pas lisible : la création du dossier suivant le dira
        }
    }
}
