using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Core.Rendering;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// Un curseur du verre. Les bornes et le pas viennent de <see cref="GlassSettings.Parameters"/> (rien n'est copié ici).
/// Bouger le curseur ne sauve rien : <see cref="IsDirty"/> note qu'un brouillon attend le lâcher, que
/// <see cref="GlassSectionViewModel.CommitAsync"/> enregistre.
/// </summary>
public sealed class GlassSliderItem : ObservableObject
{
    private double _value;

    internal GlassSliderItem(GlassParameter parameter, string label)
    {
        Id = parameter.Name;
        Label = label;
        Min = Exact(parameter.Min);
        Max = Exact(parameter.Max);
        Step = Exact(parameter.Step);
        Decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(Step)), 0, 4);
    }

    /// <summary>Le nom de la propriété de <see cref="GlassSettings"/> que ce curseur règle.</summary>
    public string Id { get; }

    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    /// <summary>Le nombre de décimales montrées, selon le pas (1 : aucune ; 0,01 : deux ; 0,005 : trois).</summary>
    private int Decimals { get; }

    /// <summary>Un brouillon tiré au curseur, pas encore enregistré.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// La valeur montrée. La changer à la main (curseur, clavier) fait un brouillon, borné et arrondi au pas ; l'enregistrer est
    /// le travail de <see cref="GlassSectionViewModel.CommitAsync"/>, au lâcher.
    /// </summary>
    public double Value
    {
        get => _value;
        set
        {
            var snapped = Snap(value);
            if (snapped == _value)
                return;
            _value = snapped;
            IsDirty = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public string ValueText => _value.ToString("F" + Decimals, CultureInfo.CurrentCulture);

    /// <summary>La valeur enregistrée, bornée et arrondie au pas.</summary>
    public float Current => (float)_value;

    /// <summary>La valeur du fichier : pas un brouillon (le brouillon est écarté).</summary>
    internal void Load(float value)
    {
        _value = Snap(value);
        IsDirty = false;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
    }

    private double Snap(double value)
    {
        if (!double.IsFinite(value))
            return _value;
        var stepped = Math.Round((value - Min) / Step) * Step + Min;
        return Math.Round(Math.Clamp(stepped, Min, Max), 4) + 0.0;   // + 0,0 : pas de -0
    }

    /// <summary>Le float tel qu'on l'écrit (0,01 et pas 0,00999999977648258), pour que les bornes du curseur soient justes.</summary>
    private static double Exact(float value) => (double)(decimal)value;
}

/// <summary>Un bouton de préréglage du verre (Clair, Givré, Prisme, Discret).</summary>
public sealed partial class GlassPresetItem : ObservableObject
{
    public GlassPresetItem(string id, string label, Func<Task> select)
    {
        Id = id;
        Label = label;
        SelectCommand = new AsyncRelayCommand(select);
    }

    public string Id { get; }
    public string Label { get; }
    public IAsyncRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Un groupe de curseurs (Verre, Lumière, Couleur, Ombre) avec son titre.</summary>
public sealed record GlassGroup(string Title, IReadOnlyList<GlassSliderItem> Sliders);
