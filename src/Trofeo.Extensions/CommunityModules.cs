using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AshboardVision.Sdk;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions;

/// <summary>
/// Le point d'entrée du service pour les modules de la communauté : il examine le dossier des modules (<see cref="CommunityCatalog"/>),
/// tient un <see cref="ModuleHostSupervisor"/> par module prêt à tourner, publie les fabriques dans le registre du service, et
/// installe, valide, règle et désinstalle les modules sur demande de la fenêtre de réglage.
/// <para>
/// <b>Rien ne tourne sans validation.</b> Un module trouvé dans le dossier mais absent de la liste scellée, ou dont les fichiers ont
/// changé, a une fabrique qui ne fait que dire pourquoi (<see cref="PlaceholderModuleFactory"/>) : aucun superviseur, donc aucun
/// processus. Un superviseur n'existe que pour un module <see cref="CommunityState.Ready"/> (ou mis de côté), et il ne lance un hôte
/// qu'à la première case posée. Un dossier de développement se lance avec les seules permissions que le créateur lui a accordées,
/// jamais avec celles de son manifeste.
/// </para>
/// <para>
/// <b>Fils.</b> Toute lecture et toute écriture de la liste scellée passent par un seul verrou (<c>_gate</c>) : le magasin n'a pas de
/// verrou et la fenêtre de réglage comme les mises de côté du superviseur écrivent. Ce verrou ne couvre jamais d'attente ; les
/// opérations asynchrones se font une à une (<c>_operations</c>), et les arrêts d'hôtes se font après, hors du verrou. Une liste
/// illisible n'est jamais réécrite (un verrou passager ne doit pas effacer les validations) : tout changement est alors refusé.
/// </para>
/// </summary>
public sealed class CommunityModules : ICommunityModules, IAsyncDisposable
{
    /// <summary>Le conflit d'identifiant : le même identifiant, mais pas le même dépôt.</summary>
    public const string ConflictMessage = "Un autre module porte déjà cet identifiant";

    private const string NotInstalledMessage = "Aucun module installé ne porte cet identifiant.";
    private const string NotApprovedMessage = "Ce module n'est pas encore validé.";

    private readonly ApprovedModulesStore _store;
    private readonly Action<IReadOnlyList<IModuleFactory>> _publish;
    private readonly Func<string, ISecretStore> _secretsFor;
    private readonly Action<string> _clearSecrets;
    private readonly Action<string> _deleteProfile;
    private readonly IServiceDataBroker _data;
    private readonly string _hostExecutable;
    private readonly TimeProvider _time;
    private readonly IHostProcessFactory? _launcher;
    private readonly ModuleInstaller _installer;
    private readonly CommunityCatalog _catalog;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forced = new(StringComparer.Ordinal);
    private ApprovedLoad _snapshot = new(ApprovedModules.Empty, Tampered: false);
    private volatile IReadOnlyList<CommunityEntry> _entries = [];
    private volatile CommunityStoreInfo _storeInfo = new(ModulesFolder.Default, false, [], false, false);
    private int _maxFps = 12;
    private JsonElement _appearance = HostPayloads.AppearanceToJson(AppearanceSettings.Default);
    private bool _disposed;

