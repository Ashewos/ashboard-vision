using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Shortcuts;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Une ligne de l'onglet Raccourcis. Elle ne garde rien : l'onglet la refait quand la liste change, et c'est lui qui sait
/// quelle ligne attend une combinaison. Le brouillon (<see cref="IsDraft"/>) est le raccourci qu'on ajoute, pas encore enregistré.
/// </summary>
public sealed partial class ShortcutRowViewModel
{
    private readonly ShortcutsTabViewModel _tab;

    internal ShortcutRowViewModel(
        ShortcutsTabViewModel tab, ShortcutBinding binding, bool isDraft, bool isCapturing,
        IReadOnlyList<ShortcutTarget> targets, string? refusal, string? failure)
    {
        _tab = tab;
        Binding = binding;
        IsDraft = isDraft;
        IsCapturing = isCapturing;
        Targets = targets;
        ActionChoices = tab.ActionChoices;
        ActionText = tab.ActionText(binding);
        ModuleText = tab.ModuleText(binding);
        Refusal = refusal;
        Failure = failure;
        _selectedTarget = targets.FirstOrDefault(t => t.Id == binding.Target) ?? targets[0];
        _selectedAction = isDraft ? tab.ActionChoices.FirstOrDefault(a => a.Matches(binding)) : null;
    }

    public ShortcutBinding Binding { get; }

    /// <summary>Les touches en syntaxe de <see cref="ShortcutKeys"/> (« Ctrl+Alt+Right »).</summary>
    public string Keys => Binding.Keys;

    public string ActionText { get; }

    /// <summary>Le raccourci est celui d'un module complémentaire : la ligne porte le point d'exclamation jaune, comme partout ailleurs.</summary>
    public bool IsCommunity => Binding.Command == ShortcutCommand.Community;

    /// <summary>Le nom du module d'un raccourci de module, sous l'action ; <c>null</c> pour les autres.</summary>
    public string? ModuleText { get; }

    public bool HasModuleText => !string.IsNullOrEmpty(ModuleText);

    /// <summary>Les touches pour la pastille : « Ctrl + Alt + → ». Vide pour un brouillon.</summary>
    public string KeysText => IsDraft ? "" : ShortcutCapture.Display(Binding.Keys);

    public bool IsDraft { get; }
    public bool IsCapturing { get; }

    /// <summary>Les touches de la ligne sont montrées en pastille (pas pendant que la ligne attend sa combinaison).</summary>
    public bool ShowsKeys => !IsDraft && !IsCapturing;

    public string? Hint => IsCapturing ? Texts.ShortcutCapture : null;
    public bool HasHint => IsCapturing;

    /// <summary>« Déjà pris par « Onglet suivant ». » ; <c>null</c> : rien à refuser.</summary>
    public string? Refusal { get; }
    public bool HasRefusal => !string.IsNullOrEmpty(Refusal);

    /// <summary>Ce que Windows a refusé au rechargement pour ce raccourci (« … déjà pris par une autre application. ») ; <c>null</c> : rien.</summary>
    public string? Failure { get; }
    public bool HasFailure => !string.IsNullOrEmpty(Failure);

    public IReadOnlyList<ShortcutTarget> Targets { get; }

    /// <summary>La ligne propose un module visé : pas pour une action de vue, qui agit sur l'écran.</summary>
    public bool HasTarget => ShortcutsTabViewModel.TakesTarget(Binding.Command);
    public IReadOnlyList<ShortcutActionChoice> ActionChoices { get; }

    /// <summary>Le module visé ; le changer l'enregistre tout de suite (sauf pour un brouillon).</summary>
    public ShortcutTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || value == _selectedTarget)
                return;
            _selectedTarget = value;
            _ = _tab.SetTargetAsync(this, value);
        }
    }

    private ShortcutTarget? _selectedTarget;

    /// <summary>L'action d'un brouillon, à choisir avant la combinaison ; <c>null</c> pour un raccourci enregistré.</summary>
    public ShortcutActionChoice? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (!IsDraft || value is null || value == _selectedAction)
                return;
            _selectedAction = value;
            _tab.SetDraftAction(value);
        }
    }

    private ShortcutActionChoice? _selectedAction;

    [RelayCommand]
    private void Edit() => _tab.BeginCapture(this);

    [RelayCommand]
    private Task DeleteAsync() => _tab.DeleteAsync(this);

    /// <summary>Même contenu, même état : l'onglet ne refait pas la liste (les champs gardent leur focus) quand rien n'a changé.</summary>
    internal bool SameShape(ShortcutRowViewModel other) =>
        Binding == other.Binding && IsDraft == other.IsDraft && IsCapturing == other.IsCapturing
        && Refusal == other.Refusal && Failure == other.Failure && Targets.SequenceEqual(other.Targets)
        && ActionText == other.ActionText && ModuleText == other.ModuleText && ReferenceEquals(ActionChoices, other.ActionChoices);
}
