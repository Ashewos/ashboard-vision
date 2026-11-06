using System.Text.Json;
using AshboardVision.Sdk;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace ModuleNamespace.Tests;

/// <summary>
/// Les tests de ton module. <see cref="ModuleTestHost{TModule}"/> le fait tourner dans le processus du test, avec de faux services et les
/// vrais thèmes du logiciel : c'est rapide et tu peux déboguer pas à pas. L'essai enfermé, comme le logiciel le lance, est celui de
/// la commande <c>ashboard-module check</c>.
/// </summary>
public sealed class ModuleClassNameTests
{
    // Le projet de tests référence le module : son manifest.json est copié à côté des tests à chaque construction.
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static bool HasDrawing(SKBitmap image) => image.Pixels.Any(pixel => pixel.Alpha > 0);

    [Theory]
    [InlineData("classique")]
    [InlineData("liquid-glass")]
    [InlineData("pixel")]
    public async Task Render_InEveryTheme_DrawsSomething(string theme)
    {
        await using var host = new ModuleTestHost<ModuleClassName>(ManifestPath);
        await host.StartAsync();

        using var image = host.Render(new GridSize(3, 1), theme);

        Assert.True(HasDrawing(image));
        Assert.Equal(ModuleHealth.Online, host.Health);
    }

    [Fact]
    public async Task Render_WithAMessageSetting_DrawsItInsteadOfTheDefaultOne()
    {
        var settings = JsonDocument.Parse("""{ "message": "Salut tout le monde" }""").RootElement;
        await using var withMessage = new ModuleTestHost<ModuleClassName>(ManifestPath, settings);
        await using var byDefault = new ModuleTestHost<ModuleClassName>(ManifestPath);
        await withMessage.StartAsync();
        await byDefault.StartAsync();

        using var custom = withMessage.Render(new GridSize(3, 1), "classique");
        using var standard = byDefault.Render(new GridSize(3, 1), "classique");

        Assert.NotEqual(standard.Bytes, custom.Bytes);
    }

    [Fact]
    public async Task Render_AtTheMinimumSize_DoesNotThrow()
    {
        await using var host = new ModuleTestHost<ModuleClassName>(ManifestPath);
        await host.StartAsync();

        using var image = host.Render(new GridSize(2, 1), "pixel");

        Assert.True(HasDrawing(image));
    }
}
