using System.Text.Json;
using AshboardVision.Sdk.Testing;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Demo.Tests;

/// <summary>Les tests du module « Démo » : il s'anime à 12 pas par seconde, suit le thème et se laisse arrêter par son réglage.</summary>
public sealed class DemoTests
{
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifest.json");

    private static readonly GridSize Size = new(3, 1);

    // L'horloge du test commence ici (le 1er janvier 2000, à minuit pile, donc au début d'un pas) et ne peut qu'avancer.
    private static readonly DateTimeOffset Start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static bool HasDrawing(SKBitmap image) => image.Pixels.Any(pixel => pixel.Alpha > 0);

    /// <summary>Un module démarré avec ces réglages ; le test pose l'heure avec <c>host.Time.SetUtcNow</c> avant de dessiner.</summary>
    private static async Task<ModuleTestHost<DemoModule>> StartedAsync(string settingsJson = "{}")
    {
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        var host = new ModuleTestHost<DemoModule>(ManifestPath, settings);
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Demo_IsAnimating()
    {
        // Animé dès sa création : le logiciel redessine la case en continu, dans la limite du plafond d'images par seconde.
        await using var module = new DemoModule();

        Assert.True(module.IsAnimating);
    }

    [Fact]
    public async Task Demo_AnimateSettingOff_FreezesDrawing()
    {
        // ModuleTestHost n'expose pas l'instance du module : on ne peut pas lire IsAnimating après StartAsync. Ce test vérifie ce qu'on voit du
        // réglage « Animer » décoché : le dessin ne bouge plus d'un pas à l'autre (Render_MovesAtTwelveStepsPerSecond montre qu'il bouge sinon).
        await using var host = await StartedAsync("""{ "animate": false }""");

        host.Time.SetUtcNow(Start);
        using var first = host.Render(Size, "classique");
        host.Time.SetUtcNow(Start.AddMilliseconds(84));
        using var second = host.Render(Size, "classique");

        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Theory]
    [InlineData("classique")]
    [InlineData("liquid-glass")]
    [InlineData("pixel")]
    public async Task Render_InEveryTheme_DrawsSomething(string theme)
    {
        await using var host = await StartedAsync();

        using var image = host.Render(Size, theme);

        Assert.True(HasDrawing(image));
        Assert.Equal(ModuleHealth.Online, host.Health);
    }

    [Fact]
    public async Task Render_MovesAtTwelveStepsPerSecond()
    {
        await using var host = await StartedAsync();

        // Un pas dure 83,3 ms : à 0 et à 40 ms on est dans le même pas, à 84 ms dans le suivant.
        host.Time.SetUtcNow(Start);
        using var atStart = host.Render(Size, "classique");
        host.Time.SetUtcNow(Start.AddMilliseconds(40));
        using var samePace = host.Render(Size, "classique");
        host.Time.SetUtcNow(Start.AddMilliseconds(84));
        using var nextPace = host.Render(Size, "classique");

        Assert.Equal(atStart.Bytes, samePace.Bytes);
        Assert.NotEqual(atStart.Bytes, nextPace.Bytes);
    }
}
