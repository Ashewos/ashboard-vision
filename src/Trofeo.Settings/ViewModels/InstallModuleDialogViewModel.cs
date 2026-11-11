using System.Collections.ObjectModel;
using AshboardVision.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>Une permission déclarée par un module, avec sa case. <see cref="IsNew"/> : la nouvelle version la demande en plus.</summary>
public sealed partial class PermissionChoice(ModulePermission permission, bool isChecked, bool isNew) : ObservableObject
{
    public ModulePermission Permission { get; } = permission;
    public string Label { get; } = PermissionLabels.Label(permission);
    public string Explanation { get; } = PermissionLabels.Explanation(permission);
    public bool IsNew { get; } = isNew;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>Ce que la boîte est en train de faire.</summary>
public enum ModuleDialogMode
{
    /// <summary>Un <c>.avmod</c> à installer.</summary>
    Install,

    /// <summary>Un module trouvé dans le dossier des modules, à valider (jamais validé, ou fichiers changés).</summary>
    Approve,

    /// <summary>Une mise à jour que le service a téléchargée et lue.</summary>
    Update,

    /// <summary>Les permissions d'un module déjà validé.</summary>
    Permissions,

    /// <summary>Un dossier de travail du mode développeur, à ajouter.</summary>
    DevAdd,

    /// <summary>Un module du mode développeur à recharger, dont le dossier déclare une permission qui n'est pas accordée.</summary>
    DevReload,
}

/// <summary>
/// Ce que la boîte montre d'un module. Le nom, l'auteur, la description et les notes viennent du module : ce sont des données non
/// fiables, nettoyées par la boîte (<see cref="UntrustedText"/>) et montrées comme du texte brut.
/// </summary>
public sealed record ModuleDialogContent(
    string? Name, string? Author, string? Version, string? InstalledVersion, string? Description, string? Notes,
    IReadOnlyList<PermissionChoice> Permissions, IReadOnlyList<string> Problems);

/// <summary>
/// La boîte qui installe, valide, met à jour ou règle les permissions d'un module complémentaire : l'avertissement (point d'exclamation
/// jaune), le nom, l'auteur, la version et la description du module, une case par permission <b>déclarée</b>, puis « Installer » (ou
/// « Valider », « Mettre à jour », « Enregistrer ») et « Annuler ». Une erreur ou un conflit s'affiche tel quel, sans bouton de
/// confirmation. Ce qui part au service est exactement ce qui est coché. Pour une mise à jour, la boîte s'ouvre pendant le
/// téléchargement (<see cref="IsPreparing"/>), et l'abandonner (Annuler, Échap) prévient le service.
/// </summary>
public sealed partial class InstallModuleDialogViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ModulePermission>, Task<ActionResult>> _commit;
    private readonly Func<Task>? _abandon;
    private readonly Action<InstallModuleDialogViewModel, bool> _closed;
    private bool _finished;

    internal InstallModuleDialogViewModel(
        ModuleDialogMode mode, ModuleDialogContent content,
        Func<IReadOnlyList<ModulePermission>, Task<ActionResult>> commit, Func<Task>? abandon, Action<InstallModuleDialogViewModel, bool> closed,
        bool preparing = false)
    {
        Mode = mode;
        _commit = commit;
        _abandon = abandon;
        _closed = closed;
        Apply(content);
        if (preparing)
            IsPreparing = true; // le service télécharge : le nom, la version et les notes sont ceux de la liste, les cases viendront
    }

    public ModuleDialogMode Mode { get; }

    /// <summary>Le titre de la boîte.</summary>
    public string Title => Mode switch
    {
        ModuleDialogMode.Install => Texts.DialogInstallTitle,
        ModuleDialogMode.Approve => Texts.DialogApproveTitle,
        ModuleDialogMode.Update => Texts.DialogUpdateTitle,
        ModuleDialogMode.DevAdd or ModuleDialogMode.DevReload => Texts.DialogDevTitle,
        _ => Texts.DialogPermissionsTitle,
    };

