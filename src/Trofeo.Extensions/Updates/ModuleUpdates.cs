using System.Diagnostics;
using AshboardVision.Sdk;
using Trofeo.Distribution;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Updates;

/// <summary>Ce que la fenêtre de réglage montre d'une mise à jour proposée pour un module.</summary>
/// <param name="Version">La version proposée.</param>
/// <param name="Notes">Les notes de la version (nettoyées, bornées).</param>
/// <param name="NewPermissions">Les permissions que la nouvelle version déclare en plus de celles de la version installée : à valider une par une.</param>
public sealed record ModuleUpdateOffer(string Version, string Notes, IReadOnlyList<ModulePermission> NewPermissions);

/// <summary>
/// Les mises à jour des modules : quand les chercher, ce qu'on en retient, et leur installation à la demande de l'utilisateur.
/// <para>
/// <b>Rythme.</b> Une vérification 60 s après qu'un module portant un dépôt (<c>repository</c>) est apparu, puis toutes les 24 h, avec
/// un seul minuteur qui n'existe que tant qu'il y a un tel module : aucun minuteur et aucune requête sans lui. L'utilisateur peut
/// aussi lancer une vérification (<see cref="CheckNowAsync"/>).
/// </para>
/// <para>
/// <b>Rien ne s'installe tout seul.</b> Une vérification ne fait que proposer. Pour proposer, le paquet est téléchargé (avec son
/// empreinte, vérifiée) dans un dossier temporaire et son manifeste est lu : c'est le seul moyen de dire quelles permissions sont
/// nouvelles. Il doit porter l'identifiant et le dépôt du module concerné, et la version de l'étiquette ; sinon rien n'est proposé.
/// Le paquet n'est pas gardé : l'installation (<see cref="InstallAsync"/>) le télécharge de nouveau, le vérifie de nouveau, puis passe
/// par <see cref="ICommunityModules.InstallAsync"/> avec les seules permissions que l'utilisateur a cochées. Les fichiers temporaires
/// sont supprimés dans tous les cas.
/// </para>
/// </summary>
public sealed class ModuleUpdates : IDisposable
{
    /// <summary>Le délai de la première vérification, comme pour le logiciel.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);

    /// <summary>L'intervalle des vérifications suivantes.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>La réponse quand aucune mise à jour n'est proposée pour le module demandé.</summary>
    public const string NoUpdateMessage = "Aucune mise à jour proposée pour ce module.";

    private const string WrongPackageMessage = "Le paquet téléchargé ne correspond pas à la mise à jour annoncée de ce module : il n'est pas installé.";

    /// <summary>
    /// Le temps total laissé au téléchargement d'un paquet (10 minutes) : le silence est déjà borné à 60 s, mais un serveur qui goutte
    /// à goutte ne doit pas garder la porte des vérifications fermée indéfiniment. Réglable pour les tests.
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
    private bool _disposed;

    /// <summary>Une proposition : ce que GitHub a annoncé et ce que la fenêtre en voit.</summary>
    private sealed record Held(ModuleUpdate Update, ModuleUpdateOffer Offer);

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
    /// Cherche les mises à jour maintenant. Attend son tour si une vérification est en cours. Ne lève jamais pour un échec réseau ou
    /// une réponse douteuse : il n'y a simplement rien de nouveau (et ce qui était proposé le reste si GitHub n'a pas répondu).
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
                if (!sources.TryGetValue(id, out var source))
                {
                    continue;
                }

                Held? known;
                lock (_gate)
                {
                    _offers.TryGetValue(id, out known);
                }

                if (known is not null && known.Update.Version == update.Version && known.Update.Package == update.Package
                    && known.Update.Sha256 == update.Sha256)
                {
                    // La même version, déjà lue : pas de nouveau téléchargement (seules les notes ont pu être retouchées).
                    next[id] = known with { Update = update, Offer = known.Offer with { Notes = update.Notes } };
                    continue;
                }

