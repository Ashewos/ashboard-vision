namespace Trofeo.ModuleKit;

/// <summary>Taille en cellules du damier (colonnes × lignes).</summary>
public readonly record struct GridSize(int Columns, int Rows);

/// <summary>Préréglages de taille proposés pour un module.</summary>
public enum SizePreset
{
    /// <summary>La plus petite taille où le module reste lisible.</summary>
    Compact,

    /// <summary>La taille pour laquelle le module est pensé.</summary>
    Ideal,

    /// <summary>Une grande taille, quand le module a de la place à remplir.</summary>
    Large,
}
