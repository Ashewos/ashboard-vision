using SkiaSharp;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Le verre du thème « Liquid Glass avancé » : le programme <c>FS_GLASS</c> de liquidglass (licence MIT, voir
/// <c>liquid-glass.sksl</c> et <c>LICENSE-liquidglass.txt</c>), adapté en SkSL et exécuté par le processeur.
/// Le verre et l'ombre sont deux programmes, compilés une fois à la construction.
/// </summary>
internal sealed class LiquidGlassEffect : IDisposable
{
    private const string ResourceName = "Trofeo.Core.Rendering.LiquidGlass.liquid-glass.sksl";
    private const string SectionMarker = "//#section ";

    private readonly SKRuntimeEffect _glass;
    private readonly SKRuntimeEffect _shadow;

    /// <summary>Compile les deux programmes. Une erreur de compilation lève <see cref="InvalidOperationException"/> avec le message de Skia.</summary>
    public LiquidGlassEffect()
    {
        var sections = ReadSections();
        _glass = Compile("glass", sections);
        try
        {
            _shadow = Compile("shadow", sections);
        }
        catch
        {
            _glass.Dispose();
            throw;
        }
    }

    /// <summary>Les poids du flou à 9 points de l'original (FS_BLUR), du centre vers le bord.</summary>
    private static readonly double[] BlurWeights = [0.227027, 0.194594, 0.121622, 0.054054, 0.016216];

    /// <summary>
    /// Le σ du flou de Skia, en pixels de l'écran, équivalent au flou de l'original : 6 passes séparables d'un flou à 9 points,
    /// au pas <c>2,5 × blur</c>. Chaque prise tombe à <c>k × pas</c> du pixel et la lit en filtrage linéaire, ce qui ajoute
    /// la variance <c>f × (1 − f)</c> (f, la partie fractionnaire de la position) : sans elle, le flou des petits réglages
    /// serait sous-estimé (σ 1,04 au lieu de 1,41 pour 0,1). La variance d'une passe est
    /// <c>Σ poids × ((k × pas)² + f × (1 − f))</c>, et σ = √(6 × variance).
    /// </summary>
    public static float BlurSigma(float blur)
    {
        if (!(blur > 0f)) return 0f;

        var step = 2.5 * blur;
        var variance = 0.0;
        for (var k = 1; k < BlurWeights.Length; k++)
        {
            var offset = k * step;
            var fraction = offset - Math.Floor(offset);
            variance += 2.0 * BlurWeights[k] * (offset * offset + fraction * (1.0 - fraction));
        }

        return (float)Math.Sqrt(6.0 * variance);
    }

    /// <summary>
    /// La marge à ajouter autour du verre pour contenir toute l'ombre visible : à 3 étendues du bord, l'ombre de
    /// l'original vaut moins d'un millième. S'y ajoutent le décalage vertical et 2 px (l'ombre commence à 1 px du bord).
    /// </summary>
    public static float ShadowMargin(GlassSettings s) =>
        MathF.Ceiling(3f * MathF.Max(s.ShadowSpread, 1f) + MathF.Abs(s.ShadowOffsetY) + 2f);

    /// <summary>
    /// Rend le verre de <paramref name="shape"/> (en coordonnées d'écran). <paramref name="sharp"/> et
    /// <paramref name="blurred"/> sont la même scène, nette et floutée, de même taille, dont le coin haut gauche est en
    /// <paramref name="imageOrigin"/> à l'écran. Le résultat a la taille de <paramref name="shape"/> arrondie au pixel
    /// supérieur et se pose en <c>shape.Location</c> ; il est en alpha prémultiplié, et son alpha vaut (masque × opacité)².
    /// Le carré reproduit ce que montre la démo de liquidglass, dont le canevas n'est pas prémultiplié : une sortie
    /// (c, a) de <c>FS_GLASS</c> s'y voit comme la couleur c·a à l'alpha a².
    /// </summary>
    public SKImage RenderGlass(SKImage sharp, SKImage blurred, SKPoint imageOrigin, SKRect shape, GlassSettings s)
    {
        ArgumentNullException.ThrowIfNull(sharp);
        ArgumentNullException.ThrowIfNull(blurred);
        ArgumentNullException.ThrowIfNull(s);
        if (blurred.Width != sharp.Width || blurred.Height != sharp.Height)
        {
            throw new ArgumentException(
                $"La scène floutée ({blurred.Width} × {blurred.Height}) n'a pas la taille de la scène nette ({sharp.Width} × {sharp.Height}).",
                nameof(blurred));
        }

        var width = Math.Max(1, (int)MathF.Ceiling(shape.Width));
        var height = Math.Max(1, (int)MathF.Ceiling(shape.Height));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        var place = SKMatrix.CreateTranslation(imageOrigin.X, imageOrigin.Y);

        using var sharpShader = sharp.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, place);
        using var blurredShader = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling, place);
        using var uniforms = new SKRuntimeEffectUniforms(_glass);
        uniforms["u_origin"] = new[] { shape.Left, shape.Top };
        uniforms["u_size"] = new[] { shape.Width, shape.Height };
        uniforms["u_radius"] = s.CornerRadius;
        uniforms["u_refract"] = s.Refraction;
        uniforms["u_chroma"] = s.ChromaticAberration;
        uniforms["u_edgeHL"] = s.EdgeHighlight;
        uniforms["u_spec"] = s.Specular;
        uniforms["u_fresnel"] = s.Fresnel;
        uniforms["u_distort"] = s.Distortion;
        uniforms["u_alpha"] = s.Opacity;
        uniforms["u_sat"] = s.Saturation;
        uniforms["u_tint"] = s.Tint;
        uniforms["u_zRadius"] = s.ZRadius;
        uniforms["u_brightness"] = s.Brightness;
        uniforms["u_bevelMode"] = s.Bevel == GlassBevel.Dome ? 1f : 0f;
        using var children = new SKRuntimeEffectChildren(_glass);
        children["u_sharp"] = sharpShader;
        children["u_blurred"] = blurredShader;

        using var shader = _glass.ToShader(uniforms, children);
        return Render(shader, width, height);
    }

    /// <summary>
    /// Rend l'ombre de <paramref name="shape"/> : une image noire translucide qui couvre <paramref name="area"/> (la forme
    /// élargie de <see cref="ShadowMargin"/>, calée sur les pixels), vide à l'intérieur du rectangle arrondi.
    /// Son alpha est le carré de celui de <c>FS_GLASS</c>, comme à l'écran dans la démo (canevas non prémultiplié).
    /// Rend <c>null</c> quand l'opacité de l'ombre est nulle.
    /// </summary>
    public SKImage? RenderShadow(SKRect shape, GlassSettings s, out SKRect area)
    {
        ArgumentNullException.ThrowIfNull(s);

        var margin = ShadowMargin(s);
        area = new SKRect(
            MathF.Floor(shape.Left - margin),
            MathF.Floor(shape.Top - margin),
            MathF.Ceiling(shape.Right + margin),
            MathF.Ceiling(shape.Bottom + margin));
        if (s.ShadowOpacity <= 0f) return null;

        using var uniforms = new SKRuntimeEffectUniforms(_shadow);
        uniforms["u_origin"] = new[] { shape.MidX - area.Left, shape.MidY - area.Top };
        uniforms["u_size"] = new[] { shape.Width, shape.Height };
        uniforms["u_radius"] = s.CornerRadius;
        uniforms["u_shadowAlpha"] = s.ShadowOpacity;
        uniforms["u_shadowSpread"] = s.ShadowSpread;
        uniforms["u_shadowOffY"] = s.ShadowOffsetY;
        using var children = new SKRuntimeEffectChildren(_shadow);

        using var shader = _shadow.ToShader(uniforms, children);
        return Render(shader, (int)area.Width, (int)area.Height);
    }

    public void Dispose()
    {
        _glass.Dispose();
        _shadow.Dispose();
    }

    /// <summary>Exécute le programme sur une image de cette taille, par le processeur, en alpha prémultiplié.</summary>
    private static SKImage Render(SKShader shader, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Impossible de créer une surface de {width} × {height} pour le verre.");
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawPaint(paint);
        return surface.Snapshot();
    }

    private static SKRuntimeEffect Compile(string section, IReadOnlyDictionary<string, string> sections)
    {
        var source = sections["common"] + "\n" + sections[section];
        var effect = SKRuntimeEffect.CreateShader(source, out var errors);
        return effect ?? throw new InvalidOperationException($"Le programme du verre (« {section} ») ne compile pas : {errors}");
    }

    /// <summary>Lit la ressource embarquée et la découpe en sections, d'après ses lignes <c>//#section nom</c>.</summary>
    private static Dictionary<string, string> ReadSections()
    {
        using var stream = typeof(LiquidGlassEffect).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Ressource introuvable : {ResourceName}");
        using var reader = new StreamReader(stream);

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var name = "header";
        var text = new System.Text.StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
            {
                sections[name] = text.ToString();
                name = line[SectionMarker.Length..].Trim();
                text.Clear();
                continue;
            }
            text.Append(line).Append('\n');
        }
        sections[name] = text.ToString();

        foreach (var required in new[] { "common", "glass", "shadow" })
            if (!sections.ContainsKey(required))
                throw new InvalidOperationException($"Section « {required} » absente de {ResourceName}.");
        return sections;
    }
}
