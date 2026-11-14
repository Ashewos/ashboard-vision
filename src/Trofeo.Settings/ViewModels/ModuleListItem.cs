using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Une ligne de la liste de l'onglet Modules : un module placé ou retiré, avec le point d'état du service. <paramref name="name"/>
/// est le nom affiché (celui donné à la case, sinon celui du module) ; <paramref name="typeName"/> celui du module.
/// </summary>
public sealed partial class ModuleListItem(Guid id, string typeId, string name, bool isPlaced, bool isCommunity = false, string? typeName = null, string? customName = null) : ObservableObject
{
    public Guid Id { get; } = id;
    public string TypeId { get; } = typeId;
    public string Name { get; } = name;

    /// <summary>Le nom du module (nettoyé), même quand la case a un nom à elle.</summary>
    public string TypeName { get; } = typeName ?? name;

    /// <summary>Le nom donné à la case, tel qu'il est dans la disposition (brut) ; <c>null</c> : pas de nom.</summary>
    public string? CustomName { get; } = customName;

    /// <summary>Le module vient de la communauté : un point d'exclamation jaune le signale dans la liste.</summary>
    public bool IsCommunity { get; } = isCommunity;

    /// <summary>« Module complémentaire : créé par la communauté, pas par Ashboard Vision » (nom accessible et infobulle du point d'exclamation).</summary>
    public string CommunityNotice => Texts.CommunityNotice;

    /// <summary>Sur l'écran ; faux : retiré (le service ne le fait pas tourner, donc n'en dit rien).</summary>
    public bool IsPlaced { get; } = isPlaced;

    /// <summary>Ce que le service dit de l'instance ; <c>null</c> : rien (un module retiré).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsRefuse), nameof(IsIdle), nameof(HealthText))]
    private ModuleHealth? _health;

    public bool IsOnline => Health == ModuleHealth.Online;
    public bool IsRefuse => Health == ModuleHealth.Offline;
    public bool IsIdle => Health is null or ModuleHealth.NotConfigured;

    /// <summary>Les mots de l'état : « En ligne », « Hors ligne », « Non configuré » ; rien pour un module retiré.</summary>
    public string HealthText => Health switch
    {
        ModuleHealth.Online => Texts.LayoutHealthOnline,
        ModuleHealth.Offline => Texts.LayoutHealthOffline,
        ModuleHealth.NotConfigured => Texts.LayoutHealthIdle,
        _ => "",
    };

    /// <summary>Même module, même nom, même rang : seule la santé a pu changer.</summary>
    internal bool SameShape(ModuleListItem other) =>
        Id == other.Id && Name == other.Name && TypeName == other.TypeName && CustomName == other.CustomName && IsPlaced == other.IsPlaced && IsCommunity == other.IsCommunity;
}
