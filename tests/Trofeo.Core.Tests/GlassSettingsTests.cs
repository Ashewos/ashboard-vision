using Trofeo.Core.Rendering;

namespace Trofeo.Core.Tests;

public class GlassSettingsTests
{
    [Fact]
    public void Defaults_AreLiquidGlassDefaults_ExceptCornerRadius()
    {
        var s = new GlassSettings();

        Assert.Equal(0f, s.Blur);
        Assert.Equal(0.69f, s.Refraction);
        Assert.Equal(40f, s.ZRadius);
        Assert.Equal(GlassBevel.Pill, s.Bevel);
        Assert.Equal(30f, s.CornerRadius);
        Assert.Equal(0f, s.Distortion);
        Assert.Equal(0.05f, s.EdgeHighlight);
        Assert.Equal(0f, s.Specular);
        Assert.Equal(1f, s.Fresnel);
        Assert.Equal(0.05f, s.ChromaticAberration);
        Assert.Equal(1f, s.Opacity);
        Assert.Equal(0f, s.Saturation);
        Assert.Equal(0f, s.Tint);
        Assert.Equal(0f, s.Brightness);
        Assert.Equal(0.3f, s.ShadowOpacity);
        Assert.Equal(10f, s.ShadowSpread);
        Assert.Equal(1f, s.ShadowOffsetY);
    }

    [Fact]
    public void Parameters_HaveTheDemoBounds()
    {
        (string Name, float Min, float Max, float Step)[] expected =
        [
            (nameof(GlassSettings.Blur), 0f, 1f, 0.01f),
            (nameof(GlassSettings.Refraction), 0f, 2f, 0.01f),
            (nameof(GlassSettings.ZRadius), 1f, 100f, 1f),
            (nameof(GlassSettings.CornerRadius), 0f, 49f, 1f),
            (nameof(GlassSettings.Distortion), 0f, 1f, 0.01f),
            (nameof(GlassSettings.EdgeHighlight), 0f, 1f, 0.01f),
            (nameof(GlassSettings.Specular), 0f, 1f, 0.01f),
            (nameof(GlassSettings.Fresnel), 0f, 2f, 0.01f),
            (nameof(GlassSettings.ChromaticAberration), 0f, 0.3f, 0.005f),
            (nameof(GlassSettings.Opacity), 0f, 1f, 0.01f),
            (nameof(GlassSettings.Saturation), -1f, 1f, 0.01f),
            (nameof(GlassSettings.Tint), 0f, 1f, 0.01f),
            (nameof(GlassSettings.Brightness), -0.5f, 0.5f, 0.01f),
            (nameof(GlassSettings.ShadowOpacity), 0f, 1f, 0.01f),
            (nameof(GlassSettings.ShadowSpread), 0f, 40f, 1f),
            (nameof(GlassSettings.ShadowOffsetY), -20f, 20f, 1f),
        ];

        Assert.Equal(16, GlassSettings.Parameters.Count);
        foreach (var (name, min, max, step) in expected)
        {
            var p = Assert.Single(GlassSettings.Parameters, x => x.Name == name);
            Assert.Equal(min, p.Min);
            Assert.Equal(max, p.Max);
            Assert.Equal(step, p.Step);
            Assert.NotNull(typeof(GlassSettings).GetProperty(name)); // le nom est celui d'une propriété
        }
    }

    [Fact]
    public void Clamped_OutOfBounds_IsBounded()
    {
        var wild = new GlassSettings(
            Blur: 5f, Refraction: -1f, ZRadius: 0f, CornerRadius: 80f, Distortion: 2f, EdgeHighlight: -4f, Specular: 9f, Fresnel: 3f,
            ChromaticAberration: 1f, Opacity: -1f, Saturation: -3f, Tint: 2f, Brightness: 4f, ShadowOpacity: 7f, ShadowSpread: 99f,
            ShadowOffsetY: -99f);

        var s = wild.Clamped(new GlassSettings());

        Assert.Equal(1f, s.Blur);
        Assert.Equal(0f, s.Refraction);
        Assert.Equal(1f, s.ZRadius);
        Assert.Equal(49f, s.CornerRadius);
        Assert.Equal(1f, s.Distortion);
        Assert.Equal(0f, s.EdgeHighlight);
        Assert.Equal(1f, s.Specular);
        Assert.Equal(2f, s.Fresnel);
        Assert.Equal(0.3f, s.ChromaticAberration);
        Assert.Equal(0f, s.Opacity);
        Assert.Equal(-1f, s.Saturation);
        Assert.Equal(1f, s.Tint);
        Assert.Equal(0.5f, s.Brightness);
        Assert.Equal(1f, s.ShadowOpacity);
        Assert.Equal(40f, s.ShadowSpread);
        Assert.Equal(-20f, s.ShadowOffsetY);
    }

