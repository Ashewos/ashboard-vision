using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Rendering;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La section « Verre » de l'onglet Personnalisation, visible avec le thème « Liquid Glass avancé » : quatre préréglages,
/// puis (dans le repli « Réglages fins ») les 16 curseurs en quatre groupes et la forme du biseau. Choisir un préréglage écrit
/// <c>new GlassAppearance(id)</c> ; lâcher un curseur écrit <c>Custom</c> avec toutes les valeurs, en un seul pas d'annulation.
/// Les bornes viennent de <see cref="GlassSettings.Parameters"/>, les valeurs du fichier de <see cref="GlassAppearance.Resolve"/>.
/// </summary>
public sealed partial class GlassSectionViewModel : ObservableObject
{
    private readonly Func<AppearanceSettings> _appearance;
    private readonly Func<Func<AppearanceSettings, AppearanceSettings>, Task> _edit;
    private readonly List<GlassSliderItem> _sliders;
    private (string Preset, GlassSettings Values) _synced;

    /// <param name="appearance">L'apparence du moment (celle du fichier).</param>
    /// <param name="edit">Une modification de l'apparence, enregistrée par <see cref="Services.EditSession"/>.</param>
    public GlassSectionViewModel(Func<AppearanceSettings> appearance, Func<Func<AppearanceSettings, AppearanceSettings>, Task> edit)
    {
        _appearance = appearance;
        _edit = edit;

        Presets =
        [
            new GlassPresetItem(GlassPresets.Clair, Texts.AppearanceGlassPresetClair, () => SetPresetAsync(GlassPresets.Clair)),
            new GlassPresetItem(GlassPresets.Givre, Texts.AppearanceGlassPresetGivre, () => SetPresetAsync(GlassPresets.Givre)),
            new GlassPresetItem(GlassPresets.Prisme, Texts.AppearanceGlassPresetPrisme, () => SetPresetAsync(GlassPresets.Prisme)),
            new GlassPresetItem(GlassPresets.Discret, Texts.AppearanceGlassPresetDiscret, () => SetPresetAsync(GlassPresets.Discret)),
        ];

        // Les groupes suivent l'ordre de GlassSettings.Parameters : Verre (5), Lumière (4), Couleur (4), Ombre (3).
        static GlassSliderItem Slider(string name, string label) =>
            new(GlassSettings.Parameters.First(p => p.Name == name), label);

        Groups =
        [
            new GlassGroup(Texts.AppearanceGlassGroupGlass,
            [
                Slider(nameof(GlassSettings.Blur), Texts.AppearanceGlassBlur),
                Slider(nameof(GlassSettings.Refraction), Texts.AppearanceGlassRefraction),
                Slider(nameof(GlassSettings.ZRadius), Texts.AppearanceGlassBevelDepth),
                Slider(nameof(GlassSettings.CornerRadius), Texts.AppearanceGlassCornerRadius),
                Slider(nameof(GlassSettings.Distortion), Texts.AppearanceGlassDistortion),
            ]),
            new GlassGroup(Texts.AppearanceGlassGroupLight,
            [
                Slider(nameof(GlassSettings.EdgeHighlight), Texts.AppearanceGlassEdgeHighlight),
                Slider(nameof(GlassSettings.Specular), Texts.AppearanceGlassSpecular),
                Slider(nameof(GlassSettings.Fresnel), Texts.AppearanceGlassFresnel),
                Slider(nameof(GlassSettings.ChromaticAberration), Texts.AppearanceGlassChromatic),
            ]),
            new GlassGroup(Texts.AppearanceGlassGroupColor,
            [
                Slider(nameof(GlassSettings.Opacity), Texts.AppearanceGlassOpacity),
                Slider(nameof(GlassSettings.Saturation), Texts.AppearanceGlassSaturation),
                Slider(nameof(GlassSettings.Tint), Texts.AppearanceGlassTint),
                Slider(nameof(GlassSettings.Brightness), Texts.AppearanceGlassBrightness),
            ]),
            new GlassGroup(Texts.AppearanceGlassGroupShadow,
            [
                Slider(nameof(GlassSettings.ShadowOpacity), Texts.AppearanceGlassShadowOpacity),
                Slider(nameof(GlassSettings.ShadowSpread), Texts.AppearanceGlassShadowSpread),
                Slider(nameof(GlassSettings.ShadowOffsetY), Texts.AppearanceGlassShadowOffset),
            ]),
        ];
        _sliders = [.. Groups.SelectMany(g => g.Sliders)];

        Sync(force: true);
    }

