using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;

namespace Trofeo.Core.Tests;

/// <summary>
/// Le rendu complet d'un thème est reproductible : mêmes modules, même thème, mêmes octets. Les vrais modules sont
/// exercés par la commande <c>--check</c> de <c>tools/Trofeo.ThemePreview</c> (seul l'outil référence les cinq projets
/// de modules) ; ici un module factice dessine du texte et une barre avec la police et les couleurs du thème.
/// </summary>
public class ThemeRenderTests
{
    public static TheoryData<string> ThemeIds()
    {
        var data = new TheoryData<string>();
        foreach (var theme in Themes.All)
        {
            data.Add(theme.Theme.Id);
        }

        return data;
    }

    private static FakeModule TextAndBar() => new((canvas, ctx) =>
    {
        var theme = ctx.Theme;
        using var font = new SKFont(theme.Typeface, 28f * ctx.TextScale);
        using var text = new SKPaint { Color = theme.Text, IsAntialias = true };
        canvas.DrawText("14:32 Éclaircies 412 Mb/s", ctx.Bounds.Left + 18f, ctx.Bounds.Top + 48f, SKTextAlign.Left, font, text);

        using var track = new SKPaint { Color = theme.Track };
        using var fill = new SKPaint { Color = theme.Accent };
        var bar = SKRect.Create(ctx.Bounds.Left + 18f, ctx.Bounds.Top + 70f, ctx.Bounds.Width - 36f, 8f);
        canvas.DrawRect(bar, track);
        canvas.DrawRect(SKRect.Create(bar.Left, bar.Top, bar.Width * 0.4f, bar.Height), fill);
    });

    private static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Compose_AllThemes_Deterministic(string id)
    {
        var definition = Themes.Find(id)!;
        var layout = DashboardLayout.Default;
        var modules = layout.Views[0].Placements.ToDictionary(p => p.InstanceId, _ => (IModule)TextAndBar());
        using var background = BackgroundFactory.Create(null, definition);

        using var first = new FrameComposer();
        using var second = new FrameComposer();
        using var a = first.Compose(layout.Views[0].Placements, modules, definition, background, DateTimeOffset.UnixEpoch);
        using var b = second.Compose(layout.Views[0].Placements, modules, definition, background, DateTimeOffset.UnixEpoch);
        using var c = first.Compose(layout.Views[0].Placements, modules, definition, background, DateTimeOffset.UnixEpoch); // même composeur, flou en cache

        var expected = Png(a);
        Assert.Equal(expected, Png(b));
        Assert.Equal(expected, Png(c));

        // Le module a bien dessiné : sans lui l'image serait autre (le test ne compare pas deux images vides).
        using var empty = first.Compose(layout.Views[0].Placements, new Dictionary<Guid, IModule>(), definition, background, DateTimeOffset.UnixEpoch);
        Assert.NotEqual(expected, Png(empty));
    }
}
