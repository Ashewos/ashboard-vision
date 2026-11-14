using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Le titre de la case choisie, dans les onglets Disposition et Modules : le nom que l'utilisateur lui a donné (sinon celui du
/// module), le nom officiel du module en petit dessous quand la case a un nom, et un crayon pour la renommer sur place. Le
/// nom tapé n'est enregistré qu'à la validation (Entrée, perte du focus), en une seule modification ; Échap l'abandonne.
/// </summary>
public sealed partial class CaseNameEditor : ObservableObject
{
    private readonly Func<Guid, string?, Task> _rename;
    private Guid? _id;
    private Guid? _renamingId;
    private string? _customName;

    /// <param name="rename">Enregistre le nom d'une case (<c>null</c> : la case reprend le nom de son module).</param>
    public CaseNameEditor(Func<Guid, string?, Task> rename) => _rename = rename;

    /// <summary>Le nom affiché : celui de la case, sinon celui du module.</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>Le nom du module (nettoyé), écrit en petit sous le nom donné à la case.</summary>
    [ObservableProperty]
    private string _officialName = "";

    /// <summary>La case a un nom à elle : le nom du module s'écrit dessous.</summary>
    [ObservableProperty]
    private bool _hasCustomName;

    /// <summary>Le crayon est proposé (une case est choisie et la disposition peut être modifiée).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPencil))]
    private bool _canRename;

    /// <summary>Le champ de saisie remplace le titre.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPencil), nameof(ShowsTitle))]
    private bool _isRenaming;

    /// <summary>Le nom en cours de saisie.</summary>
    [ObservableProperty]
    private string _editName = "";

    public bool ShowsPencil => CanRename && !IsRenaming;
    public bool ShowsTitle => !IsRenaming;

    /// <summary>Longueur au plus du nom d'une case.</summary>
    public static int MaxLength => LayoutView.MaxNameLength;

    /// <summary>« Renommer la case » (infobulle et nom accessible du crayon).</summary>
    public static string RenameLabel => Texts.CaseRename;

    /// <summary>
    /// Montre une case : son nom donné (brut, il est nettoyé ici) et le nom de son module. Une autre case que celle en cours de
    /// saisie abandonne la saisie, sans l'enregistrer.
    /// </summary>
    public void Show(Guid? id, string? customName, string officialName, bool canRename)
    {
        if (IsRenaming && id != _renamingId)
            Cancel();

        _id = id;
        _customName = UntrustedText.Line(customName) is { Length: > 0 } name ? name : null;
        OfficialName = officialName;
        HasCustomName = _customName is not null && _customName != officialName;
        Title = _customName ?? officialName;
        CanRename = canRename && id is not null;
        if (!CanRename && IsRenaming)
            Cancel();
    }

    /// <summary>Le crayon : le champ prend la place du titre, avec le nom actuel (tout sélectionné par la vue).</summary>
    [RelayCommand]
    public void StartRename()
    {
        if (!CanRename || _id is not { } id)
            return;
        _renamingId = id;
        EditName = Title;
        IsRenaming = true;
    }

    /// <summary>
    /// Entrée ou perte du focus : enregistre le nom tapé. Vide, ou égal au nom du module, la case n'a plus de nom à elle ;
    /// inchangé, rien n'est écrit (pas de pas d'annulation inutile).
    /// </summary>
    public async Task CommitAsync()
    {
        if (!IsRenaming || _renamingId is not { } id)
            return;
        IsRenaming = false;
        _renamingId = null;

        var typed = EditName.Trim();
        string? wanted = typed.Length == 0 || typed == OfficialName ? null : typed;
        if (wanted == _customName)
            return;
        await _rename(id, wanted).ConfigureAwait(true);
    }

    /// <summary>Échap : la saisie est abandonnée, le titre revient.</summary>
    public void Cancel()
    {
        IsRenaming = false;
        _renamingId = null;
    }
}
