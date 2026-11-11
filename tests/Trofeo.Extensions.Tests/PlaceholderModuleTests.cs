using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>La case d'un module présent mais qui ne peut pas tourner : elle dit pourquoi, sans charger rien du module.</summary>
public sealed class PlaceholderModuleTests
{
    private static CommunityEntry Entry(CommunityState state, string? message = null, bool withManifest = true) =>
        new("acme.weather", @"C:\modules\acme.weather\1.0.0", withManifest ? ModuleSandbox.Manifest("acme.weather") with { MinSize = new GridSize(3, 2) } : null,
            state, message, null, false);

    [Theory]
    [InlineData(CommunityState.NeedsApproval, "Ce module n'a pas encore été validé.", "À valider dans la fenêtre de réglage")]
    [InlineData(CommunityState.Changed, "Le contenu du module a changé depuis sa validation : valide-le de nouveau.", "Fichiers du module modifiés : valide-le de nouveau")]
    [InlineData(CommunityState.SetAside, "Mis de côté : le module s'est arrêté 3 fois en 10 minutes.", "Mis de côté : le module s'est arrêté 3 fois en 10 minutes.")]
    [InlineData(CommunityState.SetAside, "il plantait", "Mis de côté : il plantait")]
    [InlineData(CommunityState.NeedsNewerApp, "Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel.", "Ce module demande une version plus récente d'Ashboard Vision : mets à jour le logiciel.")]
    [InlineData(CommunityState.Unavailable, null, "Module indisponible")]
    public void Placeholder_SaysWhy(CommunityState state, string? entryMessage, string expected)
    {
        var factory = new PlaceholderModuleFactory(Entry(state, entryMessage));

        using var module = factory.Create(new ModuleContext(Guid.NewGuid(), ModuleSandbox.Json(), TimeProvider.System, new Trofeo.TestDoubles.FakeModuleLog(), new Trofeo.TestDoubles.FakeSecretStore()));

        Assert.Equal(expected, factory.Message);
        Assert.Equal(new ModuleStatus(ModuleHealth.NotConfigured, expected), module.Status);
        Assert.False(module.IsAnimating);
    }

    [Fact]
    public void Placeholder_KeepsTheTypeIdAndMinimumSize_SoTheLayoutKeepsTheCase()
    {
        var withManifest = new PlaceholderModuleFactory(Entry(CommunityState.NeedsApproval));
        var withoutManifest = new PlaceholderModuleFactory(Entry(CommunityState.Invalid, "Manifeste illisible.", withManifest: false));

        Assert.Equal(("acme.weather", new GridSize(3, 2)), (withManifest.Descriptor.TypeId, withManifest.Descriptor.MinSize));
        Assert.Equal(("acme.weather", new GridSize(1, 1)), (withoutManifest.Descriptor.TypeId, withoutManifest.Descriptor.MinSize));
    }

    [Fact]
    public void Placeholder_Render_DrawsTheMessageInTheWarningColor()
    {
        var factory = new PlaceholderModuleFactory(Entry(CommunityState.NeedsApproval));
        using var module = factory.Create(new ModuleContext(Guid.NewGuid(), ModuleSandbox.Json(), TimeProvider.System, new Trofeo.TestDoubles.FakeModuleLog(), new Trofeo.TestDoubles.FakeSecretStore()));
        var theme = Themes.ClassiqueDefinition.Theme;
        using var bitmap = new SKBitmap(400, 120);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        module.Render(canvas, new ModuleRenderContext(SKRect.Create(0, 0, 400, 120), new GridSize(3, 2), theme, 1f, DateTimeOffset.UtcNow));

        // Quelque chose a été peint, et c'est de la couleur d'avertissement du thème.
        var warning = theme.Warning;
        var painted = bitmap.Pixels.Count(pixel => pixel != SKColors.Black);
        var warningLike = bitmap.Pixels.Count(pixel => Math.Abs(pixel.Red - warning.Red) < 12 && Math.Abs(pixel.Green - warning.Green) < 12 && Math.Abs(pixel.Blue - warning.Blue) < 12);
        Assert.True(painted > 100, $"Le message devrait être dessiné ({painted} pixels).");
        Assert.True(warningLike > 20, $"Il devrait être en couleur d'avertissement ({warningLike} pixels).");
    }
}