    /// <param name="store">La liste scellée des modules validés.</param>
    /// <param name="publishFactories">Reçoit les fabriques de tous les modules de la communauté à chaque changement (le registre du service).</param>
    /// <param name="secretsFor">Le coffre à secrets d'un module (propre à son identifiant).</param>
    /// <param name="clearSecrets">Efface tous les secrets d'un module (à la désinstallation).</param>
    /// <param name="data">Ce que le service lit pour les modules qui en ont la permission.</param>
    /// <param name="hostExecutable">Le chemin complet de <c>Trofeo.ModuleHost.exe</c>.</param>
    /// <param name="time">La source de temps des superviseurs.</param>
    /// <param name="launcher">Lance les hôtes ; par défaut, dans l'AppContainer et le job du module (les tests en fournissent d'autres).</param>
    /// <param name="deleteProfile">Supprime l'AppContainer d'un module et ses données ; par défaut <see cref="Sandbox.AppContainerProfile.Delete"/>.</param>
    /// <param name="grantRead">Accorde à l'AppContainer d'un module la lecture de son dossier ; par défaut l'AppContainer du module.</param>
    public CommunityModules(
        ApprovedModulesStore store, Action<IReadOnlyList<IModuleFactory>> publishFactories, Func<string, ISecretStore> secretsFor,
        Action<string> clearSecrets, IServiceDataBroker data, string hostExecutable, TimeProvider time,
        IHostProcessFactory? launcher = null, Action<string>? deleteProfile = null, Action<string, string>? grantRead = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publish = publishFactories ?? throw new ArgumentNullException(nameof(publishFactories));
        _secretsFor = secretsFor ?? throw new ArgumentNullException(nameof(secretsFor));
        _clearSecrets = clearSecrets ?? throw new ArgumentNullException(nameof(clearSecrets));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        _hostExecutable = hostExecutable;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _launcher = launcher;
        _deleteProfile = deleteProfile ?? Sandbox.AppContainerProfile.Delete;
        _installer = new ModuleInstaller(grantRead);
        _catalog = new CommunityCatalog(() => _snapshot);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<CommunityEntry> Entries => _entries;

    /// <inheritdoc />
    public CommunityStoreInfo StoreInfo => _storeInfo;

    // --- Examen, publication --------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Refresh()
    {
        Pending pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            pending = RefreshLocked();
        }

        Complete(pending);
    }

    /// <summary>
    /// Relit la liste scellée, examine les dossiers, met les superviseurs d'accord avec ce qu'on y trouve et publie les fabriques.
    /// À appeler sous <c>_gate</c>. Les arrêts d'hôtes à faire sont rendus, pour qu'on les attende hors du verrou.
    /// </summary>
    private Pending RefreshLocked()
    {
        _snapshot = _store.Load();
        return ApplyScanLocked(_catalog.Scan());
    }

    private Pending ApplyScanLocked(IReadOnlyList<CommunityEntry> entries)
    {
        var loaded = _snapshot;
        var approvals = loaded.Tampered ? ApprovedModules.Empty : loaded.Modules;
        _entries = entries;
        _storeInfo = new CommunityStoreInfo(
            _catalog.ResolveRoot(loaded),
            approvals.DeveloperMode && !loaded.Unreadable,
            loaded.Unreadable ? [] : [.. approvals.DevFolders.Select(folder => folder.Path)],
            loaded.Tampered,
            loaded.Unreadable);

        var follow = new List<Func<Task>>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var factories = new List<IModuleFactory>();
        foreach (var entry in entries)
        {
            if (entry.Manifest is null)
            {
                continue; // sans manifeste lisible, il n'y a pas de type à proposer
            }

            if (entry.State is CommunityState.Ready or CommunityState.SetAside && !kept.Contains(entry.Id))
            {
                Reconcile(entry, GrantedFor(entry, approvals), kept, follow);
            }

            factories.Add(entry.State == CommunityState.Ready && kept.Contains(entry.Id) && _running.TryGetValue(entry.Id, out var run)
                ? new RemoteModuleFactory(entry, run.Supervisor)
                : new PlaceholderModuleFactory(entry));
        }

        foreach (var (id, run) in _running.Where(pair => !kept.Contains(pair.Key)).ToList())
        {
            _running.Remove(id);
            follow.Add(() => run.Supervisor.DisposeAsync().AsTask());
        }

        _forced.Clear();
        _publish(factories); // sous le verrou : deux examens ne publient jamais dans le désordre
        return new Pending(follow);
    }

