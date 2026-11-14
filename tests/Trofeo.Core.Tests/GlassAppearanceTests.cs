using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;

namespace Trofeo.Core.Tests;

public class GlassAppearanceTests
{
    private sealed class NoSystemFonts : ISystemFonts
    {
        public IReadOnlyList<string> Families => Array.Empty<string>();
        public SKTypeface? Match(string family) => null;
    }

    private static ThemeDefinition Resolve(AppearanceSettings settings, List<string>? warnings = null) =>
        AppearanceResolver.Resolve(settings, new NoSystemFonts(), warnings is null ? null : warnings.Add);

    private static readonly GlassSettings Clair = GlassPresets.Find(GlassPresets.Clair)!;

    // ---- GlassAppearance.Resolve ----

    [Fact]
    public void Resolve_KnownPreset_IgnoresValues()
    {
        var warnings = new List<string>();

        var s = new GlassAppearance(GlassPresets.Prisme, new GlassSettings(Blur: 0.9f)).Resolve(warnings.Add);

        Assert.Equal(GlassPresets.Find(GlassPresets.Prisme), s);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Resolve_Custom_UsesClampedValues()
    {
        var s = new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: 0.4f, CornerRadius: 80f, Saturation: float.NaN)).Resolve();

        Assert.Equal(0.4f, s.Blur);
        Assert.Equal(49f, s.CornerRadius);
        Assert.Equal(Clair.Saturation, s.Saturation); // NaN : la valeur de Clair
    }

    [Fact]
    public void Resolve_CustomWithoutValues_IsClair()
    {
        Assert.Equal(Clair, new GlassAppearance(GlassPresets.Custom).Resolve());
    }

    [Fact]
    public void Resolve_UnknownPreset_IsClairAndWarns()
    {
        var warnings = new List<string>();

        var s = new GlassAppearance("x").Resolve(warnings.Add);

        Assert.Equal(Clair, s);
        Assert.Equal("Préréglage du verre « x » inconnu : Clair utilisé.", Assert.Single(warnings));
    }

    [Fact]
    public void Resolve_Default_IsClair()
    {
        Assert.Equal(Clair, new GlassAppearance().Resolve());
    }

    // ---- Résolveur et thèmes ----

    [Fact]
    public void Resolver_AdvancedTheme_AppliesGlassAndCornerRadius()
    {
        var settings = new AppearanceSettings(
            "liquid-glass-avance", Glass: new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: 0.5f, CornerRadius: 20f)));

        var def = Resolve(settings);

        Assert.Equal(PanelKind.LiquidGlass, def.Panel.Kind);
        Assert.Equal(0.5f, def.Panel.Glass!.Blur);
        Assert.Equal(20f, def.Panel.Glass.CornerRadius);
        Assert.Equal(20f, def.Theme.CornerRadius);
        Assert.True(def.Theme.ServiceDrawsSelection);
    }

    [Fact]
    public void Resolver_AdvancedThemeWithoutGlass_IsClair()
    {
        var def = Resolve(new AppearanceSettings("liquid-glass-avance"));

        Assert.Equal(Clair, def.Panel.Glass);
        Assert.Equal(Clair.CornerRadius, def.Theme.CornerRadius);
    }

    [Fact]
    public void Resolver_AdvancedTheme_WarnsOnUnknownPreset()
    {
        var warnings = new List<string>();

        var def = Resolve(new AppearanceSettings("liquid-glass-avance", Glass: new GlassAppearance("zzz")), warnings);

        Assert.Equal(Clair, def.Panel.Glass);
        Assert.Contains(warnings, w => w.Contains("zzz"));
    }

    [Fact]
    public void Resolver_OtherThemes_IgnoreGlass()
    {
        var glass = new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: 0.9f, CornerRadius: 5f));

        var def = Resolve(new AppearanceSettings("liquid-glass", Glass: glass));

        Assert.Same(Themes.LiquidGlass, def);
        Assert.Null(def.Panel.Glass);
        Assert.False(def.Theme.ServiceDrawsSelection);
        Assert.Equal(30f, def.Theme.CornerRadius);
    }

    [Fact]
    public void Themes_All_HasFourThemes_AdvancedLast()
    {
        Assert.Equal(["classique", "liquid-glass", "pixel", "liquid-glass-avance"], Themes.All.Select(t => t.Theme.Id));
        Assert.Same(Themes.LiquidGlassAdvanced, Themes.Find("LIQUID-GLASS-AVANCE"));
    }

    [Fact]
    public void AdvancedTheme_ReusesLiquidGlassLook()
    {
        var advanced = Themes.LiquidGlassAdvanced;
        var glass = Themes.LiquidGlass;

        Assert.Equal(0.22f, advanced.Theme.PanelOpacity);
        Assert.Equal(30f, advanced.Theme.CornerRadius);
        Assert.Equal(glass.Theme.Background, advanced.Theme.Background);
        Assert.Equal(glass.Theme.Panel, advanced.Theme.Panel);
        Assert.Equal(glass.Theme.Text, advanced.Theme.Text);
        Assert.Equal(glass.Theme.TextMuted, advanced.Theme.TextMuted);
        Assert.Equal(glass.Theme.Accent, advanced.Theme.Accent);
        Assert.Equal(glass.Theme.Warning, advanced.Theme.Warning);
        Assert.Equal(glass.Theme.Highlight, advanced.Theme.Highlight);
        Assert.Equal(glass.Theme.Surface, advanced.Theme.Surface);
        Assert.Equal(glass.Theme.Track, advanced.Theme.Track);
        Assert.Same(glass.Theme.Typeface, advanced.Theme.Typeface);
        Assert.Same(glass.Theme.FeedTypeface, advanced.Theme.FeedTypeface);
        Assert.Equal(glass.Theme.Shape, advanced.Theme.Shape);
        Assert.Equal(glass.Grid, advanced.Grid);
        Assert.Equal(glass.DefaultBackground, advanced.DefaultBackground);
        Assert.True(advanced.DefaultBackground.Aurora);
        Assert.Equal(Clair, advanced.Panel.Glass);
        Assert.True(advanced.Theme.ServiceDrawsSelection);
        Assert.False(Themes.Classique.ServiceDrawsSelection);
        Assert.False(glass.Theme.ServiceDrawsSelection);
        Assert.False(Themes.Pixel.Theme.ServiceDrawsSelection);
    }

    // ---- layout.json ----

    private static LayoutLoadResult Parse(string appearanceJson, List<string> warnings) =>
        LayoutJson.Parse($$"""{"Rotation":180,"Views":[{"Id":"7e4b2a91-c3d5-4f08-a6b1-92d0e5f13c48","Name":"Vue 1","Placements":[]}],"Appearance":{{appearanceJson}}}""", warnings.Add);

    [Fact]
    public void Json_RoundTrip_KeepsPresetAndValues()
    {
        var glass = new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: 0.4f, Bevel: GlassBevel.Dome, CornerRadius: 12f));
        var layout = DashboardLayout.Default with { Appearance = new AppearanceSettings("liquid-glass-avance", Glass: glass) };

        var json = JsonSerializer.Serialize(layout, LayoutJson.Options);
        var back = LayoutJson.Parse(json, null);

        Assert.Contains("\"Dome\"", json); // l'énumération est écrite en texte
        Assert.Equal(glass, back.Layout!.Appearance!.Glass);
        Assert.Equal("liquid-glass-avance", back.Layout.Appearance.Theme);
    }

    [Fact]
    public void Json_RoundTrip_KeepsPresetOnly()
    {
        var layout = DashboardLayout.Default with
        {
            Appearance = new AppearanceSettings("liquid-glass-avance", Glass: new GlassAppearance(GlassPresets.Givre)),
        };

        var back = LayoutJson.Parse(JsonSerializer.Serialize(layout, LayoutJson.Options), null);

        Assert.Equal(new GlassAppearance(GlassPresets.Givre), back.Layout!.Appearance!.Glass);
    }

    [Fact]
    public void Json_GlassOfWrongType_KeepsRestOfAppearanceAndWarns()
    {
        var warnings = new List<string>();

        var result = Parse("""{"Theme":"liquid-glass-avance","PanelOpacity":0.5,"AnimateViewSwitch":false,"Glass":12}""", warnings);

        var appearance = result.Layout!.Appearance!;
        Assert.Equal("liquid-glass-avance", appearance.Theme);
        Assert.Equal(0.5f, appearance.PanelOpacity);
        Assert.False(appearance.AnimateViewSwitch);
        Assert.Null(appearance.Glass);
        Assert.Equal("Réglages du verre illisibles : préréglage Clair utilisé.", Assert.Single(warnings));
        Assert.Equal(warnings, result.Warnings);

        // Le verre retombe sur Clair.
        Assert.Equal(Clair, Resolve(appearance).Panel.Glass);
    }

    [Fact]
    public void Json_GlassFieldOfWrongType_KeepsRestOfAppearanceAndWarns()
    {
        var warnings = new List<string>();

        var result = Parse("""{"Theme":"pixel","Glass":{"Preset":"personnalise","Values":{"Blur":"beaucoup"}}}""", warnings);

        Assert.Equal("pixel", result.Layout!.Appearance!.Theme);
        Assert.Null(result.Layout.Appearance.Glass);
        Assert.Single(warnings);
    }

    [Fact]
    public void Json_GlassValueNaN_IsClampedOnResolve()
    {
        var warnings = new List<string>();

        var result = Parse(
            """{"Theme":"liquid-glass-avance","Glass":{"Preset":"personnalise","Values":{"Blur":"NaN","Refraction":9,"CornerRadius":80}}}""", warnings);

        var def = Resolve(result.Layout!.Appearance!);

        Assert.Empty(warnings);
        Assert.Equal(Clair.Blur, def.Panel.Glass!.Blur); // NaN : la valeur de Clair
        Assert.Equal(2f, def.Panel.Glass.Refraction);
        Assert.Equal(49f, def.Panel.Glass.CornerRadius);
        Assert.Equal(49f, def.Theme.CornerRadius);
    }

    [Fact]
    public void Json_GlassWithNaN_CanBeWrittenAgain()
    {
        var layout = DashboardLayout.Default with
        {
            Appearance = new AppearanceSettings(
                "liquid-glass-avance", Glass: new GlassAppearance(GlassPresets.Custom, new GlassSettings(Blur: float.NaN))),
        };

        var back = LayoutJson.Parse(JsonSerializer.Serialize(layout, LayoutJson.Options), null);

        Assert.True(float.IsNaN(back.Layout!.Appearance!.Glass!.Values!.Blur));
    }

    [Fact]
    public void Json_UnknownPreset_ResolvesToClairWithWarning()
    {
        var warnings = new List<string>();
        var result = Parse("""{"Theme":"liquid-glass-avance","Glass":{"Preset":"inconnu"}}""", warnings);

        var def = Resolve(result.Layout!.Appearance!, warnings);

        Assert.Equal(Clair, def.Panel.Glass);
        Assert.Contains(warnings, w => w.Contains("inconnu"));
    }

    [Fact]
    public void Json_NullGlass_IsNullWithoutWarning()
    {
        var warnings = new List<string>();

        var result = Parse("""{"Theme":"liquid-glass-avance","Glass":null}""", warnings);

        Assert.Null(result.Layout!.Appearance!.Glass);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Json_OldFileWithoutGlass_IsClair()
    {
        var warnings = new List<string>();

        var result = Parse("""{"Theme":"liquid-glass-avance","PanelOpacity":0.4}""", warnings);

        var appearance = result.Layout!.Appearance!;
        Assert.Null(appearance.Glass);
        Assert.Empty(warnings);
        Assert.Equal(Clair, Resolve(appearance).Panel.Glass);
    }
}
