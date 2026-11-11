using System.Text.Json;
using System.Text.Json.Serialization;
using Trofeo.Core.Rendering;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Layout;

/// <summary>Disposition complète du tableau de bord : rotation de l'écran, vues (modules placés) et raccourcis clavier.</summary>
/// <param name="Views">
/// Les vues de l'écran, de 1 à <see cref="LayoutView.MaxViews"/> ; l'écran en affiche une à la fois. Un fichier d'avant
/// les vues (propriété <c>Placements</c> à la racine) est lu comme une seule vue.
/// </param>
/// <param name="Shortcuts">Raccourcis clavier globaux ; <c>null</c> (ancienne disposition) : <see cref="ShortcutDefaults.All"/>.</param>
/// <param name="Appearance">Thème, opacité, police et fond ; <c>null</c> (ancienne disposition ou réglage illisible) : <see cref="AppearanceSettings.Default"/>.</param>
/// <param name="Shelf">
/// Modules retirés de l'écran, gardés avec leurs réglages pour les remettre plus tard ; <c>null</c> (ancienne
/// disposition) : aucun. Leurs emplacements ne sont pas contrôlés sur le damier. L'étagère est commune à toutes les vues.
/// </param>
/// <param name="MaxFps">Plafond d'images par seconde (1 à 12) ; <c>null</c> : celui du service.</param>
public sealed record DashboardLayout(
    int Rotation, IReadOnlyList<LayoutView> Views, IReadOnlyList<ShortcutBinding>? Shortcuts = null,
    AppearanceSettings? Appearance = null, IReadOnlyList<ModulePlacement>? Shelf = null, int? MaxFps = null)
{
    /// <summary>
    /// Identifiant de la vue unique de la disposition par défaut, et de celle d'une ancienne disposition migrée : constant,
    /// pour qu'une même lecture donne toujours le même identifiant.
    /// </summary>
    public static readonly Guid DefaultViewId = new("7e4b2a91-c3d5-4f08-a6b1-92d0e5f13c48");

    /// <summary>
    /// Disposition par défaut : une seule vue (« Vue 1 »), rotation 180° (montage d'Alex), Claude Code à gauche (colonnes
    /// 0 à 7), Monitoring et En écoute au milieu, Heure et Réseau à droite. Codex n'y est pas : il se place à la main.
    /// </summary>
    public static DashboardLayout Default { get; } = new(
        180,
        [new LayoutView(DefaultViewId, LayoutView.FirstViewName,
        [
            new ModulePlacement(
                new Guid("3c1d9f5e-7a2b-4e8c-9b1f-6d2a0e4c8b17"),
                "claudecode", 0, 0, 8, 4, TextSize.Normal, Settings("""{"view":"feed","autoFollow":true}""")),
            new ModulePlacement(
                new Guid("246ac06d-eec2-4d64-9930-24b64603a03b"),
                "monitoring", 8, 0, 4, 2, TextSize.Normal, Settings("{}")),
            new ModulePlacement(
                new Guid("8ebcc647-ab4b-401d-b2b3-f7a0ebd97b4b"),
                "nowplaying", 8, 2, 4, 2, TextSize.Normal, Settings("{}")),
            new ModulePlacement(
                new Guid("5b0c5e2e-6a53-4c0b-9d0e-3f1a7c1d2b01"),
                "clock", 12, 0, 4, 2, TextSize.Normal, Settings("{}")),
            new ModulePlacement(
                new Guid("a2002423-c807-4403-9c5a-2a983fb6a371"),
                "network", 12, 2, 4, 2, TextSize.Normal, Settings("""{"source":"pc"}""")),
        ])]);

    /// <summary>
    /// Une disposition à une seule vue (« Vue 1 », identifiant neuf) : pour les tests et l'assistant de premier lancement.
    /// </summary>
    public static DashboardLayout SingleView(
        int rotation, IReadOnlyList<ModulePlacement> placements, IReadOnlyList<ShortcutBinding>? shortcuts = null,
        AppearanceSettings? appearance = null, IReadOnlyList<ModulePlacement>? shelf = null, int? maxFps = null) =>
        new(rotation, [new LayoutView(Guid.NewGuid(), LayoutView.FirstViewName, placements)], shortcuts, appearance, shelf, maxFps);

    /// <summary>Toutes les cases placées, vue après vue (l'étagère n'en fait pas partie).</summary>
    [JsonIgnore]
    public IEnumerable<ModulePlacement> AllPlacements => Views.SelectMany(v => v.Placements);

    /// <summary>La vue d'identifiant <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">Aucune vue n'a cet identifiant.</exception>
    public LayoutView View(Guid id) =>
        Views.FirstOrDefault(v => v.Id == id) ?? throw new KeyNotFoundException($"Aucune vue ne porte l'identifiant {id}.");

    /// <summary>Remplace les cases d'une vue ; les autres vues, l'étagère et le reste sont gardés tels quels.</summary>
    /// <exception cref="KeyNotFoundException">Aucune vue n'a cet identifiant.</exception>
    public DashboardLayout WithPlacements(Guid viewId, IReadOnlyList<ModulePlacement> placements)
    {
        View(viewId); // lève si la vue n'existe pas
        return this with { Views = [.. Views.Select(v => v.Id == viewId ? v with { Placements = placements } : v)] };
    }

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
