namespace Trofeo.Core.Rendering;

/// <summary>Transforme les réglages d'apparence en thème complet, sans jamais lever d'exception pour un réglage faux.</summary>
public static class AppearanceResolver
{
    /// <summary>
    /// Thème inconnu : Classique, avec un avertissement. Opacité bornée à [0, 1] ; NaN : celle du thème, avec un avertissement.
    /// <c>Legibility</c> à faux : la garde de lisibilité est coupée (<see cref="ThemeDefinition.LegibilityGuard"/>).
    /// Thème « Liquid Glass avancé » : <c>Glass</c> donne les réglages du verre (<see cref="GlassAppearance.Resolve"/>) et leur rayon devient
    /// celui des cases ; les autres thèmes ignorent <c>Glass</c>.
    /// Police : <see cref="FontResolver"/> ; si elle est trouvée, <c>Typeface</c> et <c>FeedTypeface</c> la prennent, et
    /// <c>TypefaceScale</c> comme <c>FeedScale</c> prennent l'agrandissement de cette police
    /// (<see cref="EmbeddedFonts.ScaleOf"/> : 1,24 pour VT323, même en Classique ; 1 pour toute autre police, du système comprise). Si elle est introuvable, le thème reste tel quel ; si c'est la police du thème elle-même, le fil la prend aussi.
    /// </summary>
    public static ThemeDefinition Resolve(AppearanceSettings settings, ISystemFonts fonts, Action<string>? warn = null)
    {
        var definition = Themes.Find(settings.Theme);
        if (definition is null)
        {
            warn?.Invoke($"Thème « {settings.Theme} » inconnu : thème Classique utilisé.");
            definition = Themes.ClassiqueDefinition;
        }

        if (definition.Panel.Kind == PanelKind.LiquidGlass)
        {
            // Le verre : un préréglage inconnu ou des valeurs hors bornes retombent sur Clair (avertissement). Le rayon du verre est
            // celui des cases.
            var glass = (settings.Glass ?? new GlassAppearance()).Resolve(warn);
            definition = definition with
            {
                Panel = definition.Panel with { Glass = glass },
                Theme = definition.Theme with { CornerRadius = glass.CornerRadius },
            };
        }

        if (settings.PanelOpacity is { } opacity)
        {
            if (float.IsNaN(opacity))
                warn?.Invoke("Opacité des modules invalide : thème par défaut utilisé.");
            else
                definition = definition with { Theme = definition.Theme with { PanelOpacity = Math.Clamp(opacity, 0f, 1f) } };
        }

        if (settings.Legibility == false)
        {
            definition = definition with { LegibilityGuard = false };
        }

        if (!string.IsNullOrWhiteSpace(settings.Font))
        {
            // FontResolver rend le repli et avertit quand la police est introuvable : l'avertissement distingue
            // « introuvable » de « trouvée, et c'est justement celle du thème » (instance embarquée partagée).
            var notFound = false;
            var face = FontResolver.Resolve(settings.Font, definition.Theme.Typeface, fonts, message =>
            {
                notFound = true;
                warn?.Invoke(message);
            });
            if (!notFound)
            {
                var scale = EmbeddedFonts.ScaleOf(settings.Font);
                definition = definition with
                {
                    Theme = definition.Theme with { Typeface = face, TypefaceScale = scale, FeedTypeface = face, FeedScale = scale },
                };
            }
        }

        return definition;
    }
}
