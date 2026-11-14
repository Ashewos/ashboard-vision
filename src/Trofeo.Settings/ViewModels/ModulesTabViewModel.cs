using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels.Fields;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// L'onglet Modules : à gauche les modules placés puis les retirés, chacun avec son point d'état ; à droite, pour le
/// module choisi, son état en mots, les messages du module (<c>ActionMessage</c>, remarques) en <c>Amber</c>, puis le
/// formulaire tiré du schéma de son type (<see cref="ModuleForm"/>). La fenêtre ne connaît aucun module : tout vient du
/// schéma annoncé par le service. Les réglages passent par <see cref="EditSession"/> (le même historique que les autres
/// onglets) ; les secrets vont dans le coffre, jamais dans la disposition.
/// </summary>
public sealed partial class ModulesTabViewModel : TabViewModel, IDisposable
{
    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private readonly ISecretAccess _secrets;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ModuleTypeInfo> _types = [];
    private IReadOnlyList<ModuleTypeInfo>? _knownTypes;
    private Guid? _selectedId;
    private bool _refreshing;

    // Ce pour quoi le formulaire courant a été construit : il n'est refait que si l'un des deux change.
    private Guid? _formId;
    private ModuleTypeInfo? _formType;

    private string? _actionMessage;
    private IReadOnlyList<string> _notices = [];

    public ModulesTabViewModel(IServiceLink link, EditSession session, ISecretAccess secrets, TimeProvider? time = null)
        : base(Texts.TabModules)
    {
        _link = link;
        _session = session;
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
        _link.Changed += OnLinkChanged;
        _session.Changed += Refresh;
        Refresh();
    }

    public void Dispose()
    {
        _link.Changed -= OnLinkChanged;
        _session.Changed -= Refresh;
    }

    /// <summary>Les modules sur l'écran, dans l'ordre du fichier.</summary>
    public ObservableCollection<ModuleListItem> Placed { get; } = [];

    /// <summary>Les modules retirés de l'écran.</summary>
    public ObservableCollection<ModuleListItem> Shelved { get; } = [];

    public bool HasPlaced => Placed.Count > 0;
    public bool HasShelved => Shelved.Count > 0;

    // ---- Le module choisi ----

    public Guid? SelectedId => _selectedId;

    public ModuleListItem? Selected =>
        _selectedId is { } id ? Placed.FirstOrDefault(p => p.Id == id) ?? Shelved.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>La ligne choisie dans « Sur l'écran » (liaison à double sens avec la liste).</summary>
    public ModuleListItem? SelectedPlaced
    {
        get => Selected is { IsPlaced: true } item ? item : null;
        set
        {
            if (!_refreshing && value is not null)
                Select(value.Id);
        }
    }

    /// <summary>La ligne choisie dans « Retirés ».</summary>
    public ModuleListItem? SelectedShelved
    {
        get => Selected is { IsPlaced: false } item ? item : null;
        set
        {
            if (!_refreshing && value is not null)
                Select(value.Id);
        }
    }

    private ModuleTypeInfo? SelectedType => Selected is { } item && _types.TryGetValue(item.TypeId, out var type) ? type : null;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;
    public string SelectedName => Selected?.Name ?? "";

    // ---- Un module complémentaire : le point d'exclamation, la mention et les permissions accordées ----

    private CommunityTypeInfo? SelectedCommunity => SelectedType?.Community;

    /// <summary>Le module choisi vient de la communauté : le point d'exclamation jaune et la mention s'affichent dans son en-tête.</summary>
    public bool SelectedIsCommunity => SelectedCommunity is not null;

    /// <summary>« Module complémentaire : créé par la communauté, pas par Ashboard Vision ».</summary>
    public string SelectedCommunityNotice => Texts.CommunityNotice;

    /// <summary>« par Alex · version 1.2.0 » (texte du module, nettoyé).</summary>
    public string SelectedCommunityDetails => SelectedCommunity is { } community
        ? string.Format(Texts.CommunityAuthorVersion, UntrustedText.Line(community.Author), UntrustedText.Line(community.Version))
        : "";

