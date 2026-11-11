using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Rendering;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La fenêtre : l'en-tête (onglets, pastille de l'écran), la bande en direct et le contenu de l'onglet choisi. Quand le
/// service est arrêté, un panneau remplace le contenu des onglets ; l'onglet choisi, lui, n'est pas perdu. Tant que
/// l'assistant de première ouverture n'est pas terminé, il remplace lui aussi le contenu des onglets (l'en-tête et la bande
/// restent).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;

    public MainViewModel(
        IServiceLink link, LayoutDocument document, EditorPreferences preferences,
        IFilePicker? picker = null, IReadOnlyList<string>? systemFonts = null, ISecretAccess? secrets = null, TimeProvider? time = null,
        string? preferencesPath = null, SystemServices? system = null)
    {
        _link = link;
        Session = new EditSession(document);
        LayoutTab = new LayoutTabViewModel(link, Session);
        AppearanceTab = new AppearanceTabViewModel(Session, picker, systemFonts ?? new SkiaSystemFonts().Families);
        ModulesTab = new ModulesTabViewModel(link, Session, secrets ?? new NoSecretAccess(), time);
        ShortcutsTab = new ShortcutsTabViewModel(link, Session);
        PreferencesStore = new PreferencesStore(preferences, preferencesPath);
        SettingsTab = new SettingsTabViewModel(
            link, Session, PreferencesStore, system ?? SystemServices.None, picker, time);
        Pill = new ScreenPillViewModel(link);
        FirstRun = new FirstRunViewModel(link, Session, PreferencesStore, SettingsTab, Pill, secrets ?? new NoSecretAccess(), time);
        _isFirstRunVisible = !preferences.FirstRunDone;
        Document = document;
        Tabs = [LayoutTab, AppearanceTab, ModulesTab, ShortcutsTab, SettingsTab];
        _selectedTab = Tabs[0];

        _link.SetRealTimePreview(preferences.RealTimePreview);
        // Le bandeau « Mode développeur actif » est dans tous les onglets : l'état du mode se lit dès que la liaison est établie.
        SettingsTab.Community.Watch();
        _link.Changed += Refresh;
        _link.FrameReceived += ShowFrame;
        Session.Changed += RefreshBanner;
        PreferencesStore.Changed += OnPreferencesChanged;
        Refresh();
        RefreshBanner();
    }

    public IServiceLink Link => _link;

    /// <summary>Les modifications de la disposition et leur historique, communs à tous les onglets (Ctrl+Z, Ctrl+Y).</summary>
    public EditSession Session { get; }

    /// <summary>L'onglet Disposition : le damier de la fenêtre travaille avec lui.</summary>
    public LayoutTabViewModel LayoutTab { get; }

    public AppearanceTabViewModel AppearanceTab { get; }
    public ModulesTabViewModel ModulesTab { get; }
    public ShortcutsTabViewModel ShortcutsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }
    public LayoutDocument Document { get; }

    /// <summary>La pastille de l'écran de l'en-tête (la première étape de l'assistant montre la même).</summary>
    public ScreenPillViewModel Pill { get; }

    /// <summary>L'assistant de première ouverture.</summary>
    public FirstRunViewModel FirstRun { get; }

    /// <summary>L'assistant remplace les onglets : vrai tant que <see cref="EditorPreferences.FirstRunDone"/> est faux. « Terminer » le ferme, « Relancer l'assistant » (Paramètres) le rouvre.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTabs), nameof(ShowsTabContent), nameof(ShowsFirstRun), nameof(IsGridVisible))]
    private bool _isFirstRunVisible;

    /// <summary>L'assistant est terminé : les onglets se choisissent.</summary>
    public bool ShowsTabs => !IsFirstRunVisible;

    /// <summary>Le contenu de l'onglet choisi s'affiche : le service tourne et l'assistant est terminé.</summary>
    public bool ShowsTabContent => ShowsTabs && !HidesContent;

    /// <summary>L'assistant s'affiche à la place des onglets : le service tourne et l'assistant n'est pas terminé.</summary>
    public bool ShowsFirstRun => IsFirstRunVisible && !HidesContent;

    // Le fichier des préférences a changé : l'assistant se rouvre (première étape) ou se ferme.
    private void OnPreferencesChanged()
    {
        var show = !PreferencesStore.Current.FirstRunDone;
        if (show == IsFirstRunVisible)
            return;
        if (show)
            FirstRun.Restart();
        IsFirstRunVisible = show;
        UpdateConsumptionVisibility();

        // L'assistant vient de se fermer : le refus d'un .avmod reçu pendant qu'il était là se montre maintenant.
        if (!show && _packageProblemDuringFirstRun is { } problem)
        {
            _packageProblemDuringFirstRun = null;
            ShowPackageProblem(problem);
        }
    }

    // Le refus d'un .avmod reçu pendant l'assistant de premier lancement (les onglets n'y sont pas) : montré à sa fermeture, pas perdu.
    private string? _packageProblemDuringFirstRun;

    private void ShowPackageProblem(string problem)
    {
        SelectedTab = SettingsTab;
        SettingsTab.Community.ShowPackageProblem(problem);
    }

    /// <summary>Les préférences de la fenêtre (aperçu en rendu réel, première ouverture), écrites dans <c>settings-app.json</c> quand elles changent.</summary>
    public PreferencesStore PreferencesStore { get; }

    public EditorPreferences Preferences => PreferencesStore.Current;
    public IReadOnlyList<TabViewModel> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridVisible))]
    private TabViewModel _selectedTab;

    // Un texte tapé dans l'onglet Modules et pas encore validé s'écrit quand on le quitte ; une capture de raccourci s'abandonne.
    partial void OnSelectedTabChanging(TabViewModel? oldValue, TabViewModel newValue)
    {
        if (ReferenceEquals(oldValue, newValue))
            return;
        if (oldValue is ModulesTabViewModel modules)
            _ = modules.CommitPendingAsync();
        else if (oldValue is ShortcutsTabViewModel shortcuts)
            shortcuts.CancelCapture();
    }

    partial void OnSelectedTabChanged(TabViewModel value)
    {
        RefreshBanner();
        // Les modules complémentaires se relisent quand l'onglet Paramètres s'ouvre (jusque-là, une seule lecture par connexion, faite par
        // Watch pour le bandeau du mode développeur : ni consommation ni minuteur).
        if (value is SettingsTabViewModel)
            SettingsTab.Community.Activate();
        UpdateConsumptionVisibility();
    }

    // La consommation des modules ne se mesure que si l'onglet Paramètres est vraiment sous les yeux : ni l'assistant ni un panneau
    // « service arrêté » ne le recouvre. (La fenêtre réduite est vue par SetPreviewPaused.)
    private void UpdateConsumptionVisibility() =>
        SettingsTab.Community.Consumption.SetTabShown(SelectedTab is SettingsTabViewModel && ShowsTabContent);

    /// <summary>
    /// <c>Trofeo.Settings.exe &lt;chemin.avmod&gt;</c> : l'onglet Paramètres s'ouvre et la boîte d'installation s'affiche dès que le service est
    /// joint. Le fichier est inspecté, rien ne s'installe sans le clic sur « Installer ».
    /// </summary>
    public void OpenPackage(string path)
    {
        if (!IsFirstRunVisible)
            SelectedTab = SettingsTab;
        SettingsTab.Community.OpenPackageWhenConnected(path);
    }

    /// <summary>
    /// La ligne de commande du premier lancement, ou les chemins qu'un second lancement lui passe : le fichier <c>.avmod</c> s'ouvre
    /// (<see cref="OpenPackage"/>) s'il est un fichier local existant ; sinon la section des modules dit pourquoi il ne s'ouvre pas
    /// (partage réseau, fichier introuvable). Sans argument <c>.avmod</c>, rien ne se passe. Rien ne s'installe sans le clic sur « Installer ».
    /// </summary>
    public void OpenPackageArguments(IEnumerable<string>? args)
    {
        var resolution = PackageArgument.Resolve(args);
        if (resolution.Path is { } path)
        {
            OpenPackage(path);
        }
        else if (resolution.Problem is { } problem)
        {
            if (IsFirstRunVisible)
                _packageProblemDuringFirstRun = problem;
            else
                ShowPackageProblem(problem);
        }
    }

    /// <summary>
    /// La ligne <c>Amber</c> sous la bande, dans tous les onglets : le message de la dernière modification (refus du
    /// service, écriture impossible) puis les avertissements du rechargement qui ne regardent pas l'apparence. Les autres
    /// avertissements sont sous le réglage concerné, dans l'onglet Personnalisation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveMessage))]
    private string? _saveMessage;

    public bool HasSaveMessage => !string.IsNullOrEmpty(SaveMessage);

    /// <summary>
    /// <c>layout.json</c> ne se lit pas : la ligne ambre en donne le motif et le bouton « Repartir de la disposition par
    /// défaut » est là. Aucune modification n'est écrite dans cet état.
    /// </summary>
    [ObservableProperty]
    private bool _canResetLayout;

    /// <summary>« Repartir de la disposition par défaut » : le fichier illisible est d'abord copié en <c>layout.json.avant-reglages</c>.</summary>
    [RelayCommand]
    private Task ResetLayoutAsync() => Session.ResetToDefaultAsync();

    /// <summary>Le service ne tourne pas : le panneau « Démarrer le service » remplace les onglets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridVisible), nameof(ShowsTabContent), nameof(ShowsFirstRun), nameof(HidesContent))]
    private bool _isStopped;

    /// <summary>
    /// Le service tourne mais parle une autre version que la fenêtre : un panneau le dit, sans bouton (relancer le service n'y changerait
    /// rien, et son mutex refuserait un second lancement).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridVisible), nameof(ShowsTabContent), nameof(ShowsFirstRun), nameof(HidesContent))]
    private bool _isVersionMismatch;

    /// <summary>Un panneau (service arrêté, versions différentes) remplace le contenu des onglets.</summary>
    public bool HidesContent => IsStopped || IsVersionMismatch;

    /// <summary>Le damier se pose sur la bande dans l'onglet Disposition, tant que le service tourne et que l'assistant n'est pas là.</summary>
    public bool IsGridVisible => SelectedTab is LayoutTabViewModel && !HidesContent && !IsFirstRunVisible;

    /// <summary>Le démarrage a échoué : Trofeo.Display.exe est introuvable.</summary>
    [ObservableProperty]
    private bool _startFailed;

    /// <summary>La dernière image de l'écran, décodée ; <c>null</c> tant qu'aucune n'est arrivée.</summary>
    [ObservableProperty]
    private Bitmap? _previewImage;

    /// <summary>Rotation que l'écran applique à cette image, en degrés.</summary>
    [ObservableProperty]
    private int _previewRotation;

    /// <summary>Fenêtre réduite : plus d'images à décoder ni à demander au service ; reprise à la restauration.</summary>
    public void SetPreviewPaused(bool paused)
    {
        _link.SetPreviewPaused(paused);
        SettingsTab.Community.Consumption.SetWindowShown(!paused); // réduite ou cachée, la fenêtre ne mesure plus rien non plus
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        StartFailed = false;
        StartFailed = !await _link.StartServiceAsync();
    }

    private void RefreshBanner()
    {
        var lines = new List<string>();
        // Le fichier illisible d'abord : c'est lui qui explique pourquoi rien ne s'enregistre.
        var unreadable = Document.LoadErrorMessage;
        CanResetLayout = unreadable is not null;
        if (unreadable is not null)
            lines.Add(unreadable);
        if (!string.IsNullOrEmpty(Session.Message) && Session.Message != unreadable)
            lines.Add(Session.Message);
        // Dans l'onglet Raccourcis, ceux que Windows a refusés sont déjà sur leur ligne.
        var onShortcuts = SelectedTab is ShortcutsTabViewModel;
        lines.AddRange(Session.OtherWarnings.Where(w => !(onShortcuts && ShortcutsTabViewModel.IsHotkeyFailure(w))));
        SaveMessage = lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private void Refresh()
    {
        IsStopped = _link.State == LinkState.ServiceStopped;
        IsVersionMismatch = _link.State == LinkState.VersionMismatch;
        if (!IsStopped)
            StartFailed = false;

        Pill.Update();
        UpdateConsumptionVisibility();
    }

    private void ShowFrame(PreviewFrame frame)
    {
        Bitmap decoded;
        try
        {
            using var stream = new MemoryStream(frame.Jpeg, writable: false);
            decoded = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return; // Une image abîmée est ignorée : la suivante arrive dans un quart de seconde.
        }

        var previous = PreviewImage;
        PreviewRotation = frame.Rotation;
        PreviewImage = decoded;
        previous?.Dispose();
    }

    public void Dispose()
    {
        _link.Changed -= Refresh;
        _link.FrameReceived -= ShowFrame;
        Session.Changed -= RefreshBanner;
        PreferencesStore.Changed -= OnPreferencesChanged;
        FirstRun.Dispose();
        LayoutTab.Dispose();
        AppearanceTab.Dispose();
        ModulesTab.Dispose();
        ShortcutsTab.Dispose();
        SettingsTab.Dispose();
        Session.Dispose();
        PreviewImage?.Dispose();
        PreviewImage = null;
    }
}
