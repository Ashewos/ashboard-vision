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

/// <summary>Le module qu'un raccourci vise : le premier qui accepte les commandes (<see cref="Id"/> vide) ou une instance.</summary>
public sealed record ShortcutTarget(Guid? Id, string Name);

/// <summary>Une action qu'un raccourci peut envoyer : « Onglet précédent », « Onglet suivant » ou « Aller à l'onglet n ».</summary>
public sealed record ShortcutActionChoice(ModuleCommandKind Command, int Index, string Text);

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

    public ShortcutsTabViewModel(IServiceLink link, EditSession session) : base(Texts.TabShortcuts)
    {
        _link = link;
        _session = session;
        ActionChoices =
        [
            new(ModuleCommandKind.PreviousTab, 0, Texts.ShortcutPrevious),
            new(ModuleCommandKind.NextTab, 0, Texts.ShortcutNext),
            .. Enumerable.Range(0, 9).Select(n => new ShortcutActionChoice(ModuleCommandKind.SelectTab, n, string.Format(Texts.ShortcutSelect, n + 1))),
        ];
        _link.Changed += Refresh;
        _session.Changed += Refresh;
        Refresh();
    }

    public void Dispose()
    {
        _link.Changed -= Refresh;
        _session.Changed -= Refresh;
    }

    /// <summary>Les lignes : les raccourcis dans l'ordre du fichier, puis le brouillon d'un raccourci qu'on ajoute.</summary>
    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = [];

    /// <summary>Les actions qu'on peut choisir pour un nouveau raccourci.</summary>
    public IReadOnlyList<ShortcutActionChoice> ActionChoices { get; }

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

        var targets = Targets();
        var failures = Failures();
        var rows = new List<ShortcutRowViewModel>();
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            rows.Add(new ShortcutRowViewModel(
                this, binding, isDraft: false, isCapturing: _captureIndex == i, WithGone(targets, binding.Target),
                _refusal is { } r && r.Index == i ? r.Text : null,
                failures.GetValueOrDefault(binding.Keys.Trim().ToUpperInvariant())));
        }

        if (_draftAction is { } draft)
        {
            var binding = new ShortcutBinding("", draft.Command, draft.Index, _draftTarget);
            rows.Add(new ShortcutRowViewModel(
                this, binding, isDraft: true, isCapturing: _captureIndex == bindings.Count, WithGone(targets, _draftTarget),
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

    /// <summary>« Le premier qui accepte », puis les modules sur l'écran dont le type accepte les commandes (deux du même type : « Claude Code (2) »).</summary>
    private List<ShortcutTarget> Targets()
    {
        var targets = new List<ShortcutTarget> { new(null, Texts.ShortcutTargetFirst) };
        var counts = new Dictionary<string, int>();
        foreach (var placement in _session.Document.Current.Views[0].Placements)
        {
            if (!_types.TryGetValue(placement.TypeId, out var type) || !type.AcceptsCommands)
                continue;
            var rank = counts[type.TypeId] = counts.GetValueOrDefault(type.TypeId) + 1;
            targets.Add(new ShortcutTarget(placement.InstanceId, rank == 1 ? type.Label() : $"{type.Label()} ({rank})"));
        }

        return targets;
    }

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

    internal string ActionText(ShortcutBinding binding) => binding.Command switch
    {
        ModuleCommandKind.PreviousTab => Texts.ShortcutPrevious,
        ModuleCommandKind.NextTab => Texts.ShortcutNext,
        _ => string.Format(Texts.ShortcutSelect, binding.Index + 1),
    };

    // ---- Capture ----

    /// <summary>La ligne attend la combinaison (« Appuie sur la combinaison voulue »). Une autre capture en cours est abandonnée.</summary>
    public void BeginCapture(ShortcutRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (row.IsDraft)
            _draftAction ??= row.SelectedAction ?? ActionChoices[1];
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
            _refusal = (index, string.Format(Texts.ShortcutDuplicate, keys, ActionText(duplicate)));
            Refresh();
            return true;
        }

        if (isDraft)
        {
            var draft = new ShortcutBinding(keys, _draftAction!.Command, _draftAction.Index, _draftTarget);
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

    private static bool Same(string a, string b) =>
        ShortcutKeys.TryParse(a, out var modsA, out var keyA) && ShortcutKeys.TryParse(b, out var modsB, out var keyB)
            ? modsA == modsB && keyA == keyB
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---- Modifications ----

    /// <summary>« Ajouter un raccourci » : une ligne brouillon (action à choisir) attend sa combinaison ; rien n'est enregistré avant.</summary>
    [RelayCommand]
    public void AddShortcut()
    {
        DropDraft();
        _draftAction = ActionChoices[1];
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
        _draftAction = action;
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
