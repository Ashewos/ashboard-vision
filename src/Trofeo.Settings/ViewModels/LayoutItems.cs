using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.ViewModels;

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
