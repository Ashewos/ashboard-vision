using System.Collections.ObjectModel;
using AshboardVision.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Une ligne de la liste des dossiers de travail du mode développeur : le chemin, le module qu'il contient (celui dont le service dit
/// qu'il vient de ce dossier), « Recharger » et « Retirer ». Le chemin affiché est nettoyé ; le service reçoit le chemin brut.
/// </summary>
public sealed partial class DevFolderRow : ObservableObject
{
    private readonly CommunitySectionViewModel _owner;

    internal DevFolderRow(string path, CommunityModuleInfo? module, CommunitySectionViewModel owner)
    {
        Path = path;
        Module = module;
        _owner = owner;
        PathText = UntrustedText.Line(path);
        if (module is not null)
        {
            var name = UntrustedText.Line(module.Name);
            ModuleName = name.Length > 0 ? name : UntrustedText.Line(module.Id);
        }
    }

    /// <summary>Le chemin tel que le service l'a donné : c'est lui qui retourne au service.</summary>
    internal string Path { get; }

    /// <summary>Le module de ce dossier ; <c>null</c> si le service n'en trouve pas dans la liste (manifeste illisible…).</summary>
    internal CommunityModuleInfo? Module { get; }

    public string PathText { get; }
    public string ModuleName { get; } = "";
    public bool HasModule => Module is not null;
    public bool HasNoModule => Module is null;
    public string NoModuleText => Texts.DeveloperNoModule;

    /// <summary>Aucune demande ni boîte en cours : les boutons de la ligne marchent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReloadNow), nameof(CanRemoveNow))]
    private bool _isIdle = true;

    /// <summary>« Recharger » : seulement s'il y a un module à recharger.</summary>
    public bool CanReloadNow => IsIdle && HasModule;

    public bool CanRemoveNow => IsIdle;

    [RelayCommand]
    private Task Reload() => _owner.ReloadDevFolderAsync(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveDevFolderAsync(this);
}

// Le dossier des modules et le mode développeur.
public sealed partial class CommunitySectionViewModel
{
    private string _modulesRoot = "";
    private bool _developerMode;

    /// <summary>Le tableau « Consommation des modules ».</summary>
    public ModuleConsumptionViewModel Consumption { get; }

    // ---- Le dossier des modules ----

    /// <summary>Le dossier des modules en vigueur, tel que le service le dit (texte nettoyé).</summary>
    public string ModulesRoot => _modulesRoot;

