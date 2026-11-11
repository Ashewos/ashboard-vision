using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Control;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.ViewModels.Fields;

/// <summary>
/// Le formulaire d'une instance de module : ses champs, tirés du schéma, et ceux qui s'affichent d'après
/// <see cref="SettingsDocument.IsVisible"/> (recalculé à chaque changement des réglages). Il ne dépend que d'un
/// <see cref="IFieldContext"/> : l'onglet Modules et l'assistant de première ouverture le construisent de la même façon.
/// </summary>
public sealed class ModuleForm : ObservableObject
{
    private readonly SettingsSchema _schema;
    private readonly IFieldContext _context;

    internal ModuleForm(SettingsSchema schema, IFieldContext context, Func<SettingField, bool>? only)
    {
        _schema = schema;
        _context = context;
        Fields =
        [
            .. schema.Fields
                .Where(field => only?.Invoke(field) ?? true)
                .Select(field => Create(field)),
        ];
        Refresh();
    }

    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>Les champs affichés, dans l'ordre du schéma. Un champ qui apparaît ou disparaît s'insère ou se retire, les autres ne sont pas refaits.</summary>
    public ObservableCollection<FieldViewModel> VisibleFields { get; } = [];

    public bool IsEmpty => Fields.Count == 0;

    public T Field<T>(string key) where T : FieldViewModel =>
        Fields.OfType<T>().Single(field => field.Key == key);

    /// <summary>Relit chaque champ dans les réglages, puis recalcule ceux qui s'affichent.</summary>
    public void Refresh()
    {
        var settings = _context.Settings;
        foreach (var field in Fields)
            field.Refresh();
        foreach (var field in Fields)
            field.IsVisible = SettingsDocument.IsVisible(field.Field, settings, _schema);

        var wanted = Fields.Where(field => field.IsVisible).ToList();
        for (var i = VisibleFields.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(VisibleFields[i]))
                VisibleFields.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= VisibleFields.Count || !ReferenceEquals(VisibleFields[i], wanted[i]))
                VisibleFields.Insert(i, wanted[i]);
        }
    }

    /// <summary>Écrit les textes tapés et pas encore validés (le focus part ailleurs, l'onglet change, une action va partir).</summary>
    public Task CommitPendingAsync() => Task.WhenAll(Fields.Select(field => field.CommitPendingAsync()));

    private FieldViewModel Create(SettingField field) => field switch
    {
        ChoiceField choice => new ChoiceFieldViewModel(choice, _context),
        ToggleField toggle => new ToggleFieldViewModel(toggle, _context),
        TextField text => new TextFieldViewModel(text, _context),
        ListField list => new ListFieldViewModel(list, _context),
        SecretField secret => new SecretFieldViewModel(secret, _context),
        LookupField lookup => new LookupFieldViewModel(lookup, _context),
        ActionField action => new ActionFieldViewModel(action, _context, CommitPendingAsync),
        NumberField number => new NumberFieldViewModel(number, number.Min, number.Max, number.Default, _context),
        PortField port => new NumberFieldViewModel(port, NumberFieldViewModel.PortMinimum, NumberFieldViewModel.PortMaximum, port.Default, _context),
        _ => throw new NotSupportedException($"Champ de réglage inconnu : {field.GetType().Name}."),
    };
}

/// <summary>Construit le formulaire d'une instance à partir du schéma de son type et de son contexte.</summary>
public static class FieldFactory
{
    /// <param name="only">Garde certains champs seulement (l'assistant ne montre que les plus utiles) ; la visibilité lit toujours tout le schéma.</param>
    public static ModuleForm CreateForm(SettingsSchema schema, IFieldContext context, Func<SettingField, bool>? only = null) =>
        new(schema, context, only);

    public static ModuleForm CreateForm(ModuleTypeInfo type, IFieldContext context, Func<SettingField, bool>? only = null) =>
        new(type.Schema, context, only);
}