    [Fact]
    public void Clamped_InBounds_IsUnchanged()
    {
        var s = new GlassSettings(Blur: 0.4f, Bevel: GlassBevel.Dome, CornerRadius: 12f, Saturation: -0.5f);

        Assert.Equal(s, s.Clamped(new GlassSettings()));
    }

    [Fact]
    public void Clamped_NaN_TakesTheFallback()
    {
        var fallback = new GlassSettings(Blur: 0.3f, Refraction: 0.5f, CornerRadius: 12f, Saturation: 0.2f);
        var s = new GlassSettings(
            Blur: float.NaN, Refraction: float.PositiveInfinity, CornerRadius: float.NegativeInfinity, Saturation: float.NaN, Fresnel: 1.5f);

        var clamped = s.Clamped(fallback);

        Assert.Equal(0.3f, clamped.Blur);
        Assert.Equal(0.5f, clamped.Refraction);
        Assert.Equal(12f, clamped.CornerRadius);
        Assert.Equal(0.2f, clamped.Saturation);
        Assert.Equal(1.5f, clamped.Fresnel); // une valeur valide reste
    }

    [Fact]
    public void Clamped_UnknownBevel_IsPill()
    {
        var s = new GlassSettings(Bevel: (GlassBevel)7);

        Assert.Equal(GlassBevel.Pill, s.Clamped(new GlassSettings()).Bevel);
    }

    private static void AssertInBounds(GlassSettings s)
    {
        foreach (var p in GlassSettings.Parameters)
        {
            var value = (float)typeof(GlassSettings).GetProperty(p.Name)!.GetValue(s)!;
            Assert.InRange(value, p.Min, p.Max);
        }
    }

    [Fact]
    public void Presets_HaveTheirValues()
    {
        Assert.Equal(
            new GlassSettings(
                Blur: 0.1f, Refraction: 0.69f, ChromaticAberration: 0.05f, EdgeHighlight: 0.3f, Specular: 0.4f, Fresnel: 1f,
                Saturation: 0.2f, Brightness: 0.05f, ShadowOpacity: 0.25f),
            GlassPresets.Find(GlassPresets.Clair));
        Assert.Equal(
            new GlassSettings(
                Blur: 0.6f, Refraction: 0.4f, ChromaticAberration: 0.02f, EdgeHighlight: 0.25f, Specular: 0.25f, Fresnel: 0.8f,
                Saturation: 0.3f, Tint: 0.1f, Brightness: 0.08f),
            GlassPresets.Find(GlassPresets.Givre));
        Assert.Equal(
            new GlassSettings(
                Blur: 0.15f, Refraction: 0.9f, ChromaticAberration: 0.25f, EdgeHighlight: 0.5f, Specular: 0.8f, Fresnel: 1.2f),
            GlassPresets.Find(GlassPresets.Prisme));
        Assert.Equal(
            new GlassSettings(
                Blur: 0.3f, Refraction: 0.3f, ChromaticAberration: 0f, EdgeHighlight: 0.1f, Specular: 0.1f, Fresnel: 0.5f),
            GlassPresets.Find(GlassPresets.Discret));
    }

    [Fact]
    public void Presets_AreAllInBounds()
    {
        Assert.Equal(["clair", "givre", "prisme", "discret"], GlassPresets.Ids);
        foreach (var id in GlassPresets.Ids)
            AssertInBounds(GlassPresets.Find(id)!);
    }

    [Fact]
    public void Presets_Find_IgnoresCase_AndIsNullForCustomOrUnknown()
    {
        Assert.Equal(GlassPresets.Find("prisme"), GlassPresets.Find("PRISME"));
        Assert.NotNull(GlassPresets.Find("Givre"));
        Assert.Null(GlassPresets.Find(GlassPresets.Custom));
        Assert.Null(GlassPresets.Find("inconnu"));
        Assert.Null(GlassPresets.Find(null));
        Assert.Equal("personnalise", GlassPresets.Custom);
    }
}