    /// <summary>Met le superviseur d'un module d'accord avec son état : en crée un (module prêt), le garde, ou le remplace s'il a changé.</summary>
    private void Reconcile(CommunityEntry entry, IReadOnlyList<ModulePermission> granted, HashSet<string> kept, List<Func<Task>> follow)
    {
        if (_running.TryGetValue(entry.Id, out var run) && run.Dev != entry.FromDevFolder)
        {
            _running.Remove(entry.Id);
            follow.Add(() => run.Supervisor.DisposeAsync().AsTask());
            run = null;
        }

        if (run is not null)
        {
            kept.Add(entry.Id);
            if (entry.State == CommunityState.Ready)
            {
                // Les capacités d'un AppContainer sont fixées au lancement : un changement de permissions, de version ou de fichiers
                // (ou un rechargement demandé) fait repartir l'hôte. Un module mis de côté garde son superviseur, qui connaît sa panne.
                var signature = Signature(entry, granted);
                if (_forced.Contains(entry.Id) || signature != run.Signature)
                {
                    // Notée tout de suite (un examen qui arrive pendant la relance ne la programme pas une seconde fois), et reprise si
                    // la relance échoue : le prochain examen réessaiera.
                    var previous = run.Signature;
                    run.Signature = signature;
                    var supervisor = run.Supervisor;
                    follow.Add(async () =>
                    {
                        try
                        {
                            await supervisor.ApplyEntryAsync(entry, granted).ConfigureAwait(false);
                        }
                        catch
                        {
                            run.Signature = previous;
                            throw;
                        }
                    });
                }
            }

            return;
        }

        if (entry.State != CommunityState.Ready)
        {
            return; // mis de côté (ou tout juste redémarré) : une case posée dit pourquoi, sans lancer quoi que ce soit
        }

        var created = new ModuleHostSupervisor(
            entry, granted, _hostExecutable, _secretsFor(entry.Id), _data, _time, OnSetAside, _launcher);
        created.SetMaxFps(_maxFps);
        created.SetAppearance(_appearance);
        _running[entry.Id] = new Running(created, entry.FromDevFolder, Signature(entry, granted));
        kept.Add(entry.Id);
    }

    /// <summary>
    /// Les permissions avec lesquelles on lance le module : celles que l'utilisateur a accordées à son installation, ou à son dossier de
    /// développement. Jamais celles que le manifeste déclare.
    /// </summary>
    private static IReadOnlyList<ModulePermission> GrantedFor(CommunityEntry entry, ApprovedModules approvals) =>
        entry.FromDevFolder
            ? approvals.DevFolders.FirstOrDefault(folder => SamePath(folder.Path, entry.Directory))?.Granted ?? []
            : entry.Approval?.Granted ?? [];

    private static string Signature(CommunityEntry entry, IReadOnlyList<ModulePermission> granted) =>
        string.Join('|', entry.Directory, entry.Approval?.Version, entry.Approval?.ContentHash, entry.Manifest?.Entry,
            string.Join(',', granted.Select(p => (int)p).Order()));

    private sealed class Running(ModuleHostSupervisor supervisor, bool dev, string signature)
    {
        public ModuleHostSupervisor Supervisor { get; } = supervisor;

        public bool Dev { get; } = dev;

        public string Signature { get; set; } = signature;
    }

    /// <summary>Ce qu'il reste à faire hors du verrou après un examen : arrêter ou relancer des hôtes.</summary>
    private sealed record Pending(List<Func<Task>> Follow);

    /// <summary>Fait ce qui reste (hôtes à arrêter ou à relancer) puis lève <see cref="Changed"/>.</summary>
    private async Task CompleteAsync(Pending pending)
    {
        if (pending.Follow.Count > 0)
        {
            await Task.WhenAll(pending.Follow.Select(RunQuietlyAsync)).ConfigureAwait(false);
        }

        RaiseChanged();
    }

    /// <summary>Comme <see cref="CompleteAsync"/> pour une opération synchrone : sans hôte à arrêter, tout est fait au retour.</summary>
    private void Complete(Pending pending)
    {
        if (pending.Follow.Count == 0)
        {
            RaiseChanged();
            return;
        }

        _ = CompleteAsync(pending);
    }

