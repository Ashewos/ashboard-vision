using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.ViewModels.Fields;

/// <summary>
/// Un nombre entier borné : le champ <see cref="NumberField"/> (ses bornes) ou <see cref="PortField"/> (1 à 65535). La zone montre
/// la valeur écrite, ou la valeur proposée tant que rien n'est écrit. Comme un texte, la frappe ne change que le brouillon : il
/// s'enregistre à la perte du focus ou sur Entrée. Hors des bornes (ou pas un nombre entier), un message en <c>Refuse</c> le dit et
/// rien n'est écrit ; la zone vidée revient à la valeur proposée (la clé disparaît). Le nombre s'écrit en nombre JSON.
/// </summary>
public sealed partial class NumberFieldViewModel : FieldViewModel
{
    public const int PortMinimum = 1;
    public const int PortMaximum = 65535;

    private string _committed = "";

    public NumberFieldViewModel(SettingField field, int minimum, int maximum, int @default, IFieldContext context) : base(field, context)
    {
        Minimum = minimum;
        Maximum = maximum;
        Default = @default;
        _committed = Stored;
        _text = _committed;
        ValidateStored();
    }

    public int Minimum { get; }
    public int Maximum { get; }

    /// <summary>La valeur proposée tant que rien n'est écrit.</summary>
    public int Default { get; }

    /// <summary>« De 1 à 65535, 25565 par défaut. »</summary>
    public string RangeText => string.Format(Texts.ModulesNumberRange, Minimum, Maximum, Default);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _text;

    /// <summary>Le brouillon n'est pas ce qui est écrit.</summary>
    public bool IsDirty => Text != _committed;

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ce que les réglages disent : le nombre écrit (tel quel, même hors bornes) ou la valeur proposée.</summary>
    private string Stored => SettingsDocument.Get(Context.Settings, Key) is { } written && written.ValueKind is JsonValueKind.Number or JsonValueKind.String
        ? (written.ValueKind == JsonValueKind.String ? written.GetString() ?? "" : written.GetRawText())
        : Format(Default);

    private bool IsWritten => SettingsDocument.Get(Context.Settings, Key) is not null;

    private bool TryParse(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= Minimum && value <= Maximum;

    private string InvalidMessage => string.Format(Texts.ModulesNumberInvalid, Minimum, Maximum);

    // Une valeur écrite à la main hors des bornes est montrée telle quelle, avec son message.
    private void ValidateStored() => Error = IsWritten && !TryParse(_committed, out _) ? InvalidMessage : null;

    public async Task CommitAsync()
    {
        var typed = Text.Trim();
        if (typed.Length == 0)
        {
            // Vide : retour à la valeur proposée.
            Error = null;
            await EditAsync(settings => SettingsDocument.Set(settings, Key, null)).ConfigureAwait(true);
            _committed = Format(Default);
            Text = _committed;
            return;
        }

        if (!TryParse(typed, out var value))
        {
            Error = InvalidMessage; // rien n'est écrit, la saisie reste pour être corrigée
            return;
        }

        Error = null;
        var normalized = Format(value);
        // Rien d'écrit et la valeur proposée choisie : il n'y a rien à écrire. Une valeur déjà écrite reste écrite.
        if (normalized == _committed && (IsWritten || value == Default))
        {
            Text = normalized;
            return;
        }

        await EditAsync(settings => SettingsDocument.Set(settings, Key, JsonSerializer.SerializeToElement(value))).ConfigureAwait(true);
        _committed = normalized;
        Text = normalized;
        Refresh();
    }

    public override Task CommitPendingAsync() => IsDirty ? CommitAsync() : Task.CompletedTask;

    public override void Refresh()
    {
        var stored = Stored;
        var wasDirty = IsDirty;
        _committed = stored;
        if (wasDirty)
            OnPropertyChanged(nameof(IsDirty)); // le brouillon reste, mais il est peut-être devenu la valeur écrite
        else
            Text = stored;
        if (!wasDirty)
            ValidateStored();
    }
}
