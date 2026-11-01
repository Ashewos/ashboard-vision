namespace Trofeo.Display.Tests;

public sealed class TaskbarThemeTests
{
    [Fact]
    public void IconFor_LightTaskbar_UsesLightIcon()
    {
        Assert.Equal("tray-clair.ico", TaskbarTheme.IconResourceFor(lightTaskbar: true));
    }

    [Fact]
    public void IconFor_DarkTaskbar_UsesDarkIcon()
    {
        Assert.Equal("tray-sombre.ico", TaskbarTheme.IconResourceFor(lightTaskbar: false));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)] // valeur absente : barre sombre
    [InlineData("1", false)] // pas un entier : on ne devine pas
    public void IsLight_ReadsSystemUsesLightTheme(object? registryValue, bool expected)
    {
        Assert.Equal(expected, TaskbarTheme.IsLight(registryValue));
    }

    [Fact]
    public void TryLoadIcon_UnknownResource_ReturnsNull()
    {
        Assert.Null(TaskbarTheme.TryLoadIcon("inexistant.ico"));
    }

    // Les deux icônes sont des ressources incorporées de Trofeo.Display (assets/brand) : sans elles, la zone de notification
    // retomberait en silence sur l'icône générique de Windows.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryLoadIcon_BrandIcon_IsEmbeddedAndLoads(bool lightTaskbar)
    {
        var name = TaskbarTheme.IconResourceFor(lightTaskbar);

        using var icon = TaskbarTheme.TryLoadIcon(name);

        Assert.NotNull(icon);
        Assert.True(icon.Width > 0 && icon.Height > 0);
    }
}
