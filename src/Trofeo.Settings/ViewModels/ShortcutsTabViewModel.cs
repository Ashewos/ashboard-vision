using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Shortcuts;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>Le module qu'un raccourci vise : le premier qui accepte les commandes (<see cref="Id"/> vide) ou une case, de n'importe quelle vue.</summary>
public sealed record ShortcutTarget(Guid? Id, string Name);

/// <summary>
/// Une action qu'un raccourci peut envoyer : « Onglet précédent », « Onglet suivant », « Aller à l'onglet n », « Vue précédente »,
/// « Vue suivante », « Vue n° n » ou un raccourci d'un module complémentaire, qui porte alors son module et son identifiant
/// (<paramref name="Group"/> : le nom du module, pour regrouper les actions d'un même module).
/// </summary>
public sealed record ShortcutActionChoice(
    ShortcutCommand Command, int Index, string Text, string? ModuleId = null, string? ShortcutId = null, string? Group = null)
{
    public bool HasGroup => !string.IsNullOrEmpty(Group);

    /// <summary>Cette action est celle que <paramref name="binding"/> envoie.</summary>
    public bool Matches(ShortcutBinding binding) =>
        Command == binding.Command && Index == binding.Index && ModuleId == binding.ModuleId && ShortcutId == binding.ShortcutId;
}

/// <summary>
/// L'onglet Raccourcis : une ligne par raccourci (l'action en mots, les touches en pastille, le module visé), « Modifier »
/// qui attend la combinaison, « Supprimer », « Ajouter un raccourci » et « Remettre les raccourcis par défaut ». Chaque
/// modification passe par <see cref="EditSession"/> (le même historique que les autres onglets). Les doublons sont refusés
/// à la capture ; les raccourcis que Windows refuse au rechargement s'affichent sur leur ligne.
/// </summary>
public sealed partial class ShortcutsTabViewModel : TabViewModel, IDisposable
{
    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly Dictionary<string, ModuleTypeInfo> _types = [];

    // La capture en cours : l'index de la ligne (celui du brouillon vaut le nombre de raccourcis), et les raccourcis du
    // moment, pour la lâcher si la liste change sous nos pieds (annulation, fichier relu).
    private int? _captureIndex;
    private IReadOnlyList<ShortcutBinding> _snapshot = [];
    private ShortcutActionChoice? _draftAction;
    private Guid? _draftTarget;
    private (int Index, string Text)? _refusal;

    // Les modules complémentaires que le service connaît, avec les raccourcis que leurs manifestes déclarent : ils donnent leur nom
    // aux liaisons des modules et leurs actions à la liste des choix. Lus quand l'onglet s'ouvre, puis chaque fois que les modules changent ;
    // tant que l'onglet n'a pas été ouvert, la fenêtre n'a rien demandé de plus au service.
    private IReadOnlyList<CommunityModuleInfo> _community = [];
    private bool _active;
    private IReadOnlyList<ModuleTypeInfo>? _seenTypes;
    private bool _wasConnected;
    private int _communityReads;
    private readonly IReadOnlyList<ShortcutActionChoice> _builtInChoices;

    public ShortcutsTabViewModel(IServiceLink link, EditSession session) : base(Texts.TabShortcuts)
    {
        _link = link;
        _session = session;
        _builtInChoices =
        [
            new(ShortcutCommand.PreviousTab, 0, Texts.ShortcutPrevious),
            new(ShortcutCommand.NextTab, 0, Texts.ShortcutNext),
            .. Enumerable.Range(0, 9).Select(n => new ShortcutActionChoice(ShortcutCommand.SelectTab, n, string.Format(Texts.ShortcutSelect, n + 1))),
            new(ShortcutCommand.PreviousView, 0, Texts.ShortcutPreviousView),
            new(ShortcutCommand.NextView, 0, Texts.ShortcutNextView),
            .. Enumerable.Range(0, 9).Select(n => new ShortcutActionChoice(ShortcutCommand.SelectView, n, string.Format(Texts.ShortcutSelectView, n + 1))),
        ];
        ActionChoices = _builtInChoices;
        DefaultChoice = ActionChoices[1]; // « Onglet suivant »
        _wasConnected = link.State == LinkState.Connected;
        _seenTypes = link.KnownTypes();
        _link.Changed += OnLinkChanged;
        _session.Changed += Refresh;
        Refresh();
    }