    /// <summary>La description que le module donne de lui-même (texte brut, nettoyé).</summary>
    public string SelectedCommunityDescription => SelectedCommunity is { } community ? UntrustedText.Block(community.Description) : "";

    public bool HasSelectedCommunityDescription => SelectedCommunityDescription.Length > 0;

    /// <summary>« Permissions accordées : Internet. » ou « Aucune permission accordée. ».</summary>
    public string SelectedCommunityPermissions => SelectedCommunity is { } community ? PermissionLabels.List(community.Granted) : "";

    /// <summary>Les mots de l'état du module placé ; rien pour un module retiré.</summary>
    public string SelectedHealthText => Selected?.HealthText ?? "";

    public bool HasHealth => Selected is { IsPlaced: true };
    public bool SelectedOnline => Selected?.IsOnline == true;
    public bool SelectedRefuse => Selected?.IsRefuse == true;
    public bool SelectedIdle => Selected is not null && Selected.IsIdle;

    /// <summary>Le message du module (« Appuie sur ► sur ta Freebox… ») ; <c>null</c> : rien.</summary>
    public string? ActionMessage => _actionMessage;

    public bool HasActionMessage => !string.IsNullOrEmpty(_actionMessage);

    /// <summary>Les remarques du module sur sa configuration (« 3 lignes sur 5 tiennent à cette taille. »).</summary>
    public IReadOnlyList<string> Notices => _notices;

    public bool HasNotices => _notices.Count > 0;

    /// <summary>Le formulaire du module choisi ; <c>null</c> : aucun module, type sans schéma ou service inconnu.</summary>
    public ModuleForm? Form { get; private set; }

    public bool HasForm => Form is not null;

    /// <summary>Le type du module choisi n'a rien à régler : « Ce module n'a rien à régler. ».</summary>
    public bool HasNoSchema => Selected is not null && SelectedType is { Schema.Fields.Count: 0 };

    /// <summary>Choisit un module de l'une des deux listes. Le texte encore tapé du module quitté est écrit d'abord.</summary>
    public void Select(Guid? id)
    {
        if (id is { } wanted && Find(wanted) is null)
            id = null;
        if (id == _selectedId)
            return;

        _selectedId = id;
        UpdateForm(); // le formulaire quitté écrit son texte tapé avant d'être remplacé
        RaiseSelection();
    }

    /// <summary>Écrit les textes tapés et pas encore validés (l'onglet change, le module change).</summary>
    public async Task CommitPendingAsync()
    {
        if (Form is { } form)
            await form.CommitPendingAsync().ConfigureAwait(true);
    }

    private void CommitPending()
    {
        if (Form is not { } form)
            return;
        // Pas d'attente : l'écriture passe par la file de la session, après ce qui est déjà en route.
        _ = form.CommitPendingAsync();
    }

    private ModuleListItem? Find(Guid id) => Placed.FirstOrDefault(p => p.Id == id) ?? Shelved.FirstOrDefault(p => p.Id == id);

    // ---- Les listes, tenues à jour ----

    private void OnLinkChanged()
    {
        // Le service renvoie son état chaque seconde ; les listes et le formulaire ne se refont que si ce qu'il a annoncé a changé.
        if (!ReferenceEquals(_knownTypes, _link.KnownTypes()))
            Refresh();
        else
            RefreshStatus();
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

            var layout = _session.Document.Current;
            Fill(Placed, [.. layout.AllPlacements.Select(p => new ModuleListItem(p.InstanceId, p.TypeId, NameOf(p.TypeId), isPlaced: true, IsCommunityType(p.TypeId)))]);
            Fill(Shelved, [.. (layout.Shelf ?? []).Select(p => new ModuleListItem(p.InstanceId, p.TypeId, NameOf(p.TypeId), isPlaced: false, IsCommunityType(p.TypeId)))]);

            // Le module choisi n'existe plus (oublié) ou rien n'est choisi encore : le premier module de l'écran.
            if (_selectedId is not { } id || Find(id) is null)
                _selectedId = Placed.FirstOrDefault()?.Id ?? Shelved.FirstOrDefault()?.Id;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateForm();
        OnPropertyChanged(nameof(HasPlaced));
        OnPropertyChanged(nameof(HasShelved));
        RaiseSelection();
    }

