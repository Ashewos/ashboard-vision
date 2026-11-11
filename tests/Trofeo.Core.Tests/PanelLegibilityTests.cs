using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

/// <summary>Garde de lisibilité : option pour la couper, et lissage dans le temps sur un fond animé.</summary>
public sealed class PanelLegibilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(1000.0 / 12);

    private static readonly SKColor Gray = new(128, 128, 128);

    /// <summary>Fond de test : un gris constant, sauf un bloc à droite du panneau qui alterne blanc et noir à chaque image.</summary>
    private sealed class BlinkingBackground(SKRectI panel, bool animated) : IBackground
    {
        private SKImage _image = Make(panel, white: false);
        private bool _white;

        public bool IsAnimated => animated;
        public SKImage Current => _image;
        public int Version { get; private set; } = 1;
        public TimeSpan? UntilNextFrame => TimeSpan.FromMilliseconds(83);

        public bool Advance(TimeSpan elapsed) => false;

        /// <summary>Passe à l'image suivante : nouvelle version, l'ancienne image n'est plus valable.</summary>
        public void Toggle()
        {
            _white = !_white;
            _image.Dispose();
            _image = Make(panel, _white);
            Version++;
        }

        private static SKImage Make(SKRectI panel, bool white) => StubBackground.Make(canvas =>
        {
            canvas.Clear(Gray);
            using var paint = new SKPaint { Color = white ? SKColors.White : SKColors.Black };
            canvas.DrawRect(SKRect.Create(panel.Right - 300, panel.Top, 300, panel.Height), paint);
        });

        public void Dispose() => _image.Dispose();
    }

    private static ModulePlacement Place() =>
        new(Guid.NewGuid(), "fake", 4, 1, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private static double Luminance(SKColor c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
    }

    private static ThemeDefinition Transparent(bool guard = true) =>
        Themes.ClassiqueDefinition with { Theme = Themes.Classique with { PanelOpacity = 0f }, LegibilityGuard = guard };

    [Fact]
    public void GuardOff_WhiteBackground_PanelKeepsExactlyTheChosenOpacity()
    {
        var layout = DashboardLayout.SingleView(0, [Place()]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        using var white = new ColorBackground(SKColors.White);
        using var composer = new FrameComposer();

        using var guarded = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Transparent(), white, T0);
        using var raw = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), Transparent(guard: false), white, T0);

        int x = (rect.Left + rect.Right) / 2, y = (rect.Top + rect.Bottom) / 2;
        Assert.InRange(Luminance(guarded.GetPixel(x, y)), 0.0, 0.36); // garde active : assombri
        Assert.Equal(SKColors.White, raw.GetPixel(x, y)); // garde coupée : opacité 0, rien n'est peint
    }

    [Fact]
    public void GuardOff_BackgroundDoesNotChangeTheAlpha()
    {
        var layout = DashboardLayout.SingleView(0, [Place()]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        var def = Themes.ClassiqueDefinition with { Theme = Themes.Classique with { PanelOpacity = 0.3f }, LegibilityGuard = false };
        using var white = new ColorBackground(SKColors.White);
        using var composer = new FrameComposer();

        using var frame = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, white, T0);

        var p = frame.GetPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        var panel = def.Theme.Panel;
        Assert.InRange(p.Red, panel.Red * 0.3 + 255 * 0.7 - 2, panel.Red * 0.3 + 255 * 0.7 + 2);
    }

    /// <summary>Luminance, image après image, d'un pixel du panneau où le fond reste gris ; seul l'alpha du panneau la fait bouger.</summary>
    private static List<double> Series(bool animated, bool guard, int frames)
    {
        var layout = DashboardLayout.SingleView(0, [Place()]);
        var rect = GridGeometry.ModuleRect(4, 1, 4, 2);
        using var background = new BlinkingBackground(rect, animated);
        using var composer = new FrameComposer();
        var def = Transparent(guard);

        var series = new List<double>();
        for (int i = 0; i < frames; i++)
        {
            background.Toggle(); // image 0 : blanc, 1 : noir, 2 : blanc…
            using var frame = composer.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), def, background, T0 + i * Frame);
            series.Add(Luminance(frame.GetPixel(rect.Left + 30, (rect.Top + rect.Bottom) / 2)));
        }

        return series;
    }

    private static double BiggestStep(List<double> series) =>
        Enumerable.Range(1, series.Count - 1).Max(i => Math.Abs(series[i] - series[i - 1]));

    [Fact]
    public void FixedBackground_AlphaFollowsTheBackdropAtOnce()
    {
        // Contrôle : sur un fond fixe (pas de lissage), le même fond qui clignote fait sauter l'alpha d'une image à l'autre.
        var series = Series(animated: false, guard: true, frames: 24);

        Assert.True(BiggestStep(series) > 0.1, $"plus grand saut : {BiggestStep(series)}");
    }

    [Fact]
    public void AnimatedBackground_AlphaDoesNotJump()
    {
        var series = Series(animated: true, guard: true, frames: 48);

        Assert.True(BiggestStep(series) < 0.04, $"plus grand saut : {BiggestStep(series)}");
    }

    [Fact]
    public void AnimatedBackground_FirstFrameTakesTheWantedAlpha()
    {
        var smoothed = Series(animated: true, guard: true, frames: 1);
        var immediate = Series(animated: false, guard: true, frames: 1);

        Assert.Equal(immediate[0], smoothed[0], 6);
    }

    [Fact]
    public void AnimatedBackground_StaysDarkerOnAverageThanTheUnsmoothedPanel()
    {
        // La cible oscille entre 0 et environ 0,65 à chaque image ; l'alpha appliqué monte vite et descend lentement, donc
        // il se range près du haut : le panneau reste plus opaque (et plus sombre ici) que sans lissage.
        var smoothed = Series(animated: true, guard: true, frames: 96).Skip(48).Average();
        var raw = Series(animated: false, guard: true, frames: 96).Skip(48).Average();

        Assert.True(smoothed < raw, $"{smoothed} devrait être plus sombre que {raw}");
    }

    [Fact]
    public void AnimatedBackground_GuardOff_AlphaIsConstant()
    {
        var series = Series(animated: true, guard: false, frames: 24);

        Assert.True(BiggestStep(series) < 0.0005, $"plus grand saut : {BiggestStep(series)}");
    }
}
