using System.Text.Json;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;
using Trofeo.TestDoubles;
using Xunit.Abstractions;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Aucune différence visible entre un module natif et un module de la communauté (plan 9, tâche 5) : le même dessin (un trait avant,
/// <c>DrawSelection</c>, du texte après) est composé dans « Liquid Glass avancé » une fois comme module natif, une fois comme image et
/// rectangles reçus d'un hôte par un <see cref="RemoteModule"/>.
/// </summary>
[Collection("Hosts")]
public sealed class CommunitySelectionParityTests(ITestOutputHelper output) : IAsyncLifetime
{
    private sealed class Sink : ISelectionSink
    {
        public List<SKRect> Rects { get; } = [];

        public void Add(SKRect caseLocalRect) => Rects.Add(caseLocalRect);
    }

    /// <summary>Un fond fixe de 1920 × 462 plein de détails, pour que le verre et la pastille se voient.</summary>
    private sealed class FixedBackground : IBackground
    {
        private readonly SKImage _image;

        public FixedBackground()
        {
            using var surface = SKSurface.Create(new SKImageInfo(GridGeometry.Width, GridGeometry.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            using var gradient = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(GridGeometry.Width, GridGeometry.Height),
                [new SKColor(20, 40, 90), new SKColor(230, 120, 40)], SKShaderTileMode.Clamp);
            using var back = new SKPaint { Shader = gradient };
            canvas.DrawPaint(back);
            using var white = new SKPaint { Color = SKColors.White.WithAlpha(200) };
            for (var x = 4; x < GridGeometry.Width; x += 16)
            {
                canvas.DrawRect(x, 0, 8, GridGeometry.Height, white);
            }

            _image = surface.Snapshot();
        }

        public bool IsAnimated => false;

        public SKImage Current => _image;

        public int Version => 1;

        public TimeSpan? UntilNextFrame => null;

        public bool Advance(TimeSpan elapsed) => false;

        public void Dispose() => _image.Dispose();
    }

    private readonly ModuleSandbox _folders = new();
    private readonly List<SupervisorRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        _folders.Dispose();
    }

    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Green = new(0, 255, 0);

    /// <summary>Le dessin des deux chemins : un trait avant l'appel, la pastille, du texte après. Tout est relatif à <c>ctx.Bounds</c>.</summary>
    private static void DrawBody(SKCanvas canvas, ModuleRenderContext ctx)
    {
        var b = ctx.Bounds;
        using (var paint = new SKPaint { Color = Red })
        {
            canvas.DrawRect(SKRect.Create(b.Left + 40, b.Top + 70, 30, 20), paint); // avant l'appel : la pastille passe quand même dessous
        }

        ModuleDrawing.DrawSelection(canvas, SKRect.Create(b.Left + 20, b.Top + 60, 220, 46), ctx);

        using var font = ModuleDrawing.Font(ctx.Theme, 22f * ctx.TextScale);
        using var text = new SKPaint { Color = Green, IsAntialias = true };
        canvas.DrawText("projet", b.Left + 90, b.Top + 92, SKTextAlign.Left, font, text);
    }

    [WindowsHostFact]
    public async Task NativeAndCommunity_SameDrawing_SamePixels()
    {
        var theme = Themes.LiquidGlassAdvanced;
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var placement = new ModulePlacement(
            Guid.NewGuid(), "fake", 0, 0, 4, 2, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());
        var bounds = ModulePlace.Rect(placement, theme);
        using var background = new FixedBackground();

        // Le module natif.
        using var nativeComposer = new FrameComposer();
        var native = new Dictionary<Guid, IModule> { [placement.InstanceId] = new FakeModule(DrawBody) };
        using var expected = nativeComposer.Compose([placement], native, theme, background, now);

        // Le module de la communauté : la case s'ouvre au premier dessin, l'hôte (joué à la main) répond avec l'image et les rectangles.
        var factory = new FakeHostFactory();
        var rig = new SupervisorRig(ModuleSandbox.Ready(_folders.SmallModule(), ModuleSandbox.Manifest(entry: "module.dll")), factory);
        _rigs.Add(rig);
        var remote = rig.Open(placement.InstanceId);
        using var communityComposer = new FrameComposer();
        var community = new Dictionary<Guid, IModule> { [placement.InstanceId] = remote };
        communityComposer.Compose([placement], community, theme, background, now).Dispose();

        var host = await factory.WaitForStartAsync();
        var open = await host.WaitAsync<Open>();
        var ctx = ModulePlace.Context(placement, theme, now);
        Assert.Equal((int)Math.Round(ctx.Bounds.Width), open.Width);
        Assert.Equal((int)Math.Round(ctx.Bounds.Height), open.Height);

        // Ce que fait l'hôte : le module dessine dans une image de la taille de la case, aux bornes (0, 0, largeur, hauteur) ;
        // DrawSelection y note ses rectangles (les mêmes que ceux que l'hôte met dans Frame).
        using var image = new SKBitmap(new SKImageInfo(open.Width, open.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var sink = new Sink();
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.Transparent);
            DrawBody(canvas, new ModuleRenderContext(
                new SKRect(0, 0, open.Width, open.Height), new GridSize(open.Columns, open.Rows), theme.Theme, open.TextScale, now) { Selections = sink });
        }

        Assert.Single(sink.Rects);
        await host.SendFrameAsync(open, 0, image, [.. sink.Rects.Select(r => new SelectionRect(r.Left, r.Top, r.Width, r.Height))]);
        await TestWait.UntilAsync(() => remote.FramesReceived == 1);
        using var actual = communityComposer.Compose([placement], community, theme, background, now);

        // La comparaison.
        var different = 0;
        var largest = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var a = expected.GetPixel(x, y);
                var b = actual.GetPixel(x, y);
                var delta = Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Max(Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha)));
                if (delta > 0)
                {
                    different++;
                    largest = Math.Max(largest, delta);
                }
            }
        }

        output.WriteLine($"case {bounds}, image {open.Width}x{open.Height} ; pixels différents : {different}, écart le plus grand : {largest}");

        // La pastille est bien là (sans elle, le test ne prouverait rien) : la même composition sans pastille en diffère.
        using var noPill = nativeComposer.Compose([placement],
            new Dictionary<Guid, IModule> { [placement.InstanceId] = new FakeModule((canvas, c) => { }) }, theme, background, now);
        var tab = SKRect.Create(bounds.Left + 20, bounds.Top + 60, 220, 46);
        var pillPixels = 0;
        for (var y = (int)tab.Top; y < tab.Bottom; y++)
        {
            for (var x = (int)tab.Left; x < tab.Right; x++)
            {
                if (expected.GetPixel(x, y) != noPill.GetPixel(x, y))
                {
                    pillPixels++;
                }
            }
        }

        Assert.True(pillPixels > 500, $"la pastille ne se voit pas : {pillPixels}");
        Assert.True(largest <= 1, $"{different} pixels diffèrent, de {largest} au plus");
        Assert.Equal(0, different);
    }
}
