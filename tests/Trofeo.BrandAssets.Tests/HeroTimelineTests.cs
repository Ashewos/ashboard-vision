using Trofeo.ReadmeHero;

namespace Trofeo.BrandAssets.Tests;

/// <summary>Le déroulé de l'image d'accueil : trois thèmes de 4 s, un cycle de 12 s qui reboucle sans à-coup.</summary>
public sealed class HeroTimelineTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3.99, 0)]
    [InlineData(4, 1)]
    [InlineData(7.99, 1)]
    [InlineData(8, 2)]
    [InlineData(11.99, 2)]
    public void At_GivesTheThemeOfTheCurrentFourSecondSlot(double seconds, int theme)
    {
        Assert.Equal(theme, HeroTimeline.At(seconds).Theme);
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(16, 1)]
    [InlineData(20, 2)]
    [InlineData(36, 0)]
    public void At_LoopsBackAfterTwelveSeconds(double seconds, int theme)
    {
        Assert.Equal(theme, HeroTimeline.At(seconds).Theme);
    }

    [Fact]
    public void At_ProgressGoesFromZeroToOneOnEachTheme()
    {
        foreach (var start in new[] { 0.0, 4.0, 8.0 })
        {
            Assert.Equal(0.0, HeroTimeline.At(start).Progress, 6);
            Assert.Equal(0.25, HeroTimeline.At(start + 1).Progress, 6);
            Assert.Equal(0.5, HeroTimeline.At(start + 2).Progress, 6);
            Assert.InRange(HeroTimeline.At(start + 3.999).Progress, 0.999, 1.0);
        }

        // Au changement de thème, la barre repart de zéro.
        Assert.Equal(0.0, HeroTimeline.At(12).Progress, 6);
    }

    [Fact]
    public void At_SheenSweepsOnceEveryTheme_ThenWaits()
    {
        foreach (var start in new[] { 0.0, 4.0, 8.0 })
        {
            Assert.Equal(0.0, HeroTimeline.At(start).Sheen, 6);        // le reflet attend à gauche
            Assert.Equal(0.0, HeroTimeline.At(start + 0.3).Sheen, 6);
            Assert.InRange(HeroTimeline.At(start + 0.76).Sheen, 0.4, 0.6); // en plein milieu du balayage
            Assert.Equal(1.0, HeroTimeline.At(start + 1.2).Sheen, 6);  // sorti à droite
            Assert.Equal(1.0, HeroTimeline.At(start + 3.9).Sheen, 6);
        }
    }

    [Fact]
    public void At_SheenNeverGoesBackwardsInASlot()
    {
        var previous = 0.0;
        for (var t = 0.0; t < 4.0; t += 0.05)
        {
            var sheen = HeroTimeline.At(t).Sheen;
            Assert.True(sheen >= previous, $"le reflet recule à {t} s");
            previous = sheen;
        }
    }

    [Fact]
    public void Layer_IsVisibleInItsOwnSlot_AndHiddenInTheOthers()
    {
        Assert.Equal(1.0, HeroTimeline.Layer(0, 2).Opacity, 6);
        Assert.Equal(0.0, HeroTimeline.Layer(1, 2).Opacity, 6);
        Assert.Equal(0.0, HeroTimeline.Layer(2, 2).Opacity, 6);
        Assert.Equal(1.0, HeroTimeline.Layer(1, 6).Opacity, 6);
        Assert.Equal(1.0, HeroTimeline.Layer(2, 10).Opacity, 6);
    }

    [Fact]
    public void Layer_EntersSlidingAndBlurred_ThenSettles()
    {
        var start = HeroTimeline.Layer(1, 4);
        Assert.Equal(0.0, start.Opacity, 6);
        Assert.Equal(0.06, start.Offset, 6);   // arrive de la droite
        Assert.Equal(0.97, start.Scale, 6);
        Assert.Equal(6.0, start.Blur, 6);

        var settled = HeroTimeline.Layer(1, 5);
        Assert.Equal(1.0, settled.Opacity, 6);
        Assert.Equal(0.0, settled.Offset, 6);
        Assert.Equal(1.0, settled.Scale, 6);
        Assert.Equal(0.0, settled.Blur, 6);
    }

    [Fact]
    public void Layer_LeavesToTheLeft_WhileTheNextOneEnters()
    {
        // À 4,1 s le premier thème sort encore, le deuxième entre déjà : les deux se voient un instant.
        var leaving = HeroTimeline.Layer(0, 4.1);
        var entering = HeroTimeline.Layer(1, 4.1);
        Assert.InRange(leaving.Opacity, 0.01, 0.99);
        Assert.True(leaving.Offset < 0);
        Assert.InRange(entering.Opacity, 0.01, 0.99);
        Assert.True(entering.Offset > 0);
    }

    [Fact]
    public void Layer_LastThemeLeavesAcrossTheLoop()
    {
        // Le troisième thème sort après 12 s, pendant que le premier recommence.
        Assert.InRange(HeroTimeline.Layer(2, 12.1).Opacity, 0.01, 0.99);
        Assert.InRange(HeroTimeline.Layer(0, 12.1).Opacity, 0.01, 0.99);
    }

    [Fact]
    public void Label_RisesIn_Holds_ThenLeavesUpward()
    {
        Assert.Equal(0.0, HeroTimeline.Label(0, 0).Opacity, 6);
        Assert.Equal(1.0, HeroTimeline.Label(0, 0).Rise, 6);    // part d'en dessous
        Assert.Equal(1.0, HeroTimeline.Label(0, 2).Opacity, 6);
        Assert.Equal(0.0, HeroTimeline.Label(0, 2).Rise, 6);
        Assert.Equal(0.0, HeroTimeline.Label(0, 4.5).Opacity, 6);
        Assert.Equal(-1.0, HeroTimeline.Label(0, 4.5).Rise, 6); // reparti vers le haut
        Assert.Equal(0.0, HeroTimeline.Label(1, 2).Opacity, 6); // le nom d'un autre thème est caché
    }

    [Fact]
    public void Drift_SwingsBetweenMinusAndPlusTwoPointFiveDegrees_AndLoops()
    {
        Assert.Equal(-2.5, HeroTimeline.Drift(0), 6);
        Assert.Equal(2.5, HeroTimeline.Drift(6), 6);
        Assert.Equal(0.0, HeroTimeline.Drift(3), 6);
        Assert.Equal(HeroTimeline.Drift(0), HeroTimeline.Drift(12), 6);
        Assert.All(Enumerable.Range(0, 144), i => Assert.InRange(HeroTimeline.Drift(i / 12.0), -2.5, 2.5));
    }

    [Fact]
    public void At_NegativeTimeStillFallsInTheCycle()
    {
        Assert.Equal(2, HeroTimeline.At(-1).Theme);
    }
}
