using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// L'onglet Paramètres : l'orientation de l'écran, le démarrage avec Windows, le plafond d'images par seconde, l'aperçu en
/// rendu réel, l'installation du pilote des températures, la consommation, l'export et l'import d'une disposition, la
/// version, les mises à jour (le canal Stable ou Bêta, la recherche, l'installation), et de quoi relancer l'assistant de première ouverture. Ce qui touche à la disposition (orientation, images par seconde, import) passe par <see cref="EditSession"/> : le
/// même historique que les autres onglets. L'orientation et l'import s'enregistrent tout de suite ; le curseur, au lâcher.
/// </summary>
public sealed partial class SettingsTabViewModel : TabViewModel, IDisposable
{
    private const int MaxFpsOfScreen = 12;
    private const double Megabyte = 1024 * 1024;

    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly PreferencesStore _preferences;
    private readonly IStartupRegistration _startup;
    private readonly IElevatedLauncher _launcher;
    private readonly Func<string?> _locateHelper;
    private readonly Func<long> _ownMemory;
    private readonly string? _displayExe;

    private bool _startupEnabled;
    private string? _startupMessage;
    private double _maxFps = MaxFpsOfScreen;
    private bool _maxFpsDirty;
    private bool _installing;
    private string? _sensorsMessage;
    private bool _sensorsMessageIsError;
    private string? _shareMessage;
    private bool _shareMessageIsError;
    private bool _updateBusy;
    private string? _updateActionMessage;
    private LinkUpdateState? _updateActionState;
    private DowngradeDialogViewModel? _downgrade;
    private UpdateInfo? _seenUpdate;

    public SettingsTabViewModel(
        IServiceLink link, EditSession session, PreferencesStore preferences, SystemServices system, IFilePicker? picker = null)
        : base(Texts.TabSettings)
    {
        _link = link;
        _session = session;
        _preferences = preferences;
        _startup = system.Startup;
        _launcher = system.Launcher;
        Picker = picker;
        Community = new CommunitySectionViewModel(link, session, () => Picker);
        _locateHelper = system.LocateHelper ?? (() => AppLocator.Find("Trofeo.SensorHelper.exe"));
        _ownMemory = system.OwnMemory ?? OwnPrivateBytes;

        _displayExe = (system.LocateDisplay ?? (() => AppLocator.Find("Trofeo.Display.exe")))();
        _startupEnabled = _startup.IsEnabled;
        _startupMessage = CanToggleStartup ? null : Texts.SettingsStartupMissing;

        Sync();
        _link.Changed += Refresh;
        _session.Changed += Refresh;
    }

    public void Dispose()
    {
        _link.Changed -= Refresh;
        _session.Changed -= Refresh;
        Community.Dispose();
    }

    /// <summary>La section « Modules complémentaires » : la liste, l'installation, la validation, les mises à jour.</summary>
    public CommunitySectionViewModel Community { get; }

    /// <summary>Le sélecteur de fichier de la fenêtre (export, import, installeur de PawnIO) ; la vue le pose à l'ouverture.</summary>
    public IFilePicker? Picker { get; set; }

    private static long OwnPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private void Refresh()
    {
        Sync();
        OnPropertyChanged(nameof(SensorsText));
        OnPropertyChanged(nameof(CanInstallSensors));
        OnPropertyChanged(nameof(ServiceUsageText));
        OnPropertyChanged(nameof(OwnUsageText));
        OnPropertyChanged(nameof(VersionText));
        RefreshUpdates();
        if (SensorsAreActive && _sensorsMessage is not null)
            ReportSensors(null, isError: false); // l'installation a abouti : le message n'a plus lieu d'être
    }

    // ---- Orientation ----

    public bool IsUpright => _session.Document.Current.Rotation == 0;
    public bool IsFlipped => _session.Document.Current.Rotation == 180;

    public Task SetRotationAsync(int degrees) =>
        _session.ApplyAsync(layout => layout.Rotation == degrees ? null : layout with { Rotation = degrees });

