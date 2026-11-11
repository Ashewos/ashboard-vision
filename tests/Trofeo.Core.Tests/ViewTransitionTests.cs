using SkiaSharp;
using Trofeo.Core.Rendering;

namespace Trofeo.Core.Tests;

public sealed class ViewTransitionTests
{
    private const int Width = 400;
    private const int Height = 100;

    private static SKBitmap Solid(SKColor color)
    {
        var bitmap = new SKBitmap(Width, Height);
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>Noir, avec une bande blanche à droite de <paramref name="edge"/> (un bord vertical) et une ligne blanche horizontale.</summary>
    private static SKBitmap Marked(int edge, int lineTop = -1)
    {
        var bitmap = new SKBitmap(Width, Height);
        bitmap.Erase(SKColors.Black);
        using var canvas = new SKCanvas(bitmap);
        using var white = new SKPaint { Color = SKColors.White };
        if (edge >= 0) canvas.DrawRect(edge, 0, Width - edge, Height, white);
        if (lineTop >= 0) canvas.DrawRect(0, lineTop, Width, 4, white);
        return bitmap;
    }

    private static TimeSpan Half => ViewTransition.Duration / 2;

    [Fact]
    public void AtStart_ShowsTheOldView()
    {
        using var from = Marked(edge: 150, lineTop: 40);
        using var to = Solid(SKColors.Blue);

        using var frame = new ViewTransition(from, +1, ViewTransition.Duration).Compose(to, TimeSpan.Zero);

        Assert.Equal(from.Bytes, frame.Bytes); // la même image, octet pour octet : net, sans flou
    }

    [Fact]
    public void AtEnd_ShowsTheNewView()
    {
        using var from = Solid(SKColors.Red);
        using var to = Marked(edge: 150, lineTop: 40);
        var transition = new ViewTransition(from, +1, ViewTransition.Duration);

        using var frame = transition.Compose(to, ViewTransition.Duration);
        using var later = transition.Compose(to, ViewTransition.Duration * 2); // au-delà : toujours la vue d'arrivée

        Assert.Equal(to.Bytes, frame.Bytes);
        Assert.Equal(to.Bytes, later.Bytes);
        Assert.False(transition.IsDone(ViewTransition.Duration - TimeSpan.FromMilliseconds(1)));
        Assert.True(transition.IsDone(ViewTransition.Duration));
    }

    [Fact]
    public void MidWay_RightKey_NewViewComesFromTheRight()
    {
        // Touche Droite : l'ancienne vue sort à gauche et la nouvelle arrive par la droite.
        AssertSides(+1, left: SKColors.Red, right: SKColors.Blue);
    }

    [Fact]
    public void MidWay_LeftKey_NewViewComesFromTheLeft()
    {
        AssertSides(-1, left: SKColors.Blue, right: SKColors.Red);
    }

    private static void AssertSides(int direction, SKColor left, SKColor right)
    {
        using var from = Solid(SKColors.Red);
        using var to = Solid(SKColors.Blue);

        using var frame = new ViewTransition(from, direction, ViewTransition.Duration).Compose(to, Half);

        AssertNear(left, frame.GetPixel(5, 50));
        AssertNear(left, frame.GetPixel(Width / 4, 50));
        AssertNear(right, frame.GetPixel(Width - 6, 50));
        AssertNear(right, frame.GetPixel(Width * 3 / 4, 50));
    }

    [Fact]
    public void MidWay_TheTwoViewsMeetInTheMiddle()
    {
        using var from = Solid(SKColors.Red);
        using var to = Solid(SKColors.Blue);

        using var frame = new ViewTransition(from, +1, ViewTransition.Duration).Compose(to, Half);

        var junction = frame.GetPixel(Width / 2, 50);
        Assert.InRange(junction.Red, 80, 175);   // un mélange des deux couleurs, pas une raie sombre
        Assert.InRange(junction.Blue, 80, 175);
        Assert.True(junction.Red + junction.Blue > 255 - 20, "pas de raie sombre à la jonction");
    }

    [Fact]
    public void BlurSigma_IsZeroAtBothEndsAndHighestMidway()
    {
        Assert.Equal(0f, ViewTransition.BlurSigma(0.0));
        Assert.Equal(0f, ViewTransition.BlurSigma(1.0));
        Assert.Equal(ViewTransition.MaxBlurSigma, ViewTransition.BlurSigma(0.5), 4);

        var previous = 0f;
        for (var step = 1; step <= 50; step++)
        {
            var sigma = ViewTransition.BlurSigma(step / 100.0);
            Assert.True(sigma > previous, $"le flou monte jusqu'au milieu (pas {step})");
            Assert.True(sigma <= ViewTransition.MaxBlurSigma + 1e-4f);
            previous = sigma;
        }

        // Symétrique : la vitesse redescend comme elle est montée.
        Assert.Equal(ViewTransition.BlurSigma(0.2), ViewTransition.BlurSigma(0.8), 4);
        Assert.Equal(0f, ViewTransition.BlurSigma(-1.0));
        Assert.Equal(0f, ViewTransition.BlurSigma(2.0));
    }

    [Fact]
    public void MidWay_BlurIsHorizontalOnly()
    {
        // Une ligne blanche de 4 px de haut sur toute la largeur reste nette en hauteur (l'ancienne vue est à gauche, x < 200).
        using (var from = Marked(edge: -1, lineTop: 40))
        using (var to = Solid(SKColors.Black))
        using (var frame = new ViewTransition(from, +1, ViewTransition.Duration).Compose(to, Half))
        {
            AssertNear(SKColors.Black, frame.GetPixel(100, 39));
            AssertNear(SKColors.White, frame.GetPixel(100, 40));
            AssertNear(SKColors.White, frame.GetPixel(100, 43));
            AssertNear(SKColors.Black, frame.GetPixel(100, 44));
        }

        // Un bord vertical s'étale en largeur : noir jusqu'à x = 300, blanc au-delà ; au milieu du glissement il est à x = 100.
        using (var from = Marked(edge: 300))
        using (var to = Solid(SKColors.Green))
        using (var frame = new ViewTransition(from, +1, ViewTransition.Duration).Compose(to, Half))
        {
            AssertNear(SKColors.Black, frame.GetPixel(100 - 50, 50));
            AssertNear(SKColors.White, frame.GetPixel(100 + 50, 50));
            var onTheEdge = frame.GetPixel(100, 50);
            Assert.InRange(onTheEdge.Red, 90, 165);                   // un gris, ni noir ni blanc
            Assert.InRange(frame.GetPixel(100 - 12, 50).Red, 5, 100); // encore dans le flou, côté noir
            Assert.InRange(frame.GetPixel(100 + 12, 50).Red, 155, 250);

            // Sur toute la hauteur de la colonne, le même gris : rien ne s'étale verticalement.
            Assert.Equal(onTheEdge.Red, frame.GetPixel(100, 0).Red);
            Assert.Equal(onTheEdge.Red, frame.GetPixel(100, Height - 1).Red);
        }
    }

    [Fact]
    public void Frames_AreNewBitmaps_SourcesAreUntouched()
    {
        using var from = Solid(SKColors.Red);
        using var to = Solid(SKColors.Blue);
        var transition = new ViewTransition(from, +1, ViewTransition.Duration);

        using var first = transition.Compose(to, Half);
        using var second = transition.Compose(to, Half);

        Assert.NotSame(first, second);
        Assert.NotSame(from, first);
        AssertNear(SKColors.Red, from.GetPixel(Width / 2, 50));
        AssertNear(SKColors.Blue, to.GetPixel(Width / 2, 50));
        Assert.Equal(first.Bytes, second.Bytes);
    }

    private static void AssertNear(SKColor expected, SKColor actual, int tolerance = 3)
    {
        Assert.InRange(actual.Red, Math.Max(0, expected.Red - tolerance), Math.Min(255, expected.Red + tolerance));
        Assert.InRange(actual.Green, Math.Max(0, expected.Green - tolerance), Math.Min(255, expected.Green + tolerance));
        Assert.InRange(actual.Blue, Math.Max(0, expected.Blue - tolerance), Math.Min(255, expected.Blue + tolerance));
    }
}
