using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Un dossier de l'onglet Modules : les cases d'une vue. Avec plusieurs vues, chaque dossier a son en-tête (le nom de la vue,
/// le point vert si elle est sur l'écran, le nombre de cases) et se replie d'un clic ; avec une seule vue, la liste est
/// montrée seule, sans en-tête. La case choisie est celle de l'onglet, quel que soit le dossier où elle se trouve.
/// </summary>
public sealed partial class ModuleGroupItem : ObservableObject
{
    private readonly ModulesTabViewModel _owner;

    internal ModuleGroupItem(Guid viewId, ModulesTabViewModel owner)
    {
        ViewId = viewId;
        _owner = owner;
    }

    public Guid ViewId { get; }

    /// <summary>Le nom de la vue (nettoyé).</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>La vue est sur l'écran : un point vert devant son nom.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>Plusieurs vues : l'en-tête du dossier s'affiche.</summary>
    [ObservableProperty]
    private bool _showsHeader;

    /// <summary>Le dossier est ouvert : ses cases s'affichent (gardé tant que la vue existe).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsItems))]
    private bool _isExpanded = true;

    /// <summary>« 3 modules » (nombre de cases de la vue).</summary>
    [ObservableProperty]
    private string _countText = "";

    public ObservableCollection<ModuleListItem> Items { get; } = [];

    /// <summary>Les cases s'affichent : dossier ouvert, ou pas d'en-tête (une seule vue).</summary>
    public bool ShowsItems => IsExpanded || !ShowsHeader;

    partial void OnShowsHeaderChanged(bool value) => OnPropertyChanged(nameof(ShowsItems));

    /// <summary>La case choisie, si elle est dans ce dossier (liaison à double sens avec sa liste).</summary>
    public ModuleListItem? SelectedItem
    {
        get => _owner.Selected is { IsPlaced: true } item && Items.Contains(item) ? item : null;
        set
        {
            if (value is not null)
                _owner.SelectFromList(value.Id);
        }
    }

    internal void RaiseSelection() => OnPropertyChanged(nameof(SelectedItem));

    /// <summary>Un clic sur l'en-tête ouvre ou ferme le dossier.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
