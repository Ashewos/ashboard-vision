namespace Trofeo.Core.Rendering;

/// <summary>Profil du biseau du verre : <see cref="Pill"/> (bord arrondi, plat au centre) ou <see cref="Dome"/> (bombé jusqu'au centre).</summary>
public enum GlassBevel
{
    /// <summary>Pastille : le bord seul réfracte, le centre est plat.</summary>
    Pill,

    /// <summary>Dôme : toute la surface est bombée.</summary>
    Dome,
}

/// <summary>
/// Un réglage du verre qui se règle au curseur : le nom de la propriété C# de <see cref="GlassSettings"/>, ses bornes et son pas
/// (les bornes sont celles des curseurs de la démo de liquidglass, sauf trois exceptions : voir <see cref="GlassSettings"/>).
/// </summary>
public sealed record GlassParameter(string Name, float Min, float Max, float Step);

/// <summary>
/// Les 17 réglages du verre du thème « Liquid Glass avancé ». Les valeurs par défaut sont celles de liquidglass (licence MIT),
/// sauf le rayon (<see cref="CornerRadius"/>, 30 comme Liquid Glass). Bornes : celles des curseurs de la démo, sauf
/// <see cref="CornerRadius"/> (0 à 49 : la moitié du plus petit côté d'une case 1 × 1), <see cref="Tint"/> et
/// <see cref="ShadowOffsetY"/> (pas de curseur dans la démo).
/// </summary>
public sealed record GlassSettings(
    float Blur = 0f,
    float Refraction = 0.69f,
    float ZRadius = 40f,
    GlassBevel Bevel = GlassBevel.Pill,
    float CornerRadius = 30f,
    float Distortion = 0f,
    float EdgeHighlight = 0.05f,
    float Specular = 0f,
    float Fresnel = 1f,
    float ChromaticAberration = 0.05f,
    float Opacity = 1f,
    float Saturation = 0f,
    float Tint = 0f,
    float Brightness = 0f,
    float ShadowOpacity = 0.3f,
    float ShadowSpread = 10f,
    float ShadowOffsetY = 1f)
{
    /// <summary>Les 16 réglages à curseur (tous sauf <see cref="Bevel"/>, qui est un choix), dans l'ordre des propriétés.</summary>
    public static IReadOnlyList<GlassParameter> Parameters { get; } =
    [
        new(nameof(Blur), 0f, 1f, 0.01f),
        new(nameof(Refraction), 0f, 2f, 0.01f),
        new(nameof(ZRadius), 1f, 100f, 1f),
        new(nameof(CornerRadius), 0f, 49f, 1f),
        new(nameof(Distortion), 0f, 1f, 0.01f),
        new(nameof(EdgeHighlight), 0f, 1f, 0.01f),
        new(nameof(Specular), 0f, 1f, 0.01f),
        new(nameof(Fresnel), 0f, 2f, 0.01f),
        new(nameof(ChromaticAberration), 0f, 0.3f, 0.005f),
        new(nameof(Opacity), 0f, 1f, 0.01f),
        new(nameof(Saturation), -1f, 1f, 0.01f),
        new(nameof(Tint), 0f, 1f, 0.01f),
        new(nameof(Brightness), -0.5f, 0.5f, 0.01f),
        new(nameof(ShadowOpacity), 0f, 1f, 0.01f),
        new(nameof(ShadowSpread), 0f, 40f, 1f),
        new(nameof(ShadowOffsetY), -20f, 20f, 1f),
    ];

    /// <summary>
    /// Borne chaque valeur à ses bornes. Une valeur <c>NaN</c> ou infinie prend celle de <paramref name="fallback"/> (elle-même
    /// bornée) ; un <see cref="Bevel"/> hors de l'énumération devient <see cref="GlassBevel.Pill"/>.
    /// </summary>
    public GlassSettings Clamped(GlassSettings fallback) => new(
        Blur: Bound(nameof(Blur), Blur, fallback.Blur),
        Refraction: Bound(nameof(Refraction), Refraction, fallback.Refraction),
        ZRadius: Bound(nameof(ZRadius), ZRadius, fallback.ZRadius),
        Bevel: Enum.IsDefined(Bevel) ? Bevel : GlassBevel.Pill,
        CornerRadius: Bound(nameof(CornerRadius), CornerRadius, fallback.CornerRadius),
        Distortion: Bound(nameof(Distortion), Distortion, fallback.Distortion),
        EdgeHighlight: Bound(nameof(EdgeHighlight), EdgeHighlight, fallback.EdgeHighlight),
        Specular: Bound(nameof(Specular), Specular, fallback.Specular),
        Fresnel: Bound(nameof(Fresnel), Fresnel, fallback.Fresnel),
        ChromaticAberration: Bound(nameof(ChromaticAberration), ChromaticAberration, fallback.ChromaticAberration),
        Opacity: Bound(nameof(Opacity), Opacity, fallback.Opacity),
        Saturation: Bound(nameof(Saturation), Saturation, fallback.Saturation),
        Tint: Bound(nameof(Tint), Tint, fallback.Tint),
        Brightness: Bound(nameof(Brightness), Brightness, fallback.Brightness),
        ShadowOpacity: Bound(nameof(ShadowOpacity), ShadowOpacity, fallback.ShadowOpacity),
        ShadowSpread: Bound(nameof(ShadowSpread), ShadowSpread, fallback.ShadowSpread),
        ShadowOffsetY: Bound(nameof(ShadowOffsetY), ShadowOffsetY, fallback.ShadowOffsetY));

    private static float Bound(string name, float value, float fallback)
    {
        var parameter = Parameters.First(p => p.Name == name);
        var chosen = float.IsFinite(value) ? value : fallback;
        // Un repli lui-même invalide (NaN) retombe sur le minimum : jamais de NaN en sortie.
        return float.IsFinite(chosen) ? Math.Clamp(chosen, parameter.Min, parameter.Max) : parameter.Min;
    }
}
