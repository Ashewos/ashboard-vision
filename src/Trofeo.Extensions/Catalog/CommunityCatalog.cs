using Trofeo.Distribution;

namespace Trofeo.Extensions;

/// <summary>
/// Le catalogue des modules de la communauté : parcourt <c>&lt;racine&gt;\&lt;id&gt;\&lt;version&gt;\manifest.json</c> et
/// juge chaque module d'après la liste scellée (validé ? inchangé ? compatible ? mis de côté ?). Un scan relit tous les
/// fichiers pour recalculer les empreintes : il se fait à la demande, jamais sur un minuteur.
/// </summary>
/// <param name="load">Lit la liste scellée (<see cref="ApprovedModulesStore.Load"/>).</param>
public sealed class CommunityCatalog(Func<ApprovedLoad> load)
{
    /// <summary>La phrase quand les fichiers d'un module ne se lisent pas.</summary>
    internal const string FilesUnavailableMessage =
        "Fichiers du module indisponibles (OneDrive ?) : choisis un dossier non synchronisé dans les Paramètres.";

    /// <summary>La phrase quand la liste scellée ne se lit pas : les validations sont inconnues, pas absentes.</summary>
    internal const string ApprovalsUnreadableMessage =
        "Liste des modules validés illisible pour le moment : réessaie dans un instant.";

    private const string CollisionMessage =
        "Un module installé porte déjà cet identifiant : désinstalle-le ou change l'identifiant.";

    // Un manifeste fait quelques Ko : au-delà, ce n'est pas un manifeste.
    private const long MaxManifestBytes = 256 * 1024;

    private readonly Func<ApprovedLoad> _load = load ?? throw new ArgumentNullException(nameof(load));

    // Le dossier des modules de la dernière lecture réussie de la liste scellée.
    private string? _lastGoodRoot;

    /// <summary>
    /// Les modules du dossier des modules (triés par identifiant), puis ceux des dossiers de développement si le mode
    /// développeur est allumé. Liste altérée : tout est à valider et le mode développeur est éteint. Liste illisible :
    /// les modules qu'il faudrait juger d'après les validations sont <see cref="CommunityState.Unavailable"/> et les
    /// dossiers de développement ne sont pas listés (le mode développeur est inconnu). Ne lève pas d'exception pour un
    /// dossier absent ou illisible.
    /// Le dossier des modules vient de la dernière lecture réussie de la liste (ni altérée ni illisible), gardée en
    /// mémoire : une liste altérée ou illisible ne dit plus où sont les modules (le magasin rend alors une liste vide).
    /// Tant que ce processus n'a lu aucune liste valide, c'est le dossier par défaut.
    /// </summary>
    public IReadOnlyList<CommunityEntry> Scan()
    {
        var loaded = _load();
        var modules = loaded.Tampered ? ApprovedModules.Empty : loaded.Modules;
        var judgement = new Judgement(modules, loaded.Unreadable);

        var entries = ScanInstalled(ResolveRoot(loaded), judgement);
        if (modules.DeveloperMode && !loaded.Tampered && !loaded.Unreadable)
        {
            entries.AddRange(ScanDevFolders(modules.DevFolders, entries));
        }

        return entries;
    }

    /// <summary>Le dossier à parcourir pour cette lecture ; retient celui d'une lecture réussie.</summary>
    internal string ResolveRoot(ApprovedLoad loaded)
    {
        if (loaded.Tampered || loaded.Unreadable)
        {
            return Volatile.Read(ref _lastGoodRoot) ?? ModulesFolder.Default;
        }

        var root = ModulesFolder.Resolve(loaded.Modules);
        Volatile.Write(ref _lastGoodRoot, root);
        return root;
    }

    private readonly record struct Judgement(ApprovedModules Approvals, bool ApprovalsUnknown);

