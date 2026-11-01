namespace Trofeo.ReadmeHero;

/// <summary>
/// Le déroulé de l'image d'accueil, en calcul pur (aucun dessin) : trois thèmes de 4 s sur un cycle de 12 s qui reboucle
/// sans à-coup. Les valeurs sont celles de la maquette <c>readme-accueil-v4.html</c> (animations CSS <c>show</c>,
/// <c>sheen</c>, <c>lab</c>, <c>bar</c> et <c>drift</c>).
/// </summary>
internal static class HeroTimeline
{
    public const int ThemeCount = 3;
    public const double ThemeSeconds = 4;
    public const double CycleSeconds = ThemeCount * ThemeSeconds;

    // Un thème : il entre pendant 0,72 s (6 % du cycle), reste en place jusqu'à 3,6 s (30 %), puis sort pendant 0,72 s (36 %).
    private const double EnterEnd = 0.06 * CycleSeconds;
    private const double LeaveStart = 0.30 * CycleSeconds;
    private const double LeaveEnd = 0.36 * CycleSeconds;

    /// <summary>Dérive horizontale de l'image à l'entrée et à la sortie, en part de sa largeur.</summary>
    public const double SlideOffset = 0.06;

    /// <summary>Échelle de l'image quand elle est entrée ou sortie à moitié.</summary>
    public const double SlideScale = 0.97;

    /// <summary>Flou de l'image au plus fort de la transition, en pixels de la maquette (il se met à l'échelle du dessin).</summary>
    public const double SlideBlur = 6;

    /// <summary>Le thème en cours (0 à 2), l'avancement de la barre sur ce thème (0 à 1) et le balayage du reflet (0 à 1).</summary>
    public static (int Theme, double Progress, double Sheen) At(double seconds)
    {
        var cycle = Wrap(seconds, CycleSeconds);
        var theme = Math.Min((int)(cycle / ThemeSeconds), ThemeCount - 1);
        var inTheme = cycle - theme * ThemeSeconds;
        return (theme, inTheme / ThemeSeconds, SheenSweep(inTheme));
    }

    /// <summary>L'état de l'image d'un thème : opacité, glissement (en part de sa largeur, positif = vers la droite), échelle et flou.</summary>
    public readonly record struct LayerState(double Opacity, double Offset, double Scale, double Blur);

    /// <summary>
    /// L'image (lueur et écran) d'un thème. Elle entre à l'instant où son thème commence, arrivant de la droite en
    /// floutée, et sort vers la gauche juste après la fin du thème : les deux images se voient un instant.
    /// </summary>
    public static LayerState Layer(int theme, double seconds)
    {
        var s = Wrap(seconds - theme * ThemeSeconds, CycleSeconds);
        if (s < EnterEnd)
        {
            var e = Ease(s / EnterEnd);
            return new LayerState(e, SlideOffset * (1 - e), Lerp(SlideScale, 1, e), SlideBlur * (1 - e));
        }

        if (s < LeaveStart)
        {
            return new LayerState(1, 0, 1, 0);
        }

        if (s < LeaveEnd)
        {
            var e = Ease((s - LeaveStart) / (LeaveEnd - LeaveStart));
            return new LayerState(1 - e, -SlideOffset * e, Lerp(1, SlideScale, e), SlideBlur * e);
        }

        return new LayerState(0, 0, 1, 0);
    }

    /// <summary>
    /// Le nom du thème : opacité et montée (de +1, 8 px sous sa place, à −1, 8 px au-dessus), en 0,96 s à l'entrée, tenu
    /// jusqu'à 3,36 s, puis reparti vers le haut en 0,72 s.
    /// </summary>
    public static (double Opacity, double Rise) Label(int theme, double seconds)
    {
        const double inEnd = 0.08 * CycleSeconds;
        const double outStart = 0.28 * CycleSeconds;
        const double outEnd = 0.34 * CycleSeconds;

        var s = Wrap(seconds - theme * ThemeSeconds, CycleSeconds);
        if (s < inEnd)
        {
            var e = Ease(s / inEnd);
            return (e, 1 - e);
        }

        if (s < outStart)
        {
            return (1, 0);
        }

        if (s < outEnd)
        {
            var e = Ease((s - outStart) / (outEnd - outStart));
            return (1 - e, -e);
        }

        return (0, -1);
    }

    /// <summary>L'angle de l'écran autour de l'axe vertical, en degrés : il oscille entre −2,5° et +2,5° sur un cycle.</summary>
    public static double Drift(double seconds) => -2.5 * Math.Cos(2 * Math.PI * Wrap(seconds, CycleSeconds) / CycleSeconds);

    // Le reflet est calé sur la durée d'un thème (4 s) : il attend à gauche pendant les 8 % du début (jusqu'à 0,32 s),
    // balaie la vitre jusqu'à 30 % (1,2 s) en ralentissant aux deux bouts, puis attend à droite jusqu'au thème suivant.
    // La maquette (animation de 4 s) donne les mêmes pourcentages.
    private static double SheenSweep(double inTheme)
    {
        const double start = 0.08 * ThemeSeconds;
        const double end = 0.30 * ThemeSeconds;
        if (inTheme <= start)
        {
            return 0;
        }

        if (inTheme >= end)
        {
            return 1;
        }

        return CubicBezier(0.42, 0, 0.58, 1, (inTheme - start) / (end - start));
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static double Wrap(double value, double period)
    {
        var wrapped = value % period;
        return wrapped < 0 ? wrapped + period : wrapped;
    }

    /// <summary>Le <c>ease</c> de CSS, appliqué à chaque intervalle entre deux étapes des animations de la maquette.</summary>
    private static double Ease(double t) => CubicBezier(0.25, 0.1, 0.25, 1, t);

    /// <summary>Courbe de Bézier de CSS (<c>cubic-bezier(x1, y1, x2, y2)</c>) : la valeur à l'avancement <paramref name="t"/> (0 à 1).</summary>
    private static double CubicBezier(double x1, double y1, double x2, double y2, double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        // On cherche le paramètre u tel que x(u) = t (par dichotomie : 40 tours suffisent largement), puis y(u).
        double low = 0, high = 1, u = t;
        for (var i = 0; i < 40; i++)
        {
            u = (low + high) / 2;
            if (Bezier(x1, x2, u) < t)
            {
                low = u;
            }
            else
            {
                high = u;
            }
        }

        return Bezier(y1, y2, u);
    }

    private static double Bezier(double p1, double p2, double u)
    {
        var v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }
}