    /// <summary>L'onglet s'ouvre : les modules complémentaires sont lus maintenant (leurs raccourcis donnent leurs noms et leurs actions), puis relus quand ils changent.</summary>
    public Task ActivateAsync()
    {
        _active = true;
        return _link.State == LinkState.Connected ? ReadCommunityAsync() : Task.CompletedTask;
    }

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        _session.Changed -= Refresh;
    }

    /// <summary>La liaison a changé : si l'onglet a été ouvert, les modules complémentaires se relisent à la connexion et quand leurs types changent ; puis les lignes se rafraîchissent.</summary>
    private void OnLinkChanged()
    {
        var connected = _link.State == LinkState.Connected;
        var types = _link.KnownTypes();
        var read = _active && connected && (!_wasConnected || !ReferenceEquals(types, _seenTypes));
        _wasConnected = connected;
        _seenTypes = types;
        if (read)
            _ = ReadCommunityAsync();
        Refresh();
    }

    /// <summary>Relit les modules complémentaires. Une réponse plus ancienne que la dernière demande est écartée ; un service qui ne répond pas ne change rien.</summary>
    private async Task ReadCommunityAsync()
    {
        var version = ++_communityReads;
        CommunityOverview overview;
        try
        {
            overview = await _link.GetCommunityAsync().ConfigureAwait(true);
        }
        catch (ControlException)
        {
            return;
        }

        if (version != _communityReads)
            return;
        _community = overview.Modules;
        var choices = CommunityChoices();
        if (!choices.SequenceEqual(ActionChoices.Skip(_builtInChoices.Count)))
        {
            ActionChoices = [.. _builtInChoices, .. choices];
            OnPropertyChanged(nameof(ActionChoices));
        }

        Refresh();
    }

    /// <summary>
    /// Les actions des modules complémentaires, groupées par module : celles des modules validés (en marche ou mis de côté), dans l'ordre
    /// où le service les donne. Un module n'en déclare que dix au plus (le manifeste le refuse au-delà).
    /// </summary>
    private List<ShortcutActionChoice> CommunityChoices() =>
    [
        .. _community
            .Where(m => m.State is LinkCommunityState.Ready or LinkCommunityState.SetAside)
            .SelectMany(m =>
            {
                var group = ShortcutLabels.ModuleName(_community, m.Id);
                return (m.Shortcuts ?? []).Take(10).Select(declared =>
                {
                    var name = UntrustedText.Line(declared.Name);
                    return new ShortcutActionChoice(
                        ShortcutCommand.Community, 0, name.Length > 0 ? name : UntrustedText.Line(declared.Id), m.Id, declared.Id, group);
                });
            }),
    ];

    /// <summary>Les lignes : les raccourcis dans l'ordre du fichier, puis le brouillon d'un raccourci qu'on ajoute.</summary>
    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = [];

    /// <summary>Les actions qu'on peut choisir pour un nouveau raccourci : celles du logiciel, puis celles des modules complémentaires, groupées par module.</summary>
    public IReadOnlyList<ShortcutActionChoice> ActionChoices { get; private set; }

    /// <summary>L'action d'un nouveau raccourci avant qu'on en choisisse une autre.</summary>
    internal ShortcutActionChoice DefaultChoice { get; }

    /// <summary>Un raccourci de vue ne vise aucun module : il agit sur l'écran. Les autres actions choisissent leur module.</summary>
    internal static bool TakesTarget(ShortcutCommand command) =>
        command is not (ShortcutCommand.PreviousView or ShortcutCommand.NextView or ShortcutCommand.SelectView);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Une ligne attend la combinaison : la fenêtre envoie les touches ici (<see cref="CaptureAsync"/>) au lieu de les traiter.</summary>
    public bool IsCapturing => _captureIndex is not null;

    // ---- Les lignes ----

    private IReadOnlyList<ShortcutBinding> Current => _session.Document.Current.Shortcuts ?? ShortcutDefaults.All;

    private void Refresh()
    {
        _types.Clear();
        foreach (var type in _link.Welcome?.Types ?? [])
            _types.TryAdd(type.TypeId, type);

        var bindings = Current;
        if (_captureIndex is not null && !bindings.SequenceEqual(_snapshot))
            EndCapture(); // la liste a changé pendant la capture : la ligne visée n'est peut-être plus la même

        var targets = Targets(moduleId: null);
        var failures = Failures();
        var refused = _link.Status?.RefusedShortcutKeys ?? [];
        var rows = new List<ShortcutRowViewModel>();
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var failure = refused.Any(keys => Same(keys, binding.Keys))
                ? Texts.ShortcutKeyTakenElsewhere // Windows a refusé les touches : une autre application les tient
                : failures.GetValueOrDefault(binding.Keys.Trim().ToUpperInvariant());
            rows.Add(new ShortcutRowViewModel(
                this, binding, isDraft: false, isCapturing: _captureIndex == i, WithGone(TargetsFor(binding, targets), binding.Target),
                _refusal is { } r && r.Index == i ? r.Text : null, failure));
        }

        if (_draftAction is { } draft)
        {
            var binding = new ShortcutBinding("", draft.Command, draft.Index, TakesTarget(draft.Command) ? _draftTarget : null, draft.ModuleId, draft.ShortcutId);
            rows.Add(new ShortcutRowViewModel(
                this, binding, isDraft: true, isCapturing: _captureIndex == bindings.Count, WithGone(TargetsFor(binding, targets), _draftTarget),
                _refusal is { } r && r.Index == bindings.Count ? r.Text : null, null));
        }

        if (Rows.Count != rows.Count || !Rows.Zip(rows).All(pair => pair.First.SameShape(pair.Second)))
        {
            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsCapturing));
    }

    /// <summary>
    /// « Le premier qui accepte », puis les cases de toutes les vues dont le type accepte les commandes. Une case s'appelle
    /// comme on l'a nommée, sinon comme son module ; avec plusieurs vues, le nom de la vue suit (« Claude Code — Salon »).
    /// Deux libellés identiques se distinguent par un numéro (« Claude Code (2) »). Une case d'une vue cachée reste proposée :
    /// le raccourci ne marchera que lorsque sa vue sera affichée.
    /// </summary>
    private List<ShortcutTarget> Targets(string? moduleId)
    {
        // Le raccourci d'un module complémentaire vise les cases de ce module ; sans case visée, toutes celles de la vue affichée le reçoivent.
        var targets = new List<ShortcutTarget> { new(null, moduleId is null ? Texts.ShortcutTargetFirst : Texts.ShortcutTargetAllCases) };
        var counts = new Dictionary<string, int>();
        var layout = _session.Document.Current;
        foreach (var view in layout.Views)
        {
            var viewName = UntrustedText.Line(view.Name);
            foreach (var placement in view.Placements)
            {
                if (moduleId is null
                    ? !_types.TryGetValue(placement.TypeId, out var acceptor) || !acceptor.AcceptsCommands
                    : !string.Equals(placement.TypeId, moduleId, StringComparison.Ordinal))
                    continue;
                var typeLabel = _types.TryGetValue(placement.TypeId, out var type)
                    ? type.Label()
                    : ShortcutLabels.ModuleName(_community, placement.TypeId) ?? UntrustedText.Line(placement.TypeId); // un module mis de côté n'a plus de type
                var label = UntrustedText.Line(placement.Name) is { Length: > 0 } caseName ? caseName : typeLabel;
                if (layout.Views.Count > 1 && viewName.Length > 0)
                    label = string.Format(Texts.ShortcutTargetInView, label, viewName);
                var rank = counts[label] = counts.GetValueOrDefault(label) + 1;
                targets.Add(new ShortcutTarget(placement.InstanceId, rank == 1 ? label : $"{label} ({rank})"));
            }
        }

        return targets;
    }

    /// <summary>Les modules que ce raccourci peut viser : les cases de son module pour une action de module complémentaire, sinon <paramref name="defaults"/>.</summary>
    private List<ShortcutTarget> TargetsFor(ShortcutBinding binding, List<ShortcutTarget> defaults) =>
        binding.Command == ShortcutCommand.Community ? Targets(binding.ModuleId ?? "") : defaults;

    /// <summary>Un raccourci qui vise un module qui n'est plus sur l'écran le dit au lieu de paraître viser « le premier ».</summary>
    private static IReadOnlyList<ShortcutTarget> WithGone(List<ShortcutTarget> targets, Guid? target) =>
        target is { } id && targets.All(t => t.Id != id)
            ? [.. targets, new ShortcutTarget(id, Texts.ShortcutTargetGone)]
            : targets;

    private const string FailureStart = "Raccourci « ";
    private const string FailureEnd = " » indisponible";

    /// <summary>L'avertissement est un raccourci que Windows a refusé (« Raccourci « Ctrl+Alt+Right » indisponible : … ») : il s'affiche sur sa ligne.</summary>
    internal static bool IsHotkeyFailure(string warning) =>
        warning.StartsWith(FailureStart, StringComparison.Ordinal) && warning.Contains(FailureEnd, StringComparison.Ordinal);

    /// <summary>Les avertissements de raccourcis du dernier rechargement, par touches (en majuscules).</summary>
    private Dictionary<string, string> Failures()
    {
        var failures = new Dictionary<string, string>();
        foreach (var warning in _session.OtherWarnings.Where(IsHotkeyFailure))
        {
            var close = warning.IndexOf(FailureEnd, FailureStart.Length, StringComparison.Ordinal);
            if (close >= 0)
                failures.TryAdd(warning[FailureStart.Length..close].Trim().ToUpperInvariant(), warning);
        }

        return failures;
    }

    /// <summary>L'action en mots ; pour un module complémentaire, le nom que son manifeste donne au raccourci.</summary>
    internal string ActionText(ShortcutBinding binding) => ShortcutLabels.ActionText(binding, _community);

    /// <summary>Le nom du module complémentaire d'une liaison, pour la ligne ; <c>null</c> : le raccourci n'est pas celui d'un module, ou le module n'est pas connu.</summary>
    internal string? ModuleText(ShortcutBinding binding) =>
        binding.Command == ShortcutCommand.Community ? ShortcutLabels.ModuleName(_community, binding.ModuleId) : null;

    // ---- Capture ----

    /// <summary>La ligne attend la combinaison (« Appuie sur la combinaison voulue »). Une autre capture en cours est abandonnée.</summary>
    public void BeginCapture(ShortcutRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (row.IsDraft)
            _draftAction ??= row.SelectedAction ?? DefaultChoice;
        else
            DropDraft();

        _captureIndex = index;
        _snapshot = Current;
        _refusal = null;
        Refresh();
    }

    /// <summary>Abandonne la capture (Échap, autre onglet) ; un brouillon est jeté avec elle.</summary>
    public void CancelCapture()
    {
        if (_captureIndex is null && _draftAction is null)
            return;
        EndCapture();
        Refresh();
    }

    private void EndCapture()
    {
        _captureIndex = null;
        _refusal = null;
        DropDraft();
    }

    private void DropDraft()
    {
        _draftAction = null;
        _draftTarget = null;
    }

    /// <summary>
    /// Une touche est pressée pendant la capture. Rend vrai si la capture l'a prise (la fenêtre ne doit rien en faire :
    /// Ctrl+Z n'est pas une annulation pendant la capture) et faux si aucune capture n'est en cours. Échap abandonne ; un
    /// modificateur seul ou une touche que le service ne sait pas enregistrer laisse la capture attendre ; un doublon est
    /// refusé avec son motif ; sinon le raccourci est enregistré.
    /// </summary>
    public async Task<bool> CaptureAsync(Key key, KeyModifiers modifiers)
    {
        if (_captureIndex is not { } index)
            return false;

        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            CancelCapture();
            return true;
        }

        if (ShortcutCapture.IsModifier(key))
            return true;
        if (ShortcutCapture.NeedsAltOrWin(key, modifiers))
        {
            _refusal = (index, Texts.ShortcutNeedsAltOrWin);
            Refresh();
            return true;
        }

        if (ShortcutCapture.Translate(key, modifiers) is not { } keys)
            return true;

        var bindings = Current;
        var isDraft = index >= bindings.Count;
        var other = FindOther(bindings, keys, except: isDraft ? -1 : index);
        if (other is { } duplicate)
        {
            _refusal = (index, string.Format(Texts.ShortcutDuplicate, ActionText(duplicate)));
            Refresh();
            return true;
        }

        if (isDraft)
        {
            var chosen = _draftAction!;
            var draft = new ShortcutBinding(keys, chosen.Command, chosen.Index, TakesTarget(chosen.Command) ? _draftTarget : null, chosen.ModuleId, chosen.ShortcutId);
            EndCapture();
            await Edit(list =>
            {
                list.Add(draft);
                return true;
            }).ConfigureAwait(true);
        }
        else
        {
            var original = bindings[index];
            EndCapture();
            if (Same(original.Keys, keys))
            {
                Refresh(); // les mêmes touches : rien à changer
                return true;
            }

            await Edit(list =>
            {
                if (index >= list.Count || list[index] != original)
                    return false;
                list[index] = original with { Keys = keys };
                return true;
            }).ConfigureAwait(true);
        }

        Refresh();
        return true;
    }

    /// <summary>Un autre raccourci que <paramref name="except"/> porte déjà ces touches (quelle que soit l'écriture : « alt+ctrl+right »).</summary>
    private static ShortcutBinding? FindOther(IReadOnlyList<ShortcutBinding> bindings, string keys, int except)
    {
        for (var i = 0; i < bindings.Count; i++)
        {
            if (i != except && Same(bindings[i].Keys, keys))
                return bindings[i];
        }

        return null;
    }

    private static bool Same(string a, string b) => ShortcutKeys.Same(a, b);

    // ---- Modifications ----

    /// <summary>« Ajouter un raccourci » : une ligne brouillon (action à choisir) attend sa combinaison ; rien n'est enregistré avant.</summary>
    [RelayCommand]
    public void AddShortcut()
    {
        DropDraft();
        _draftAction = DefaultChoice;
        _captureIndex = Current.Count;
        _snapshot = Current;
        _refusal = null;
        Refresh();
    }

    public async Task DeleteAsync(ShortcutRowViewModel row)
    {
        if (row.IsDraft)
        {
            CancelCapture();
            return;
        }

        var binding = row.Binding;
        var index = Rows.IndexOf(row);
        EndCapture();
        await Edit(list =>
        {
            if (index < 0 || index >= list.Count || list[index] != binding)
                return false;
            list.RemoveAt(index);
            return true;
        }).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>« Remettre les raccourcis par défaut » : <see cref="ShortcutDefaults.All"/>. Rien n'est écrit s'ils sont déjà ceux-là.</summary>
    [RelayCommand]
    public async Task ResetAsync()
    {
        EndCapture();
        await _session.ApplyAsync(layout =>
            layout.Shortcuts is null || layout.Shortcuts.SequenceEqual(ShortcutDefaults.All) ? null : layout with { Shortcuts = ShortcutDefaults.All })
            .ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Le module visé change : enregistré tout de suite pour un raccourci, gardé pour un brouillon.</summary>
    public async Task SetTargetAsync(ShortcutRowViewModel row, ShortcutTarget target)
    {
        if (row.IsDraft)
        {
            _draftTarget = target.Id;
            Refresh();
            return;
        }

        var binding = row.Binding;
        var index = Rows.IndexOf(row);
        if (binding.Target == target.Id)
            return;
        await Edit(list =>
        {
            if (index < 0 || index >= list.Count || list[index] != binding)
                return false;
            list[index] = binding with { Target = target.Id };
            return true;
        }).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>L'action d'un brouillon change (rien n'est enregistré avant la combinaison).</summary>
    internal void SetDraftAction(ShortcutActionChoice action)
    {
        if (_draftAction is null || _draftAction == action)
            return;
        var otherModule = _draftAction.ModuleId != action.ModuleId;
        _draftAction = action;
        if (!TakesTarget(action.Command) || otherModule)
            _draftTarget = null; // une action de vue ne vise aucun module ; la case choisie n'est pas celle d'un autre module
        Refresh();
    }

    /// <summary>
    /// Une modification de la liste, calculée sur celle du moment (la liste par défaut quand le fichier n'en règle aucune).
    /// <paramref name="change"/> rend faux si elle ne s'applique plus (la liste a changé entre-temps) : rien n'est écrit.
    /// </summary>
    private Task<SaveOutcome?> Edit(Func<List<ShortcutBinding>, bool> change) =>
        _session.ApplyAsync(layout =>
        {
            var list = (layout.Shortcuts ?? ShortcutDefaults.All).ToList();
            return change(list) ? layout with { Shortcuts = list } : null;
        });
}