    /// <summary>
    /// « Changer… » : choisit un dossier, que le service accepte ou refuse (son message reste affiché tel quel). Un dossier non vide d'un autre
    /// logiciel est accepté ; les modules déjà installés restent où ils sont.
    /// </summary>
    [RelayCommand]
    public async Task ChangeModulesFolderAsync()
    {
        if (!IsIdle || _picker() is not { } picker)
            return;

        Message = null;
        string? path;
        IsBusy = true;
        try
        {
            path = await picker.PickFolderAsync(Texts.CommunityFolderPickTitle).ConfigureAwait(true);
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
            await RunAsync(() => _link.SetModulesRootAsync(path)).ConfigureAwait(true);
    }

    // ---- Le mode développeur ----

    /// <summary>Le mode développeur est allumé (d'après le service). L'interrupteur envoie la demande ; un refus le remet à sa place.</summary>
    public bool DeveloperMode
    {
        get => _developerMode;
        set
        {
            if (value != _developerMode)
                _ = SetDeveloperModeAsync(value);
        }
    }

    /// <summary>Le bandeau « Mode développeur actif », en haut de la fenêtre, tant que le mode est allumé.</summary>
    public bool ShowsDeveloperBanner => DeveloperMode;

    public string DeveloperBannerText => Texts.DeveloperBanner;

    /// <summary>
    /// Allume ou éteint le mode développeur. La liste des modules est relue à chaque fois (éteint, les modules des dossiers de travail
    /// disparaissent) ; un refus s'affiche tel quel et l'interrupteur revient.
    /// </summary>
    public async Task SetDeveloperModeAsync(bool on)
    {
        if (!IsIdle)
        {
            OnPropertyChanged(nameof(DeveloperMode));
            return;
        }

        if (!await RunAsync(() => _link.SetDeveloperModeAsync(on)).ConfigureAwait(true))
            OnPropertyChanged(nameof(DeveloperMode));
    }

    /// <summary>Les dossiers de travail, avec leur module.</summary>
    public ObservableCollection<DevFolderRow> DevFolders { get; } = [];

    public bool HasDevFolders => DevFolders.Count > 0;
    public bool HasNoDevFolders => DevFolders.Count == 0;

    private void ReadGeneral(CommunityOverview overview)
    {
        _modulesRoot = UntrustedText.Line(overview.ModulesRoot);
        OnPropertyChanged(nameof(ModulesRoot));

        var idle = !IsBusy && Dialog is null;
        DevFolders.Clear();
        foreach (var path in overview.DevFolders)
        {
            var module = overview.Modules.FirstOrDefault(m => m.DevFolderPath is not null && SamePath(m.DevFolderPath, path));
            DevFolders.Add(new DevFolderRow(path, module, this) { IsIdle = idle });
        }

        OnPropertyChanged(nameof(HasDevFolders));
        OnPropertyChanged(nameof(HasNoDevFolders));

        if (_developerMode != overview.DeveloperMode)
        {
            _developerMode = overview.DeveloperMode;
            OnPropertyChanged(nameof(DeveloperMode));
            OnPropertyChanged(nameof(ShowsDeveloperBanner));
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ---- Ajouter, recharger, retirer un dossier de travail ----

    /// <summary>
    /// « Ajouter un dossier… » : choisit un dossier, le service lit son manifeste (sans rien lancer), puis la boîte montre le module, l'avertissement et ses
    /// permissions déclarées. Rien n'est ajouté avant la confirmation ; pour un dossier déjà dans la liste, ce qui était accordé reste coché.
    /// </summary>
    [RelayCommand]
    public async Task AddDevFolderAsync()
    {
        if (!IsIdle || _picker() is not { } picker)
            return;

        Message = null;
        string? path;
        IsBusy = true;
        try
        {
            path = await picker.PickFolderAsync(Texts.DeveloperPickTitle).ConfigureAwait(true);
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

        if (string.IsNullOrEmpty(path))
            return;

        var listed = DevFolders.FirstOrDefault(f => SamePath(f.Path, path));
        await OpenDevFolderAsync(path, listed?.Module, reload: false).ConfigureAwait(true);
    }

    /// <summary>
    /// « Recharger » : le manifeste est relu d'abord. Si le dossier déclare une permission qui n'est pas accordée, la boîte la redemande
    /// (l'accordé est pré-coché, le reste décoché et signalé) et ne recharge qu'après confirmation ; sinon le module est rechargé tout de suite.
    /// </summary>
    internal async Task ReloadDevFolderAsync(DevFolderRow row)
    {
        if (!IsIdle || row.Module is null)
            return;
        await OpenDevFolderAsync(row.Path, row.Module, reload: true).ConfigureAwait(true);
    }

    internal async Task RemoveDevFolderAsync(DevFolderRow row)
    {
        if (!IsIdle)
            return;
        await RunAsync(() => _link.RemoveDevFolderAsync(row.Path)).ConfigureAwait(true);
    }

    private async Task OpenDevFolderAsync(string path, CommunityModuleInfo? module, bool reload)
    {
        Message = null;
        PackageInfo package;
        IsBusy = true;
        try
        {
            package = await _link.InspectDevFolderAsync(path).ConfigureAwait(true);
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

        // Une autre boîte s'est ouverte pendant la lecture : celle-là reste.
        if (Dialog is not null)
            return;

        if (reload && module is not null)
        {
            if (package.Errors.Count > 0)
            {
                var problems = package.Errors.Select(UntrustedText.Block).Where(p => p.Length > 0).ToList();
                Message = problems.Count > 0 ? string.Join('\n', problems) : Texts.DeveloperNoModule;
                return;
            }

            // Rien de nouveau à accorder : on recharge tout de suite.
            if (!package.Declared.Any(p => !module.Granted.Contains(p)))
            {
                await RunAsync(() => _link.ReloadDevModuleAsync(module.Id)).ConfigureAwait(true);
                return;
            }
        }

        // Ce qui est accordé reste coché, ce que le dossier déclare en plus est décoché et signalé ; un nouveau dossier : tout décoché.
        IReadOnlyList<ModulePermission> added = module is null ? [] : [.. package.Declared.Where(p => !module.Granted.Contains(p))];
        var name = string.IsNullOrWhiteSpace(package.Name) ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) : package.Name;
        var content = new ModuleDialogContent(
            name, package.Author, package.Version, null, package.Description, null,
            package.Errors.Count > 0 ? [] : Choices(package.Declared, module, added), package.Errors);

        Func<IReadOnlyList<ModulePermission>, Task<ActionResult>> commit = reload && module is not null
            ? checkedPermissions => AddThenReloadAsync(path, package.Id ?? module.Id, checkedPermissions)
            : checkedPermissions => _link.AddDevFolderAsync(path, checkedPermissions);
        Open(new InstallModuleDialogViewModel(reload ? ModuleDialogMode.DevReload : ModuleDialogMode.DevAdd, content, commit, null, OnDialogClosed));
    }

    // Confirmer le rechargement avec une permission nouvelle : le dossier est ajouté de nouveau avec ce qui est coché, puis le module est rechargé.
    private async Task<ActionResult> AddThenReloadAsync(string path, string id, IReadOnlyList<ModulePermission> granted)
    {
        var added = await _link.AddDevFolderAsync(path, granted).ConfigureAwait(true);
        return added.Ok ? await _link.ReloadDevModuleAsync(id).ConfigureAwait(true) : added;
    }
}
