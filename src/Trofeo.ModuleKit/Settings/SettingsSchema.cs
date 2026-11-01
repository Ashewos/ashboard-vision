using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trofeo.ModuleKit;

/// <summary>
/// Un champ de réglage d'un module. Données pures : la fenêtre de réglage (un autre processus) construit son
/// formulaire à partir du schéma, reçu en JSON, sans connaître les modules. Le discriminant <c>kind</c> porte le genre.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChoiceField), "choice")]
[JsonDerivedType(typeof(ToggleField), "toggle")]
[JsonDerivedType(typeof(TextField), "text")]
[JsonDerivedType(typeof(ListField), "list")]
[JsonDerivedType(typeof(SecretField), "secret")]
[JsonDerivedType(typeof(LookupField), "lookup")]
[JsonDerivedType(typeof(ActionField), "action")]
[JsonDerivedType(typeof(NumberField), "number")]
[JsonDerivedType(typeof(PortField), "port")]
public abstract record SettingField(string Key, string Label)
{
    /// <summary>Phrase d'aide affichée sous le champ.</summary>
    public string? Help { get; init; }

    /// <summary>Le champ n'apparaît que si le champ visé a cette valeur ; absent : toujours visible.</summary>
    public FieldCondition? VisibleWhen { get; init; }
}

/// <summary>Condition de visibilité : le champ <c>Key</c> vaut <c>Value</c> (booléen écrit « true »/« false »).</summary>
public sealed record FieldCondition(string Key, string Value);

public sealed record SettingOption(string Value, string Label);

public sealed record ChoiceField(string Key, string Label, IReadOnlyList<SettingOption> Options, string Default) : SettingField(Key, Label);

public sealed record ToggleField(string Key, string Label, bool Default) : SettingField(Key, Label);

public sealed record TextField(string Key, string Label, int MaxLength = 80) : SettingField(Key, Label)
{
    public string? Placeholder { get; init; }
}

/// <summary>Sous-ensemble ordonné des options, sans doublon.</summary>
public sealed record ListField(string Key, string Label, IReadOnlyList<SettingOption> Options, IReadOnlyList<string> Default) : SettingField(Key, Label);

/// <summary>Key est la clé du coffre, dans la portée du type de module ; jamais écrite dans Settings.</summary>
public sealed record SecretField(string Key, string Label) : SettingField(Key, Label);

/// <summary>
/// Valeur : le Value d'un <see cref="LookupResult"/>. Searchable faux : la liste vient de
/// <c>LookupAsync(Key, "")</c> à l'ouverture.
/// </summary>
public sealed record LookupField(string Key, string Label, bool Searchable = true) : SettingField(Key, Label)
{
    public string? Placeholder { get; init; }

    /// <summary>Champ qui nomme une valeur objet.</summary>
    public string LabelPath { get; init; } = "name";

    /// <summary>Texte affiché quand la valeur est absente.</summary>
    public string? EmptyLabel { get; init; }
}

public sealed record ActionField(string Key, string Label) : SettingField(Key, Label)
{
    /// <summary>Question de confirmation avant de lancer l'action ; absente : pas de confirmation.</summary>
    public string? Confirm { get; init; }
}

/// <summary>Un nombre entier entre <c>Min</c> et <c>Max</c> (bornes comprises), proposé à <c>Default</c>.</summary>
public sealed record NumberField(string Key, string Label, int Min, int Max, int Default) : SettingField(Key, Label);

/// <summary>Un numéro de port TCP, de 1 à 65535, proposé à <c>Default</c>. Les ports saisis ici sont ceux que le service relaie à un module qui a la permission « Cet ordinateur ».</summary>
public sealed record PortField(string Key, string Label, int Default) : SettingField(Key, Label);

/// <summary>Les réglages d'un type de module, dans l'ordre d'affichage.</summary>
public sealed record SettingsSchema(IReadOnlyList<SettingField> Fields)
{
    public static SettingsSchema Empty { get; } = new([]);
}

public sealed record LookupResult(string Label, JsonElement Value);

public sealed record ActionResult(bool Ok, string Message);