    /// <summary>Le texte du bouton de confirmation : « Installer », « Valider », « Mettre à jour » ou « Enregistrer ».</summary>
    public string ConfirmLabel => Mode switch
    {
        ModuleDialogMode.Install => Texts.DialogInstall,
        ModuleDialogMode.Approve => Texts.DialogApprove,
        ModuleDialogMode.Update => Texts.CommunityUpdate,
        ModuleDialogMode.DevAdd => Texts.DialogDevAdd,
        ModuleDialogMode.DevReload => Texts.DeveloperReload,
        _ => Texts.DialogSave,
    };

    /// <summary>L'avertissement du point d'exclamation jaune ; la boîte des permissions d'un module déjà validé ne le répète pas.</summary>
    public string WarningText => Texts.DialogWarning;

    public bool ShowsWarning => Mode != ModuleDialogMode.Permissions;

    // ---- Ce que la boîte montre du module ----

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _author = "";

    [ObservableProperty]
    private string _versionText = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _notes = "";

    public bool HasAuthor => Author.Length > 0;

    /// <summary>« Auteur : Alex » (texte du module, nettoyé).</summary>
    public string AuthorText => string.Format(Texts.DialogAuthor, Author);
    public bool HasVersion => VersionText.Length > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasNotes => Notes.Length > 0;

    /// <summary>Les cases, une par permission déclarée ; vide tant que le service prépare une mise à jour.</summary>
    public ObservableCollection<PermissionChoice> Permissions { get; } = [];

    public bool HasPermissions => Permissions.Count > 0;

    /// <summary>« Ce module ne demande aucune permission. » : le manifeste n'en déclare aucune.</summary>
    public bool ShowsNoPermissions => !IsPreparing && !HasProblems && Permissions.Count == 0;

    /// <summary>Une mise à jour qui déclare une permission de plus le dit au-dessus des cases.</summary>
    public bool HasNewPermissions => Permissions.Any(p => p.IsNew);

    public string NewPermissionsText => Texts.DialogNewPermissions;
    public string PermissionsIntro => Texts.DialogPermissionsIntro;
    public string NoPermissionsText => Texts.DialogNoPermissions;
    public string NotesTitle => Texts.DialogNotes;
    public string NewMark => Texts.PermissionNew;
    public string CommunityNotice => Texts.CommunityNotice;

    /// <summary>Les erreurs du paquet, le conflit d'identifiant, le refus du service : tels quels, en rouge. Il y en a, plus de bouton de confirmation.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    // ---- Où en est la boîte ----

    /// <summary>Le service télécharge et lit la mise à jour : les cases arriveront.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(ShowsNoPermissions), nameof(ShowsPermissionArea))]
    private bool _isPreparing;

    public string PreparingText => string.IsNullOrEmpty(RawVersion) ? Texts.DialogWorking : string.Format(Texts.DialogPreparing, RawVersion);

    private string RawVersion { get; set; } = "";

    /// <summary>La confirmation est en route.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(CanCancel))]
    private bool _isBusy;

    /// <summary>La réponse du service à la confirmation quand elle a été refusée : tel quel. La boîte reste ouverte.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultMessage))]
    private string? _resultMessage;

    /// <summary>
    /// Annuler (et Échap) : pas pendant que le service installe — fermer la boîte ne l'arrêterait pas, et abandonnerait le fichier d'une mise
    /// à jour en cours d'installation. La fermeture de la fenêtre, elle, reste possible.
    /// </summary>
    public bool CanCancel => !IsBusy && !_finished;

    public bool HasResultMessage => !string.IsNullOrEmpty(ResultMessage);

    /// <summary>Le bouton de confirmation : ni erreur ni conflit, ni préparation ni demande en cours.</summary>
    public bool CanConfirm => !IsPreparing && !IsBusy && !HasProblems && !_finished;

    public bool ShowsPermissionArea => !IsPreparing && !HasProblems;