    [RelayCommand]
    private Task SetUprightAsync() => SetRotationAsync(0);

    [RelayCommand]
    private Task SetFlippedAsync() => SetRotationAsync(180);

    // ---- Démarrer avec Windows ----

    /// <summary>Trofeo.Display.exe est trouvé (ou déjà inscrit, et on peut le retirer) : l'interrupteur marche.</summary>
    public bool CanToggleStartup => _displayExe is not null || _startupEnabled;

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (value == _startupEnabled)
                return;

            if (value && _displayExe is null)
            {
                SetStartupMessage(Texts.SettingsStartupMissing);
                OnPropertyChanged(); // l'interrupteur revient à sa place
                return;
            }

            try
            {
                if (value)
                    _startup.Enable(_displayExe!);
                else
                    _startup.Disable();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                SetStartupMessage(string.Format(Texts.SettingsStartupFailed, ex.Message));
                OnPropertyChanged();
                return;
            }

            _startupEnabled = value;
            SetStartupMessage(CanToggleStartup ? null : Texts.SettingsStartupMissing);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggleStartup));
        }
    }

    public string? StartupMessage => _startupMessage;
    public bool HasStartupMessage => !string.IsNullOrEmpty(_startupMessage);

    private void SetStartupMessage(string? message)
    {
        _startupMessage = message;
        OnPropertyChanged(nameof(StartupMessage));
        OnPropertyChanged(nameof(HasStartupMessage));
    }

    // ---- Images par seconde ----

    /// <summary>
    /// Le plafond montré par le curseur, de 1 à 12. Le tirer n'enregistre rien : <see cref="CommitMaxFpsAsync"/> le fait, au
    /// lâcher, en un seul pas d'annulation.
    /// </summary>
    public double MaxFpsValue
    {
        get => _maxFps;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), 1, MaxFpsOfScreen);
            if (rounded == _maxFps)
                return;
            _maxFps = rounded;
            _maxFpsDirty = rounded != SavedMaxFps;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaxFpsText));
        }
    }

    public string MaxFpsText => _maxFps >= MaxFpsOfScreen
        ? Texts.SettingsFpsMax
        : ((int)_maxFps).ToString(CultureInfo.CurrentCulture);

    private int SavedMaxFps => _session.Document.Current.MaxFps ?? MaxFpsOfScreen;

    /// <summary>Le curseur est lâché : la valeur est enregistrée. Le maximum de l'écran (12) est le plafond du service : il n'écrit rien dans le fichier.</summary>
    public async Task CommitMaxFpsAsync()
    {
        if (!_maxFpsDirty)
            return;
        int? wanted = _maxFps >= MaxFpsOfScreen ? null : (int)_maxFps;
        try
        {
            // Le curseur reste « tiré » jusqu'à la fin : la session prévient les onglets avant d'avoir écrit, et ils ne doivent pas le ramener en arrière.
            await _session.ApplyAsync(layout => layout.MaxFps == wanted ? null : layout with { MaxFps = wanted }).ConfigureAwait(true);
        }
        finally
        {
            _maxFpsDirty = false;
            Sync();
        }
    }

    // ---- Aperçu ----

    public bool RealTimePreview
    {
        get => _preferences.Current.RealTimePreview;
        set
        {
            if (value == _preferences.Current.RealTimePreview)
                return;
            _preferences.Update(p => p with { RealTimePreview = value });
            _link.SetRealTimePreview(value);
            OnPropertyChanged();
        }
    }

    // ---- Températures du processeur ----

    private SensorInfo? Sensors => _link.Status?.Sensors;

    private bool SensorsAreActive => Sensors is { PawnIoInstalled: true, HelperInstalled: true, HelperConnected: true };

    /// <summary>L'état des capteurs en une phrase.</summary>
    public string SensorsText => Sensors switch
    {
        null => "",
        { PawnIoInstalled: false } => Texts.SensorsNoDriver,
        { HelperInstalled: true, HelperConnected: true } => Texts.SensorsActive,
        _ => Texts.SensorsNoHelper,
    };

    public bool CanInstallSensors => Sensors is not null && !SensorsAreActive && !_installing;

    public string? SensorsMessage => _sensorsMessage;
    public bool HasSensorsMessage => !string.IsNullOrEmpty(_sensorsMessage);
    public bool SensorsMessageIsError => _sensorsMessageIsError;

    /// <summary>
    /// « Installer le pilote des températures » : si PawnIO manque, demande d'abord son installeur, puis lance
    /// <c>Trofeo.SensorHelper.exe install</c> en administrateur (Windows demande l'autorisation).
    /// </summary>
    [RelayCommand]
    public async Task InstallSensorsAsync()
    {
        if (_installing || Sensors is not { } sensors)
            return;

        ReportSensors(null, isError: false);
        if (_locateHelper() is not { } helper)
        {
            ReportSensors(Texts.SensorsHelperMissing, isError: true);
            return;
        }

        _installing = true;
        OnPropertyChanged(nameof(CanInstallSensors));
        try
        {
            var arguments = "install";
            if (!sensors.PawnIoInstalled)
            {
                if (Picker is not { } picker)
                    return;
                string? setup;
                try
                {
                    setup = await picker.PickAsync(Texts.SensorsPickTitle, Texts.SensorsPickType, ["exe"]).ConfigureAwait(true);
                }
                catch (FilePickerException ex)
                {
                    ReportSensors(ex.Message, isError: true);
                    return;
                }

                if (string.IsNullOrEmpty(setup))
                    return; // l'utilisateur a annulé
                arguments = $"install --pawnio \"{setup}\"";
            }

            bool accepted;
            try
            {
                accepted = _launcher.Launch(helper, arguments);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                ReportSensors(string.Format(Texts.SensorsLaunchFailed, ex.Message), isError: true);
                return;
            }

            ReportSensors(accepted ? Texts.SensorsStarted : Texts.SensorsDenied, isError: !accepted);
        }
        finally
        {
            _installing = false;
            OnPropertyChanged(nameof(CanInstallSensors));
        }
    }

    private void ReportSensors(string? message, bool isError)
    {
        _sensorsMessage = message;
        _sensorsMessageIsError = isError;
        OnPropertyChanged(nameof(SensorsMessage));
        OnPropertyChanged(nameof(HasSensorsMessage));
        OnPropertyChanged(nameof(SensorsMessageIsError));
    }

    // ---- Consommation ----

    /// <summary>« Service d'affichage : 44 Mo, 0,8 % du processeur ».</summary>
    public string ServiceUsageText => _link.Status?.Process is { } process
        ? string.Format(Texts.UsageService, Megabytes(process.PrivateBytes), Percent(process.CpuPercent))
        : "";

    /// <summary>« Réglages : 96 Mo, libérés à la fermeture » : la mémoire de cette fenêtre.</summary>
    public string OwnUsageText => string.Format(Texts.UsageOwn, Megabytes(_ownMemory()));

    private static string Megabytes(long bytes) =>
        ((long)Math.Round(bytes / Megabyte, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Une décimale et la virgule française, quelle que soit la langue de Windows.</summary>
    private static string Percent(double percent) =>
        percent.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    // ---- Partager ----

    public string? ShareMessage => _shareMessage;
    public bool HasShareMessage => !string.IsNullOrEmpty(_shareMessage);
    public bool ShareMessageIsError => _shareMessageIsError;

    private void ReportShare(string? message, bool isError = false)
    {
        _shareMessage = message;
        _shareMessageIsError = isError;
        OnPropertyChanged(nameof(ShareMessage));
        OnPropertyChanged(nameof(HasShareMessage));
        OnPropertyChanged(nameof(ShareMessageIsError));
    }

    /// <summary>« Exporter la disposition… » : écrit <see cref="LayoutTransfer.Export"/> dans un fichier <c>.trofeo.json</c>.</summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        if (Picker is not { } picker)
            return;
        ReportShare(null);

        string? path;
        try
        {
            path = await picker.SaveAsync(Texts.ShareExportTitle, Texts.ShareFileType, "trofeo.json", "disposition.trofeo.json").ConfigureAwait(true);
        }
        catch (FilePickerException ex)
        {
            ReportShare(ex.Message, isError: true);
            return;
        }

        if (string.IsNullOrEmpty(path))
            return;
        if (!Path.HasExtension(path))
            path += ".trofeo.json";

        try
        {
            File.WriteAllText(path, LayoutTransfer.Export(_session.Document.Current));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ReportShare(string.Format(Texts.ShareWriteFailed, ex.Message), isError: true);
            return;
        }

        ReportShare(string.Format(Texts.ShareExported, Path.GetFileName(path)));
    }

    /// <summary>
    /// « Importer une disposition… » : valide le fichier (comme le service au rechargement, types et tailles minimum
    /// compris), garde la disposition en cours dans <c>layout.json.avant-import</c>, puis enregistre. Un fichier refusé
    /// affiche son motif et ne change rien. L'import est une seule modification, que Ctrl+Z défait. L'orientation de ce PC est gardée,
    /// de même que les réglages des modules dont le schéma a un champ secret (voir <see cref="KeepLocalSecretSettings"/>).
    /// </summary>
    [RelayCommand]
    public async Task ImportAsync()
    {
        if (Picker is not { } picker)
            return;
        ReportShare(null);

        string? path;
        try
        {
            path = await picker.PickAsync(Texts.ShareImportTitle, Texts.ShareFileType, ["json"]).ConfigureAwait(true);
        }
        catch (FilePickerException ex)
        {
            ReportShare(ex.Message, isError: true);
            return;
        }

        if (string.IsNullOrEmpty(path))
            return;

        string json;
        try
        {
            json = File.ReadAllText(path); // un petit fichier : lu tout de suite, sans quitter le fil de l'interface
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ReportShare(string.Format(Texts.ShareReadFailed, ex.Message), isError: true);
            return;
        }

        var result = LayoutTransfer.Import(json);
        if (result.Layout is not { } imported)
        {
            ReportShare(result.Error, isError: true);
            return;
        }

        if (Unsupported(imported) is { } reason)
        {
            ReportShare(reason, isError: true);
            return;
        }

        string? backupError = null;
        IReadOnlyList<string> kept = [];
        var outcome = await _session.ApplyAsync(current =>
        {
            // La copie se fait au moment d'écrire : c'est bien la disposition en cours, même si le fichier a été relu entre-temps.
            try
            {
                if (File.Exists(_session.Document.FilePath))
                    File.Copy(_session.Document.FilePath, _session.Document.FilePath + ".avant-import", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                backupError = ex.Message;
                return null;
            }

            // Le sens de l'écran tient au montage de CE PC, pas à celui de l'ami qui a exporté : l'orientation reste la mienne.
            // Un fichier reçu ne choisit pas où part une clé de ce PC : les réglages des modules à secret restent ceux de ce PC.
            var (protectedLayout, keptNames) = KeepLocalSecretSettings(imported, current);
            kept = keptNames;
            return protectedLayout with { Rotation = current.Rotation };
        }).ConfigureAwait(true);

        if (backupError is not null)
            ReportShare(string.Format(Texts.ShareWriteFailed, backupError), isError: true);
        else if (outcome is null || outcome is { Saved: true, Message: null })
            ReportShare(kept.Count == 0
                ? Texts.ShareImported
                : $"{Texts.ShareImported} {string.Format(Texts.ShareKeptSettings, string.Join(", ", kept))}");
        // Sinon (fichier changé ailleurs, refus du service) : la ligne Amber de la fenêtre en dit le motif.
    }

    /// <summary>
    /// Pour chaque type de module dont le schéma déclare un <see cref="SecretField"/> (une clé du coffre de CE PC) ou un
    /// <see cref="PortField"/> (un port de CE PC que le service relaie à un module qui a la permission « Cet ordinateur »), garde les
    /// réglages de ce PC : une instance importée reprend ceux de l'instance locale du même type (dans l'ordre, emplacements puis
    /// modules retirés), ou <c>{}</c> si ce PC n'en a pas assez. Sans quoi le fichier d'un tiers fixerait où la clé est envoyée
    /// (l'hôte de la console UniFi, par exemple). Sans le catalogue du service, aucun type n'est connu pour sûr : tous gardent
    /// ceux de ce PC. Rend la disposition protégée et les noms des modules concernés (ceux qui sont dans le fichier).
    /// </summary>
    private (DashboardLayout Layout, IReadOnlyList<string> Kept) KeepLocalSecretSettings(DashboardLayout imported, DashboardLayout current)
    {
        var catalog = _link.KnownTypes()?.GroupBy(t => t.TypeId).ToDictionary(g => g.Key, g => g.First());
        bool TouchesASecret(string typeId) =>
            catalog is null || (catalog.TryGetValue(typeId, out var type) && type.Schema.Fields.Any(field => field is SecretField or PortField));

        var local = current.Placements.Concat(current.Shelf ?? []).ToList();
        var taken = new Dictionary<string, int>(); // combien d'instances de chaque type ont déjà repris les réglages locaux
        var keptTypes = new List<string>();
        var empty = JsonDocument.Parse("{}").RootElement.Clone();

        ModulePlacement Protect(ModulePlacement placement)
        {
            if (!TouchesASecret(placement.TypeId))
                return placement;

            if (!keptTypes.Contains(placement.TypeId))
                keptTypes.Add(placement.TypeId);
            taken.TryGetValue(placement.TypeId, out var index);
            taken[placement.TypeId] = index + 1;
            var own = local.Where(p => p.TypeId == placement.TypeId).Skip(index).FirstOrDefault();
            return placement with { Settings = own?.Settings ?? empty };
        }

        var protectedLayout = imported with
        {
            Placements = [.. imported.Placements.Select(Protect)],
            Shelf = imported.Shelf is null ? null : [.. imported.Shelf.Select(Protect)],
        };
        var names = keptTypes.Select(id => catalog?.GetValueOrDefault(id)?.Label() ?? id).ToList();
        return (protectedLayout, names);
    }

    /// <summary>Ce que le service ne saurait pas appliquer : un type de module inconnu, ou un module plus petit que son minimum.</summary>
    private string? Unsupported(DashboardLayout layout)
    {
        if (_link.KnownTypes() is not { } known)
            return null; // sans le catalogue du service, il le dira au rechargement
        var types = known.GroupBy(t => t.TypeId).ToDictionary(g => g.Key, g => g.First());

        foreach (var placement in layout.Placements.Concat(layout.Shelf ?? []))
        {
            if (!types.ContainsKey(placement.TypeId))
                return string.Format(Texts.ShareUnknownType, placement.TypeId);
        }

        foreach (var placement in layout.Placements)
        {
            var type = types[placement.TypeId];
            if (placement.Columns < type.MinSize.Columns || placement.Rows < type.MinSize.Rows)
                return string.Format(Texts.ShareTooSmall, type.Label(), type.MinSize.Columns, type.MinSize.Rows);
        }

        return null;
    }

    // ---- Assistant de première ouverture ----

    /// <summary>
    /// « Relancer l'assistant » : remet <see cref="EditorPreferences.FirstRunDone"/> à faux. La fenêtre rouvre l'assistant à la
    /// première étape (et il se rouvrirait aussi au prochain lancement s'il n'est pas terminé).
    /// </summary>
    [RelayCommand]
    private void RestartFirstRun() => _preferences.Update(p => p with { FirstRunDone = false });

    // ---- Version ----

    /// <summary>« Version 0.5.0 » : celle du service (la fenêtre et lui sont livrés ensemble).</summary>
    public string VersionText => string.Format(Texts.SettingsVersion, _link.Welcome?.ServiceVersion ?? AssemblyVersion());

    private static string AssemblyVersion()
    {
        var version = typeof(SettingsTabViewModel).Assembly.GetName().Version;
        return version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    // ---- Mises à jour ----

    private UpdateInfo? CurrentUpdate => _link.Updates;

    /// <summary>Le canal en vigueur dans le service ; « Stable » tant qu'on ne sait pas (c'est le choix par défaut).</summary>
    public bool IsStableChannel => CurrentUpdate is null || CurrentUpdate.Channel == LinkUpdateChannel.Stable;
    public bool IsBetaChannel => CurrentUpdate?.Channel == LinkUpdateChannel.Beta;

    /// <summary>
    /// Le service répond, aucune demande n'est en cours et il ne cherche ni ne télécharge : le canal se choisit, l'installation se
    /// lance. Pendant la recherche, une demande attendrait le verrou du service jusqu'au délai (45 s).
    /// </summary>
    public bool UpdateControlsEnabled => CanCheckUpdates;

    /// <summary>
    /// Le message du service, tel quel. Le résultat d'une demande refusée (installation déjà lancée, autorisation de Windows non
    /// donnée, liaison coupée) passe devant jusqu'à ce que l'état des mises à jour change.
    /// </summary>
    public string UpdateStatusText => _updateActionMessage ?? CurrentUpdate?.Message ?? "";
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatusText);

    /// <summary>Pas pendant la recherche ni le téléchargement. L'installateur lancé (« Installing ») ne verrouille rien : il arrête le service.</summary>
    public bool CanCheckUpdates =>
        CurrentUpdate is { State: not (LinkUpdateState.Checking or LinkUpdateState.Downloading) } && !_updateBusy;

    /// <summary>Une version est proposée. Après un échec, l'offre est gardée (comme l'icône de notification) : on peut réessayer.</summary>
    public bool CanInstallUpdate =>
        CurrentUpdate is { Version: not null, State: LinkUpdateState.Available or LinkUpdateState.Failed };

    /// <summary>Le bouton « Installer… » : caché pendant que la boîte du retour en arrière pose la même question.</summary>
    public bool ShowInstallUpdate => CanInstallUpdate && _downgrade is null;

    public string InstallUpdateText => string.Format(Texts.UpdatesInstall, CurrentUpdate?.Version);

    /// <summary>La boîte du retour de la bêta à la stable, tant qu'elle attend la réponse ; sinon <c>null</c>.</summary>
    public DowngradeDialogViewModel? Downgrade
    {
        get => _downgrade;
        private set
        {
            if (ReferenceEquals(value, _downgrade))
                return;
            _downgrade = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDowngrade));
            OnPropertyChanged(nameof(ShowInstallUpdate));
        }
    }

    public bool HasDowngrade => _downgrade is not null;

    [RelayCommand]
    private Task SetStableAsync() => SetChannelAsync(LinkUpdateChannel.Stable);

    [RelayCommand]
    private Task SetBetaAsync() => SetChannelAsync(LinkUpdateChannel.Beta);

    /// <summary>
    /// Choisit le canal. Repasser de la bêta à la stable fait proposer la dernière stable, plus ancienne : la boîte s'ouvre alors,
    /// rien n'est installé avant la réponse.
    /// </summary>
    public async Task SetChannelAsync(LinkUpdateChannel channel)
    {
        if (!UpdateControlsEnabled || CurrentUpdate is not { } before || before.Channel == channel)
            return;

        var leavesBeta = before.Channel == LinkUpdateChannel.Beta && channel == LinkUpdateChannel.Stable;
        if (await RunUpdateAsync(() => _link.SetUpdatePreferencesAsync(channel, false)).ConfigureAwait(true)
            && leavesBeta && CurrentUpdate is { State: LinkUpdateState.Available, IsDowngrade: true, Version: { } version })
            OpenDowngrade(version);
    }

    /// <summary>« Rechercher maintenant ».</summary>
    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        if (!CanCheckUpdates)
            return;
        await RunUpdateAsync(() => _link.CheckUpdatesAsync()).ConfigureAwait(true);
    }

    /// <summary>« Installer la version… » : un retour en arrière passe d'abord par la boîte.</summary>
    [RelayCommand]
    public async Task InstallUpdateAsync()
    {
        if (!UpdateControlsEnabled || !CanInstallUpdate || CurrentUpdate is not { Version: { } version } offer)
            return;
        if (offer.IsDowngrade)
        {
            OpenDowngrade(version);
            return;
        }

        await InstallAsync(confirmDowngrade: false).ConfigureAwait(true);
    }

    private void OpenDowngrade(string version) =>
        Downgrade = new DowngradeDialogViewModel(version, ConfirmDowngradeAsync, () => Downgrade = null);

    // « Garder ma bêta » (case cochée) enregistre le choix sans rien installer ; sinon, l'installation de la stable est confirmée.
    private async Task ConfirmDowngradeAsync(bool waitForNextStable)
    {
        Downgrade = null;
        if (waitForNextStable)
            await RunUpdateAsync(() => _link.SetUpdatePreferencesAsync(LinkUpdateChannel.Stable, waitForNextStable: true)).ConfigureAwait(true);
        else
            await InstallAsync(confirmDowngrade: true).ConfigureAwait(true);
    }

    private Task<bool> InstallAsync(bool confirmDowngrade) => RunUpdateAsync(async () =>
    {
        var result = await _link.InstallUpdateAsync(confirmDowngrade).ConfigureAwait(true);
        if (!result.Ok)
            ReportUpdate(result.Message);
    });

    /// <summary>Une demande au service. Vrai si elle a abouti ; une liaison coupée affiche son message à la place de l'état.</summary>
    private async Task<bool> RunUpdateAsync(Func<Task> request)
    {
        ReportUpdate(null);
        _updateBusy = true;
        NotifyUpdates();
        try
        {
            await request().ConfigureAwait(true);
            return true;
        }
        catch (ControlException ex)
        {
            ReportUpdate(ex.Message);
            return false;
        }
        finally
        {
            _updateBusy = false;
            NotifyUpdates();
        }
    }

    private void ReportUpdate(string? message)
    {
        _updateActionMessage = message;
        _updateActionState = message is null ? null : CurrentUpdate?.State;
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasUpdateStatus));
    }

    /// <summary>Le service a relu l'état des mises à jour : un message de demande refusée s'efface dès que l'état change, la boîte se ferme si l'offre a disparu.</summary>
    private void RefreshUpdates()
    {
        // L'état est relu chaque seconde : tant qu'il est le même, rien n'a changé et rien n'est notifié.
        if (Equals(CurrentUpdate, _seenUpdate))
            return;
        _seenUpdate = CurrentUpdate;

        if (_updateActionMessage is not null && CurrentUpdate?.State != _updateActionState)
        {
            _updateActionMessage = null;
            _updateActionState = null;
        }

        if (_downgrade is not null && !(CanInstallUpdate && CurrentUpdate is { IsDowngrade: true }))
            Downgrade = null;

        NotifyUpdates();
    }

    private void NotifyUpdates()
    {
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsBetaChannel));
        OnPropertyChanged(nameof(UpdateControlsEnabled));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(ShowInstallUpdate));
        OnPropertyChanged(nameof(InstallUpdateText));
    }

    // ---- État ----

    /// <summary>Remet les réglages affichés d'accord avec le fichier. Un curseur en cours de tirage n'est pas touché.</summary>
    private void Sync()
    {
        if (!_maxFpsDirty)
        {
            _maxFps = SavedMaxFps;
            OnPropertyChanged(nameof(MaxFpsValue));
            OnPropertyChanged(nameof(MaxFpsText));
        }

        OnPropertyChanged(nameof(IsUpright));
        OnPropertyChanged(nameof(IsFlipped));
    }
}