    private static async Task RunQuietlyAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Un module de la communauté n'a pas pu être arrêté ou relancé : {ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"Un abonné à Changed a échoué : {ex}");
        }
    }

    /// <summary>Un superviseur met son module de côté : la raison est rangée dans la liste scellée (elle survit au redémarrage).</summary>
    private void OnSetAside(string id, string reason)
    {
        Pending pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var loaded = _store.Load();
            if (loaded.Tampered || loaded.Unreadable)
            {
                Trace.TraceWarning($"La mise de côté de « {id} » n'est pas enregistrée : la liste des modules validés est illisible ou altérée.");
                return;
            }

            var module = loaded.Modules.Modules.FirstOrDefault(m => m.Id == id);
            if (module is null)
            {
                return; // un dossier de développement n'a pas de validation : le superviseur retient la mise de côté lui-même
            }

            try
            {
                _store.Save(WithModule(loaded.Modules, module with { SetAsideReason = reason }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Trace.TraceWarning($"La mise de côté de « {id} » n'a pas pu être enregistrée : {ex.Message}");
                return;
            }

            pending = RefreshLocked();
        }

        Complete(pending);
    }

    // --- Transactions sur la liste scellée --------------------------------------------------------------------------------------------

    /// <summary>Ce qu'un changement veut écrire (<c>null</c> : rien) et ce qu'il répond.</summary>
    /// <param name="Next">La liste à écrire, ou <c>null</c>.</param>
    /// <param name="Result">La réponse.</param>
    /// <param name="Force">Un module dont l'hôte doit repartir au prochain examen ; pris en compte seulement si l'écriture réussit.</param>
    private readonly record struct Outcome(ApprovedModules? Next, ActionResult Result, string? Force = null);

    private static Outcome Refuse(string message) => new(null, new ActionResult(false, message));

    /// <summary>
    /// Relit la liste, applique <paramref name="change"/>, l'écrit puis examine de nouveau, le tout sous le verrou. Une liste illisible
    /// n'est jamais écrite. Une liste altérée repart d'une liste vide (qui garde le dossier des modules connu).
    /// </summary>
    private (ActionResult Result, Pending? Pending) Transact(Func<ApprovedModules, Outcome> change)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (new ActionResult(false, "Le service s'arrête."), null);
            }

            _snapshot = _store.Load();
            if (_snapshot.Unreadable)
            {
                return (new ActionResult(false, CommunityCatalog.ApprovalsUnreadableMessage), null);
            }

            var current = _snapshot.Tampered ? FreshStore(_snapshot) : _snapshot.Modules;
            var outcome = change(current);
            if (outcome.Next is null)
            {
                return (outcome.Result, null);
            }

            try
            {
                _store.Save(outcome.Next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return (new ActionResult(false, $"La liste des modules validés n'a pas pu être enregistrée : {ex.Message}"), null);
            }

            if (outcome.Force is { } forced)
            {
                _forced.Add(forced);
            }

            return (outcome.Result, RefreshLocked());
        }
    }

    /// <summary>Une liste altérée n'est plus de confiance : on repart de rien, sauf le dossier des modules que ce processus connaît.</summary>
    private ApprovedModules FreshStore(ApprovedLoad tampered)
    {
        var root = _catalog.ResolveRoot(tampered);
        return ApprovedModules.Empty with { ModulesRoot = SamePath(root, ModulesFolder.Default) ? null : root };
    }

    private static ApprovedModules WithModule(ApprovedModules store, ApprovedModule module)
    {
        var modules = store.Modules.ToList();
        var index = modules.FindIndex(m => m.Id == module.Id);
        if (index >= 0)
        {
            modules[index] = module;
        }
        else
        {
            modules.Add(module);
        }

        return store with { Modules = modules };
    }

    /// <summary>
    /// L'entrée installée d'un identifiant. Plusieurs entrées peuvent le porter (le reste d'une extraction interrompue donne une entrée
    /// invalide du même nom) : celle qui a un manifeste lisible passe d'abord.
    /// </summary>
    private static CommunityEntry? FindInstalled(IReadOnlyList<CommunityEntry> entries, string id) =>
        entries.Where(e => !e.FromDevFolder && e.Id == id).OrderBy(e => e.Manifest is null).FirstOrDefault();

    private static ApprovedModules WithoutModule(ApprovedModules store, string id) =>
        store with { Modules = [.. store.Modules.Where(m => m.Id != id)] };

    private IDisposable Enter()
    {
        _operations.Wait();
        return new Releaser(_operations);
    }

    private readonly struct Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deux dépôts sont « le même » seulement si les deux sont nommés et identiques (GitHub ne distingue pas les majuscules).</summary>
    private static bool SameRepository(string? installed, string? incoming) =>
        installed is not null && incoming is not null && string.Equals(installed, incoming, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refuse une permission que le manifeste ne déclare pas : on n'accorde que ce qui a été demandé.</summary>
    private static ActionResult? UndeclaredPermission(ModuleManifest manifest, IReadOnlyList<ModulePermission> granted)
    {
        foreach (var permission in granted)
        {
            if (!manifest.Permissions.Contains(permission))
            {
                return new ActionResult(false, $"Le module ne déclare pas la permission « {PermissionTexts.Label(permission)} » : elle n'est pas accordée.");
            }
        }

        return null;
    }

    // --- Installation ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public PackageInspection Inspect(string avmodPath) => InspectCore(avmodPath, out _, out _);

    private PackageInspection InspectCore(string avmodPath, out ApprovedLoad loaded, out string root)
    {
        loaded = new ApprovedLoad(ApprovedModules.Empty, Tampered: false);
        root = ModulesFolder.Default;
        AvmodInspection inspection;
        try
        {
            inspection = AvmodPackage.Inspect(avmodPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PackageInspection(null, [$"Le fichier n'a pas pu être ouvert : {ex.Message}"], null, null);
        }

        if (inspection.Manifest is not { } manifest)
        {
            return new PackageInspection(null, inspection.Errors, null, null);
        }

        lock (_gate)
        {
            loaded = _store.Load();
            root = _catalog.ResolveRoot(loaded);
        }

        var errors = new List<string>();
        var compatibility = Compatibility.Check(manifest.Sdk);
        if (compatibility != CompatibilityKind.Compatible)
        {
            errors.Add(Compatibility.Message(compatibility));
        }

        if (loaded.Unreadable)
        {
            errors.Add(CommunityCatalog.ApprovalsUnreadableMessage);
        }

        var directory = ModuleInstaller.VersionDirectory(root, manifest.Id, manifest.Version);
        if (directory is null)
        {
            errors.Add("L'identifiant ou la version de ce module ne peut pas servir de nom de dossier.");
        }
        else if (Directory.Exists(directory) || File.Exists(directory))
        {
            errors.Add("Cette version du module est déjà dans le dossier des modules : désinstalle-la d'abord, ou valide-la depuis la liste.");
        }

        var installed = loaded.Modules.Modules.FirstOrDefault(m => m.Id == manifest.Id);
        var conflict = installed is not null && !SameRepository(installed.Repository, manifest.Repository) ? ConflictMessage : null;
        return new PackageInspection(manifest, errors, conflict, installed);
    }

    /// <inheritdoc />
    public async Task<ActionResult> InstallAsync(string avmodPath, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(granted);
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var inspection = InspectCore(avmodPath, out var loaded, out var root);
            if (inspection.Manifest is not { } manifest)
            {
                return new ActionResult(false, string.Join(" ", inspection.Errors));
            }

            if (inspection.Conflict is { } conflict)
            {
                return new ActionResult(false, conflict);
            }

            if (inspection.Errors.Count > 0)
            {
                return new ActionResult(false, string.Join(" ", inspection.Errors));
            }

            if (UndeclaredPermission(manifest, granted) is { } refused)
            {
                return refused;
            }

            var directory = ModuleInstaller.VersionDirectory(root, manifest.Id, manifest.Version)!;
            var extracted = await Task.Run(() => _installer.Extract(avmodPath, directory, manifest), ct).ConfigureAwait(false);
            if (extracted.Manifest is not { } written || extracted.ContentHash is not { } hash)
            {
                return new ActionResult(false, extracted.Error ?? "Le module n'a pas pu être installé.");
            }

            ApprovedModule? previous = null;
            var (result, pending) = ct.IsCancellationRequested
                ? (new ActionResult(false, "Installation annulée."), null)
                : Transact(current =>
                {
                    previous = current.Modules.FirstOrDefault(m => m.Id == written.Id);
                    if (previous is not null && !SameRepository(previous.Repository, written.Repository))
                    {
                        return Refuse(ConflictMessage);
                    }

                    var approval = new ApprovedModule(written.Id, written.Version, directory, hash, [.. granted.Distinct()], null, written.Repository);
                    var verb = previous is null ? "installé" : "mis à jour";
                    return new Outcome(WithModule(current, approval), new ActionResult(true, $"« {written.Name} » {written.Version} est {verb}."));
                });

            if (!result.Ok || pending is null)
            {
                ModuleInstaller.TryDelete(directory); // rien n'a été validé : on ne laisse pas des fichiers qu'on n'a pas annoncés
                return result;
            }

            await CompleteAsync(pending).ConfigureAwait(false);

            // L'ancienne version n'est retirée qu'une fois la nouvelle en place, et que son hôte (qui verrouillait ses fichiers) est arrêté.
            if (previous is not null && previous.Version != written.Version
                && ModuleInstaller.VersionDirectory(root, previous.Id, previous.Version) is { } old)
            {
                if (ModuleInstaller.TryDelete(old) is { } problem)
                {
                    Trace.TraceWarning($"L'ancienne version de « {previous.Id} » n'a pas pu être supprimée ({old}) : {problem}");
                }
            }

            return result;
        }
        finally
        {
            _operations.Release();
        }
    }

    // --- Validation, permissions ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public ActionResult Approve(string id, IReadOnlyList<ModulePermission> granted)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(granted);
        using var _ = Enter();
        var (result, pending) = Transact(current =>
        {
            var entry = FindInstalled(_catalog.Scan(), id);
            if (entry is null)
            {
                return Refuse(NotInstalledMessage);
            }

            if (entry.State is not (CommunityState.NeedsApproval or CommunityState.Changed) || entry.Manifest is not { } manifest)
            {
                return Refuse(entry.State == CommunityState.Ready ? "Ce module est déjà validé." : entry.Message ?? NotApprovedMessage);
            }

            if (UndeclaredPermission(manifest, granted) is { } refused)
            {
                return new Outcome(null, refused);
            }

            var existing = current.Modules.FirstOrDefault(m => m.Id == id);
            if (existing is not null && existing.Version != manifest.Version && !SameRepository(existing.Repository, manifest.Repository))
            {
                return Refuse(ConflictMessage);
            }

            string hash;
            try
            {
                hash = ContentHash.Compute(entry.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Refuse($"Les fichiers du module ne se lisent pas pour le moment : {ex.Message}");
            }

            var approval = new ApprovedModule(id, manifest.Version, entry.Directory, hash, [.. granted.Distinct()], null, manifest.Repository);
            return new Outcome(WithModule(current, approval), new ActionResult(true, $"« {manifest.Name} » est validé."));
        });

        if (pending is not null)
        {
            Complete(pending);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ActionResult> SetPermissionsAsync(string id, IReadOnlyList<ModulePermission> granted)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(granted);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            var (result, pending) = Transact(current =>
            {
                var entry = _catalog.Scan().FirstOrDefault(e => e.Id == id && e.Manifest is not null && (e.FromDevFolder || e.Approval is not null));
                if (entry?.Manifest is not { } manifest)
                {
                    return Refuse(NotApprovedMessage);
                }

                if (UndeclaredPermission(manifest, granted) is { } refused)
                {
                    return new Outcome(null, refused);
                }

                var wanted = granted.Distinct().ToList();
                var done = new ActionResult(true, "Les permissions sont enregistrées.");
                if (entry.FromDevFolder)
                {
                    return new Outcome(
                        current with { DevFolders = [.. current.DevFolders.Select(f => SamePath(f.Path, entry.Directory) ? f with { Granted = wanted } : f)] },
                        done);
                }

                var approval = current.Modules.First(m => m.Id == id);
                return new Outcome(WithModule(current, approval with { Granted = wanted }), done);
            });

            if (pending is not null)
            {
                await CompleteAsync(pending).ConfigureAwait(false); // l'hôte en cours est relancé avec les nouvelles permissions
            }

            return result;
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ActionResult> RelaunchAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            var (result, pending) = Transact(current =>
            {
                var approval = current.Modules.FirstOrDefault(m => m.Id == id);
                var dev = approval is null && _catalog.Scan().Any(e => e.FromDevFolder && e.Id == id);
                if (approval is null && !dev)
                {
                    return Refuse(NotApprovedMessage);
                }

                // Même sans mise de côté enregistrée, le superviseur repart de zéro (une fois la liste écrite : un échec ne laisse rien en attente).
                var done = new ActionResult(true, "Le module est relancé.");
                return approval is { SetAsideReason: not null }
                    ? new Outcome(WithModule(current, approval with { SetAsideReason = null }), done, id)
                    : new Outcome(current, done, id);
            });

            if (pending is not null)
            {
                await CompleteAsync(pending).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _operations.Release();
        }
    }

    // --- Désinstallation ------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ActionResult> UninstallAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            string? root = null;
            var (result, pending) = Transact(current =>
            {
                var entries = _catalog.Scan();
                var installed = FindInstalled(entries, id);
                if (installed is null && !current.Modules.Any(m => m.Id == id))
                {
                    return Refuse(entries.Any(e => e.FromDevFolder && e.Id == id)
                        ? "Ce module vient d'un dossier de développement : retire le dossier à la place."
                        : NotInstalledMessage);
                }

                root = _catalog.ResolveRoot(_snapshot);
                return new Outcome(WithoutModule(current, id), new ActionResult(true, ""));
            });

            if (!result.Ok || pending is null)
            {
                return result;
            }

            // Plus validé : l'hôte (et le verrou sur ses fichiers) s'arrête d'abord, puis le dossier, le conteneur et les secrets partent.
            await CompleteAsync(pending).ConfigureAwait(false);
            var problems = new List<string>();
            if (ModuleInstaller.ModuleDirectory(root!, id) is { } directory)
            {
                if (ModuleInstaller.TryDelete(directory) is { } folderProblem)
                {
                    problems.Add($"le dossier du module n'a pas pu être supprimé ({folderProblem})");
                }
            }

            // Le conteneur et le coffre ne portent que des identifiants de modules de la communauté : un dossier déposé qui porte le nom d'un
            // type d'origine (« network ») n'a ni l'un ni l'autre, et son coffre est celui du module d'origine.
            if (ManifestReader.IsValidId(id))
            {
                Try(() => _deleteProfile(id), "son conteneur n'a pas pu être supprimé", problems);
                Try(() => _clearSecrets(id), "ses secrets n'ont pas pu être effacés", problems);
            }

            Refresh(); // le dossier est parti : le module quitte la liste

            return problems.Count == 0
                ? new ActionResult(true, "Le module est désinstallé.")
                : new ActionResult(false, $"Le module n'est plus validé, mais {string.Join(", ", problems)}.");
        }
        finally
        {
            _operations.Release();
        }
    }

    private static void Try(Action step, string failure, List<string> problems)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add($"{failure} ({ex.Message})");
        }
    }

    // --- Dossier des modules, mode développeur --------------------------------------------------------------------------------------

    /// <inheritdoc />
    public ActionResult SetModulesRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return new ActionResult(false, "Choisis un dossier avec son chemin complet (par exemple D:\\Modules).");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Path.GetPathRoot(full) is { } driveRoot && SamePath(driveRoot, full))
            {
                return new ActionResult(false, "Choisis un dossier, pas la racine d'un disque.");
            }

            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ActionResult(false, $"Ce dossier n'est pas utilisable : {ex.Message}");
        }

        using var _ = Enter();
        var (result, pending) = Transact(current => new Outcome(
            current with { ModulesRoot = SamePath(full, ModulesFolder.Default) ? null : full },
            new ActionResult(true, "Le dossier des modules est changé.")));
        if (pending is not null)
        {
            Complete(pending);
        }

        return result;
    }

    /// <inheritdoc />
    public ActionResult SetDeveloperMode(bool on)
    {
        using var _ = Enter();
        var (result, pending) = Transact(current => new Outcome(
            current with { DeveloperMode = on },
            new ActionResult(true, on ? "Le mode développeur est allumé." : "Le mode développeur est éteint.")));
        if (pending is not null)
        {
            Complete(pending);
        }

        return result;
    }

    /// <inheritdoc />
    public ActionResult AddDevFolder(string path, IReadOnlyList<ModulePermission> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return new ActionResult(false, "Choisis un dossier avec son chemin complet (par exemple C:\\Dev\\MonModule).");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ActionResult(false, $"Ce chemin n'est pas utilisable : {ex.Message}");
        }

        if (!Directory.Exists(full))
        {
            return new ActionResult(false, "Ce dossier n'existe pas.");
        }

        var read = CommunityCatalog.ReadManifest(full);
        if (read.Manifest is not { } manifest)
        {
            return new ActionResult(false, read.Message ?? "Le manifeste de ce dossier est illisible.");
        }

        if (UndeclaredPermission(manifest, granted) is { } refused)
        {
            return refused;
        }

        using var _ = Enter();
        var (result, pending) = Transact(current =>
        {
            if (!current.DeveloperMode)
            {
                return Refuse("Allume d'abord le mode développeur.");
            }

            var folders = current.DevFolders.Where(f => !SamePath(f.Path, full)).ToList();
            folders.Add(new DevFolder(full, [.. granted.Distinct()]));
            return new Outcome(current with { DevFolders = folders }, new ActionResult(true, $"Le dossier de « {manifest.Name} » est ajouté."));
        });

        if (pending is not null)
        {
            Complete(pending);
        }

        return result;
    }

    /// <inheritdoc />
    public ActionResult RemoveDevFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ActionResult(false, "Ce dossier n'est pas dans la liste.");
        }

        using var _ = Enter();
        var (result, pending) = Transact(current =>
        {
            var remaining = current.DevFolders.Where(f => !SamePath(f.Path, path)).ToList();
            return remaining.Count == current.DevFolders.Count
                ? Refuse("Ce dossier n'est pas dans la liste.")
                : new Outcome(current with { DevFolders = remaining }, new ActionResult(true, "Le dossier est retiré de la liste."));
        });

        if (pending is not null)
        {
            Complete(pending);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ActionResult> ReloadDevModuleAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Pending pending;
            CommunityEntry? entry;
            lock (_gate)
            {
                if (_disposed)
                {
                    return new ActionResult(false, "Le service s'arrête.");
                }

                _snapshot = _store.Load();
                if (_snapshot.Unreadable)
                {
                    return new ActionResult(false, CommunityCatalog.ApprovalsUnreadableMessage);
                }

                var entries = _catalog.Scan();
                entry = entries.FirstOrDefault(e => e.FromDevFolder && e.Id == id);
                if (entry is null)
                {
                    return new ActionResult(false, "Aucun dossier de développement ne contient ce module (le mode développeur est-il allumé ?).");
                }

                _forced.Add(id);
                pending = ApplyScanLocked(entries);
            }

            await CompleteAsync(pending).ConfigureAwait(false);
            return entry.State == CommunityState.Ready
                ? new ActionResult(true, "Le module est rechargé.")
                : new ActionResult(false, entry.Message ?? "Le module ne peut pas tourner pour le moment.");
        }
        finally
        {
            _operations.Release();
        }
    }

    // --- Affichage ------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void SetDisplayOptions(int maxFps, AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        lock (_gate)
        {
            _maxFps = Math.Max(1, maxFps);
            _appearance = HostPayloads.AppearanceToJson(appearance);
            foreach (var run in _running.Values)
            {
                run.Supervisor.SetMaxFps(_maxFps);
                run.Supervisor.SetAppearance(_appearance);
            }
        }
    }

    /// <summary>Arrête tous les hôtes (chacun a quelques secondes pour finir) et libère les fichiers des modules.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Running> running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = [.. _running.Values];
            _running.Clear();
        }

        await Task.WhenAll(running.Select(run => RunQuietlyAsync(() => run.Supervisor.DisposeAsync().AsTask()))).ConfigureAwait(false);
    }
}
