using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.ModuleKit;

namespace Trofeo.ThemePreview;

/// <summary>
/// <c>--check</c> : pour chaque thème et chaque taille de texte, avec des textes très longs, vérifie que rien n'est
/// peint hors des panneaux (écarts du damier et marge de l'écran). Deux contrôles :
/// <list type="number">
/// <item>l'écran composé avec les modules est comparé à l'écran composé sans eux (même disposition, mêmes panneaux) :
/// tout pixel différent hors des rectangles des panneaux est un débordement, quelle que soit la décoration du thème ;</item>
/// <item>chaque module est dessiné seul, <b>sans le rognage du composeur</b>, sur une image transparente : tout pixel
/// peint hors de son rectangle serait coupé net à l'écran (texte tronqué) et est signalé.</item>
/// </list>
/// </summary>
internal static class OverflowCheck
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        using var scene = await PreviewScene.CreateAsync(longTexts: true).ConfigureAwait(false);
        using var composer = new FrameComposer();
        var faults = new List<string>();
        var none = new Dictionary<Guid, IModule>();

        foreach (var theme in Themes.All)
        {
            using var background = BackgroundFactory.Create(null, theme);
            foreach (var size in Enum.GetValues<TextSize>())
            {
                var layout = PreviewScene.Layout(size);
                var rects = layout.Views[0].Placements
                    .Select(p => (p, Rect: GridGeometry.ModuleRect(p.Column, p.Row, p.Columns, p.Rows, theme.Grid)))
                    .ToList();

                CheckComposed(faults, theme, size, layout, rects, scene, composer, background, none);
                CheckUnclipped(faults, theme, size, rects, scene);
            }
        }

        if (scene.NotOnline.Count > 0)
        {
            // Le contrôle porterait sur des textes de module hors ligne, donc trop courts : il ne prouve rien.
            faults.Add("Contrôle non fiable : module(s) hors ligne : " + string.Join(", ", scene.NotOnline) + ".");
        }

        if (faults.Count == 0)
        {
            output.WriteLine("OK");
            return 0;
        }

        foreach (var fault in faults)
        {
            output.WriteLine(fault);
        }

        return 1;
    }

    private static void CheckComposed(
        List<string> faults, ThemeDefinition theme, TextSize size, DashboardLayout layout,
        List<(ModulePlacement Placement, SKRectI Rect)> rects, PreviewScene scene, FrameComposer composer,
        IBackground background, IReadOnlyDictionary<Guid, IModule> none)
    {
        using var with = composer.Compose(layout.Views[0].Placements, scene.ByPlacement, theme, background, PreviewScene.Now);
        using var without = composer.Compose(layout.Views[0].Placements, none, theme, background, PreviewScene.Now);
        var a = with.Pixels;
        var b = without.Pixels;

        var counts = new Dictionary<Guid, (int Count, int X, int Y)>();
        for (var y = 0; y < GridGeometry.Height; y++)
        {
            for (var x = 0; x < GridGeometry.Width; x++)
            {
                if (a[y * GridGeometry.Width + x] == b[y * GridGeometry.Width + x] || Inside(rects, x, y))
                {
                    continue;
                }

                var owner = Nearest(rects, x, y);
                counts[owner] = counts.TryGetValue(owner, out var seen) ? (seen.Count + 1, seen.X, seen.Y) : (1, x, y);
            }
        }

        foreach (var (id, (count, x, y)) in counts)
        {
            faults.Add($"Débordement dans l'écart du damier : thème {theme.Theme.Id}, texte {size}, module {scene.Names[id]}, "
                + $"premier pixel ({x}, {y}), {count} pixels.");
        }
    }

    private static void CheckUnclipped(
        List<string> faults, ThemeDefinition theme, TextSize size,
        List<(ModulePlacement Placement, SKRectI Rect)> rects, PreviewScene scene)
    {
        foreach (var (placement, rect) in rects)
        {
            using var bitmap = new SKBitmap(GridGeometry.Width, GridGeometry.Height); // transparent
            using (var canvas = new SKCanvas(bitmap))
            {
                var bounds = SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height);
                var context = new ModuleRenderContext(
                    bounds, new GridSize(placement.Columns, placement.Rows), theme.Theme, size.Factor(), PreviewScene.Now);
                scene.ByPlacement[placement.InstanceId].Render(canvas, context);
            }

            var pixels = bitmap.Pixels;
            var count = 0;
            int firstX = 0, firstY = 0;
            for (var y = 0; y < GridGeometry.Height; y++)
            {
                for (var x = 0; x < GridGeometry.Width; x++)
                {
                    if (pixels[y * GridGeometry.Width + x].Alpha == 0 || Contains(rect, x, y))
                    {
                        continue;
                    }

                    if (count++ == 0)
                    {
                        (firstX, firstY) = (x, y);
                    }
                }
            }

            if (count > 0)
            {
                faults.Add($"Dessin hors du rectangle du module (coupé à l'écran) : thème {theme.Theme.Id}, texte {size}, "
                    + $"module {scene.Names[placement.InstanceId]}, premier pixel ({firstX}, {firstY}), {count} pixels.");
            }
        }
    }

    private static bool Contains(SKRectI rect, int x, int y) => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    private static bool Inside(List<(ModulePlacement Placement, SKRectI Rect)> rects, int x, int y)
    {
        foreach (var (_, rect) in rects)
        {
            if (Contains(rect, x, y))
            {
                return true;
            }
        }

        return false;
    }

    private static Guid Nearest(List<(ModulePlacement Placement, SKRectI Rect)> rects, int x, int y)
    {
        var best = rects[0].Placement.InstanceId;
        var bestDistance = long.MaxValue;
        foreach (var (placement, rect) in rects)
        {
            long dx = Math.Max(Math.Max(rect.Left - x, 0), x - (rect.Right - 1));
            long dy = Math.Max(Math.Max(rect.Top - y, 0), y - (rect.Bottom - 1));
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                (best, bestDistance) = (placement.InstanceId, distance);
            }
        }

        return best;
    }
}
