using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La boîte qui prévient du retour de la bêta à la version stable, plus ancienne : une confirmation dans l'onglet Paramètres,
/// comme celles des modules. Avec la case « Attendre la prochaine version stable » cochée, rien ne s'installe : la bêta est gardée.
/// </summary>
public sealed partial class DowngradeDialogViewModel(string version, Func<bool, Task> confirm, Action cancel) : ObservableObject
{
    /// <summary>« Tu repasses à la version stable 1.0.0, plus ancienne que ta bêta… ».</summary>
    public string Text => string.Format(Texts.UpdatesDowngrade, version);

    /// <summary>La case « Attendre la prochaine version stable ».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel))]
    private bool _waitForNextStable;

    /// <summary>« Installer la version 1.0.0 » ; case cochée, le bouton dit ce qu'il fait : « Garder ma bêta ».</summary>
    public string ConfirmLabel => WaitForNextStable ? Texts.UpdatesKeepBeta : string.Format(Texts.UpdatesInstall, version);

    [RelayCommand]
    public Task ConfirmAsync() => confirm(WaitForNextStable);

    [RelayCommand]
    public void Cancel() => cancel();
}
