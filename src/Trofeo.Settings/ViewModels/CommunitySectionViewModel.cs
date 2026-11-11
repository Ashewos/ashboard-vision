using System.Collections.ObjectModel;
using AshboardVision.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La section « Modules complémentaires » de l'onglet Paramètres : une ligne par module (nom, auteur, version, état en clair, mise à
/// jour proposée) avec ses boutons, le bouton « Installer un module… », le bandeau d'une liste de modules validés altérée, et la
/// boîte (<see cref="InstallModuleDialogViewModel"/>) qui installe, valide, met à jour ou règle les permissions. Rien ne s'installe sans
/// un clic sur « Installer » (ou « Valider », « Mettre à jour ») dans la boîte. La liste se relit quand l'onglet s'ouvre, après chaque
/// demande et quand le service annonce d'autres types de modules ; tant que l'onglet n'a pas été ouvert, elle ne coûte rien.
/// </summary>
public sealed partial class CommunitySectionViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly Func<IFilePicker?> _picker;
    private CommunityOverview? _overview;
    private IReadOnlyList<ModuleTypeInfo>? _seenTypes;
    private bool _active;
    private int _refreshes;
    private bool _watching;
    private bool _wasConnected;
    private string? _packageWhenConnected;
    private string? _waitingPackage;
    private string? _readFailure;

    public CommunitySectionViewModel(IServiceLink link, EditSession session, Func<IFilePicker?> picker, TimeProvider? time = null)
    {
        _link = link;
        _session = session;
        _picker = picker;
        Consumption = new ModuleConsumptionViewModel(link, time);
        _seenTypes = link.KnownTypes();
        _wasConnected = link.State == LinkState.Connected;
        _link.Changed += OnLinkChanged;
    }

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        Consumption.Dispose();
        // La fenêtre se ferme au milieu d'une mise à jour : le service jette le fichier de lui-même au bout de 15 minutes, on le prévient quand même.
        if (Dialog is { Mode: ModuleDialogMode.Update, IsAbandoned: false } && _updatingId is { } id)
            _ = AbandonQuietlyAsync(id);
    }

    private async Task AbandonQuietlyAsync(string id)
    {
        try { await _link.CancelModuleUpdateAsync(id).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ControlException or TimeoutException or OperationCanceledException) { }
    }

    /// <summary>Une ligne par module complémentaire trouvé.</summary>
    public ObservableCollection<CommunityModuleRow> Modules { get; } = [];

    public bool HasModules => Modules.Count > 0;
    public bool HasNoModules => Modules.Count == 0;

    /// <summary>La liste des modules validés a été trouvée altérée : rien n'est validé, le bandeau le dit.</summary>
    public bool StoreTampered { get; private set; }

    public string TamperedText => Texts.CommunityTampered;

    /// <summary>La dernière demande qui a échoué, telle que le service la dit (désinstallation refusée, liaison coupée…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>La boîte ouverte (installation, validation, mise à jour, permissions) ; <c>null</c> : aucune. La fenêtre la montre par-dessus tout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog), nameof(IsIdle))]
    private InstallModuleDialogViewModel? _dialog;

    public bool HasDialog => Dialog is not null;

    /// <summary>Une demande de la section est en route (retrait, relance, choix d'un fichier).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Aucune demande en route ni boîte ouverte : « Installer un module… » et les boutons des lignes marchent.</summary>
    public bool IsIdle => !IsBusy && Dialog is null;

    partial void OnDialogChanged(InstallModuleDialogViewModel? value)
    {
        if (value is null)
            _updatingId = null;
        SyncRows();
    }

    partial void OnIsBusyChanged(bool value) => SyncRows();

    private void SyncRows()
    {
        var idle = !IsBusy && Dialog is null;
        foreach (var row in Modules)
            row.IsIdle = idle;
        foreach (var folder in DevFolders)
            folder.IsIdle = idle;
    }

    // ---- La liste ----

    /// <summary>
    /// L'onglet Paramètres s'ouvre : la liste est relue maintenant, puis chaque fois que les types de modules changent. (Avant, seul
    /// <see cref="Watch"/> lit, une fois par connexion, pour le bandeau du mode développeur.)
    /// </summary>
    public void Activate()
    {
        _active = true;
        if (_link.State == LinkState.Connected)
            _ = RefreshAsync();
    }

    private void OnLinkChanged()
    {
        if (_packageWhenConnected is { } path && _link.State == LinkState.Connected)
        {
            _packageWhenConnected = null;
            _ = OpenPackageAsync(path);
        }

        var connected = _link.State == LinkState.Connected;
        var justConnected = connected && !_wasConnected;
        _wasConnected = connected;

        // Le service renvoie son état chaque seconde ; la liste ne se relit que si les types de modules ont changé.
        var types = _link.KnownTypes();
        var typesChanged = !ReferenceEquals(types, _seenTypes);
        _seenTypes = types;
        if (connected && (_active && typesChanged || _watching && justConnected))
            _ = RefreshAsync();
    }

    /// <summary>
    /// La fenêtre s'ouvre : l'état du mode développeur doit se savoir tout de suite (le bandeau « Mode développeur actif » est en haut de la
    /// fenêtre, dans tous les onglets). Une seule lecture à chaque connexion au service, sans minuteur ; la liste complète, elle, attend toujours l'onglet.
    /// </summary>
    public void Watch()
    {
        _watching = true;
        if (_link.State == LinkState.Connected)
            _ = RefreshAsync();
    }

    /// <summary>Relit les modules complémentaires. Une réponse plus ancienne que la dernière demande est écartée.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_refreshes;
        CommunityOverview overview;
        try
        {
            overview = await _link.GetCommunityAsync().ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            if (version == _refreshes)
                Message = _readFailure = UntrustedText.Block(ex.Message);
            return;
        }

        if (version != _refreshes)
            return;

        // Le message d'une lecture qui avait échoué n'a plus lieu d'être ; celui d'une demande refusée (désinstallation…) reste.
        if (_readFailure is not null && Message == _readFailure)
            Message = null;
        _readFailure = null;

        _overview = overview;
        var confirming = Modules.Where(m => m.ConfirmingUninstall).Select(m => m.Id).ToHashSet();
        Modules.Clear();
        foreach (var info in overview.Modules)
        {
            var row = new CommunityModuleRow(info, this) { IsIdle = !IsBusy && Dialog is null };
            row.ConfirmingUninstall = confirming.Contains(info.Id);
            Modules.Add(row);
        }

        ReadGeneral(overview);
        StoreTampered = overview.StoreTampered;
        OnPropertyChanged(nameof(StoreTampered));
        OnPropertyChanged(nameof(HasModules));
        OnPropertyChanged(nameof(HasNoModules));
    }

    // ---- Installer ----

    /// <summary>« Installer un module… » : choisit un fichier <c>.avmod</c>, puis montre ce qu'il contient avant toute installation.</summary>
    [RelayCommand]
    public async Task InstallFromFileAsync()
    {
        if (!IsIdle || _picker() is not { } picker)
            return;

        Message = null;
        string? path;
        IsBusy = true;
        try
        {
            path = await picker.PickAsync(Texts.CommunityPickTitle, Texts.CommunityPickType, ["avmod"]).ConfigureAwait(true);
        }
        catch (FilePickerException ex)
        {
            Message = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (!string.IsNullOrEmpty(path))
            await OpenPackageAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>Trofeo.Settings.exe &lt;chemin.avmod&gt;</c> : la boîte d'installation s'ouvre dès que la liaison avec le service est établie. Le
    /// chemin vient de la ligne de commande : il passe par l'inspection du service, comme un fichier choisi, et rien ne s'installe sans
    /// le clic sur « Installer » dans la boîte.
    /// </summary>
    public void OpenPackageWhenConnected(string path)
    {
        if (_link.State == LinkState.Connected)
            _ = OpenPackageAsync(path);
        else
            _packageWhenConnected = path;
    }

    /// <summary>
    /// Un .avmod demandé par la ligne de commande ne peut pas s'ouvrir (fichier d'un partage réseau, fichier disparu) : la section le dit,
    /// avec la phrase que <see cref="Services.PackageArgument"/> a choisie. Rien n'est envoyé au service.
    /// </summary>
    public void ShowPackageProblem(string problem)
    {
        Message = problem;
    }

    /// <summary>Fait lire le paquet au service (sans rien écrire sur le disque) et ouvre la boîte avec ce qu'il y a vu.</summary>
    public async Task OpenPackageAsync(string path)
    {
        // Une boîte est déjà ouverte : le fichier attend qu'elle se ferme, il n'est pas perdu.
        if (Dialog is not null)
        {
            _waitingPackage = path;
            return;
        }

        Message = null;
        PackageInfo package;
        IsBusy = true;
        try
        {
            package = await _link.InspectPackageAsync(path).ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            Message = UntrustedText.Block(ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Un paquet qui remplace un module installé est une mise à jour : ce que l'utilisateur lui a déjà accordé, et que le paquet
        // déclare encore, est coché d'avance ; ce qu'il déclare en plus est décoché et signalé.
        CommunityModuleInfo? installed = null;
        if (package.InstalledVersion is not null && package.Id is not null)
        {
            _overview ??= await TryReadOverviewAsync().ConfigureAwait(true);
            installed = _overview?.Modules.FirstOrDefault(m => m.Id == package.Id);
        }

        // Pendant l'inspection (ou la lecture des modules), une autre boîte a pu s'ouvrir : celle-là reste, ce fichier attend.
        if (Dialog is not null)
        {
            _waitingPackage = path;
            return;
        }

        var problems = new List<string>(package.Errors);
        if (!string.IsNullOrEmpty(package.Conflict))
            problems.Add(package.Conflict);

        var name = string.IsNullOrWhiteSpace(package.Name) ? Path.GetFileName(path) : package.Name;
        var content = new ModuleDialogContent(
            name, package.Author, package.Version, installed is null ? null : package.InstalledVersion, package.Description, null,
            problems.Count > 0 ? [] : Choices(package.Declared, installed), problems);

        Open(new InstallModuleDialogViewModel(ModuleDialogMode.Install, content, granted => _link.InstallPackageAsync(path, granted), null, OnDialogClosed));
    }

    private async Task<CommunityOverview?> TryReadOverviewAsync()
    {
        try { return await _link.GetCommunityAsync().ConfigureAwait(true); }
        catch (ControlException) { return null; }
    }

    /// <summary>
    /// Les cases d'un module : une par permission déclarée. Sans module déjà installé (première installation, validation), toutes
    /// décochées ; avec lui (mise à jour), celles qu'il avait déjà et qui sont encore déclarées sont cochées d'avance, celles qu'il
    /// déclare en plus sont décochées et signalées. Une permission qui n'est plus déclarée n'apparaît pas.
    /// </summary>
    private static List<PermissionChoice> Choices(
        IReadOnlyList<ModulePermission> declared, CommunityModuleInfo? installed, IReadOnlyList<ModulePermission>? newOnes = null) =>
    [
        .. declared.Distinct().Order().Select(permission =>
        {
            var isNew = installed is not null && (!installed.Declared.Contains(permission) || newOnes?.Contains(permission) == true);
            return new PermissionChoice(permission, isChecked: installed is not null && !isNew && installed.Granted.Contains(permission), isNew);
        }),
    ];

    // ---- Valider, régler les permissions ----

    internal void Approve(CommunityModuleRow row)
    {
        if (Dialog is not null)
            return;
        var info = row.Info;
        var content = new ModuleDialogContent(
            info.Name, info.Author, info.Version, null, info.Description, null, Choices(info.Declared, installed: null), []);
        Open(new InstallModuleDialogViewModel(ModuleDialogMode.Approve, content, granted => _link.ApproveModuleAsync(info.Id, granted), null, OnDialogClosed));
    }

    internal void EditPermissions(CommunityModuleRow row)
    {
        if (Dialog is not null)
            return;
        var info = row.Info;
        // Les mêmes cases ; celles que l'utilisateur a accordées sont cochées.
        var choices = info.Declared.Distinct().Order().Select(p => new PermissionChoice(p, info.Granted.Contains(p), isNew: false)).ToList();
        var content = new ModuleDialogContent(info.Name, info.Author, info.Version, null, info.Description, null, choices, []);
        Open(new InstallModuleDialogViewModel(ModuleDialogMode.Permissions, content, granted => _link.SetModulePermissionsAsync(info.Id, granted), null, OnDialogClosed));
    }

    // ---- Mettre à jour (en deux temps) ----

    private string? _updatingId;

    /// <summary>
    /// « Mettre à jour » : le service télécharge le paquet et le lit (la boîte s'ouvre tout de suite et dit que le téléchargement est
    /// en cours), puis la boîte montre les notes, les permissions déclarées et celles qui sont nouvelles. Installer, c'est la
    /// confirmation de la boîte ; l'abandonner prévient le service, y compris pendant le téléchargement.
    /// </summary>
    internal async Task UpdateAsync(CommunityModuleRow row)
    {
        var info = row.Info;
        if (Dialog is not null || info.UpdateVersion is null)
            return;

        var shown = new ModuleDialogContent(info.Name, info.Author, info.UpdateVersion, info.Version, info.Description, info.UpdateNotes, [], []);
        var dialog = new InstallModuleDialogViewModel(
            ModuleDialogMode.Update, shown, granted => _link.InstallModuleUpdateAsync(info.Id, granted),
            () => _link.CancelModuleUpdateAsync(info.Id), OnDialogClosed, preparing: true);
        Message = null;
        _updatingId = info.Id;
        Dialog = dialog;

        ModuleUpdatePreparation preparation;
        try
        {
            preparation = await _link.PrepareModuleUpdateAsync(info.Id).ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            // Annulée ou remplacée parce que l'utilisateur a fermé la boîte : un abandon, pas une erreur.
            if (!dialog.IsAbandoned)
                dialog.Fail(ex.Message);
            return;
        }

        if (dialog.IsAbandoned)
            return;
        if (!preparation.Ok)
        {
            dialog.Fail(preparation.Message ?? "");
            return;
        }

        dialog.Apply(new ModuleDialogContent(
            info.Name, info.Author, preparation.Version ?? info.UpdateVersion, info.Version, info.Description, preparation.Notes ?? info.UpdateNotes,
            Choices(preparation.Declared, info, preparation.NewPermissions), []));
    }

    // ---- Relancer, désinstaller ----

    internal async Task RelaunchAsync(CommunityModuleRow row)
    {
        if (!IsIdle)
            return;
        await RunAsync(() => _link.RelaunchModuleAsync(row.Id)).ConfigureAwait(true);
    }

    internal void AskUninstall(CommunityModuleRow row) => row.ConfirmingUninstall = true;

    internal void CancelUninstall(CommunityModuleRow row) => row.ConfirmingUninstall = false;

    /// <summary>
    /// Désinstalle le module, puis retire ses emplacements de la disposition (placés et rangés) et recharge : si une case restait
    /// quand même (disposition illisible, modification refusée), l'avertissement « type inconnu » du service s'affiche.
    /// </summary>
    internal async Task UninstallAsync(CommunityModuleRow row)
    {
        if (!IsIdle)
            return;

        row.ConfirmingUninstall = false;
        var id = row.Id;
        if (!await RunAsync(() => _link.UninstallModuleAsync(id)).ConfigureAwait(true))
            return; // le service a refusé : ses cases restent, le motif est affiché

        var outcome = await _session.ApplyAsync(layout => RemovePlacements(layout, id)).ConfigureAwait(true);
        if (outcome is null)
            await _session.ReloadAsync().ConfigureAwait(true); // rien à retirer : on recharge quand même, pour que le service dise ce qu'il voit
    }

    private static DashboardLayout? RemovePlacements(DashboardLayout layout, string typeId)
    {
        var views = layout.Views.Select(v => v with { Placements = v.Placements.Where(p => p.TypeId != typeId).ToList() }).ToList();
        var shelf = layout.Shelf?.Where(p => p.TypeId != typeId).ToList();
        if (views.Sum(v => v.Placements.Count) == layout.AllPlacements.Count() && (shelf is null || shelf.Count == layout.Shelf!.Count))
            return null;

        // Les raccourcis qui visaient ces cases partent avec elles, dans la même modification.
        var gone = layout.AllPlacements.Concat(layout.Shelf ?? []).Where(p => p.TypeId == typeId).Select(p => p.InstanceId).ToHashSet();
        return LayoutBindings.WithoutTargets(layout with { Views = views, Shelf = shelf }, gone);
    }

    /// <summary>Une demande qui change les modules : vrai si le service l'a acceptée ; sinon son message reste affiché, tel quel. La liste est relue.</summary>
    private async Task<bool> RunAsync(Func<Task<Trofeo.ModuleKit.ActionResult>> request)
    {
        Message = null;
        IsBusy = true;
        try
        {
            var result = await request().ConfigureAwait(true);
            if (!result.Ok)
                Message = UntrustedText.Block(result.Message);
            return result.Ok;
        }
        catch (ControlException ex)
        {
            Message = UntrustedText.Block(ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            _ = RefreshAsync();
        }
    }

    // ---- La boîte ----

    private void Open(InstallModuleDialogViewModel dialog)
    {
        Message = null;
        Dialog = dialog;
    }

    private void OnDialogClosed(InstallModuleDialogViewModel dialog, bool changed)
    {
        if (ReferenceEquals(Dialog, dialog))
            Dialog = null;
        if (changed)
            _ = RefreshAsync();
        if (_waitingPackage is { } waiting && Dialog is null)
        {
            _waitingPackage = null;
            _ = OpenPackageAsync(waiting);
        }
    }
}

/// <summary>Une ligne de la section « Modules complémentaires ». Le texte qui vient du module est nettoyé : c'est une donnée non fiable.</summary>
public sealed partial class CommunityModuleRow : ObservableObject
{
    private readonly CommunitySectionViewModel _owner;

    internal CommunityModuleRow(CommunityModuleInfo info, CommunitySectionViewModel owner)
    {
        Info = info;
        _owner = owner;
        var name = UntrustedText.Line(info.Name);
        Name = name.Length > 0 ? name : info.Id;
        Id = info.Id;
        var author = UntrustedText.Line(info.Author);
        var version = UntrustedText.Line(info.Version);
        AuthorLine = author.Length > 0 && version.Length > 0 ? string.Format(Texts.CommunityAuthorVersion, author, version)
            : author.Length > 0 ? string.Format(Texts.DialogAuthor, author)
            : version.Length > 0 ? string.Format(Texts.DialogVersion, version) : "";
        StateText = info.State switch
        {
            LinkCommunityState.Ready => Texts.CommunityStateReady,
            LinkCommunityState.NeedsApproval => Texts.CommunityStateNeedsApproval,
            LinkCommunityState.Changed => Texts.CommunityStateChanged,
            LinkCommunityState.NeedsNewerApp => Texts.CommunityStateNeedsNewerApp,
            LinkCommunityState.TooOld => Texts.CommunityStateTooOld,
            LinkCommunityState.Invalid => Texts.CommunityStateInvalid,
            LinkCommunityState.Unavailable => Texts.CommunityStateUnavailable,
            _ => Texts.CommunityStateSetAside,
        };
        var stateBase = StateText;
        StateText += info.FromDevFolder ? " · " + Texts.CommunityDevFolder : "";
        // Le service écrit un message pour chaque état : on ne le répète pas quand l'état le dit déjà (« À valider »).
        var message = UntrustedText.Block(info.Message);
        Message = message.StartsWith(stateBase, StringComparison.Ordinal) || stateBase.StartsWith(message, StringComparison.Ordinal) ? "" : message;
        UpdateOffer = string.IsNullOrWhiteSpace(info.UpdateVersion) ? "" : string.Format(Texts.CommunityUpdateOffer, UntrustedText.Line(info.UpdateVersion));
        PermissionsText = PermissionLabels.List(info.Granted);
    }

    internal CommunityModuleInfo Info { get; }

    public string Id { get; }
    public string Name { get; }
    public string AuthorLine { get; }
    public bool HasAuthorLine => AuthorLine.Length > 0;
    public string StateText { get; private set; }
    public bool StateIsProblem => Info.State != LinkCommunityState.Ready;
    public bool StateIsOk => Info.State == LinkCommunityState.Ready;

    /// <summary>Ce que le service dit de plus de l'état (écrit pour être lu tel quel), sans le répéter.</summary>
    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    public string UpdateOffer { get; }
    public bool HasUpdateOffer => UpdateOffer.Length > 0;

    /// <summary>« Permissions accordées : Internet. »</summary>
    public string PermissionsText { get; }

    // ---- Ce que chaque bouton peut faire ----

    private bool Validated => Info.State is LinkCommunityState.Ready or LinkCommunityState.SetAside;

    public bool CanApprove => Info.State is LinkCommunityState.NeedsApproval or LinkCommunityState.Changed;
    public bool CanPermissions => Validated && Info.Declared.Count > 0;
    public bool CanUpdate => Info.UpdateVersion is not null && !Info.FromDevFolder && Validated;
    public bool CanRelaunch => Info.State == LinkCommunityState.SetAside;

    /// <summary>Un module d'un dossier de développement se retire avec son dossier (mode développeur), pas avec « Désinstaller ».</summary>
    public bool CanUninstall => !Info.FromDevFolder;

    /// <summary>Aucune demande ni boîte en cours : les boutons de la ligne marchent. Le bouton « Mettre à jour » reste grisé pendant la préparation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApproveNow), nameof(CanPermissionsNow), nameof(CanUpdateNow), nameof(CanRelaunchNow), nameof(CanUninstallNow))]
    private bool _isIdle = true;

    public bool CanApproveNow => IsIdle && CanApprove;
    public bool CanPermissionsNow => IsIdle && CanPermissions;
    public bool CanUpdateNow => IsIdle && CanUpdate;
    public bool CanRelaunchNow => IsIdle && CanRelaunch;
    public bool CanUninstallNow => IsIdle && CanUninstall;

    /// <summary>« Désinstaller » a été demandé : la ligne attend la confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConfirming))]
    private bool _confirmingUninstall;

    public bool IsNotConfirming => !ConfirmingUninstall;

    public string UninstallConfirmText => Texts.CommunityUninstallConfirm;

    [RelayCommand]
    private void Approve() => _owner.Approve(this);

    [RelayCommand]
    private void Permissions() => _owner.EditPermissions(this);

    [RelayCommand]
    private Task Update() => _owner.UpdateAsync(this);

    [RelayCommand]
    private Task Relaunch() => _owner.RelaunchAsync(this);

    [RelayCommand]
    private void AskUninstall() => _owner.AskUninstall(this);

    [RelayCommand]
    private void CancelUninstall() => _owner.CancelUninstall(this);

    [RelayCommand]
    private Task ConfirmUninstall() => _owner.UninstallAsync(this);
}
