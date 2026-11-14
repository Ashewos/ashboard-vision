using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

// Les vues de l'onglet Disposition (onglets, choix, ajout, renommage, copie, suppression, ordre) et le nom de la case choisie.
public sealed partial class LayoutTabViewModel
{    // ---- Les vues ----

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
        if (CurrentViews().All(v => v.Id != tab.Id))
            return;

        var same = tab.Id == SelectedViewId;
        if (!same)
        {
            _viewChosen = true;
            CancelDrag();
            Notice = null;
            _pendingDeleteViewId = null;
            _selectedViewId = tab.Id;
            Refresh();
        }

        // Cliquer l'onglet d'une vue fait passer l'écran sur elle, même si elle était déjà choisie : le clavier a pu emmener l'écran ailleurs.
        if (FollowView && (!same || _link.ShownViewId != tab.Id))
            await ShowSelectedViewAsync().ConfigureAwait(true);
    }

    private async Task ShowSelectedViewAsync()
    {
        try
        {
            if (await _link.ShowViewAsync(SelectedViewId).ConfigureAwait(true))
                return;

            // Le service ne connaît pas cette vue : sa disposition n'est pas celle de la fenêtre. Il la relit, puis on redemande une fois.
            await _session.ReloadAsync().ConfigureAwait(true);
            Refresh();
            if (CurrentViews().Any(v => v.Id == SelectedViewId))
                await _link.ShowViewAsync(SelectedViewId).ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            Notice = ex.Message; // le service ne répond pas : la vue reste choisie pour l'aperçu, l'écran n'a pas bougé
        }
    }

    /// <summary>
    /// L'onglet Disposition est celui qu'on regarde (la bande est alors son éditeur). Ailleurs, la bande montre la vue qui est sur
    /// l'écran (« L'écran en direct ») : la vue choisie, si elle est cachée, n'est plus abonnée, et ses cases de module
    /// complémentaire redeviennent cachées. En revenant, l'aperçu repart sur la vue choisie.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value == _isActive)
                return;
            _isActive = value;
            SyncPreview();
        }
    }

    /// <summary>
    /// Met l'aperçu sur la vue qui doit l'être : la vue choisie tant que l'onglet est regardé, sinon la vue affichée. Une seule demande par
    /// changement (la liaison n'a qu'un abonnement, remplacé d'un coup côté service). Se compare à ce que la liaison demande vraiment :
    /// si elle est retombée sur la vue affichée (vue refusée par le service), la demande est refaite à la modification suivante.
    /// </summary>
    private void SyncPreview()
    {
        Guid? wanted = _isActive ? _selectedViewId : null;
        if (_link.PreviewViewId != wanted)
            _link.SetPreviewView(wanted);
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
        // Sans service relié, rien ne s'enregistre : pas de confirmation qui ne ferait rien ensuite.
        if (!CanEdit || CurrentViews().Count <= 1 || CurrentViews().FirstOrDefault(v => v.Id == tab.Id) is not { } view)
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

    /// <summary>Le titre de la case choisie : son nom (ou celui du module), le nom du module dessous, et le crayon pour la renommer.</summary>
    public CaseNameEditor CaseTitle { get; }

    /// <summary>Longueur au plus du nom d'une case ou d'une vue.</summary>
    public static int NameMaxLength => LayoutView.MaxNameLength;
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
        if (_pendingDeleteViewId is { } pending && (!Exists(pending) || !CanEdit))
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
            tab.RefreshAvailability();
        }

        _selectedViewItem = ViewTabs.FirstOrDefault(t => t.IsSelected);
    }
}
