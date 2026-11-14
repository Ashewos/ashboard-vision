namespace Trofeo.Core.Rendering;

/// <summary>Les préréglages du verre du thème « Liquid Glass avancé » : quatre jeux de valeurs, plus l'état « personnalisé ».</summary>
public static class GlassPresets
{
    public const string Clair = "clair";
    public const string Givre = "givre";
    public const string Prisme = "prisme";
    public const string Discret = "discret";

    /// <summary>L'état « personnalisé » : les valeurs viennent de <see cref="GlassAppearance.Values"/>, pas d'un préréglage.</summary>
    public const string Custom = "personnalise";

    private static readonly GlassSettings ClairValues = new(
        Blur: 0.1f, Refraction: 0.69f, ChromaticAberration: 0.05f, EdgeHighlight: 0.3f, Specular: 0.4f, Fresnel: 1f,
        Saturation: 0.2f, Brightness: 0.05f, ShadowOpacity: 0.25f);

    private static readonly GlassSettings GivreValues = new(
        Blur: 0.6f, Refraction: 0.4f, ChromaticAberration: 0.02f, EdgeHighlight: 0.25f, Specular: 0.25f, Fresnel: 0.8f,
        Saturation: 0.3f, Tint: 0.1f, Brightness: 0.08f);

    // Les franges (ChromaticAberration) sont à 0,25 : la démo de liquidglass borne ce réglage à 0,3.
    private static readonly GlassSettings PrismeValues = new(
        Blur: 0.15f, Refraction: 0.9f, ChromaticAberration: 0.25f, EdgeHighlight: 0.5f, Specular: 0.8f, Fresnel: 1.2f);

    private static readonly GlassSettings DiscretValues = new(
        Blur: 0.3f, Refraction: 0.3f, ChromaticAberration: 0f, EdgeHighlight: 0.1f, Specular: 0.1f, Fresnel: 0.5f);

    /// <summary>Les quatre préréglages, dans l'ordre : clair, givre, prisme, discret.</summary>
    public static IReadOnlyList<string> Ids { get; } = [Clair, Givre, Prisme, Discret];

    /// <summary>Les valeurs d'un préréglage (casse ignorée) ; <c>null</c> pour <see cref="Custom"/> ou un identifiant inconnu.</summary>
    public static GlassSettings? Find(string? id)
    {
        if (id is null) return null;

        if (string.Equals(id, Clair, StringComparison.OrdinalIgnoreCase)) return ClairValues;
        if (string.Equals(id, Givre, StringComparison.OrdinalIgnoreCase)) return GivreValues;
        if (string.Equals(id, Prisme, StringComparison.OrdinalIgnoreCase)) return PrismeValues;
        if (string.Equals(id, Discret, StringComparison.OrdinalIgnoreCase)) return DiscretValues;
        return null;
    }
}
