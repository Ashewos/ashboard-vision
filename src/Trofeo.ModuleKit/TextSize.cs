namespace Trofeo.ModuleKit;

/// <summary>Taille du texte réglable par module.</summary>
public enum TextSize
{
    /// <summary>Petit (facteur 0,85).</summary>
    Small,

    /// <summary>Normal (facteur 1).</summary>
    Normal,

    /// <summary>Grand (facteur 1,2).</summary>
    Large,
}

/// <summary>Le facteur d'une taille de texte.</summary>
public static class TextSizeExtensions
{
    /// <summary>Facteur multiplicatif : 0.85 / 1.0 / 1.2.</summary>
    public static float Factor(this TextSize s) => s switch
    {
        TextSize.Small => 0.85f,
        TextSize.Normal => 1.0f,
        TextSize.Large => 1.2f,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };
}
