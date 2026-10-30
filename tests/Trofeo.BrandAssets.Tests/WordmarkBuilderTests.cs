namespace Trofeo.BrandAssets.Tests;

public sealed class WordmarkBuilderTests
{
    private static readonly string Font = Path.Combine(AppContext.BaseDirectory, "Manrope-Medium.ttf");

    [Fact]
    public void PathData_NotEmpty_NoTextElement()
    {
        var data = WordmarkBuilder.PathData("Ashboard", Font, 100f);

        Assert.False(string.IsNullOrWhiteSpace(data));
        Assert.StartsWith("M", data);
        Assert.DoesNotContain("<text", data);
    }

    [Fact]
    public void Build_LetterSpacing_WidensTheWord()
    {
        using var tight = WordmarkBuilder.Build("VISION", Font, 50f, letterSpacing: 0f);
        using var spaced = WordmarkBuilder.Build("VISION", Font, 50f, letterSpacing: 14f);

        // Six lettres, donc cinq espaces entre elles.
        Assert.Equal(tight.Bounds.Width + 5 * 14f, spaced.Bounds.Width, precision: 1);
    }

    [Fact]
    public void PathData_MissingFont_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => WordmarkBuilder.PathData("Ashboard", "absente.ttf", 100f));
    }
}