    private static List<CommunityEntry> ScanInstalled(string root, Judgement judgement)
    {
        var entries = new List<CommunityEntry>();
        string[] idDirectories;
        try
        {
            idDirectories = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        Array.Sort(idDirectories, StringComparer.Ordinal);
        foreach (var idDirectory in idDirectories)
        {
            var id = Path.GetFileName(idDirectory);
            string[] versionDirectories;
            try
            {
                versionDirectories = Directory.GetDirectories(idDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new CommunityEntry(id, idDirectory, null, CommunityState.Unavailable, FilesUnavailableMessage, null, false));
                continue;
            }

            Array.Sort(versionDirectories, StringComparer.Ordinal);
            var candidates = new List<(string Name, string Directory, AppVersion Version)>();
            foreach (var versionDirectory in versionDirectories)
            {
                var name = Path.GetFileName(versionDirectory);
                if (AppVersion.TryParse(name, out var version))
                {
                    candidates.Add((name, versionDirectory, version));
                }
                else
                {
                    entries.Add(new CommunityEntry(id, versionDirectory, null, CommunityState.Invalid,
                        $"Le nom du dossier « {name} » n'est pas un numéro de version valide.", null, false));
                }
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            // Seule la version approuvée compte ; à défaut, la plus récente (par l'ordre des versions, pas des chaînes).
            var approval = FindApproval(judgement.Approvals, id);
            var chosen = approval is null
                ? default
                : candidates.FirstOrDefault(c => string.Equals(c.Name, approval.Version, StringComparison.Ordinal));
            if (chosen.Directory is null)
            {
                chosen = candidates[0];
                foreach (var candidate in candidates)
                {
                    if (candidate.Version.CompareTo(chosen.Version) > 0)
                    {
                        chosen = candidate;
                    }
                }
            }

            entries.Add(Evaluate(id, chosen.Name, chosen.Directory, approval, judgement));
        }

        return entries;
    }

    private static List<CommunityEntry> ScanDevFolders(IReadOnlyList<DevFolder> folders, List<CommunityEntry> installed)
    {
        var taken = installed.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var entries = new List<CommunityEntry>();
        foreach (var folder in folders)
        {
            var path = folder.Path;
            var fallbackId = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var read = ReadManifest(path);
            if (read.Manifest is not { } manifest)
            {
                entries.Add(new CommunityEntry(fallbackId, path, null, read.State, read.Message, null, true));
                continue;
            }

            var compatibility = Compatibility.Check(manifest.Sdk);
            CommunityEntry entry;
            if (!taken.Add(manifest.Id))
            {
                entry = new CommunityEntry(manifest.Id, path, manifest, CommunityState.Invalid, CollisionMessage, null, true);
            }
            else if (compatibility != CompatibilityKind.Compatible)
            {
                entry = new CommunityEntry(manifest.Id, path, manifest, ToState(compatibility), Compatibility.Message(compatibility), null, true);
            }
            else
            {
                // Le contenu d'un dossier de développement change sans cesse : son empreinte n'est pas vérifiée.
                entry = new CommunityEntry(manifest.Id, path, manifest, CommunityState.Ready, null, null, true);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static CommunityEntry Evaluate(string id, string version, string directory, ApprovedModule? approval, Judgement judgement)
    {
        CommunityEntry Entry(ModuleManifest? manifest, CommunityState state, string? message) =>
            new(id, directory, manifest, state, message, approval, false);

        var read = ReadManifest(directory);
        if (read.Manifest is not { } manifest)
        {
            return Entry(null, read.State, read.Message);
        }

        // Un module ne se fait pas passer pour un autre par le nom de son dossier.
        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal))
        {
            return Entry(null, CommunityState.Invalid,
                $"L'identifiant du manifeste (« {manifest.Id} ») ne correspond pas au nom du dossier (« {id} ») : réinstalle le module.");
        }

        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            return Entry(null, CommunityState.Invalid,
                $"La version du manifeste (« {manifest.Version} ») ne correspond pas au nom du dossier (« {version} ») : réinstalle le module.");
        }

        var compatibility = Compatibility.Check(manifest.Sdk);
        if (compatibility != CompatibilityKind.Compatible)
        {
            return Entry(manifest, ToState(compatibility), Compatibility.Message(compatibility));
        }

        if (judgement.ApprovalsUnknown)
        {
            return Entry(manifest, CommunityState.Unavailable, ApprovalsUnreadableMessage);
        }

        if (approval is null)
        {
            return Entry(manifest, CommunityState.NeedsApproval, "Ce module n'a pas encore été validé.");
        }

        if (!string.Equals(approval.Version, version, StringComparison.Ordinal))
        {
            return Entry(manifest, CommunityState.NeedsApproval,
                "Cette version du module n'a pas encore été validée : vérifie ses permissions avant de la valider.");
        }

        if (approval.SetAsideReason is { } reason)
        {
            return Entry(manifest, CommunityState.SetAside, reason);
        }

        string hash;
        try
        {
            hash = ContentHash.Compute(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Entry(manifest, CommunityState.Unavailable, FilesUnavailableMessage);
        }
        catch (ArgumentException)
        {
            // Deux fichiers au même chemin à la casse près (impossible sous Windows, possible ailleurs).
            return Entry(manifest, CommunityState.Invalid, "Le dossier du module contient deux fichiers dont les noms ne diffèrent que par la casse.");
        }

        return string.Equals(hash, approval.ContentHash, StringComparison.Ordinal)
            ? Entry(manifest, CommunityState.Ready, null)
            : Entry(manifest, CommunityState.Changed, "Le contenu du module a changé depuis sa validation : valide-le de nouveau.");
    }

    private static ApprovedModule? FindApproval(ApprovedModules approvals, string id) =>
        approvals.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    private static CommunityState ToState(CompatibilityKind kind) => kind switch
    {
        CompatibilityKind.NeedsNewerApp => CommunityState.NeedsNewerApp,
        CompatibilityKind.TooOld => CommunityState.TooOld,
        _ => CommunityState.Ready,
    };

    internal readonly record struct ManifestOutcome(ModuleManifest? Manifest, CommunityState State, string? Message);

    /// <summary>Lit le <c>manifest.json</c> d'un dossier de module (taille bornée) ; jamais d'exception pour un fichier absent ou illisible.</summary>
    internal static ManifestOutcome ReadManifest(string directory)
    {
        var path = Path.Combine(directory, "manifest.json");
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return new ManifestOutcome(null, CommunityState.Invalid, "Le fichier manifest.json est introuvable dans le dossier du module.");
            }

            if (new FileInfo(path).Length > MaxManifestBytes)
            {
                return new ManifestOutcome(null, CommunityState.Invalid, "Le fichier manifest.json est trop gros pour être un manifeste.");
            }

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ManifestOutcome(null, CommunityState.Unavailable, FilesUnavailableMessage);
        }

        var result = ManifestReader.Read(json);
        return result.Manifest is { } manifest
            ? new ManifestOutcome(manifest, CommunityState.Ready, null)
            : new ManifestOutcome(null, CommunityState.Invalid, string.Join(" ", result.Errors));
    }
}