    /// <summary>Met la collection au même contenu sans la refaire quand rien n'a changé (la liste garde sa sélection et son défilement).</summary>
    private static void Fill(ObservableCollection<ModuleListItem> target, List<ModuleListItem> fresh)
    {
        if (target.Count == fresh.Count && target.Zip(fresh).All(pair => pair.First.SameShape(pair.Second)))
            return;
        target.Clear();
        foreach (var item in fresh)
            target.Add(item);
    }

    // Le nom d'un module complémentaire vient de son manifeste : une donnée non fiable, nettoyée avant d'être montrée.
    private string NameOf(string typeId) => _types.TryGetValue(typeId, out var type) ? type.Label() : typeId;

    private bool IsCommunityType(string typeId) => _types.TryGetValue(typeId, out var type) && type.Community is not null;

    /// <summary>L'état de chaque module, ses messages et ses remarques : ce que le service en dit.</summary>
    private void RefreshStatus()
    {
        var states = _link.Status?.Modules;
        foreach (var item in Placed)
            item.Health = states?.FirstOrDefault(s => s.InstanceId == item.Id)?.Health ?? ModuleHealth.NotConfigured; // absent de l'état : non configuré
        foreach (var item in Shelved)
            item.Health = null;

        var state = Selected is { IsPlaced: true } placed ? states?.FirstOrDefault(s => s.InstanceId == placed.Id) : null;
        var message = string.IsNullOrWhiteSpace(state?.ActionMessage) ? null : state.ActionMessage;
        if (message != _actionMessage)
        {
            _actionMessage = message;
            OnPropertyChanged(nameof(ActionMessage));
            OnPropertyChanged(nameof(HasActionMessage));
        }

        IReadOnlyList<string> notices = state?.Notices ?? [];
        if (!notices.SequenceEqual(_notices))
        {
            _notices = notices;
            OnPropertyChanged(nameof(Notices));
            OnPropertyChanged(nameof(HasNotices));
        }

        OnPropertyChanged(nameof(SelectedHealthText));
        OnPropertyChanged(nameof(SelectedOnline));
        OnPropertyChanged(nameof(SelectedRefuse));
        OnPropertyChanged(nameof(SelectedIdle));
    }

    private void RaiseSelection()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedId), nameof(Selected), nameof(SelectedPlaced), nameof(SelectedShelved), nameof(HasSelection),
                     nameof(HasNoSelection), nameof(SelectedName), nameof(HasHealth), nameof(HasNoSchema),
                     nameof(SelectedIsCommunity), nameof(SelectedCommunityNotice), nameof(SelectedCommunityDetails), nameof(SelectedCommunityPermissions), nameof(SelectedCommunityDescription), nameof(HasSelectedCommunityDescription),
                 })
            OnPropertyChanged(name);
        RefreshStatus();
    }

    // ---- Le formulaire ----

    private void UpdateForm()
    {
        var item = Selected;
        var type = SelectedType;
        if (item is null || type is null || type.Schema.Fields.Count == 0)
        {
            SetForm(null, null, null);
            return;
        }

        if (Form is not null && _formId == item.Id && ReferenceEquals(_formType, type))
        {
            Form.Refresh(); // même module : les réglages ont pu changer (annulation, autre onglet), le brouillon est gardé
            return;
        }

        var context = new InstanceFieldContext(_session, _link, _secrets, item.TypeId, item.Id, _time);
        SetForm(FieldFactory.CreateForm(type, context), item.Id, type);
    }

    private void SetForm(ModuleForm? form, Guid? id, ModuleTypeInfo? type)
    {
        if (!ReferenceEquals(Form, form))
            CommitPending();
        Form = form;
        _formId = id;
        _formType = type;
        OnPropertyChanged(nameof(Form));
        OnPropertyChanged(nameof(HasForm));
    }
}