    public IReadOnlyList<GlassPresetItem> Presets { get; }

    /// <summary>Les quatre groupes de curseurs : Verre, Lumière, Couleur, Ombre.</summary>
    public IReadOnlyList<GlassGroup> Groups { get; }

    /// <summary>Les 16 curseurs, dans l'ordre de <see cref="GlassSettings.Parameters"/>.</summary>
    public IReadOnlyList<GlassSliderItem> Sliders => _sliders;

    /// <summary>« Personnalisé » : les réglages ne sont plus ceux d'un préréglage.</summary>
    public bool IsCustom => EffectivePreset() == GlassPresets.Custom;

    /// <summary>Le biseau en dôme (sinon en pilule).</summary>
    public bool IsDome => Resolve(_appearance()).Bevel == GlassBevel.Dome;

    public bool IsPill => !IsDome;

    /// <summary>Le repli « Réglages fins » : fermé par défaut.</summary>
    [ObservableProperty]
    private bool _showsDetails;

    [RelayCommand]
    private void ToggleDetails() => ShowsDetails = !ShowsDetails;

    /// <summary>Choisit un préréglage : écrit <c>new GlassAppearance(id)</c> et rien d'autre.</summary>
    public async Task SetPresetAsync(string id)
    {
        if (!GlassPresets.Ids.Contains(id))
            return;

        await _edit(a =>
        {
            // Clair est déjà le verre par défaut : sans choix enregistré, il n'y a rien à écrire.
            if (a.Glass is null && id == GlassPresets.Clair)
                return a;
            return a with { Glass = new GlassAppearance(id) };
        }).ConfigureAwait(true);
        Sync(force: true);
    }

    [RelayCommand]
    private Task SetPillAsync() => SetBevelAsync(GlassBevel.Pill);

    [RelayCommand]
    private Task SetDomeAsync() => SetBevelAsync(GlassBevel.Dome);

    /// <summary>Choisit la forme du biseau : le verre devient « personnalisé », avec les valeurs du moment.</summary>
    public async Task SetBevelAsync(GlassBevel bevel)
    {
        await _edit(a =>
        {
            var values = Resolve(a);
            if (values.Bevel == bevel)
                return a;
            return a with { Glass = new GlassAppearance(GlassPresets.Custom, values with { Bevel = bevel }) };
        }).ConfigureAwait(true);
        Sync(force: false);
    }

    /// <summary>
    /// Un curseur est lâché : les curseurs bougés sont enregistrés d'un coup (« personnalisé », avec toutes les valeurs), en un seul
    /// pas d'annulation. Sans déplacement, ou revenu à la même valeur, rien n'est écrit.
    /// </summary>
    public async Task CommitAsync()
    {
        var moved = _sliders.Where(s => s.IsDirty).ToDictionary(s => s.Id, s => s.Current);
        if (moved.Count == 0)
            return;

        await _edit(a =>
        {
            var current = Resolve(a);
            var values = current;
            foreach (var (name, value) in moved)
                values = With(values, name, value);
            return values == current ? a : a with { Glass = new GlassAppearance(GlassPresets.Custom, values) };
        }).ConfigureAwait(true);
        Sync(force: true);
    }