    /// <summary>
    /// Remplit la boîte avec le contenu lu (le paquet inspecté, la mise à jour préparée). Appelée une fois pour une installation, une
    /// validation ou des permissions ; pour une mise à jour, quand le service a fini de préparer.
    /// </summary>
    internal void Apply(ModuleDialogContent content)
    {
        Name = UntrustedText.Line(content.Name);
        Author = UntrustedText.Line(content.Author);
        OnPropertyChanged(nameof(AuthorText));
        Description = UntrustedText.Block(content.Description);
        Notes = UntrustedText.Block(content.Notes);
        VersionText = VersionLine(content.Version, content.InstalledVersion);
        RawVersion = UntrustedText.Line(content.Version);
        // « A des problèmes » vient de ce qui a été reçu, pas de ce qu'il en reste une fois nettoyé : des erreurs qui se réduisent à rien
        // ne doivent pas faire apparaître le bouton de confirmation.
        var cleanedProblems = content.Problems.Select(UntrustedText.Block).Where(p => p.Length > 0).ToList();
        Problems = content.Problems.Count > 0 && cleanedProblems.Count == 0 ? [Texts.DialogInvalidPackage] : cleanedProblems;

        Permissions.Clear();
        foreach (var permission in content.Permissions)
            Permissions.Add(permission);

        IsPreparing = false;
        NotifyContent();
    }

    /// <summary>La mise à jour ne s'est pas préparée : le motif du service (ou de la liaison), tel quel, sans bouton de confirmation.</summary>
    internal void Fail(string message)
    {
        var cleaned = UntrustedText.Block(message);
        Problems = [cleaned.Length > 0 ? cleaned : Texts.DialogPreparationFailed];
        Permissions.Clear();
        IsPreparing = false;
        NotifyContent();
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasPermissions));
        OnPropertyChanged(nameof(HasNewPermissions));
        OnPropertyChanged(nameof(ShowsNoPermissions));
        OnPropertyChanged(nameof(ShowsPermissionArea));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HasAuthor));
        OnPropertyChanged(nameof(HasVersion));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(PreparingText));
    }

    private static string VersionLine(string? version, string? installed)
    {
        var cleaned = UntrustedText.Line(version);
        if (cleaned.Length == 0)
            return "";
        var previous = UntrustedText.Line(installed);
        return previous.Length == 0 ? string.Format(Texts.DialogVersion, cleaned) : string.Format(Texts.DialogVersionUpdate, previous, cleaned);
    }

    /// <summary>Les permissions cochées, dans l'ordre des cases : c'est tout ce qui part au service.</summary>
    public IReadOnlyList<ModulePermission> CheckedPermissions => [.. Permissions.Where(p => p.IsChecked).Select(p => p.Permission)];

    /// <summary>Confirme avec les permissions cochées. Un refus du service s'affiche tel quel et la boîte reste ouverte.</summary>
    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (!CanConfirm)
            return;

        ResultMessage = null;
        IsBusy = true;
        ActionResult result;
        try
        {
            result = await _commit(CheckedPermissions).ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            ResultMessage = UntrustedText.Block(ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (!result.Ok)
        {
            ResultMessage = UntrustedText.Block(result.Message);
            return;
        }

        _finished = true;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanCancel));
        _closed(this, true);
    }

    /// <summary>Annule : la boîte se ferme, rien n'est installé. Une mise à jour en cours prévient le service, qui supprime le fichier.</summary>
    [RelayCommand]
    public async Task CancelAsync()
    {
        if (!CanCancel)
            return;
        _finished = true;
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanCancel));
        _closed(this, false);
        if (_abandon is null)
            return;
        try
        {
            await _abandon().ConfigureAwait(true);
        }
        catch (ControlException)
        {
            // Le service n'est plus là : il jette le fichier de lui-même (15 minutes au plus).
        }
    }

    /// <summary>La boîte a été fermée avant la fin d'une préparation : le résultat qui arrivera ensuite n'intéresse plus personne.</summary>
    public bool IsAbandoned => _finished;
}
