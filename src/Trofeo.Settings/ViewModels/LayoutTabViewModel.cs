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
/// L'onglet Disposition : la liste des modules placés et retirés, le module choisi, et les glissements sur le damier.
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

    public LayoutTabViewModel(IServiceLink link, EditSession session) : base(Texts.TabLayout)
    {
        _link = link;
        _session = session;
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

    /// <summary>Les modules placés, dans l'ordre du fichier.</summary>
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

    private ModulePlacement? SelectedPlacement =>
        _selectedId is { } id ? _document.Current.Placements.FirstOrDefault(p => p.InstanceId == id) : null;

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
        nameof(SelectedCells), nameof(SelectedPixels), nameof(SelectedMinimum),
        nameof(IsPresetCompact), nameof(IsPresetIdeal), nameof(IsPresetLarge),
        nameof(HasPresetCompact), nameof(HasPresetIdeal), nameof(HasPresetLarge),
        nameof(IsTextSmall), nameof(IsTextNormal), nameof(IsTextLarge), nameof(Spacing),
    ];

    /// <summary>Choisit un module (ou aucun : un clic sur une case vide).</summary>
    public void Select(Guid? id)
    {
        if (id is { } wanted && Placed.All(p => p.Id != wanted))
            id = null;
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
        if (!CanEdit || _session.IsBusy || _document.Current.Placements.FirstOrDefault(p => p.InstanceId == id) is not { } origin)
            return;
        var (column, row) = GridGeometry.CellAt((float)screen.X, (float)screen.Y, Spacing);
        StartDrag(new Drag(origin, ResizeEdge.Right, isResize: false, column, row));
    }

    /// <summary>Commence à redimensionner un module par l'une de ses huit poignées.</summary>
    public void BeginResize(Guid id, ResizeEdge edge)
    {
        if (!CanEdit || _session.IsBusy || _document.Current.Placements.FirstOrDefault(p => p.InstanceId == id) is not { } origin)
            return;
        StartDrag(new Drag(origin, edge, isResize: true, 0, 0));
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
            ? editor.Resize(layout, drag.Origin.InstanceId, drag.Edge, drag.DeltaColumns, drag.DeltaRows)
            : editor.Move(layout, drag.Origin.InstanceId, drag.TargetColumn, drag.TargetRow);

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
    public Task ApplyPresetAsync(SizePreset preset) =>
        SelectedId is { } id ? EditAsync((editor, layout) => editor.ApplyPreset(layout, id, preset), SelectedPlacement?.TypeId) : Task.CompletedTask;

    [RelayCommand]
    public Task SetTextSizeAsync(TextSize size) =>
        SelectedId is { } id ? EditAsync((editor, layout) => editor.SetTextSize(layout, id, size)) : Task.CompletedTask;

    [RelayCommand]
    public Task RemoveSelectedAsync() =>
        SelectedId is { } id ? EditAsync((editor, layout) => editor.Remove(layout, id)) : Task.CompletedTask;

    /// <summary>Ajoute un module du type choisi à la première place libre ; le module ajouté est choisi.</summary>
    public async Task AddAsync(AddTypeItem type)
    {
        var before = _document.Current;
        await EditAsync((editor, layout) => editor.Add(layout, type.TypeId, Guid.NewGuid())).ConfigureAwait(true);
        SelectAddedSince(before);
    }

    /// <summary>Remet un module retiré sur l'écran, avec ses réglages (l'éditeur reprend le dernier retiré de ce type).</summary>
    public async Task RestoreAsync(ShelvedModuleItem item)
    {
        var before = _document.Current;
        await EditAsync((editor, layout) =>
        {
            // L'éditeur reprend le dernier retiré du type : celui qu'on veut passe donc en dernier.
            var shelf = layout.Shelf ?? [];
            var chosen = shelf.FirstOrDefault(s => s.InstanceId == item.Id);
            var ordered = chosen is null ? layout : layout with { Shelf = [.. shelf.Where(s => s.InstanceId != item.Id), chosen] };
            return editor.Add(ordered, item.TypeId, Guid.NewGuid());
        }).ConfigureAwait(true);
        SelectAddedSince(before);
    }

    /// <summary>« Oublier » demande d'abord confirmation : « Ses réglages seront perdus. ».</summary>
    public void AskForget(ShelvedModuleItem item) => item.ConfirmingForget = true;

    public void CancelForget(ShelvedModuleItem item) => item.ConfirmingForget = false;

    public Task ForgetAsync(ShelvedModuleItem item) =>
        EditAsync((editor, layout) => editor.Forget(layout, item.Id));

    [RelayCommand]
    public Task UndoAsync() => StepHistoryAsync(undo: true);

    [RelayCommand]
    public Task RedoAsync() => StepHistoryAsync(undo: false);

    /// <summary>Choisit le module que la dernière modification a ajouté ou remis sur l'écran, s'il y en a un.</summary>
    private void SelectAddedSince(DashboardLayout before)
    {
        var added = _document.Current.Placements.FirstOrDefault(p => before.Placements.All(b => b.InstanceId != p.InstanceId));
        if (added is not null)
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
            result.Blocker is { } blocker && layout.Placements.FirstOrDefault(p => p.InstanceId == blocker) is { } other
                ? NameOf(other.TypeId)
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
        if (!ReferenceEquals(_knownTypes, _link.KnownTypes()))
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
            var placed = layout.Placements.Select(p => new PlacedModuleItem(p, NameOf(p.TypeId), IsCommunityType(p.TypeId))).ToList();
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

            var shelved = (layout.Shelf ?? []).Select(p => new ShelvedModuleItem(p, NameOf(p.TypeId), this)).ToList();
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
    private sealed class Drag(ModulePlacement origin, ResizeEdge edge, bool isResize, int grabColumn, int grabRow)
    {
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