    /// <summary>
    /// Remet la section d'accord avec le fichier. Un curseur tiré (brouillon) n'est écarté que si le verre enregistré a changé depuis
    /// (Ctrl+Z, autre onglet, préréglage).
    /// </summary>
    internal void Sync(bool force)
    {
        var state = (Preset: EffectivePreset(), Values: Resolve(_appearance()));
        if (force || state != _synced)
        {
            _synced = state;
            foreach (var slider in _sliders)
                slider.Load(Get(state.Values, slider.Id));
        }

        foreach (var preset in Presets)
            preset.IsSelected = preset.Id == state.Preset;
        OnPropertyChanged(string.Empty);
    }

    private static GlassSettings Resolve(AppearanceSettings appearance) => (appearance.Glass ?? new GlassAppearance()).Resolve();

    /// <summary>Le préréglage montré comme choisi : celui du fichier, « personnalisé », ou Clair (aucun choix, ou préréglage inconnu : le verre est Clair).</summary>
    private string EffectivePreset()
    {
        var preset = _appearance().Glass?.Preset;
        if (GlassPresets.Find(preset) is not null)
            return GlassPresets.Ids.First(id => string.Equals(id, preset, StringComparison.OrdinalIgnoreCase));
        return string.Equals(preset, GlassPresets.Custom, StringComparison.OrdinalIgnoreCase) ? GlassPresets.Custom : GlassPresets.Clair;
    }

    private static float Get(GlassSettings s, string name) => name switch
    {
        nameof(GlassSettings.Blur) => s.Blur,
        nameof(GlassSettings.Refraction) => s.Refraction,
        nameof(GlassSettings.ZRadius) => s.ZRadius,
        nameof(GlassSettings.CornerRadius) => s.CornerRadius,
        nameof(GlassSettings.Distortion) => s.Distortion,
        nameof(GlassSettings.EdgeHighlight) => s.EdgeHighlight,
        nameof(GlassSettings.Specular) => s.Specular,
        nameof(GlassSettings.Fresnel) => s.Fresnel,
        nameof(GlassSettings.ChromaticAberration) => s.ChromaticAberration,
        nameof(GlassSettings.Opacity) => s.Opacity,
        nameof(GlassSettings.Saturation) => s.Saturation,
        nameof(GlassSettings.Tint) => s.Tint,
        nameof(GlassSettings.Brightness) => s.Brightness,
        nameof(GlassSettings.ShadowOpacity) => s.ShadowOpacity,
        nameof(GlassSettings.ShadowSpread) => s.ShadowSpread,
        nameof(GlassSettings.ShadowOffsetY) => s.ShadowOffsetY,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static GlassSettings With(GlassSettings s, string name, float value) => name switch
    {
        nameof(GlassSettings.Blur) => s with { Blur = value },
        nameof(GlassSettings.Refraction) => s with { Refraction = value },
        nameof(GlassSettings.ZRadius) => s with { ZRadius = value },
        nameof(GlassSettings.CornerRadius) => s with { CornerRadius = value },
        nameof(GlassSettings.Distortion) => s with { Distortion = value },
        nameof(GlassSettings.EdgeHighlight) => s with { EdgeHighlight = value },
        nameof(GlassSettings.Specular) => s with { Specular = value },
        nameof(GlassSettings.Fresnel) => s with { Fresnel = value },
        nameof(GlassSettings.ChromaticAberration) => s with { ChromaticAberration = value },
        nameof(GlassSettings.Opacity) => s with { Opacity = value },
        nameof(GlassSettings.Saturation) => s with { Saturation = value },
        nameof(GlassSettings.Tint) => s with { Tint = value },
        nameof(GlassSettings.Brightness) => s with { Brightness = value },
        nameof(GlassSettings.ShadowOpacity) => s with { ShadowOpacity = value },
        nameof(GlassSettings.ShadowSpread) => s with { ShadowSpread = value },
        nameof(GlassSettings.ShadowOffsetY) => s with { ShadowOffsetY = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
