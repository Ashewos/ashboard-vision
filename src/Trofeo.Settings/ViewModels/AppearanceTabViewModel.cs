using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Rendering;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// L'onglet Personnalisation : le thème, la transparence des panneaux, la police, le fond de l'écran, la garde de
/// lisibilité et le glissement entre les vues. Chaque changement est une modification de l'apparence enregistrée par <see cref="EditSession"/> (le même
/// historique que la disposition, donc Ctrl+Z l'annule) ; rien d'autre du fichier n'est touché. Les avertissements du
/// rechargement s'affichent sous le réglage concerné. Tout se passe sur le fil de l'interface.
/// </summary>
public sealed partial class AppearanceTabViewModel : TabViewModel, IDisposable
{
    /// <summary>Huit teintes moyennes à sombres (rouge, orange, olive, vert, sarcelle, bleu, violet, rose), pour que les modules restent lisibles dessus ; la couleur exacte se tape dans le champ.</summary>
    private static readonly string[] PaletteHexes =
        ["#7A2A31", "#7A4A14", "#6B6417", "#1F6B3E", "#14696B", "#24498A", "#5A33A0", "#85305F"];

    private static readonly Regex HexColor = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private readonly EditSession _session;
    private IFilePicker? _picker;
    private bool _refreshing;

    // Ce que la fenêtre montrait la dernière fois que le fichier a été lu : on ne touche aux brouillons que si cela change.
    private (float? Opacity, string Theme) _syncedOpacity;
    private BackgroundSettings? _syncedBackground;
    private BackgroundKind? _draftKind;
    private bool _opacityDirty;
    private double _opacityPercent;
    private string _colorText = "";

