namespace Trofeo.Core.Rendering;

/// <summary>
/// Le choix du verre dans <c>layout.json</c> (clé <c>Glass</c> de <c>Appearance</c>) : un préréglage (<see cref="GlassPresets"/>),
/// ou <see cref="GlassPresets.Custom"/> avec ses propres <paramref name="Values"/>.
/// </summary>
/// <param name="Preset">Identifiant du préréglage ; « personnalise » : les valeurs de <paramref name="Values"/>.</param>
/// <param name="Values">Les valeurs du verre personnalisé ; ignorées pour un préréglage connu.</param>
public sealed record GlassAppearance(string Preset = GlassPresets.Clair, GlassSettings? Values = null)
{
    /// <summary>
    /// Les réglages du verre. Un préréglage connu donne ses valeurs (<see cref="Values"/> est ignoré) ; « personnalise » donne
    /// <see cref="Values"/> bornées (repli : Clair), ou Clair sans valeurs ; un préréglage inconnu donne Clair, avec un avertissement.
    /// Les valeurs lues dans <c>layout.json</c> sont déjà bornées à la lecture, avec un avertissement (<c>LayoutJson</c>) : la borne
    /// d'ici ne sert qu'aux valeurs construites dans le code.
    /// </summary>
    public GlassSettings Resolve(Action<string>? warn = null)
    {
        var clair = GlassPresets.Find(GlassPresets.Clair)!;
        if (GlassPresets.Find(Preset) is { } known) return known;

        if (string.Equals(Preset, GlassPresets.Custom, StringComparison.OrdinalIgnoreCase))
            return Values is null ? clair : Values.Clamped(clair);

        warn?.Invoke($"Préréglage du verre « {Preset} » inconnu : Clair utilisé.");
        return clair;
    }
}
