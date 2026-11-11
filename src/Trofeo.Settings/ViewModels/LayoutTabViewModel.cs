using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// L'onglet Disposition : les onglets de vues, la liste des modules placés (de la vue choisie) et retirés, le module choisi,
/// et les glissements sur le damier. La vue choisie est un état de la fenêtre : l'aperçu la montre, et « L'écran suit la vue
/// choisie » la fait afficher aussi sur l'écran ; le point vert, lui, suit la vue que le service dit afficher.
/// Toute modification passe par <see cref="LayoutEditor"/> (les règles de placement), puis par
/// <see cref="LayoutDocument.SaveAsync"/> (écriture et rechargement du service) ; <see cref="LayoutHistory"/> garde de
/// quoi annuler. Une modification qui ne change rien n'écrit rien. Les points des glissements sont en pixels de l'écran
/// (1920 × 462, non tourné) : c'est le damier qui convertit depuis la bande.
/// </summary>
public sealed partial class LayoutTabViewModel : TabViewModel, IDisposable
{
    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly LayoutDocument _document;
    private readonly Dictionary<string, ModuleTypeInfo> _types = [];
    private LayoutEditor _editor = new(new Dictionary<string, ModuleDescriptor>());
    private IReadOnlyList<ModuleTypeInfo>? _knownTypes;
    private Guid? _selectedId;
    private PlacedModuleItem? _selectedItem;
    private Drag? _drag;
    private bool _refreshing;
    private readonly PreferencesStore _preferences;
    private Guid? _selectedViewId;
    private int _selectedViewIndex;
    private ViewTabItem? _selectedViewItem;
    private bool _viewChosen;
    private bool _shownSeen;
    private Guid? _shownView;
    private Guid? _previewViewId;
    private Guid? _pendingDeleteViewId;
    private string? _caseNameDraft;

