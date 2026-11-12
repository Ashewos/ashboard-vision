using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Un raccourci que le module propose à l'installation (ou à la mise à jour), avec sa case. Une touche libre arrive cochée ; une touche
/// déjà prise arrive décochée, <see cref="TakenBy"/> dit par qui (le logiciel : « Touche déjà prise par « Onglet suivant ». » ; ou un autre
/// logiciel de Windows). Le nom vient du manifeste (donnée non fiable) : il est nettoyé.
/// </summary>
public sealed partial class ProposedShortcutItem : ObservableObject
{
    internal ProposedShortcutItem(string moduleId, string shortcutId, string name, string keys, string? takenBy, bool takenBySoftware, bool isChecked)
    {
        ModuleId = moduleId;
        ShortcutId = shortcutId;
        var cleaned = UntrustedText.Line(name);
        Name = cleaned.Length > 0 ? cleaned : UntrustedText.Line(shortcutId);
        Keys = keys;
        KeysText = ShortcutCapture.Display(keys);
        TakenBy = takenBy;
        CanCheck = !takenBySoftware;
        _isChecked = isChecked;
    }

    public string ModuleId { get; }
    public string ShortcutId { get; }

    /// <summary>Le nom du raccourci, nettoyé.</summary>
    public string Name { get; }

    /// <summary>Les touches proposées, en syntaxe de <c>ShortcutKeys</c> (« Ctrl+Alt+Shift+R »).</summary>
    public string Keys { get; }

    /// <summary>Les touches pour la pastille : « Ctrl + Alt + Maj + R ».</summary>
    public string KeysText { get; }

    /// <summary>Par qui la touche est déjà prise, en phrase ; <c>null</c> : elle est libre.</summary>
    public string? TakenBy { get; }

    public bool HasTakenBy => !string.IsNullOrEmpty(TakenBy);

    /// <summary>Une touche que le logiciel donne déjà à une autre action ne peut pas être cochée (deux raccourcis ne partagent pas leurs touches).</summary>
    public bool CanCheck { get; }

    [ObservableProperty]
    private bool _isChecked;
}
