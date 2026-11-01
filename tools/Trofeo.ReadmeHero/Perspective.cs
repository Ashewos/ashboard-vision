using SkiaSharp;

namespace Trofeo.ReadmeHero;

/// <summary>
/// La perspective de la maquette (<c>perspective: 1400px</c>, <c>rotateX(10deg) rotateY(±2,5deg)</c> autour du milieu du bord bas),
/// en matrice 3 × 3 : on projette les quatre coins du plan de l'écran, puis on en tire la matrice qui y envoie tout le plan.
/// </summary>
internal static class Perspective
{
    /// <summary>
    /// La matrice qui envoie le plan de l'écran, en coordonnées locales (0, 0) à (<paramref name="width"/>, <paramref name="height"/>),
    /// vers l'image : le plan est incliné de <paramref name="tiltXDegrees"/> vers l'arrière (le haut s'éloigne) et tourné de
    /// <paramref name="turnYDegrees"/>, autour du milieu de son bord bas posé en (<paramref name="left"/> + largeur / 2, <paramref name="top"/> + hauteur).
    /// <paramref name="distance"/> est celle de l'œil au plan, qui regarde le centre du plan.
    /// </summary>
    public static SKMatrix Plane(
        float left, float top, float width, float height, double tiltXDegrees, double turnYDegrees, double distance)
    {
        var a = tiltXDegrees * Math.PI / 180;
        var b = turnYDegrees * Math.PI / 180;
        var eyeX = left + width / 2.0;
        var eyeY = top + height / 2.0;

        (double X, double Y) Project(double x, double y)
        {
            // Par rapport à l'origine de la transformation : le milieu du bord bas.
            var xl = x - width / 2.0;
            var yl = y - height;

            // rotateY puis rotateX : le plan est à z = 0, donc z passe par l'axe vertical d'abord.
            var x1 = xl * Math.Cos(b);
            var z1 = -xl * Math.Sin(b);
            var y2 = yl * Math.Cos(a) - z1 * Math.Sin(a);
            var z2 = yl * Math.Sin(a) + z1 * Math.Cos(a);

            // La perspective, vue de l'œil : plus un point est proche (z positif), plus il grandit.
            var scale = distance / (distance - z2);
            var sx = eyeX + (left + width / 2.0 + x1 - eyeX) * scale;
            var sy = eyeY + (top + height + y2 - eyeY) * scale;
            return (sx, sy);
        }

        return UnitSquareToQuad(
            Project(0, 0), Project(width, 0), Project(width, height), Project(0, height), width, height);
    }

    /// <summary>
    /// Matrice qui envoie le rectangle (0, 0, <paramref name="width"/>, <paramref name="height"/>) sur le quadrilatère p0 (haut gauche),
    /// p1 (haut droit), p2 (bas droit), p3 (bas gauche) (Heckbert, « Fundamentals of Texture Mapping and Image Warping »).
    /// </summary>
    internal static SKMatrix UnitSquareToQuad(
        (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, float width, float height)
    {
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, sx = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, sy = p0.Y - p1.Y + p2.Y - p3.Y;

        double g = 0, h = 0;
        if (Math.Abs(sx) > 1e-9 || Math.Abs(sy) > 1e-9)
        {
            var den = dx1 * dy2 - dx2 * dy1;
            g = (sx * dy2 - dx2 * sy) / den;
            h = (dx1 * sy - sx * dy1) / den;
        }

        var a = p1.X - p0.X + g * p1.X;
        var bb = p3.X - p0.X + h * p3.X;
        var d = p1.Y - p0.Y + g * p1.Y;
        var e = p3.Y - p0.Y + h * p3.Y;

        // Les coordonnées locales vont de 0 à width et de 0 à height : on divise les colonnes par ces tailles.
        return new SKMatrix
        {
            ScaleX = (float)(a / width),
            SkewX = (float)(bb / height),
            TransX = (float)p0.X,
            SkewY = (float)(d / width),
            ScaleY = (float)(e / height),
            TransY = (float)p0.Y,
            Persp0 = (float)(g / width),
            Persp1 = (float)(h / height),
            Persp2 = 1,
        };
    }
}