    /// <param name="preferences">Les préférences de la fenêtre (« L'écran suit la vue choisie » s'y garde) ; sans elles, les valeurs par défaut, non écrites.</param>
    public LayoutTabViewModel(IServiceLink link, EditSession session, PreferencesStore? preferences = null) : base(Texts.TabLayout)
    {
        _link = link;
        _session = session;
        _preferences = preferences ?? new PreferencesStore(new EditorPreferences());
        _document = session.Document;
        _link.Changed += OnLinkChanged;
        _document.Replaced += OnReplaced;
        _session.Changed += Refresh;
        Refresh();
    }

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        _document.Replaced -= OnReplaced;
        _session.Changed -= Refresh;
    }

    /// <summary>Les onglets de vues, dans l'ordre des vues.</summary>
    public ObservableCollection<ViewTabItem> ViewTabs { get; } = [];

    /// <summary>Les modules placés dans la vue choisie, dans l'ordre du fichier.</summary>
    public ObservableCollection<PlacedModuleItem> Placed { get; } = [];

    /// <summary>Les modules retirés, dans l'ordre où ils l'ont été.</summary>
    public ObservableCollection<ShelvedModuleItem> Shelved { get; } = [];

    [ObservableProperty] private IReadOnlyList<AddTypeItem> _addTypes = [];
    [ObservableProperty] private bool _hasPlaced;
    [ObservableProperty] private bool _hasShelved;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    /// <summary>Le service a répondu à la poignée de main : on connaît les types, donc les règles de placement.</summary>
    [ObservableProperty] private bool _canEdit;

    /// <summary>Le cadre fantôme du glissement en cours ; <c>null</c> hors glissement.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefusalText), nameof(HasRefusal))]
    private DragGhost? _ghost;

    /// <summary>Le motif du dernier refus hors glissement (ajout impossible, préréglage refusé, lâcher refusé).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefusalText), nameof(HasRefusal))]
    private string? _notice;

    public bool IsDragging => _drag is not null;

    /// <summary>Le motif à montrer sous la bande : celui du fantôme refusé, sinon celui du dernier refus.</summary>
    public string? RefusalText => Ghost is { Ok: false } ghost ? ghost.Reason : Notice;

    public bool HasRefusal => !string.IsNullOrEmpty(RefusalText);

    /// <summary>Marge et écart du thème en cours : ils décident des cases du damier et des pixels des modules.</summary>
    public GridSpacing Spacing =>
        Themes.Find(_document.Current.Appearance?.Theme ?? "classique")?.Grid ?? GridSpacing.Default;

    // ---- Le module choisi ----

    public Guid? SelectedId => _selectedId;

    /// <summary>Le module choisi dans la liste (liaison à double sens avec la liste).</summary>
    public PlacedModuleItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_refreshing)
                return; // la liste se vide et se remplit : elle ne choisit rien.
            Select(value?.Id);
        }
    }

    // ---- Les vues ----

    /// <summary>La vue choisie dans les onglets ; la première si celle qu'on avait choisie n'existe plus.</summary>
    private LayoutView CurrentView =>
        _document.Current.Views.FirstOrDefault(v => v.Id == _selectedViewId) ?? _document.Current.Views[0];

    /// <summary>L'identifiant de la vue choisie : celle que montrent la liste « Sur l'écran », le damier et l'aperçu.</summary>
    public Guid SelectedViewId => CurrentView.Id;

    /// <summary>L'onglet de la vue choisie (liaison à double sens avec la rangée d'onglets).</summary>
    public ViewTabItem? SelectedView
    {
        get => _selectedViewItem;
        set
        {
            if (_refreshing || value is null)
                return; // la rangée se vide et se remplit : elle ne choisit rien.
            _ = SelectViewAsync(value);
        }
    }

    /// <summary>« L'écran suit la vue choisie » : choisir un onglet affiche aussi la vue sur l'écran. Gardé avec les préférences de la fenêtre.</summary>
    public bool FollowView
    {
        get => _preferences.Current.FollowView;
        set
        {
            if (value == _preferences.Current.FollowView)
                return;
            _preferences.Update(p => p with { FollowView = value });
            OnPropertyChanged();
            if (value)
                _ = ShowSelectedViewAsync(); // la case vient d'être cochée : l'écran rejoint la vue choisie
        }
    }

    /// <summary>La confirmation de suppression d'une vue qui a des modules ; <c>null</c> : rien à confirmer.</summary>
    public string? ViewDeleteQuestion => _pendingDeleteViewId is { } id && CurrentViews().FirstOrDefault(v => v.Id == id) is { } view
        ? string.Format(Texts.LayoutViewDeleteConfirm, ViewTabItem.Clean(view.Name))
        : null;

    public bool IsConfirmingViewDelete => ViewDeleteQuestion is not null;

    private IReadOnlyList<LayoutView> CurrentViews() => _document.Current.Views;

    /// <summary>
    /// Choisit un onglet : l'aperçu passe sur cette vue et, si « L'écran suit la vue choisie » est cochée, le service l'affiche aussi.
    /// Décochée, seul l'aperçu change et l'écran reste où il est.
    /// </summary>
    public async Task SelectViewAsync(ViewTabItem tab)
    {
        if (tab.Id == SelectedViewId || CurrentViews().All(v => v.Id != tab.Id))
            return;

        _viewChosen = true;
        CancelDrag();
        Notice = null;
        _pendingDeleteViewId = null;
        _selectedViewId = tab.Id;
        Refresh();

        if (FollowView)
            await ShowSelectedViewAsync().ConfigureAwait(true);
    }

    private async Task ShowSelectedViewAsync()
    {
        try
        {
            await _link.ShowViewAsync(SelectedViewId).ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            Notice = ex.Message; // le service ne répond pas : la vue reste choisie pour l'aperçu, l'écran n'a pas bougé
        }
    }

    /// <summary>Ajoute une vue vide (« Vue 4 »…) à la fin et la choisit. Refusé au-delà de 9 vues.</summary>
    public async Task AddViewAsync()
    {
        var id = Guid.NewGuid();
        await EditViewsAsync(layout => ViewEditor.AddView(layout, NewViewName(layout), id)).ConfigureAwait(true);
        await ChooseCreatedViewAsync(id).ConfigureAwait(true);
    }

    /// <summary>Renomme une vue (le nom est nettoyé et coupé à 30 caractères) ; un nom vide est ignoré.</summary>
    public async Task RenameViewAsync(ViewTabItem tab, string name)
    {
        tab.IsRenaming = false;
        if (string.IsNullOrWhiteSpace(name) || ViewTabItem.Clean(name) == tab.Name)
            return;
        await EditViewsAsync(layout => ViewEditor.RenameView(layout, tab.Id, name)).ConfigureAwait(true);
    }

    /// <summary>Copie une vue et ses modules juste après elle (« … (copie) ») et choisit la copie. Refusé au-delà de 9 vues.</summary>
    public async Task DuplicateViewAsync(ViewTabItem tab)
    {
        var id = Guid.NewGuid();
        await EditViewsAsync(layout => ViewEditor.DuplicateView(layout, tab.Id, id, Guid.NewGuid)).ConfigureAwait(true);
        await ChooseCreatedViewAsync(id).ConfigureAwait(true);
    }

    /// <summary>
    /// Supprime une vue ; ses modules vont dans « Retirés ». Une vue qui a des modules demande d'abord confirmation
    /// (<see cref="ViewDeleteQuestion"/>, puis <see cref="ConfirmDeleteViewAsync"/>) ; une vue vide part tout de suite.
    /// </summary>
    public Task DeleteViewAsync(ViewTabItem tab)
    {
        if (CurrentViews().Count <= 1 || CurrentViews().FirstOrDefault(v => v.Id == tab.Id) is not { } view)
            return Task.CompletedTask;

        if (view.Placements.Count == 0)
            return EditViewsAsync(layout => ViewEditor.DeleteView(layout, tab.Id));

        _pendingDeleteViewId = tab.Id;
        RaiseViewDelete();
        return Task.CompletedTask;
    }

    public async Task ConfirmDeleteViewAsync()
    {
        if (_pendingDeleteViewId is not { } id)
            return;
        _pendingDeleteViewId = null;
        RaiseViewDelete();
        await EditViewsAsync(layout => ViewEditor.DeleteView(layout, id)).ConfigureAwait(true);
    }

    public void CancelDeleteView()
    {
        _pendingDeleteViewId = null;
        RaiseViewDelete();
    }

    private void RaiseViewDelete()
    {
        OnPropertyChanged(nameof(ViewDeleteQuestion));
        OnPropertyChanged(nameof(IsConfirmingViewDelete));
    }

    /// <summary>Met l'onglet au rang <paramref name="to"/> (à partir de 0). Glisser un onglet sur un autre fait appel à lui.</summary>
    public Task MoveViewAsync(int from, int to)
    {
        var views = CurrentViews();
        if (from < 0 || from >= views.Count || to < 0 || to >= views.Count || from == to)
            return Task.CompletedTask;
        var id = views[from].Id;
        return EditViewsAsync(layout => ViewEditor.MoveView(layout, id, to));
    }

    [RelayCommand]
    private Task AddView() => AddViewAsync();

    /// <summary>« Vue 4 », ou le premier numéro libre si ce nom est pris.</summary>
    private static string NewViewName(DashboardLayout layout)
    {
        var names = layout.Views.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = layout.Views.Count + 1; ; n++)
        {
            var name = $"Vue {n}";
            if (!names.Contains(name))
                return name;
        }
    }

    /// <summary>La vue qu'on vient de créer devient la vue choisie, si la disposition l'a bien reçue.</summary>
    private async Task ChooseCreatedViewAsync(Guid id)
    {
        if (CurrentViews().FirstOrDefault(v => v.Id == id) is not null && ViewTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            await SelectViewAsync(tab).ConfigureAwait(true);
    }

    /// <summary>
    /// Une modification des vues : refusée avec son motif (« 9 vues au plus. »), sinon enregistrée dans l'historique comme les
    /// autres. <see cref="EditSession"/> les fait se suivre une à une.
    /// </summary>
    private async Task EditViewsAsync(Func<DashboardLayout, ViewEditResult> edit)
    {
        if (!CanEdit)
            return;

        Notice = null;
        ViewEditResult? refused = null;
        await _session.ApplyAsync(layout =>
        {
            var result = edit(layout);
            refused = result.Ok ? null : result;
            return result.Ok ? result.Layout : null;
        }).ConfigureAwait(true);

        if (refused is { Error: ViewError.TooManyViews })
            Notice = Texts.LayoutTooManyViews;
        Refresh();
    }

    /// <summary>Le nom d'une case dans les listes : celui que l'utilisateur lui a donné, sinon le nom de son module.</summary>
    private string DisplayName(ModulePlacement placement) =>
        UntrustedText.Line(placement.Name) is { Length: > 0 } name ? name : NameOf(placement.TypeId);

    // ---- Le nom de la case choisie ----

    /// <summary>Le nom de la case choisie, tel qu'on le tape (brouillon tant qu'il n'est pas validé) ; vide : pas de nom.</summary>
    public string CaseName
    {
        get => _caseNameDraft ?? SelectedPlacement?.Name ?? "";
        set => _caseNameDraft = value;
    }

    /// <summary>Longueur au plus du nom d'une case ou d'une vue.</summary>
    public static int NameMaxLength => LayoutView.MaxNameLength;

    /// <summary>Entrée ou perte du focus : le nom tapé est enregistré, en une seule modification (un pas d'annulation).</summary>
    public async Task CommitCaseNameAsync()
    {
        var draft = _caseNameDraft;
        _caseNameDraft = null;
        if (draft is null || SelectedId is not { } id)
            return;

        await EditAsync((editor, layout) => editor.SetName(layout, id, draft)).ConfigureAwait(true);
        OnPropertyChanged(nameof(CaseName));
    }

    private ModulePlacement? SelectedPlacement =>
        _selectedId is { } id ? CurrentView.Placements.FirstOrDefault(p => p.InstanceId == id) : null;

    private ModuleTypeInfo? SelectedType =>
        SelectedPlacement is { } p && _types.TryGetValue(p.TypeId, out var type) ? type : null;

    public bool HasSelection => SelectedPlacement is not null;
    public bool HasNoSelection => SelectedPlacement is null;
    public string SelectedName => SelectedPlacement is { } p ? NameOf(p.TypeId) : "";
    public string SelectedCells => SelectedPlacement is { } p ? string.Format(Texts.LayoutCells, p.Columns, p.Rows) : "";

    /// <summary>Les pixels du module à l'écran, avec les écarts du thème en cours.</summary>
    public string SelectedPixels
    {
        get
        {
            if (SelectedPlacement is not { } p)
                return "";
            var rect = GridGeometry.ModuleRect(p.Column, p.Row, p.Columns, p.Rows, Spacing);
            return string.Format(Texts.LayoutPixels, rect.Width, rect.Height);
        }
    }

    public string SelectedMinimum =>
        SelectedType is { } type ? string.Format(Texts.LayoutMinimum, type.MinSize.Columns, type.MinSize.Rows) : "";

    public bool IsPresetCompact => IsPreset(SizePreset.Compact);
    public bool IsPresetIdeal => IsPreset(SizePreset.Ideal);
    public bool IsPresetLarge => IsPreset(SizePreset.Large);
    public bool HasPresetCompact => HasPreset(SizePreset.Compact);
    public bool HasPresetIdeal => HasPreset(SizePreset.Ideal);
    public bool HasPresetLarge => HasPreset(SizePreset.Large);

    public bool IsTextSmall => SelectedPlacement?.TextSize == TextSize.Small;
    public bool IsTextNormal => SelectedPlacement?.TextSize == TextSize.Normal;
    public bool IsTextLarge => SelectedPlacement?.TextSize == TextSize.Large;

    private bool HasPreset(SizePreset preset) => SelectedType?.Presets.ContainsKey(preset) == true;

    private bool IsPreset(SizePreset preset) =>
        SelectedPlacement is { } p && SelectedType?.Presets.TryGetValue(preset, out var size) == true
        && size.Columns == p.Columns && size.Rows == p.Rows;

    private static readonly string[] SelectionProperties =
    [
        nameof(SelectedId), nameof(SelectedItem), nameof(HasSelection), nameof(HasNoSelection), nameof(SelectedName),
        nameof(SelectedCells), nameof(SelectedPixels), nameof(SelectedMinimum), nameof(CaseName),
        nameof(IsPresetCompact), nameof(IsPresetIdeal), nameof(IsPresetLarge),
        nameof(HasPresetCompact), nameof(HasPresetIdeal), nameof(HasPresetLarge),
        nameof(IsTextSmall), nameof(IsTextNormal), nameof(IsTextLarge), nameof(Spacing),
    ];

    /// <summary>Choisit un module (ou aucun : un clic sur une case vide).</summary>
    public void Select(Guid? id)
    {
        if (id is { } wanted && Placed.All(p => p.Id != wanted))
            id = null;
        if (id != _selectedId)
            _caseNameDraft = null; // le brouillon d'un nom ne passe pas d'une case à l'autre
        _selectedId = id;
        _selectedItem = id is { } chosen ? Placed.FirstOrDefault(p => p.Id == chosen) : null;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        foreach (var name in SelectionProperties)
            OnPropertyChanged(name);
    }

    // ---- Les glissements ----

    /// <summary>Commence à déplacer un module saisi au point <paramref name="screen"/> (pixels de l'écran).</summary>
    public void BeginMove(Guid id, Point screen)
    {
        var view = CurrentView;
        if (!CanEdit || _session.IsBusy || view.Placements.FirstOrDefault(p => p.InstanceId == id) is not { } origin)
            return;
        var (column, row) = GridGeometry.CellAt((float)screen.X, (float)screen.Y, Spacing);
        StartDrag(new Drag(view.Id, origin, ResizeEdge.Right, isResize: false, column, row));
    }

    /// <summary>Commence à redimensionner un module par l'une de ses huit poignées.</summary>
    public void BeginResize(Guid id, ResizeEdge edge)
    {
        var view = CurrentView;
        if (!CanEdit || _session.IsBusy || view.Placements.FirstOrDefault(p => p.InstanceId == id) is not { } origin)
            return;
        StartDrag(new Drag(view.Id, origin, edge, isResize: true, 0, 0));
    }

    private void StartDrag(Drag drag)
    {
        Notice = null;
        _session.ClearMessage();
        Select(drag.Origin.InstanceId);
        _drag = drag;
        OnPropertyChanged(nameof(IsDragging));
        Ghost = new DragGhost(drag.Origin.Column, drag.Origin.Row, drag.Origin.Columns, drag.Origin.Rows, true, null);
    }

    /// <summary>Le pointeur a bougé : le cadre fantôme se cale sur les cases. Rien n'est enregistré.</summary>
    public void UpdateDrag(Point screen)
    {
        if (_drag is not { } drag)
            return;

        var spacing = Spacing;
        var origin = drag.Origin;
        if (drag.IsResize)
        {
            var (dc, dr) = ResizeDeltas(drag, screen, spacing);
            drag.DeltaColumns = dc;
            drag.DeltaRows = dr;
        }
        else
        {
            var (column, row) = GridGeometry.CellAt((float)screen.X, (float)screen.Y, spacing);
            drag.TargetColumn = origin.Column + column - drag.GrabColumn;
            drag.TargetRow = origin.Row + row - drag.GrabRow;
        }

        var layout = _document.Current;
        var result = Evaluate(_editor, layout, drag);
        var (column2, row2, columns2, rows2) = drag.IsResize
            ? ResizedRect(origin, drag.Edge, drag.DeltaColumns, drag.DeltaRows)
            : (drag.TargetColumn, drag.TargetRow, origin.Columns, origin.Rows);
        Ghost = new DragGhost(column2, row2, columns2, rows2, result.Ok, result.Ok ? null : ReasonOf(layout, result, origin.TypeId));
    }

    /// <summary>Le pointeur est relâché : un cadre libre qui change quelque chose est enregistré, un cadre refusé ne l'est pas.</summary>
    public async Task EndDragAsync()
    {
        if (_drag is not { } drag)
            return;
        var ghost = Ghost;
        _drag = null;
        Ghost = null;
        OnPropertyChanged(nameof(IsDragging));

        if (ghost is { Ok: false })
        {
            Notice = ghost.Reason; // le motif reste lisible après le lâcher, jusqu'à la prochaine modification
            return;
        }

        await EditAsync((editor, layout) => Evaluate(editor, layout, drag), drag.Origin.TypeId).ConfigureAwait(true);
    }

    /// <summary>Échap : le glissement est abandonné, rien n'est enregistré.</summary>
    public void CancelDrag()
    {
        if (_drag is null)
            return;
        _drag = null;
        Ghost = null;
        Notice = null;
        OnPropertyChanged(nameof(IsDragging));
    }

    private (int Columns, int Rows) ResizeDeltas(Drag drag, Point screen, GridSpacing spacing)
    {
        var origin = drag.Origin;
        var columnEdge = GridGeometry.NearestColumnEdge((float)screen.X, spacing);
        var rowEdge = GridGeometry.NearestRowEdge((float)screen.Y, spacing);
        var dc = drag.Edge switch
        {
            ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft => columnEdge - origin.Column,
            ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight => columnEdge - (origin.Column + origin.Columns),
            _ => 0,
        };
        var dr = drag.Edge switch
        {
            ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight => rowEdge - origin.Row,
            ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight => rowEdge - (origin.Row + origin.Rows),
            _ => 0,
        };
        return (dc, dr);
    }

    private static EditResult Evaluate(LayoutEditor editor, DashboardLayout layout, Drag drag) =>
        drag.IsResize
            ? editor.Resize(layout, drag.ViewId, drag.Origin.InstanceId, drag.Edge, drag.DeltaColumns, drag.DeltaRows)
            : editor.Move(layout, drag.ViewId, drag.Origin.InstanceId, drag.TargetColumn, drag.TargetRow);

    /// <summary>
    /// Le rectangle (colonne, rangée, colonnes, rangées) que demande un redimensionnement, pour le dessiner même s'il est
    /// refusé. Sous une case, il garde une case de côté, le bord opposé restant en place.
    /// </summary>
    private static (int Column, int Row, int Columns, int Rows) ResizedRect(ModulePlacement p, ResizeEdge edge, int dc, int dr)
    {
        int column = p.Column, row = p.Row, columns = p.Columns, rows = p.Rows;
        if (edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            var right = column + columns;
            column += dc;
            columns = Math.Max(1, right - column);
            column = right - columns;
        }

        if (edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            columns = Math.Max(1, columns + dc);

        if (edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            var bottom = row + rows;
            row += dr;
            rows = Math.Max(1, bottom - row);
            row = bottom - rows;
        }

        if (edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            rows = Math.Max(1, rows + dr);

        return (column, row, columns, rows);
    }

    // ---- Les modifications ----

    [RelayCommand]
    public Task ApplyPresetAsync(SizePreset preset)
    {
        var viewId = SelectedViewId;
        return SelectedId is { } id ? EditAsync((editor, layout) => editor.ApplyPreset(layout, viewId, id, preset), SelectedPlacement?.TypeId) : Task.CompletedTask;
    }

    [RelayCommand]
    public Task SetTextSizeAsync(TextSize size) =>
        SelectedId is { } id ? EditAsync((editor, layout) => editor.SetTextSize(layout, id, size)) : Task.CompletedTask;

    [RelayCommand]
    public Task RemoveSelectedAsync()
    {
        var viewId = SelectedViewId;
        return SelectedId is { } id ? EditAsync((editor, layout) => editor.Remove(layout, viewId, id)) : Task.CompletedTask;
    }

    /// <summary>Ajoute un module du type choisi à la première place libre de la vue choisie ; le module ajouté est choisi.</summary>
    public async Task AddAsync(AddTypeItem type)
    {
        var before = _document.Current;
        var viewId = SelectedViewId;
        await EditAsync((editor, layout) => editor.Add(layout, viewId, type.TypeId, Guid.NewGuid())).ConfigureAwait(true);
        SelectAddedSince(before, viewId);
    }

    /// <summary>Remet un module retiré dans la vue choisie, avec ses réglages (l'éditeur reprend le dernier retiré de ce type).</summary>
    public async Task RestoreAsync(ShelvedModuleItem item)
    {
        var before = _document.Current;
        var viewId = SelectedViewId;
        await EditAsync((editor, layout) =>
        {
            // L'éditeur reprend le dernier retiré du type : celui qu'on veut passe donc en dernier.
            var shelf = layout.Shelf ?? [];
            var chosen = shelf.FirstOrDefault(s => s.InstanceId == item.Id);
            var ordered = chosen is null ? layout : layout with { Shelf = [.. shelf.Where(s => s.InstanceId != item.Id), chosen] };
            return editor.Add(ordered, viewId, item.TypeId, Guid.NewGuid());
        }).ConfigureAwait(true);
        SelectAddedSince(before, viewId);
    }

    /// <summary>« Oublier » demande d'abord confirmation : « Ses réglages seront perdus. ».</summary>
    public void AskForget(ShelvedModuleItem item) => item.ConfirmingForget = true;

    public void CancelForget(ShelvedModuleItem item) => item.ConfirmingForget = false;

    /// <summary>
    /// Oublie la case retirée pour de bon, avec les raccourcis qui la visaient : un seul pas d'annulation, Ctrl+Z remet la case et ses
    /// raccourcis ensemble.
    /// </summary>
    public Task ForgetAsync(ShelvedModuleItem item) =>
        EditAsync((editor, layout) =>
        {
            var result = editor.Forget(layout, item.Id);
            return result.Ok ? result with { Layout = LayoutBindings.WithoutTargets(result.Layout, [item.Id]) } : result;
        });

    [RelayCommand]
    public Task UndoAsync() => StepHistoryAsync(undo: true);

    [RelayCommand]
    public Task RedoAsync() => StepHistoryAsync(undo: false);

    /// <summary>Choisit le module que la dernière modification a ajouté ou remis sur l'écran, s'il y en a un.</summary>
    private void SelectAddedSince(DashboardLayout before, Guid viewId)
    {
        var previous = before.Views.FirstOrDefault(v => v.Id == viewId)?.Placements ?? [];
        var added = _document.Current.Views.FirstOrDefault(v => v.Id == viewId)?.Placements
            .FirstOrDefault(p => previous.All(b => b.InstanceId != p.InstanceId));
        if (added is not null && viewId == SelectedViewId)
            Select(added.InstanceId);
    }

    /// <summary>
    /// Une modification : calculée sur la disposition du moment, refusée avec son motif, ignorée si elle ne change rien,
    /// sinon enregistrée. <see cref="EditSession"/> les fait se suivre une à une et tient l'historique.
    /// </summary>
    private async Task EditAsync(Func<LayoutEditor, DashboardLayout, EditResult> edit, string? typeId = null)
    {
        if (!CanEdit)
            return;

        Notice = null;
        EditResult? refused = null;
        DashboardLayout? basis = null;
        await _session.ApplyAsync(layout =>
        {
            basis = layout;
            var result = edit(_editor, layout);
            refused = result.Ok ? null : result;
            return result.Ok ? result.Layout : null;
        }).ConfigureAwait(true);

        if (refused is not null && basis is not null)
            Notice = ReasonOf(basis, refused, typeId);
        Refresh();
    }

    private async Task StepHistoryAsync(bool undo)
    {
        if (!CanEdit || IsDragging)
            return;

        Notice = null;
        await (undo ? _session.UndoAsync() : _session.RedoAsync()).ConfigureAwait(true);
        Refresh();
    }

    private string? ReasonOf(DashboardLayout layout, EditResult result, string? typeId) => result.Error switch
    {
        PlacementError.Overlap => string.Format(Texts.LayoutBlocked,
            result.Blocker is { } blocker && layout.AllPlacements.FirstOrDefault(p => p.InstanceId == blocker) is { } other
                ? DisplayName(other)
                : ""),
        PlacementError.OutOfBounds => Texts.LayoutOutside,
        PlacementError.BelowMinSize when typeId is not null && _types.TryGetValue(typeId, out var type) =>
            string.Format(Texts.LayoutBelowMinimum, NameOf(typeId), type.MinSize.Columns, type.MinSize.Rows),
        PlacementError.NoRoom => Texts.LayoutNoRoom,
        _ => Texts.LayoutUnknownType,
    };

    // ---- La liste, tenue à jour ----

    // Le nom d'un module complémentaire vient de son manifeste : une donnée non fiable, nettoyée avant d'être montrée.
    private string NameOf(string typeId) => _types.TryGetValue(typeId, out var type) ? type.Label() : typeId;

    private bool IsCommunityType(string typeId) => _types.TryGetValue(typeId, out var type) && type.Community is not null;

    private void OnLinkChanged()
    {
        // Le service renvoie son état chaque seconde ; la liste ne se reconstruit que si ce qu'il a annoncé a changé.
        // Le point vert suit la vue que le service dit afficher : le clavier la change sans que la fenêtre y soit pour rien.
        if (!ReferenceEquals(_knownTypes, _link.KnownTypes()) || _shownView != _link.ShownViewId)
            Refresh();
        else
            RefreshHealth();
    }

    private void OnReplaced()
    {
        // Le fichier a changé en dehors des réglages (l'historique est vidé par la session) : le glissement ne vaut plus rien.
        CancelDrag();
        Notice = null;
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            _knownTypes = _link.KnownTypes();
            _types.Clear();
            foreach (var type in _knownTypes ?? [])
                _types.TryAdd(type.TypeId, type);
            _editor = LayoutEditors.From(_types.Values);
            CanEdit = _knownTypes is not null;
            if (!CanEdit)
                CancelDrag();

            var layout = _document.Current;
            ReconcileViews(layout);
            var placed = CurrentView.Placements.Select(p => new PlacedModuleItem(p, DisplayName(p), IsCommunityType(p.TypeId))).ToList();
            ApplyHealth(placed);
            if (Placed.Count == placed.Count && Placed.Zip(placed).All(pair => pair.First.SameShape(pair.Second)))
            {
                foreach (var (existing, fresh) in Placed.Zip(placed))
                    existing.Health = fresh.Health;
            }
            else
            {
                Placed.Clear();
                foreach (var item in placed)
                    Placed.Add(item);
            }

            var shelved = (layout.Shelf ?? []).Select(p => new ShelvedModuleItem(p, DisplayName(p), this)).ToList();
            if (!Shelved.Select(s => (s.Id, s.Name)).SequenceEqual(shelved.Select(s => (s.Id, s.Name))))
            {
                var confirming = Shelved.Where(s => s.ConfirmingForget).Select(s => s.Id).ToHashSet();
                Shelved.Clear();
                foreach (var item in shelved)
                {
                    item.ConfirmingForget = confirming.Contains(item.Id);
                    Shelved.Add(item);
                }
            }

            var types = _types.Values.Select(t => (t.TypeId, Name: t.Label(), IsCommunity: t.Community is not null)).ToList();
            if (!AddTypes.Select(t => (t.TypeId, t.Name, t.IsCommunity)).SequenceEqual(types))
                AddTypes = types.Select(t => new AddTypeItem(t.TypeId, t.Name, this, t.IsCommunity)).ToList();

            HasPlaced = Placed.Count > 0;
            HasShelved = Shelved.Count > 0;
            CanUndo = _session.CanUndo;
            CanRedo = _session.CanRedo;

            if (_selectedId is { } id && Placed.All(p => p.Id != id))
                _selectedId = null;
            _selectedItem = _selectedId is { } kept ? Placed.FirstOrDefault(p => p.Id == kept) : null;
        }
        finally
        {
            _refreshing = false;
        }

        RaiseSelection();
        OnPropertyChanged(nameof(SelectedView));
        OnPropertyChanged(nameof(SelectedViewId));
        OnPropertyChanged(nameof(CanDeleteView));
        RaiseViewDelete();

        // L'aperçu montre la vue choisie. Une seule demande par changement de vue : le service remplace l'abonnement d'un coup,
        // la vue qu'on quitte n'est donc jamais abonnée en même temps que la nouvelle.
        if (_previewViewId != _selectedViewId)
        {
            _previewViewId = _selectedViewId;
            _link.SetPreviewView(_selectedViewId);
        }
    }

    /// <summary>« Supprimer » est proposé tant qu'il reste plus d'une vue.</summary>
    public bool CanDeleteView => ViewTabs.Count > 1;

    /// <summary>
    /// Remet les onglets d'accord avec la disposition et le service : quelle vue est choisie, laquelle est sur l'écran. Au premier
    /// affichage, la vue choisie est celle de l'écran ; ensuite elle ne bouge que si on la choisit (ou si elle disparaît : sa voisine).
    /// </summary>
    private void ReconcileViews(DashboardLayout layout)
    {
        var views = layout.Views;
        var shown = _link.ShownViewId;
        bool Exists(Guid? id) => id is { } wanted && views.Any(v => v.Id == wanted);

        if (!Exists(_selectedViewId))
        {
            _selectedViewId = _selectedViewId is null
                ? (Exists(shown) ? shown : views[0].Id)
                : views[Math.Clamp(_selectedViewIndex, 0, views.Count - 1)].Id; // la vue choisie a disparu : sa voisine
        }
        else if (!_viewChosen && !_shownSeen && Exists(shown))
        {
            _selectedViewId = shown;
        }

        if (Exists(shown))
            _shownSeen = true;
        _shownView = shown;
        _selectedViewIndex = views.ToList().FindIndex(v => v.Id == _selectedViewId);
        if (_pendingDeleteViewId is { } pending && !Exists(pending))
            _pendingDeleteViewId = null;

        var tabs = views.Select(v => (v.Id, Name: ViewTabItem.Clean(v.Name))).ToList();
        if (!ViewTabs.Select(t => (t.Id, t.Name)).SequenceEqual(tabs))
        {
            var renaming = ViewTabs.FirstOrDefault(t => t.IsRenaming);
            ViewTabs.Clear();
            foreach (var (id, name) in tabs)
            {
                var tab = new ViewTabItem(id, name, this);
                if (renaming is not null && renaming.Id == id && renaming.Name == name)
                {
                    tab.EditName = renaming.EditName; // une modification qui n'est pas la sienne ne referme pas la saisie du nom
                    tab.IsRenaming = true;
                }

                ViewTabs.Add(tab);
            }
        }

        foreach (var tab in ViewTabs)
        {
            tab.IsOnScreen = tab.Id == shown;
            tab.IsSelected = tab.Id == _selectedViewId;
        }

        _selectedViewItem = ViewTabs.FirstOrDefault(t => t.IsSelected);
    }

    private void RefreshHealth() => ApplyHealth(Placed);

    /// <summary>Le point d'état de chaque module : ce que le service dit de son instance.</summary>
    private void ApplyHealth(IReadOnlyList<PlacedModuleItem> items)
    {
        var states = _link.Status?.Modules;
        foreach (var item in items)
            item.Health = states?.FirstOrDefault(s => s.InstanceId == item.Id)?.Health;
    }

    /// <summary>Un glissement en cours : le module d'origine, ce qui le tire, et la dernière cible calculée.</summary>
    private sealed class Drag(Guid viewId, ModulePlacement origin, ResizeEdge edge, bool isResize, int grabColumn, int grabRow)
    {
        public Guid ViewId { get; } = viewId;
        public ModulePlacement Origin { get; } = origin;
        public ResizeEdge Edge { get; } = edge;
        public bool IsResize { get; } = isResize;
        public int GrabColumn { get; } = grabColumn;
        public int GrabRow { get; } = grabRow;
        public int TargetColumn { get; set; } = origin.Column;
        public int TargetRow { get; set; } = origin.Row;
        public int DeltaColumns { get; set; }
        public int DeltaRows { get; set; }
    }
}
