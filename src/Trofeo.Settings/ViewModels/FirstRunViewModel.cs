using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels.Fields;

namespace Trofeo.Settings.ViewModels;

/// <summary>Les étapes de l'assistant, dans l'ordre (spec 9.2).</summary>
public enum FirstRunStep { Screen, Orientation, Modules, ClaudeCode, Network, Weather, Sensors, Startup }

/// <summary>
/// L'assistant de première ouverture : une vraie suite d'étapes, numérotées par une barre de progression, qui remplace les
/// onglets tant que <see cref="EditorPreferences.FirstRunDone"/> est faux. Il ne refait rien de ce que savent déjà les onglets :
/// les modifications passent par <see cref="EditSession"/> (le même historique), les formulaires des étapes Réseau et Météo
/// sont ceux de l'onglet Modules (<see cref="FieldFactory"/>, réduits aux champs utiles), et les étapes Températures et
/// Démarrage reprennent les pièces de <see cref="SettingsTabViewModel"/>.
/// <para>
/// « Continuer » applique ce que l'étape montre (la plupart l'ont déjà appliqué en direct) ; « Passer » avance sans rien changer.
/// Les étapes dont le module n'est pas placé n'existent pas : le compteur (« Étape 3 sur 8 ») ne compte que celles qui sont là,
/// et se recalcule dès que la disposition change.
/// </para>
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private const string ClaudeType = "claudecode";
    private const string NetworkType = "network";
    private const string WeatherType = "clock";

    private static readonly FirstRunStep[] AllSteps = Enum.GetValues<FirstRunStep>();

    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly PreferencesStore _preferences;
    private readonly ISecretAccess _secrets;
    private readonly TimeProvider _time;
    private readonly FormSlot _claude = new(ClaudeType, field => field is ActionField);
    // Réseau : la source et ses réglages, pas « Oublier le certificat ». Météo : l'unité et la ville, pas le format de l'heure.
    private readonly FormSlot _network = new(NetworkType, field => field is not ActionField);
    private readonly FormSlot _weather = new(WeatherType, field => field.Key is "unit" or "location");

    private FirstRunStep _step = FirstRunStep.Screen;
    private IReadOnlyList<FirstRunStep> _visible = AllSteps;
    private Welcome? _welcome;
    private LayoutEditor _editor = LayoutEditors.From([]);

    public FirstRunViewModel(
        IServiceLink link, EditSession session, PreferencesStore preferences, SettingsTabViewModel settings,
        ScreenPillViewModel pill, ISecretAccess secrets, TimeProvider? time = null)
    {
        _link = link;
        _session = session;
        _preferences = preferences;
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
        Settings = settings;
        Pill = pill;
        _link.Changed += OnLinkChanged;
        _session.Changed += Recompute;
        Recompute();
    }

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        _session.Changed -= Recompute;
    }

    /// <summary>L'onglet Paramètres : l'assistant en reprend le bloc des capteurs et l'interrupteur de démarrage.</summary>
    public SettingsTabViewModel Settings { get; }

    /// <summary>La pastille de l'écran, la même que celle de l'en-tête.</summary>
    public ScreenPillViewModel Pill { get; }

    // ---- Les étapes ----

    public FirstRunStep Step => _step;

    /// <summary>Les étapes qui existent, dans l'ordre : celles dont le module n'est pas placé sont absentes.</summary>
    public IReadOnlyList<FirstRunStep> VisibleSteps => _visible;

    public int StepCount => _visible.Count;
    public int StepNumber => _visible.ToList().IndexOf(_step) + 1;
    public string ProgressText => string.Format(Texts.FirstRunProgress, StepNumber, StepCount);
    public bool IsLastStep => StepNumber == StepCount;
    public string ContinueText => IsLastStep ? Texts.FirstRunFinish : Texts.FirstRunContinue;

    /// <summary>« Continuer » manque à l'étape du sens : « Oui » en tient lieu.</summary>
    public bool ShowsContinue => _step != FirstRunStep.Orientation;

    public string StepTitle => _step switch
    {
        FirstRunStep.Screen => Texts.FirstRunScreenTitle,
        FirstRunStep.Orientation => Texts.FirstRunOrientationTitle,
        FirstRunStep.Modules => Texts.FirstRunModulesTitle,
        FirstRunStep.ClaudeCode => Texts.FirstRunClaudeTitle,
        FirstRunStep.Network => Texts.FirstRunNetworkTitle,
        FirstRunStep.Weather => Texts.FirstRunWeatherTitle,
        FirstRunStep.Sensors => Texts.SettingsSensors,
        _ => Texts.FirstRunStartupTitle,
    };

    public bool IsScreenStep => _step == FirstRunStep.Screen;
    public bool IsOrientationStep => _step == FirstRunStep.Orientation;
    public bool IsModulesStep => _step == FirstRunStep.Modules;
    public bool IsClaudeStep => _step == FirstRunStep.ClaudeCode;
    public bool IsNetworkStep => _step == FirstRunStep.Network;
    public bool IsWeatherStep => _step == FirstRunStep.Weather;
    public bool IsSensorsStep => _step == FirstRunStep.Sensors;
    public bool IsStartupStep => _step == FirstRunStep.Startup;

    /// <summary>Rouvre l'assistant à la première étape (« Relancer l'assistant »).</summary>
    public void Restart()
    {
        StartupWanted = true;
        foreach (var item in Modules)
            item.Reason = null;
        _step = FirstRunStep.Screen;
        Recompute(force: true);
    }

    /// <summary>« Continuer » : applique ce que l'étape montre, puis passe à la suivante (ou termine, à la dernière).</summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        switch (_step)
        {
            case FirstRunStep.ClaudeCode:
                await CommitAsync(_claude).ConfigureAwait(true);
                break;
            case FirstRunStep.Network:
                await CommitAsync(_network).ConfigureAwait(true);
                break;
            case FirstRunStep.Weather:
                await CommitAsync(_weather).ConfigureAwait(true);
                break;
            case FirstRunStep.Startup:
                if (!ApplyStartup())
                    return; // Windows a refusé : la raison est sous l'interrupteur, on reste pour la lire
                break;
        }

        Advance();
    }

    /// <summary>« Passer » : avance sans rien changer. À la dernière étape, termine.</summary>
    [RelayCommand]
    public void Skip() => Advance();

    private static Task CommitAsync(FormSlot slot) => slot.Form?.CommitPendingAsync() ?? Task.CompletedTask;

    private void Advance()
    {
        var index = _visible.ToList().IndexOf(_step);
        if (index + 1 >= _visible.Count)
        {
            Finish();
            return;
        }

        _step = _visible[index + 1];
        RaiseStep();
    }

    /// <summary>« Terminer » : l'assistant ne se rouvrira plus tout seul. La fenêtre rend la place aux onglets.</summary>
    private void Finish() => _preferences.Update(p => p with { FirstRunDone = true });

    // ---- 1. L'écran ----

    private ServiceStatus? Status => _link.Status;

    /// <summary>Ce qu'il y a à dire de l'écran ; <c>null</c> : rien (le service ne répond pas encore).</summary>
    public string? ScreenMessage => ReadScreen().Message;

    public bool HasScreenMessage => ScreenMessage is not null;

    /// <summary>L'écran est occupé ou débranché : le message est en <c>Amber</c>.</summary>
    public bool ScreenMessageIsProblem => ReadScreen().IsProblem;

    private (string? Message, bool IsProblem) ReadScreen() => Status switch
    {
        null => (null, false),
        { Screen.Reason: { } reason } when reason.Contains("TRCC", StringComparison.OrdinalIgnoreCase) => (Texts.FirstRunScreenBusy, true),
        { Emulated: true } => (Texts.FirstRunScreenEmulated, false),
        { Screen.State: LinkScreenState.Disconnected } => (Texts.FirstRunScreenMissing, true),
        { Screen.State: LinkScreenState.Ready } => (Texts.FirstRunScreenReady, false),
        _ => (null, false),
    };

    // ---- 2. Le sens ----

    /// <summary>« Oui » : le texte est à l'endroit, on continue sans rien changer.</summary>
    [RelayCommand]
    public void AnswerYes() => Advance();

    /// <summary>« Non, retourner » : bascule la rotation et l'applique tout de suite ; on reste, pour voir si c'est mieux.</summary>
    [RelayCommand]
    public Task AnswerNoAsync() => Settings.SetRotationAsync(_session.Document.Current.Rotation == 180 ? 0 : 180);

    // ---- 3. Les modules ----

    /// <summary>Une case par type de module que le service annonce.</summary>
    public ObservableCollection<FirstRunModuleItem> Modules { get; } = [];

    /// <summary>La dernière modification lancée par une case de la fenêtre ; les tests l'attendent.</summary>
    public Task PendingModuleChange { get; private set; } = Task.CompletedTask;

    internal void StartModuleChange(FirstRunModuleItem item, bool wanted) =>
        PendingModuleChange = SetModuleAsync(item.TypeId, wanted);

    /// <summary>
    /// Coche (<c>Add</c> : la première place libre, et les réglages du dernier module retiré de ce type) ou décoche
    /// (<c>Remove</c> : le module rejoint les retirés avec ses réglages). Faute de place, la case reste décochée et dit pourquoi.
    /// </summary>
    public async Task SetModuleAsync(string typeId, bool wanted)
    {
        var item = Modules.FirstOrDefault(m => m.TypeId == typeId);
        if (item is null)
            return;

        item.Reason = null;
        string? reason = null;
        await _session.ApplyAsync(layout =>
        {
            var placed = layout.Views[0].Placements.Where(p => p.TypeId == typeId).ToList();
            if (wanted)
            {
                if (placed.Count > 0)
                    return null;
                var added = _editor.Add(layout, layout.Views[0].Id, typeId, Guid.NewGuid());
                if (!added.Ok)
                    reason = added.Error == PlacementError.NoRoom ? Texts.LayoutNoRoom : Texts.LayoutUnknownType;
                return added.Ok ? added.Layout : null;
            }

            var edited = layout;
            foreach (var module in placed)
            {
                var removed = _editor.Remove(edited, edited.Views[0].Id, module.InstanceId);
                if (!removed.Ok)
                {
                    reason = Texts.LayoutUnknownType;
                    return null;
                }

                edited = removed.Layout;
            }

            return placed.Count == 0 ? null : edited;
        }).ConfigureAwait(true);

        item.Reason = reason;
        Recompute(force: true); // la case revient d'accord avec la disposition (refus : décochée)
    }

    // ---- 4 à 6. Les formulaires ----

    /// <summary>Claude Code : l'action « Installer le mod des quotas », seule.</summary>
    public ModuleForm? ClaudeForm => _claude.Form;

    public ModuleForm? NetworkForm => _network.Form;
    public ModuleForm? WeatherForm => _weather.Form;

    // ---- 8. Le démarrage avec Windows ----

    private bool _startupWanted = true;

    /// <summary>
    /// L'interrupteur : sur « oui » au départ, mais rien n'est inscrit dans Windows avant « Continuer » à cette étape
    /// (« Passer » le laisse éteint).
    /// </summary>
    public bool StartupWanted
    {
        get => _startupWanted;
        set => SetProperty(ref _startupWanted, value);
    }

    /// <summary>Inscrit (ou retire) le démarrage d'après l'interrupteur. Faux si Windows a refusé.</summary>
    private bool ApplyStartup()
    {
        if (!Settings.CanToggleStartup || Settings.StartupEnabled == _startupWanted)
            return true; // Trofeo.Display.exe est introuvable (le message est déjà là) ou rien à changer
        Settings.StartupEnabled = _startupWanted;
        return Settings.StartupEnabled == _startupWanted;
    }

    // ---- État ----

    private void OnLinkChanged()
    {
        OnPropertyChanged(nameof(ScreenMessage));
        OnPropertyChanged(nameof(HasScreenMessage));
        OnPropertyChanged(nameof(ScreenMessageIsProblem));
        Recompute();
    }

    private void Recompute() => Recompute(force: false);

    /// <summary>Remet l'assistant d'accord avec la disposition et le catalogue du service : étapes, cases, formulaires.</summary>
    private void Recompute(bool force)
    {
        if (!ReferenceEquals(_welcome, _link.Welcome))
        {
            _welcome = _link.Welcome;
            _editor = LayoutEditors.From(_welcome?.Types ?? []);
            Modules.Clear();
            foreach (var type in _welcome?.Types.GroupBy(t => t.TypeId).Select(g => g.First()) ?? [])
                Modules.Add(new FirstRunModuleItem(type.TypeId, type.Label(), this, type.Community is not null));
        }

        var layout = _session.Document.Current;
        foreach (var item in Modules)
            item.Sync(layout.Views[0].Placements.Any(p => p.TypeId == item.TypeId));

        var visible = AllSteps.Where(step => step switch
        {
            FirstRunStep.ClaudeCode => layout.Views[0].Placements.Any(p => p.TypeId == ClaudeType),
            FirstRunStep.Network => layout.Views[0].Placements.Any(p => p.TypeId == NetworkType),
            FirstRunStep.Weather => layout.Views[0].Placements.Any(p => p.TypeId == WeatherType),
            _ => true,
        }).ToList();

        var stepsChanged = force || !visible.SequenceEqual(_visible);
        _visible = visible;
        if (!visible.Contains(_step))
        {
            // L'étape courante n'existe plus (Ctrl+Z a retiré son module) : la suivante qui existe.
            _step = visible.FirstOrDefault(step => step > _step, visible[^1]);
            stepsChanged = true;
        }

        var formsChanged = SyncForm(_claude, nameof(ClaudeForm))
            | SyncForm(_network, nameof(NetworkForm))
            | SyncForm(_weather, nameof(WeatherForm));
        if (stepsChanged || formsChanged)
            RaiseStep();
    }

    /// <summary>Le formulaire d'un module placé : refait quand l'instance ou le type change, relu sinon. Vrai s'il a été refait ou retiré.</summary>
    private bool SyncForm(FormSlot slot, string property)
    {
        var placed = _session.Document.Current.Views[0].Placements.FirstOrDefault(p => p.TypeId == slot.TypeId);
        var type = _welcome?.Types.FirstOrDefault(t => t.TypeId == slot.TypeId);
        if (placed is null || type is null)
        {
            if (slot.Form is null)
                return false;
            slot.Reset(null, null, null);
            OnPropertyChanged(property);
            return true;
        }

        if (slot.Form is not null && slot.Id == placed.InstanceId && ReferenceEquals(slot.Type, type))
        {
            slot.Form.Refresh();
            return false;
        }

        var context = new InstanceFieldContext(_session, _link, _secrets, placed.TypeId, placed.InstanceId, _time);
        slot.Reset(FieldFactory.CreateForm(type, context, slot.Only), placed.InstanceId, type);
        OnPropertyChanged(property);
        return true;
    }

    private void RaiseStep()
    {
        foreach (var name in new[]
                 {
                     nameof(Step), nameof(VisibleSteps), nameof(StepCount), nameof(StepNumber), nameof(ProgressText), nameof(IsLastStep),
                     nameof(ContinueText), nameof(ShowsContinue), nameof(StepTitle), nameof(IsScreenStep), nameof(IsOrientationStep),
                     nameof(IsModulesStep), nameof(IsClaudeStep), nameof(IsNetworkStep), nameof(IsWeatherStep), nameof(IsSensorsStep),
                     nameof(IsStartupStep),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Le formulaire d'un type de module, avec ce pour quoi il a été construit.</summary>
    private sealed class FormSlot(string typeId, Func<SettingField, bool> only)
    {
        public string TypeId { get; } = typeId;
        public Func<SettingField, bool> Only { get; } = only;
        public ModuleForm? Form { get; private set; }
        public Guid? Id { get; private set; }
        public ModuleTypeInfo? Type { get; private set; }

        public void Reset(ModuleForm? form, Guid? id, ModuleTypeInfo? type)
        {
            Form = form;
            Id = id;
            Type = type;
        }
    }
}

/// <summary>Une case de l'étape des modules : un type, coché quand un module de ce type est sur l'écran.</summary>
public sealed partial class FirstRunModuleItem(string typeId, string name, FirstRunViewModel owner, bool isCommunity = false) : ObservableObject
{
    private bool _isChecked;
    private bool _syncing;

    public string TypeId { get; } = typeId;
    public string Name { get; } = name;

    /// <summary>Un module de la communauté (déjà validé) : le même point d'exclamation jaune que dans les autres listes.</summary>
    public bool IsCommunity { get; } = isCommunity;

    public string CommunityNotice => Texts.CommunityNotice;

    /// <summary>L'état de la case (liaison à double sens) ; le changer ajoute ou retire le module.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
            if (!_syncing)
                owner.StartModuleChange(this, value);
        }
    }

    /// <summary>Pourquoi le module n'a pas pu être ajouté (« Plus de place libre… ») ; <c>null</c> : rien.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    private string? _reason;

    public bool HasReason => !string.IsNullOrEmpty(Reason);

    /// <summary>La case reprend l'état de la disposition, sans rien écrire.</summary>
    internal void Sync(bool isPlaced)
    {
        _syncing = true;
        try
        {
            if (_isChecked != isPlaced)
            {
                _isChecked = isPlaced;
                OnPropertyChanged(nameof(IsChecked));
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
