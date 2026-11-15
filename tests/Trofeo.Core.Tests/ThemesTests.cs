using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public class ThemesTests
{
    [Fact]
    public void Classique_IsUnchanged()
    {
        var theme = Themes.Classique;

        Assert.Equal("classique", theme.Id);
        Assert.Equal(SKColor.Parse("#0E1116"), theme.Background);
        Assert.Equal(SKColor.Parse("#111317"), theme.Panel);
        Assert.Equal(0.86f, theme.PanelOpacity);
        Assert.Equal(14f, theme.CornerRadius);
        Assert.Equal(SKColor.Parse("#ECEEF0"), theme.Text);
        Assert.Equal(SKColor.Parse("#8C929C"), theme.TextMuted);
        Assert.Equal(SKColor.Parse("#7FD1C7"), theme.Accent);
        Assert.Equal(SKColor.Parse("#F2B84B"), theme.Warning);
        Assert.StartsWith("Barlow", theme.Typeface.FamilyName);
        Assert.Equal(ThemeShape.Rounded, theme.Shape);
        Assert.Equal(theme.Text, theme.Highlight);
        Assert.Equal(theme.Text.WithAlpha(18), theme.Surface);
        Assert.Equal(theme.Text.WithAlpha(20), theme.Track);
        Assert.Same(theme.Typeface, theme.FeedTypeface);
        Assert.Equal(1f, theme.FeedScale);
        Assert.Same(theme, Themes.ClassiqueDefinition.Theme);
        Assert.Same(theme, Themes.Find("classique")!.Theme);
    }

    [Theory]
    [InlineData("classique", "CLASSIQUE")]
    [InlineData("liquid-glass", "Liquid-Glass")]
    [InlineData("pixel", "PIXEL")]
    public void Find_ById_IgnoresCase(string id, string typed)
    {
        Assert.Same(Themes.Find(id), Themes.Find(typed));
        Assert.NotNull(Themes.Find(typed));
        Assert.Equal(id, Themes.Find(typed)!.Theme.Id);
    }

    [Theory]
    [InlineData("inconnu")]
    [InlineData("")]
    [InlineData(null)]
    public void Find_Unknown_IsNull(string? id)
    {
        Assert.Null(Themes.Find(id));
    }

    [Fact]
    public void All_ListsFourThemesInOrder()
    {
        Assert.Equal(new[] { "classique", "liquid-glass", "pixel", "liquid-glass-avance" }, Themes.All.Select(t => t.Theme.Id));
        Assert.Same(Themes.ClassiqueDefinition, Themes.All[0]);
        Assert.Same(Themes.LiquidGlass, Themes.All[1]);
        Assert.Same(Themes.Pixel, Themes.All[2]);
        Assert.Same(Themes.LiquidGlassAdvanced, Themes.All[3]);
    }

    public static TheoryData<string, string, string, float, int, int, ThemeShape, string, string, float> MockupValues => new()
    {
        { "classique", "#7FD1C7", "#F2B84B", 14f, 14, 12, ThemeShape.Rounded, "Barlow Semi Condensed", "Barlow Semi Condensed", 1f },
        { "liquid-glass", "#FFFFFF", "#FFD56B", 30f, 14, 12, ThemeShape.Rounded, "Manrope", "Manrope", 1f },
        { "pixel", "#7CE0A3", "#FFD27A", 0f, 18, 20, ThemeShape.Pixel, "VT323", "VT323", 1.24f },
    };

    [Theory]
    [MemberData(nameof(MockupValues))]
    public void Themes_MatchMockup(string id, string accent, string warning, float radius, int inset, int gap,
        ThemeShape shape, string typeface, string feedTypeface, float feedScale)
    {
        var def = Themes.Find(id)!;

        Assert.Equal(SKColor.Parse(accent), def.Theme.Accent);
        Assert.Equal(SKColor.Parse(warning), def.Theme.Warning);
        Assert.Equal(radius, def.Theme.CornerRadius);
        Assert.Equal(new GridSpacing(inset, gap), def.Grid);
        Assert.Equal(shape, def.Theme.Shape);
        Assert.Same(EmbeddedFonts.Find(typeface), def.Theme.Typeface);
        Assert.Same(EmbeddedFonts.Find(feedTypeface), def.Theme.FeedTypeface);
        Assert.Equal(feedScale, def.Theme.FeedScale);
    }

    [Fact]
    public void Pixel_UsesVt323WithScale()
    {
        var t = Themes.Pixel.Theme;

        Assert.Same(EmbeddedFonts.Find("VT323"), t.Typeface);
        Assert.Equal(1.24f, t.TypefaceScale);
        Assert.Same(EmbeddedFonts.Find("VT323"), t.FeedTypeface);
        Assert.Equal(1.24f, t.FeedScale);
    }

    [Fact]
    public void LiquidGlass_Values()
    {
        var t = Themes.LiquidGlass.Theme;
        var white = SKColor.Parse("#FFFFFF");

        Assert.Equal(SKColor.Parse("#101220"), t.Panel);
        Assert.Equal(0.22f, t.PanelOpacity);
        Assert.Equal(white, t.Text);
        Assert.Equal(white.WithAlpha(184), t.TextMuted);
        Assert.Equal(white, t.Highlight);
        Assert.Equal(white.WithAlpha(56), t.Surface);
        Assert.Equal(white.WithAlpha(56), t.Track);
        Assert.Equal(SKColor.Parse("#241A3D"), t.Background);
        Assert.Equal(new ThemeBackground(SKColor.Parse("#241A3D"), true), Themes.LiquidGlass.DefaultBackground);

        var p = Themes.LiquidGlass.Panel;
        Assert.Equal(PanelKind.Glass, p.Kind);
        Assert.Equal(white.WithAlpha(56), p.Outline);
        Assert.Equal(26f, p.BlurSigma);
        Assert.Equal(1.7f, p.Saturation);
        Assert.Equal(white.WithAlpha(46), p.SheenTop);
        Assert.Equal(white.WithAlpha(10), p.SheenMid);
        Assert.Equal(white.WithAlpha(20), p.SheenBottom);
        Assert.Equal(white.WithAlpha(140), p.TopLight);
        Assert.Equal(new SKColor(0, 0, 0, 46), p.DropShadow);
    }

    [Fact]
    public void Pixel_Values()
    {
        var t = Themes.Pixel.Theme;

        Assert.Equal(SKColor.Parse("#17142A"), t.Panel);
        Assert.Equal(1f, t.PanelOpacity);
        Assert.Equal(SKColor.Parse("#F4EEDE"), t.Text);
        Assert.Equal(SKColor.Parse("#8F86B8"), t.TextMuted);
        Assert.Equal(SKColor.Parse("#FFD27A"), t.Highlight);
        Assert.Equal(SKColor.Parse("#2E2850"), t.Surface);
        Assert.Equal(SKColor.Parse("#2E2850"), t.Track);
        Assert.Equal(SKColor.Parse("#17142A"), t.Background);
        Assert.Equal(new ThemeBackground(SKColor.Parse("#17142A"), true), Themes.Pixel.DefaultBackground);

        var p = Themes.Pixel.Panel;
        Assert.Equal(PanelKind.Pixel, p.Kind);
        Assert.Equal(SKColor.Parse("#2E2850"), p.Border);
        Assert.Equal(4, p.BorderWidth);
        Assert.Equal(new SKColor(0x0A, 0x08, 0x14, 153), p.PixelShadow);
    }

    [Fact]
    public void Classique_PanelAndBackground()
    {
        var def = Themes.ClassiqueDefinition;

        Assert.Equal(PanelKind.Matte, def.Panel.Kind);
        Assert.Equal(new SKColor(255, 255, 255, 15), def.Panel.Outline);
        Assert.Equal(GridSpacing.Default, def.Grid);
        Assert.Equal(new ThemeBackground(SKColor.Parse("#0E1116"), false), def.DefaultBackground);
    }
}
