using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Layout;

/// <summary>
/// Résultat de la lecture d'une disposition : soit <see cref="Layout"/>, soit <see cref="Error"/> (en français).
/// <see cref="Warnings"/> liste ce qui a été écarté ou corrigé sans rejeter la disposition.
/// </summary>
public sealed record LayoutLoadResult(DashboardLayout? Layout, string? Error, IReadOnlyList<string> Warnings);

/// <summary>Lecture et validation d'une disposition en JSON, communes à <see cref="LayoutStore"/> et <see cref="LayoutTransfer"/>.</summary>
internal static class LayoutJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Avertissement quand une <c>Appearance</c> illisible est abandonnée.</summary>
    internal const string AppearanceDropped =
        "Réglages d'apparence illisibles dans layout.json : apparence par défaut utilisée.";

    internal const string MaxFpsInvalid = "Plafond d'images invalide : 12 images/s utilisées.";
    internal const string ShortcutsDropped = "Raccourcis illisibles dans layout.json : raccourcis par défaut utilisés.";
    internal const string ViewsDropped = "Vues illisibles dans layout.json : elles sont ignorées.";
    internal const string Unreadable = "Le fichier layout.json est illisible (JSON invalide).";
    internal const int MinFps = 1;
    internal const int MaxFpsLimit = 12;

    /// <summary>
    /// Lit et valide. Ne lève jamais d'exception ; ne touche à aucun fichier. <c>Appearance</c>, <c>Shortcuts</c> (un
    /// par un) et <c>MaxFps</c> sont lus avec indulgence : ce qui est illisible est écarté avec un avertissement, la
    /// disposition est gardée. Les vues sont lues de la même façon (voir <see cref="ReadViews"/>). Chaque avertissement
    /// est aussi transmis à <paramref name="warn"/>.
    /// </summary>
    internal static LayoutLoadResult Parse(string json, Action<string>? warn) => Parse(json, warn, out _);

    /// <summary>
    /// Comme <see cref="Parse(string, Action{string}?)"/>, et dit si le fichier avait une propriété <c>Views</c>
    /// (<paramref name="viewsPresent"/>) : sans elle, c'est un fichier d'avant les vues, dont les raccourcis par défaut
    /// se migrent.
    /// </summary>
    internal static LayoutLoadResult Parse(string json, Action<string>? warn, out bool viewsPresent)
    {
        viewsPresent = false;
        var warnings = new List<string>();
        void Warn(string message)
        {
            warnings.Add(message);
            warn?.Invoke(message);
        }

        try
        {
            var layout = Deserialize(json, Warn, out viewsPresent);
            if (layout is null) return Fail(Unreadable, warnings);

            var error = Validate(layout);
            return error is null
                ? new LayoutLoadResult(layout, null, warnings)
                : Fail(error, warnings);
        }
        catch (Exception)
        {
            // Toute erreur de lecture (JSON, type incohérent, débordement...) : illisible.
            return Fail(Unreadable, warnings);
        }
    }

    private static LayoutLoadResult Fail(string error, List<string> warnings) => new(null, error, warnings);

    private static DashboardLayout? Deserialize(string json, Action<string> warn, out bool viewsPresent)
    {
        viewsPresent = false;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null; // racine non-objet

            // Lever ici (clé en double) vaut ArgumentException, à la première lecture d'une propriété : voir plus bas.
            var hasAppearance = Take(root, nameof(DashboardLayout.Appearance), out var appearance);
            var hasShortcuts = Take(root, nameof(DashboardLayout.Shortcuts), out var shortcuts);
            Take(root, nameof(DashboardLayout.MaxFps), out var maxFps);
            viewsPresent = Take(root, nameof(DashboardLayout.Views), out var views);
            Take(root, LegacyPlacements, out var legacyPlacements);
            if (root[nameof(DashboardLayout.Shelf)] is JsonArray shelfNodes) DropBadNames(shelfNodes, warn);

            var layout = root.Deserialize<DashboardLayout>(Options);
            if (layout is null) return null;

            return layout with
            {
                Views = ReadViews(views, legacyPlacements, warn),
                Shelf = layout.Shelf is null ? null : [.. layout.Shelf.Select(p => p is null ? p! : CleanCaseName(p, warn))],
                Appearance = hasAppearance ? ReadAppearance(appearance, warn) : null,
                Shortcuts = hasShortcuts ? ReadShortcuts(shortcuts, warn) : null,
                MaxFps = ReadMaxFps(maxFps, warn),
            };
        }
        catch (ArgumentException)
        {
            // Un nom de propriété en double : JsonNode refuse, le sérialiseur l'acceptait (la dernière valeur gagne) et
            // les fichiers d'avant le plan 4 doivent rester lus. Chemin d'avant, sans lecture indulgente.
            var legacy = JsonSerializer.Deserialize<LegacyWire>(json, Options);
            if (legacy is null) return null;

            viewsPresent = legacy.Views is not null;
            List<LayoutView> views = legacy.Views is { } wired
                ? [.. wired.Where(v => v is not null)]
                : [new LayoutView(DashboardLayout.DefaultViewId, LayoutView.FirstViewName, legacy.Placements ?? [])];
            return new DashboardLayout(
                legacy.Rotation, Normalize(views, warn), DropNullShortcuts(legacy.Shortcuts, warn), legacy.Appearance,
                legacy.Shelf is null ? null : [.. legacy.Shelf.Select(p => p is null ? p! : CleanCaseName(p, warn))],
                CheckMaxFps(legacy.MaxFps, warn));
        }
    }

    /// <summary>Nom, à la racine, des cases d'un fichier d'avant les vues.</summary>
    private const string LegacyPlacements = "Placements";

    /// <summary>Forme d'un fichier lu par le chemin d'avant (propriété en double) : les vues, ou les cases d'avant les vues.</summary>
    private sealed record LegacyWire(
        int Rotation, IReadOnlyList<LayoutView>? Views, IReadOnlyList<ModulePlacement>? Placements,
        IReadOnlyList<ShortcutBinding>? Shortcuts, AppearanceSettings? Appearance, IReadOnlyList<ModulePlacement>? Shelf,
        int? MaxFps);

    /// <summary>
    /// Lit les vues avec indulgence : une entrée qui n'est pas un objet est écartée, un nom absent devient « Vue n »,
    /// un identifiant absent est refait (voir <see cref="Normalize"/>). Sans <c>Views</c> utilisable, les anciennes
    /// <c>Placements</c> de la racine forment une seule vue « Vue 1 » ; sans rien, une vue vide. Les cases elles-mêmes
    /// sont lues strictement, comme avant (une case illisible fait refuser le fichier).
    /// </summary>
    private static List<LayoutView> ReadViews(JsonNode? views, JsonNode? legacyPlacements, Action<string> warn)
    {
        var read = new List<LayoutView>();
        if (views is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                {
                    warn("Vue illisible ignorée : ce n'est pas un objet.");
                    continue;
                }

                var id = obj[nameof(LayoutView.Id)] is JsonValue idValue && idValue.TryGetValue<string>(out var text) &&
                         Guid.TryParse(text, out var parsed)
                    ? parsed
                    : Guid.Empty;
                var name = obj[nameof(LayoutView.Name)] is JsonValue nameValue && nameValue.TryGetValue<string>(out var nameText)
                    ? nameText
                    : null;
                var placements = ReadPlacements(obj[nameof(LayoutView.Placements)], warn, $"Vue n° {read.Count + 1}");
                read.Add(new LayoutView(id, name ?? "", placements));
            }
        }
        else if (views is not null)
        {
            warn(ViewsDropped);
        }

        if (read.Count == 0 && views is not JsonArray)
        {
            // Pas de vues (fichier d'avant, ou illisible) : les cases de la racine, sinon rien.
            read.Add(new LayoutView(
                DashboardLayout.DefaultViewId, LayoutView.FirstViewName, ReadPlacements(legacyPlacements, warn, null)));
        }
        else if (read.Count == 0)
        {
            read.Add(new LayoutView(DashboardLayout.DefaultViewId, LayoutView.FirstViewName, []));
        }

        return Normalize(read, warn);
    }

    /// <summary>
    /// Les cases d'une vue. Absentes ou <c>null</c> : aucune (avec un avertissement si <paramref name="owner"/> nomme
    /// une vue). Pas un tableau, ou une case de mauvais type : lève, le fichier est illisible.
    /// </summary>
    private static List<ModulePlacement> ReadPlacements(JsonNode? node, Action<string> warn, string? owner)
    {
        if (node is null)
        {
            if (owner is not null) warn($"{owner} sans cases : vue vide.");
            return [];
        }

        if (node is not JsonArray array) throw new JsonException("Les cases d'une vue doivent former un tableau.");

        DropBadNames(array, warn);
        return array.Deserialize<List<ModulePlacement>>(Options) ?? [];
    }

    /// <summary>Un <c>Name</c> qui n'est ni du texte ni <c>null</c> est écarté (avec un avertissement) : la case est gardée.</summary>
    private static void DropBadNames(JsonArray placements, Action<string> warn)
    {
        foreach (var item in placements)
        {
            if (item is not JsonObject obj || !obj.TryGetPropertyValue(nameof(ModulePlacement.Name), out var name)) continue;
            if (name is null || name is JsonValue value && value.TryGetValue<string>(out _)) continue;

            obj.Remove(nameof(ModulePlacement.Name));
            warn("Nom de case illisible ignoré : ce n'est pas du texte.");
        }
    }

    /// <summary>
    /// Remet les vues dans les règles, sans jamais refuser le fichier : au plus <see cref="LayoutView.MaxViews"/> (les
    /// suivantes sont ignorées), noms nettoyés et coupés à <see cref="LayoutView.MaxNameLength"/> (un nom absent devient
    /// « Vue n »), identifiants absents ou en double refaits, et une même case dans deux vues gardée dans la première
    /// seulement. Les noms des cases sont nettoyés et coupés de même.
    /// </summary>
    private static List<LayoutView> Normalize(List<LayoutView> views, Action<string> warn)
    {
        if (views.Count == 0) views = [new LayoutView(DashboardLayout.DefaultViewId, LayoutView.FirstViewName, [])];

        if (views.Count > LayoutView.MaxViews)
        {
            warn($"{views.Count} vues dans layout.json : seules les {LayoutView.MaxViews} premières sont gardées.");
            views = views.GetRange(0, LayoutView.MaxViews);
        }

        var result = new List<LayoutView>(views.Count);
        var viewIds = new HashSet<Guid>();
        var seenInstances = new HashSet<Guid>();
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            var id = view.Id;
            if (id == Guid.Empty)
            {
                warn($"Vue n° {i + 1} sans identifiant : un identifiant neuf lui est donné.");
                id = NewViewId(viewIds);
            }
            else if (!viewIds.Add(id))
            {
                warn($"Identifiant de vue en double (vue n° {i + 1}) : un identifiant neuf lui est donné.");
                id = NewViewId(viewIds);
            }

            viewIds.Add(id);
            var name = ViewEditor.CleanName(view.Name);
            if (name is null) name = $"Vue {i + 1}";
            else if (view.Name.Trim().Length > LayoutView.MaxNameLength)
                warn($"Nom de vue « {name} » coupé à {LayoutView.MaxNameLength} caractères.");

            var placements = new List<ModulePlacement>(view.Placements?.Count ?? 0);
            foreach (var p in view.Placements ?? [])
            {
                if (p is null)
                {
                    placements.Add(p!); // refusé plus tard par Validate
                    continue;
                }

                if (seenInstances.Contains(p.InstanceId))
                {
                    warn($"Module {p.TypeId} déjà placé dans une vue précédente : il est ignoré dans « {name} ».");
                    continue;
                }

                placements.Add(CleanCaseName(p, warn));
            }

            foreach (var p in placements)
                if (p is not null) seenInstances.Add(p.InstanceId);

            result.Add(new LayoutView(id, name, placements));
        }

        return result;
    }

    private static Guid NewViewId(HashSet<Guid> used)
    {
        Guid id;
        do id = Guid.NewGuid(); while (used.Contains(id));
        return id;
    }

    /// <summary>Nettoie le nom d'une case : vide : aucun ; plus long que <see cref="LayoutView.MaxNameLength"/> : coupé (avertissement).</summary>
    private static ModulePlacement CleanCaseName(ModulePlacement placement, Action<string> warn)
    {
        var name = ViewEditor.CleanName(placement.Name);
        if (name is not null && placement.Name!.Trim().Length > LayoutView.MaxNameLength)
            warn($"Nom de case « {name} » coupé à {LayoutView.MaxNameLength} caractères.");
        return name == placement.Name ? placement : placement with { Name = name };
    }

    /// <summary>Retire la propriété de l'objet pour que le sérialiseur ne la lise pas (elle est lue avec indulgence).</summary>
    private static bool Take(JsonObject root, string name, out JsonNode? node)
    {
        var found = root.TryGetPropertyValue(name, out node);
        root.Remove(name);
        return found;
    }

    private static AppearanceSettings? ReadAppearance(JsonNode? node, Action<string> warn)
    {
        if (node is null) return null; // "Appearance": null, voulu

        try
        {
            var appearance = node.Deserialize<AppearanceSettings>(Options);
            if (appearance is not null && appearance.Theme is not null &&
                (appearance.Background is not { } background || Enum.IsDefined(background.Kind)))
                return appearance;
        }
        catch (Exception)
        {
            // Mauvais type JSON, Kind inconnu... : pas d'apparence, mais la disposition reste lue.
        }

        warn(AppearanceDropped);
        return null;
    }

    private static int? ReadMaxFps(JsonNode? node, Action<string> warn)
    {
        if (node is null) return null; // absent, ou null voulu
        if (node is JsonValue value && value.TryGetValue<long>(out var n) && n is >= MinFps and <= MaxFpsLimit)
            return (int)n;

        warn(MaxFpsInvalid);
        return null;
    }

    private static int? CheckMaxFps(int? maxFps, Action<string> warn)
    {
        if (maxFps is null or (>= MinFps and <= MaxFpsLimit)) return maxFps;

        warn(MaxFpsInvalid);
        return null;
    }

    /// <summary>Chemin d'avant : les entrées <c>null</c> sont écartées avec le même avertissement que la lecture indulgente.</summary>
    private static IReadOnlyList<ShortcutBinding>? DropNullShortcuts(IReadOnlyList<ShortcutBinding>? shortcuts, Action<string> warn)
    {
        if (shortcuts is null || !shortcuts.Contains(null!)) return shortcuts;

        var kept = new List<ShortcutBinding>();
        foreach (var binding in shortcuts)
        {
            if (binding is not null) kept.Add(binding);
            else warn("Raccourci « ? » ignoré : touches absentes.");
        }

        return kept;
    }

    /// <summary>Un raccourci illisible est écarté (avec un avertissement) ; il ne fait pas rejeter la disposition.</summary>
    private static List<ShortcutBinding>? ReadShortcuts(JsonNode? node, Action<string> warn)
    {
        if (node is null) return null; // "Shortcuts": null, voulu
        if (node is not JsonArray array)
        {
            warn(ShortcutsDropped);
            return null;
        }

        var bindings = new List<ShortcutBinding>();
        foreach (var item in array)
        {
            var binding = ReadShortcut(item, out var reason, out var keys);
            if (binding is not null) bindings.Add(binding);
            else warn($"Raccourci « {keys} » ignoré : {reason}.");
        }

        return bindings;
    }

    private static ShortcutBinding? ReadShortcut(JsonNode? item, out string reason, out string keys)
    {
        keys = "?";
        reason = "";
        var obj = item as JsonObject;
        if (obj?[nameof(ShortcutBinding.Keys)] is not JsonValue keysValue || !keysValue.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            reason = "touches absentes";
            return null;
        }

        keys = text;
        if (!IsKnownCommand(obj[nameof(ShortcutBinding.Command)]))
        {
            reason = "commande inconnue";
            return null;
        }

        if (!ShortcutKeys.TryParse(text, out _, out _))
        {
            reason = "touches non reconnues";
            return null;
        }

        try
        {
            var binding = obj.Deserialize<ShortcutBinding>(Options);
            if (binding is not null) return binding;
        }
        catch (Exception)
        {
            // Index ou Target du mauvais type : même sort.
        }

        reason = "réglage illisible";
        return null;
    }

    private static bool IsKnownCommand(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<string>(out var name))
            return !int.TryParse(name, out _) && Enum.TryParse<ModuleCommandKind>(name, ignoreCase: true, out var kind) &&
                   Enum.IsDefined(kind);
        return value.TryGetValue<int>(out var number) && Enum.IsDefined((ModuleCommandKind)number);
    }

    /// <summary>La raison du refus, en français, ou <c>null</c> si la disposition est valide.</summary>
    private static string? Validate(DashboardLayout layout)
    {
        if (layout.Rotation is not (0 or 180)) return $"Rotation {layout.Rotation}° refusée : 0 ou 180.";
        if (layout.Views is null || layout.Views.Count == 0) return Unreadable;

        foreach (var view in layout.Views)
        {
            if (view?.Placements is null) return Unreadable;
            foreach (var p in view.Placements)
            {
                if (p is null) return Unreadable;
                if (string.IsNullOrWhiteSpace(p.TypeId)) return "Module sans type.";
                if (p.Settings.ValueKind == JsonValueKind.Undefined) return $"Module sans réglages : {p.TypeId}.";
                if (!LayoutValidator.IsInBounds(p)) return $"Module hors du damier : {p.TypeId}.";
            }
        }

        if (layout.Shelf is { } shelf)
        {
            foreach (var p in shelf)
            {
                if (p is null) return "Module retiré illisible.";
                if (string.IsNullOrWhiteSpace(p.TypeId)) return "Module retiré sans type.";
                if (p.Settings.ValueKind == JsonValueKind.Undefined) return $"Module retiré sans réglages : {p.TypeId}.";
                if (p.Columns < 1 || p.Rows < 1) return $"Module retiré de taille invalide : {p.TypeId}.";
            }
        }

        var ids = layout.AllPlacements.Concat(layout.Shelf ?? []).Select(p => p.InstanceId).ToList();
        if (ids.Distinct().Count() != ids.Count) return "Identifiant de module en double.";

        // Chaque vue a son propre damier : on ne compare que les cases d'une même vue.
        foreach (var placements in layout.Views.Select(v => v.Placements))
            for (var i = 0; i < placements.Count; i++)
                for (var j = i + 1; j < placements.Count; j++)
                    if (LayoutValidator.Intersects(placements[i], placements[j]))
                        return $"Deux modules se chevauchent : {placements[i].TypeId} et {placements[j].TypeId}.";

        return null;
    }
}