                if (await BuildOfferAsync(entries.First(e => e.Id == id), source, update, ct).ConfigureAwait(false) is { } offer)
                {
                    next[id] = new Held(update, offer);
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
    /// Installe la mise à jour proposée pour <paramref name="id"/>, avec les seules permissions de <paramref name="granted"/>. Télécharge
    /// le paquet et vérifie son empreinte, puis qu'il porte bien l'identifiant, le dépôt et la version de la proposition, avant de passer
    /// par l'installation ordinaire. Un échec se dit dans le résultat, sans rien laisser derrière.
    /// </summary>
    public async Task<ActionResult> InstallAsync(string id, IReadOnlyList<ModulePermission> granted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(granted);
        Held? held;
        lock (_gate)
        {
            _offers.TryGetValue(id, out held);
        }

        var entry = _community.Entries.FirstOrDefault(e => e.Id == id);
        if (held is null || entry is null || ModuleUpdateChecker.UpdateSource.Of(entry) is not { } source)
        {
            return new ActionResult(false, NoUpdateMessage);
        }

        var folder = NewTempFolder(out var problem);
        if (folder is null)
        {
            return new ActionResult(false, problem!);
        }

        try
        {
            var file = Path.Combine(folder, $"{id}-{held.Update.Version}.avmod");
            if (await DownloadAsync(held.Update, file, ct).ConfigureAwait(false) is { } failure)
            {
                return new ActionResult(false, failure);
            }

            if (Inspect(file, source, held.Update) is null)
            {
                return new ActionResult(false, WrongPackageMessage);
            }

            var result = await _community.InstallAsync(file, granted, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                lock (_gate)
                {
                    _offers.Remove(id);
                }
            }

            return result;
        }
        finally
        {
            DeleteQuietly(folder);
        }
    }

    /// <summary>Arrête le minuteur et ne suit plus les modules. Une vérification en cours est annulée.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _community.Changed -= OnChanged;
        _lifetime.Cancel();
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
    /// propositions qui n'ont plus de sens (module parti, version installée entre-temps).
    /// </summary>
    private void Evaluate()
    {
        var sources = SourcesOf(_community.Entries);
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
                if (!sources.TryGetValue(id, out var source)
                    || !AppVersion.TryParse(held.Update.Version, out var offered)
                    || offered.CompareTo(source.Installed) <= 0)
                {
                    _offers.Remove(id);
                }
            }
        }
    }

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

    // --- Lecture d'un paquet -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Télécharge le paquet proposé, vérifie son empreinte et lit son manifeste : la proposition n'existe que s'il porte l'identifiant et le
    /// dépôt du module, la version annoncée, et qu'il s'installerait. Le paquet n'est pas gardé. <c>null</c> sinon (sans bruit).
    /// </summary>
    private async Task<ModuleUpdateOffer?> BuildOfferAsync(
        CommunityEntry entry, ModuleUpdateChecker.UpdateSource source, ModuleUpdate update, CancellationToken ct)
    {
        var folder = NewTempFolder(out _);
        if (folder is null)
        {
            return null;
        }

        try
        {
            var file = Path.Combine(folder, $"{entry.Id}-{update.Version}.avmod");
            if (await DownloadAsync(update, file, ct).ConfigureAwait(false) is { } failure)
            {
                Trace.TraceInformation($"Mises à jour des modules : « {entry.Id} » {update.Version} n'est pas proposée : {failure}");
                return null;
            }

            if (Inspect(file, source, update) is not { } manifest)
            {
                Trace.TraceInformation($"Mises à jour des modules : le paquet de « {entry.Id} » {update.Version} n'est pas celui qui est annoncé.");
                return null;
            }

            var installed = entry.Manifest?.Permissions ?? entry.Approval?.Granted ?? [];
            return new ModuleUpdateOffer(update.Version, update.Notes, [.. manifest.Permissions.Where(p => !installed.Contains(p))]);
        }
        finally
        {
            DeleteQuietly(folder);
        }
    }

    /// <summary>Télécharge et vérifie (empreinte, taille) ; rend la raison de l'échec en français, ou <c>null</c> si le fichier est là et vérifié.</summary>
    private async Task<string?> DownloadAsync(ModuleUpdate update, string file, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);
        try
        {
            await VerifiedDownload.DownloadAsync(_http, update.Package, update.Sha256, file, null, deadline.Token, ModuleUpdateChecker.MaxPackageBytes)
                .ConfigureAwait(false);
            return null;
        }
        catch (UpdateException ex)
        {
            return ex.Message;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return "Le téléchargement a pris trop de temps.";
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

    /// <summary>Un dossier neuf sous <c>tempRoot</c> ; <c>null</c> (et la raison) s'il ne se crée pas.</summary>
    private string? NewTempFolder(out string? problem)
    {
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

    private static void DeleteQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Mises à jour des modules : {folder} n'a pas pu être supprimé : {ex.Message}");
        }
    }
}
