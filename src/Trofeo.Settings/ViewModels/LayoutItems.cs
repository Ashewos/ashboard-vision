using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Un onglet de vue de l'onglet Disposition. Le point vert (<see cref="IsOnScreen"/>) marque la vue que le service affiche sur l'écran,
/// qui n'est pas forcément la vue choisie (<see cref="IsSelected"/>).
/// </summary>
public sealed partial class ViewTabItem(Guid id, string name, LayoutTabViewModel owner) : ObservableObject
{
    public Guid Id { get; } = id;

    /// <summary>Le nom de la vue, nettoyé (il peut venir d'une disposition partagée).</summary>
    public string Name { get; } = name;

    /// <summary>La vue est sur l'écran en ce moment : le point vert.</summary>
    [ObservableProperty] private bool _isOnScreen;

    /// <summary>La vue est celle que montrent l'éditeur et l'aperçu.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Le nom est en cours de saisie (menu « Renommer »).</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>Le nom tel qu'on le tape.</summary>
    [ObservableProperty] private string _editName = name;

    public string OnScreenText => Texts.LayoutViewOnScreen;

    /// <summary>Le menu de l'onglet ne sert que service relié (sans lui, rien ne s'enregistre).</summary>
    public bool CanEdit => owner.CanEdit;

    /// <summary>« Supprimer » n'est pas proposé pour la dernière vue.</summary>
    public bool CanDelete => owner.CanEdit && owner.CanDeleteView;

    internal void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>Le texte d'une vue, sans ce qui peut tromper l'œil ; vide : le nom de la première vue, pour qu'un onglet ne soit jamais muet.</summary>
    public static string Clean(string? name) => UntrustedText.Line(name) is { Length: > 0 } cleaned ? cleaned : LayoutView.FirstViewName;

    [RelayCommand]
    private void Rename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private Task CommitRename() => IsRenaming ? owner.RenameViewAsync(this, EditName) : Task.CompletedTask;

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task Duplicate() => owner.DuplicateViewAsync(this);

    [RelayCommand]
    private Task Delete() => owner.DeleteViewAsync(this);
}

/// <summary>Un module placé sur l'écran, tel que la colonne « Sur l'écran » et le damier le montrent.</summary>
public sealed partial class PlacedModuleItem(ModulePlacement placement, string name, bool isCommunity = false) : ObservableObject
{
    public Guid Id { get; } = placement.InstanceId;
    public string TypeId { get; } = placement.TypeId;
    public string Name { get; } = name;
    /// <summary>Le module vient de la communauté : un point d'exclamation jaune le signale dans la liste et dans un angle de sa case.</summary>
    public bool IsCommunity { get; } = isCommunity;

    public string CommunityNotice => Texts.CommunityNotice;

    public int Column { get; } = placement.Column;
    public int Row { get; } = placement.Row;
    public int Columns { get; } = placement.Columns;
    public int Rows { get; } = placement.Rows;

    /// <summary>« 8 × 4 ».</summary>
    public string SizeText { get; } = string.Format(Texts.LayoutSize, placement.Columns, placement.Rows);

    /// <summary>Ce que le service dit du module ; <c>null</c> : rien (non configuré).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsRefuse), nameof(IsIdle), nameof(HealthText))]
    private ModuleHealth? _health;

    public bool IsOnline => Health == ModuleHealth.Online;
    public bool IsRefuse => Health == ModuleHealth.Offline;
    public bool IsIdle => Health is null or ModuleHealth.NotConfigured;

    public string HealthText => Health switch
    {
        ModuleHealth.Online => Texts.LayoutHealthOnline,
        ModuleHealth.Offline => Texts.LayoutHealthOffline,
        _ => Texts.LayoutHealthIdle,
    };

    /// <summary>Même module, même nom, même place : seule la santé a pu changer.</summary>
    internal bool SameShape(PlacedModuleItem other) =>
        Id == other.Id && Name == other.Name && IsCommunity == other.IsCommunity && Column == other.Column && Row == other.Row && Columns == other.Columns && Rows == other.Rows;
}

/// <summary>Un module retiré de l'écran, gardé avec ses réglages.</summary>
public sealed partial class ShelvedModuleItem(ModulePlacement placement, string name, LayoutTabViewModel owner) : ObservableObject
{
    public Guid Id { get; } = placement.InstanceId;
    public string TypeId { get; } = placement.TypeId;
    public string Name { get; } = name;

    /// <summary>« Oublier » a été demandé : la ligne attend la confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConfirming))]
    private bool _confirmingForget;

    public bool IsNotConfirming => !ConfirmingForget;

    [RelayCommand]
    private Task Restore() => owner.RestoreAsync(this);

    [RelayCommand]
    private void AskForget() => owner.AskForget(this);

    [RelayCommand]
    private void CancelForget() => owner.CancelForget(this);

    [RelayCommand]
    private Task ConfirmForget() => owner.ForgetAsync(this);
}

/// <summary>Un type de module que le service sait créer : une ligne de la liste « Ajouter un module ».</summary>
public sealed partial class AddTypeItem(string typeId, string name, LayoutTabViewModel owner, bool isCommunity = false) : ObservableObject
{
    public string TypeId { get; } = typeId;
    public string Name { get; } = name;

    /// <summary>Le type vient d'un module de la communauté : un point d'exclamation jaune le signale dans la liste.</summary>
    public bool IsCommunity { get; } = isCommunity;

    public string CommunityNotice => Texts.CommunityNotice;

    [RelayCommand]
    private Task Add() => owner.AddAsync(this);
}

/// <summary>
/// Le cadre fantôme d'un glissement, en cases : libre (<c>Ok</c>) ou refusé avec son motif. Un cadre refusé garde la
/// taille et la place demandées, même hors du damier.
/// </summary>
public sealed record DragGhost(int Column, int Row, int Columns, int Rows, bool Ok, string? Reason);
