namespace Trofeo.BrandAssets.Tests;

public sealed class HorizontalLogoTests
{
    private const string Symbol = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
          <title>Symbole d'essai</title>
          <defs><linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="0" y2="256"><stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#000000"/></linearGradient></defs>
          <path fill="url(#g)" d="M32 32 H224 V224 H32 Z"/>
        </svg>
        """;

    private static readonly string Font = Path.Combine(AppContext.BaseDirectory, "Manrope-Medium.ttf");

    [Fact]
    public void Build_CarriesSymbolAndNameAsPaths_NoTextElement()
    {
        var svg = HorizontalLogo.Build(Symbol, Font, "#ECECEE");

        Assert.Contains("linearGradient", svg);        // le dégradé du symbole suit
        Assert.Contains("fill=\"#ECECEE\"", svg);      // le nom, dans la couleur demandée
        Assert.Contains("fill-opacity=\"0.62\"", svg); // VISION, à 62 %
        Assert.DoesNotContain("<text", svg);
        Assert.Equal(2, svg.Split("<path d=\"M", StringSplitOptions.None).Length - 1); // Ashboard et VISION
    }

    [Fact]
    public void Build_IsAWellFormedSvgWithAViewBox()
    {
        var svg = HorizontalLogo.Build(Symbol, Font, "#17181C");

        var document = System.Xml.Linq.XDocument.Parse(svg);
        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.NotNull(document.Root.Attribute("viewBox"));
    }
}