    public AppearanceTabViewModel(EditSession session, IFilePicker? picker = null, IReadOnlyList<string>? systemFonts = null)
        : base(Texts.TabAppearance)
    {
        _session = session;
        _picker = picker;

        Themes =
        [
            new ThemeCard("classique", Texts.AppearanceThemeClassic, Texts.AppearanceThemeClassicText, () => SetThemeAsync("classique")),
            new ThemeCard("liquid-glass", Texts.AppearanceThemeLiquidGlass, Texts.AppearanceThemeLiquidGlassText, () => SetThemeAsync("liquid-glass")),
            new ThemeCard("pixel", Texts.AppearanceThemePixel, Texts.AppearanceThemePixelText, () => SetThemeAsync("pixel")),
        ];

        List<FontItem> fonts = [new FontItem(Texts.AppearanceFromThemeFeminine, null, isThemeDefault: true)];
        fonts.AddRange(EmbeddedFonts.Families.Select(name => new FontItem(name, name)));
        var system = (systemFonts ?? []).Where(name => !EmbeddedFonts.Families.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (system.Count > 0)
        {
            fonts.Add(new FontItem("", null, isSeparator: true));
            fonts.AddRange(system.Select(name => new FontItem(name, name)));
        }

        Fonts = fonts;

        Kinds =
        [
            new BackgroundKindItem(BackgroundKind.Theme, Texts.AppearanceFromThemeMasculine, () => SetBackgroundKindAsync(BackgroundKind.Theme)),
            new BackgroundKindItem(BackgroundKind.Color, Texts.AppearanceBackgroundColor, () => SetBackgroundKindAsync(BackgroundKind.Color)),
            new BackgroundKindItem(BackgroundKind.Image, Texts.AppearanceBackgroundPhoto, () => SetBackgroundKindAsync(BackgroundKind.Image)),
            new BackgroundKindItem(BackgroundKind.Animated, Texts.AppearanceBackgroundGif, () => SetBackgroundKindAsync(BackgroundKind.Animated)),
            new BackgroundKindItem(BackgroundKind.Video, Texts.AppearanceBackgroundVideo, () => SetBackgroundKindAsync(BackgroundKind.Video)),
        ];

        Palette = [.. PaletteHexes.Select(hex => new SwatchItem(hex, () => SetColorAsync(hex)))];

        _session.Changed += Refresh;
        Sync(force: true);
    }

    public void Dispose() => _session.Changed -= Refresh;

    /// <summary>Le sélecteur de fichier de la fenêtre (posé par la vue, qui connaît sa fenêtre) ; <c>null</c> : pas de choix de fichier.</summary>
    public IFilePicker? Picker
    {
        get => _picker;
        set
        {
            _picker = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPickFile));
        }
    }

    private AppearanceSettings Appearance => _session.Document.Current.Appearance ?? AppearanceSettings.Default;

    // ---- Thème ----

    public IReadOnlyList<ThemeCard> Themes { get; }

    [RelayCommand]
    public Task SetThemeAsync(string id) =>
        Themes.All(t => t.Id != id) ? Task.CompletedTask : EditAsync(a => a with { Theme = id });

    // ---- Transparence ----

    /// <summary>La transparence montrée par le curseur, de 0 à 100. La changer ne sauve rien : <see cref="CommitOpacityAsync"/> le fait, au lâcher.</summary>
    public double OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), 0, 100);
            if (rounded == _opacityPercent)
                return;
            _opacityPercent = rounded;
            if (!_refreshing)
                _opacityDirty = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityText));
        }
    }

    public string OpacityText => string.Format(Texts.AppearanceOpacityValue, _opacityPercent);

    /// <summary>Aucune transparence imposée : celle du thème.</summary>
    public bool IsOpacityFromTheme => Appearance.PanelOpacity is null;

    /// <summary>Le curseur est lâché : la valeur est enregistrée, en un seul pas d'annulation. Sans déplacement, rien n'est écrit.</summary>
    public async Task CommitOpacityAsync()
    {
        if (!_opacityDirty)
            return;
        _opacityDirty = false;
        var opacity = (float)(_opacityPercent / 100.0);
        await EditAsync(a => a with { PanelOpacity = opacity }).ConfigureAwait(true);
        Sync(force: false);
    }

    /// <summary>« Celle du thème » : <c>PanelOpacity</c> redevient <c>null</c>.</summary>
    [RelayCommand]
    public async Task UseThemeOpacityAsync()
    {
        _opacityDirty = false;
        await EditAsync(a => a with { PanelOpacity = null }).ConfigureAwait(true);
        Sync(force: true);
    }

    // ---- Police ----

    /// <summary>« Celle du thème », les polices embarquées, un séparateur, puis les polices du système.</summary>
    public IReadOnlyList<FontItem> Fonts { get; }

    /// <summary>
    /// La police en cours ; <c>null</c> si elle n'est pas dans la liste (non installée). Lecture seule : la liste suit le
    /// focus (les flèches déplacent la sélection), donc choisir une ligne n'enregistre pas ; la vue appelle
    /// <see cref="SetFontAsync"/> au clic ou à Entrée.
    /// </summary>
    public FontItem? SelectedFont
    {
        get
        {
            var font = Appearance.Font;
            if (string.IsNullOrWhiteSpace(font))
                return Fonts[0];
            return Fonts.FirstOrDefault(f => f.IsChoice && f.Value is not null && string.Equals(f.Value, font.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task SetFontAsync(FontItem item) =>
        item.IsSeparator ? Task.CompletedTask : EditAsync(a => a with { Font = item.Value });

    // ---- Fond ----

    public IReadOnlyList<BackgroundKindItem> Kinds { get; }

    public IReadOnlyList<SwatchItem> Palette { get; }

    /// <summary>La sorte de fond montrée : celle qu'on vient de choisir (sans fichier ni couleur encore), sinon celle du fichier.</summary>
    public BackgroundKind BackgroundKind => _draftKind ?? Appearance.Background?.Kind ?? BackgroundKind.Theme;

    public bool ShowsColor => BackgroundKind == BackgroundKind.Color;
    public bool ShowsFile => BackgroundKind is BackgroundKind.Image or BackgroundKind.Animated or BackgroundKind.Video;
    public bool ShowsVideoAdvice => BackgroundKind == BackgroundKind.Video;
    public bool CanPickFile => Picker is not null;

    /// <summary>Le nom du fichier choisi (sans son dossier) ; <c>null</c> si cette sorte de fond n'en a pas encore.</summary>
    public string? FileName
    {
        get
        {
            if (Appearance.Background is not { Path: { Length: > 0 } path } background || background.Kind != BackgroundKind)
                return null;
            // Le chemin peut avoir été écrit sous Windows : on coupe aux deux sortes de barres.
            return path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        }
    }

    /// <summary>Le sélecteur de fichier n'a pas pu s'ouvrir (message en français) ; <c>null</c> sinon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileError))]
    private string? _fileError;

    public bool HasFileError => !string.IsNullOrEmpty(FileError);

    public bool HasFileName => FileName is not null;
    public string FileLabel => FileName ?? Texts.AppearanceNoFile;

    /// <summary>Le texte du champ #RRGGBB.</summary>
    public string ColorText
    {
        get => _colorText;
        set
        {
            if (_colorText == value)
                return;
            _colorText = value;
            ColorError = null;
            OnPropertyChanged();
        }
    }

    /// <summary>« Couleur invalide : écris-la comme #1A2B3C. » ; <c>null</c> si le champ est bon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColorError))]
    private string? _colorError;

    public bool HasColorError => !string.IsNullOrEmpty(ColorError);

    /// <summary>
    /// Choisit la sorte de fond. « Celui du thème » s'enregistre tout de suite ; les autres montrent d'abord leur réglage
    /// (palette, ou bouton « Choisir un fichier… ») : le fond ne change qu'avec la couleur ou le fichier choisis.
    /// </summary>
    public async Task SetBackgroundKindAsync(BackgroundKind kind)
    {
        if (kind == BackgroundKind.Theme)
        {
            _draftKind = null;
            await EditAsync(a => a with { Background = null }).ConfigureAwait(true);
        }
        else
        {
            _draftKind = kind;
        }

        FileError = null;
        Refresh();
    }

    /// <summary>Valide le champ #RRGGBB (avec ou sans #, majuscules ou non) puis enregistre la couleur.</summary>
    public async Task SetColorTextAsync(string? text)
    {
        ColorText = text ?? "";
        var trimmed = (text ?? "").Trim();
        if (!HexColor.IsMatch(trimmed))
        {
            ColorError = Texts.AppearanceBadColor;
            return;
        }

        await SetColorAsync("#" + trimmed.TrimStart('#').ToUpperInvariant()).ConfigureAwait(true);
    }

    /// <summary>Entrée ou perte du focus dans le champ : un champ resté vide n'est pas une erreur (l'utilisateur est passé dessus).</summary>
    [RelayCommand]
    public Task ApplyColorTextAsync() =>
        string.IsNullOrWhiteSpace(ColorText) ? Task.CompletedTask : SetColorTextAsync(ColorText);

    public async Task SetColorAsync(string hex)
    {
        ColorError = null;
        await EditAsync(a => a with { Background = new BackgroundSettings(BackgroundKind.Color, Color: hex) }).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Ouvre le sélecteur de fichier pour la sorte de fond montrée (extensions de <see cref="BackgroundFiles"/>) puis enregistre le chemin.</summary>
    [RelayCommand]
    public async Task ChooseFileAsync()
    {
        var kind = BackgroundKind;
        if (Picker is not { } picker || !ShowsFile)
            return;

        FileError = null;
        string? path;
        try
        {
            path = await picker.PickAsync(BackgroundFiles.Title(kind), BackgroundFiles.TypeName(kind), BackgroundFiles.Extensions(kind)).ConfigureAwait(true);
        }
        catch (FilePickerException ex)
        {
            FileError = ex.Message; // sélecteur impossible à ouvrir : dit en français sous le bouton
            return;
        }

        if (string.IsNullOrEmpty(path))
            return; // l'utilisateur a annulé

        await EditAsync(a => a with { Background = new BackgroundSettings(kind, Path: path) }).ConfigureAwait(true);
        Refresh();
    }

    // ---- Garde de lisibilité ----

    /// <summary>Allumée par défaut : <c>Legibility</c> absent ou vrai.</summary>
    public bool IsLegibilityOn
    {
        get => Appearance.Legibility != false;
        set
        {
            if (_refreshing || value == IsLegibilityOn)
                return;
            _ = SetLegibilityAsync(value);
        }
    }

    public Task SetLegibilityAsync(bool on) => EditAsync(a => a with { Legibility = on });

    // ---- Passage entre les vues ----

    /// <summary>Allumé par défaut : les vues glissent l'une vers l'autre quand l'écran change de vue.</summary>
    public bool IsAnimateViewSwitchOn
    {
        get => Appearance.AnimateViewSwitch;
        set
        {
            if (_refreshing || value == IsAnimateViewSwitchOn)
                return;
            _ = SetAnimateViewSwitchAsync(value);
        }
    }

    public Task SetAnimateViewSwitchAsync(bool on) => EditAsync(a => a with { AnimateViewSwitch = on });

    // ---- Avertissements du rechargement, sous le réglage concerné ----

    public string ThemeWarning => Lines(_session.AppearanceWarnings.Theme);
    public string OpacityWarning => Lines(_session.AppearanceWarnings.Opacity);
    public string FontWarning => Lines(_session.AppearanceWarnings.Font);
    public string BackgroundWarning => Lines(_session.AppearanceWarnings.Background);
    public bool HasThemeWarning => ThemeWarning.Length > 0;
    public bool HasOpacityWarning => OpacityWarning.Length > 0;
    public bool HasFontWarning => FontWarning.Length > 0;
    public bool HasBackgroundWarning => BackgroundWarning.Length > 0;

    private static string Lines(IReadOnlyList<string> warnings) => string.Join('\n', warnings);

    // ---- Modifications et état ----

    /// <summary>
    /// Une modification de l'apparence, calculée sur celle du moment. Rien n'est écrit si elle ne change rien ; revenir à
    /// « aucun réglage » retire la clé <c>Appearance</c> du fichier.
    /// </summary>
    private Task<SaveOutcome?> EditAsync(Func<AppearanceSettings, AppearanceSettings> change) =>
        _session.ApplyAsync(layout =>
        {
            var current = layout.Appearance ?? AppearanceSettings.Default;
            var next = change(current);
            if (next == current)
                return null;
            return layout with { Appearance = next == AppearanceSettings.Default ? null : next };
        });

    private void Refresh() => Sync(force: false);

    /// <summary>
    /// Remet la fenêtre d'accord avec le fichier. Un brouillon (curseur tiré, champ de couleur tapé, sorte de fond choisie
    /// sans fichier) n'est écarté que si le réglage enregistré a changé depuis.
    /// </summary>
    private void Sync(bool force)
    {
        var appearance = Appearance;
        var themeOpacity = ThemeOpacity(appearance.Theme);
        var opacityState = (appearance.PanelOpacity, appearance.Theme);
        var opacityChanged = force || opacityState != _syncedOpacity;
        var backgroundChanged = force || !Equals(appearance.Background, _syncedBackground);

        _refreshing = true;
        try
        {
            if (opacityChanged)
            {
                _syncedOpacity = opacityState;
                _opacityDirty = false;
                _opacityPercent = Math.Round((appearance.PanelOpacity ?? themeOpacity) * 100.0);
            }

            if (backgroundChanged)
            {
                _syncedBackground = appearance.Background;
                _draftKind = null;
                _colorText = appearance.Background is { Kind: BackgroundKind.Color, Color: { } color } ? color : "";
                ColorError = null;
            }

            foreach (var card in Themes)
                card.IsSelected = string.Equals(card.Id, appearance.Theme, StringComparison.OrdinalIgnoreCase);
            foreach (var kind in Kinds)
                kind.IsSelected = kind.Kind == BackgroundKind;
            foreach (var swatch in Palette)
                swatch.IsSelected = appearance.Background is { Kind: BackgroundKind.Color, Color: { } hex }
                                    && string.Equals(hex, swatch.Hex, StringComparison.OrdinalIgnoreCase);

            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static float ThemeOpacity(string themeId) =>
        Trofeo.Core.Rendering.Themes.Find(themeId)?.Theme.PanelOpacity ?? Trofeo.Core.Rendering.Themes.ClassiqueDefinition.Theme.PanelOpacity;
}
