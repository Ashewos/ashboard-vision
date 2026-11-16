using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Display.Gpu;
using Trofeo.Display.Video;
using Trofeo.ModuleKit;
using Xunit.Abstractions;

namespace Trofeo.Display.Tests;

/// <summary>Un test qui a besoin d'une vraie carte graphique (décodage vidéo matériel) : sauté sans carte matérielle.</summary>
internal sealed class HardwareGpuTheoryAttribute : TheoryAttribute
{
    public HardwareGpuTheoryAttribute()
    {
        if (!GpuVideoTests.HasHardwareCard) Skip = "Aucune carte graphique matérielle";
    }
}

/// <summary>
/// Le décodage des vidéos par la carte graphique, essayé sur chaque carte matérielle de ce PC (les tests sont sautés sans carte).
/// La vidéo est fabriquée une fois : 1920 × 462, 3 s, un dégradé coloré qui bouge, par l'encodeur H.264 de Windows.
/// </summary>
[Collection(GpuTestSupport.CollectionName)]
public sealed class GpuVideoTests(GpuVideoTests.VideoFixture fixture, ITestOutputHelper output) : IClassFixture<GpuVideoTests.VideoFixture>
{
    private const int FrameMs = 100, FrameCount = 30;

    /// <summary>La vidéo des essais, écrite au premier test et supprimée à la fin.</summary>
    public sealed class VideoFixture : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trofeo-gpu-video-" + Guid.NewGuid().ToString("N"));
        private string? _path;

        public string VideoPath => _path ??= Write();

        private string Write()
        {
            Directory.CreateDirectory(_folder);
            var path = System.IO.Path.Combine(_folder, "degrade.mp4");
            TestVideoWriter.Write(path, Gradient, FrameMs, withAudio: false, GridGeometry.Width, GridGeometry.Height, FrameCount);
            return path;
        }

        /// <summary>Un dégradé de gauche à droite dont les couleurs tournent d'une image à l'autre : chaque image est bien distincte.</summary>
        private static SKColor Gradient(int frame, int x, int y)
        {
            double phase = frame * 0.35;
            double t = x / (double)GridGeometry.Width;
            byte r = (byte)(128 + 110 * Math.Sin(phase + t * 3.0));
            byte g = (byte)(128 + 110 * Math.Sin(phase * 0.7 + t * 2.0 + 2.0));
            byte b = (byte)(128 + 110 * Math.Sin(phase * 1.3 + (1 - t) * 3.0 + 4.0));
            return new SKColor(r, g, b);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly Lazy<IReadOnlyList<GpuAdapterInfo>> Cards = new(() => OperatingSystem.IsWindows() ? GpuAdapters.List() : []);

    internal static bool HasHardwareCard => Cards.Value.Count > 0;

    /// <summary>Un cas par carte matérielle (au moins un, pour que la théorie existe même sans carte : elle est alors sautée).</summary>
    public static IEnumerable<object[]> CardIndexes() =>
        Enumerable.Range(0, Math.Max(1, Cards.Value.Count)).Select(i => new object[] { i });

    private static GpuAdapterInfo Card(int index) => Cards.Value[index];

    private static MediaFoundationBackground OpenOn(string path, GpuAdapterInfo? card, TimeSpan startAt, List<string>? warnings = null)
    {
        warnings ??= [];
        var background = MediaFoundationBackground.TryOpen(path, warnings.Add, new VideoDecodeOptions(card, startAt));
        Assert.True(background is not null, string.Join(" | ", warnings));
        return background!;
    }

    /// <summary>Les pixels de l'image, au format de l'écran (le rapatriement d'une image de la carte passe par ReadPixels).</summary>
    private static SKBitmap Pixels(SKImage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.True(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0), "ReadPixels a échoué");
        return bitmap;
    }

    /// <summary>L'écart moyen par canal (sur 255) et la part des pixels dont aucun canal ne s'écarte de plus de <paramref name="limit"/>.</summary>
    private static (double Mean, double ShareWithin, int Max) Compare(SKBitmap expected, SKBitmap actual, int limit)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        var a = expected.GetPixelSpan();
        var b = actual.GetPixelSpan();
        long sum = 0;
        int within = 0, max = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                int i = y * expected.RowBytes + x * 4, j = y * actual.RowBytes + x * 4;
                int pixelMax = 0;
                for (int c = 0; c < 3; c++) // l'alpha est de 255 des deux côtés
                {
                    int d = Math.Abs(a[i + c] - b[j + c]);
                    sum += d;
                    if (d > pixelMax) pixelMax = d;
                }
                if (pixelMax <= limit) within++;
                if (pixelMax > max) max = pixelMax;
            }
        }
        double pixels = (double)expected.Width * expected.Height;
        return (sum / (pixels * 3), within / pixels, max);
    }

    private (double Mean, double ShareWithin, int Max) Report(string label, SKBitmap expected, SKBitmap actual, int limit = 6)
    {
        var result = Compare(expected, actual, limit);
        output.WriteLine($"{label} : écart moyen {result.Mean:0.000}/255, {result.ShareWithin:P3} des pixels à {limit}/255 au plus, écart maximal {result.Max}/255");
        return result;
    }

    private static SKBitmap CpuFrameAt(string path, TimeSpan time)
    {
        // Par pas d'une demi-seconde : un bond de plus d'une seconde n'est pas rattrapé (voir MaxLag), il ressemble à une pause.
        using var cpu = OpenOn(path, null, TimeSpan.Zero);
        for (var t = TimeSpan.FromSeconds(0.5); t < time; t += TimeSpan.FromSeconds(0.5)) cpu.Advance(t);
        cpu.Advance(time);
        return SKBitmap.FromImage(cpu.Current);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_SameColoursAsCpu(int index)
    {
        var card = Card(index);
        output.WriteLine($"Carte : {card.Id.Name}");
        using var cpu = OpenOn(fixture.VideoPath, null, TimeSpan.Zero);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);
        Assert.Null(cpu.DecoderLuid);
        Assert.Equal(card.Luid, gpu.DecoderLuid);

        foreach (var second in new[] { 0.0, 1.0, 2.0 })
        {
            var at = TimeSpan.FromSeconds(second);
            cpu.Advance(at);
            gpu.Advance(at);

            using var expected = SKBitmap.FromImage(cpu.Current);
            using var actual = SKBitmap.FromImage(gpu.Current); // l'image décodée par la carte, rapatriée
            var result = Report($"{card.Id.Name}, image à {second} s", expected, actual);
            Assert.True(result.Mean < 1.0, $"écart moyen {result.Mean:0.000}");
            Assert.True(result.ShareWithin >= 0.99, $"seulement {result.ShareWithin:P3} des pixels à 6/255 au plus");
        }
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_GpuDrawing_NoCpuCopy(int index)
    {
        var card = Card(index);
        using var device = D3D12GpuDevice.Create(card);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);
        output.WriteLine($"Carte : {card.Id.Name}");

        int? previousVersion = null;
        foreach (var second in new[] { 0.0, 0.5, 1.0, 1.7, 2.4 })
        {
            var at = TimeSpan.FromSeconds(second);
            if (second > 0) gpu.Advance(at);
            if (previousVersion is { } v) Assert.NotEqual(v, gpu.Version);
            previousVersion = gpu.Version;

            using var image = gpu.TryGetGpuFrame(device);
            Assert.NotNull(image);
            Assert.True(image.IsTextureBacked, "l'image doit être une texture de la carte");
            Assert.Equal(GridGeometry.Width, image.Width);
            Assert.Equal(GridGeometry.Height, image.Height);
            Assert.Equal(0, gpu.CpuReadbacks); // Current n'a pas été calculé : aucune copie par le processeur

            using var onGpu = Pixels(image);
            using var reference = CpuFrameAt(fixture.VideoPath, at);
            var result = Report($"{card.Id.Name}, image de la carte à {second} s", reference, onGpu);
            Assert.True(result.Mean < 1.0, $"écart moyen {result.Mean:0.000}");
            Assert.True(result.ShareWithin >= 0.99, $"seulement {result.ShareWithin:P3} des pixels à 6/255 au plus");
        }

        Assert.False(device.IsLost);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_StaysInStepWithTheCpuOverTwoLoops(int index)
    {
        // 70 images de suite, à 100 ms, sur plus de deux tours de la vidéo (3 s) : la barrière de copie et celle de lecture tiennent,
        // aucune image n'est ni en retard, ni écrasée, et le retour au début se fait sans image noire.
        var card = Card(index);
        using var device = D3D12GpuDevice.Create(card);
        using var cpu = OpenOn(fixture.VideoPath, null, TimeSpan.Zero);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);
        double worstMean = 0;
        int compared = 0;

        for (int step = 1; step <= 70; step++)
        {
            var at = TimeSpan.FromMilliseconds(step * 100);
            cpu.Advance(at);
            gpu.Advance(at);
            using var image = gpu.TryGetGpuFrame(device);
            Assert.NotNull(image);
            if (step % 3 != 0) continue; // une image sur trois est comparée, toutes sont envoyées

            using var onGpu = Pixels(image);
            using var expected = SKBitmap.FromImage(cpu.Current);
            var result = Compare(expected, onGpu, 6);
            worstMean = Math.Max(worstMean, result.Mean);
            compared++;
            Assert.True(result.Mean < 1.0, $"à {at.TotalSeconds:0.0} s : écart moyen {result.Mean:0.000}");
        }

        output.WriteLine($"{card.Id.Name} : {compared} images comparées sur 7 s, pire écart moyen {worstMean:0.000}/255, rapatriements {gpu.CpuReadbacks}");
        Assert.Equal(0, gpu.CpuReadbacks);
        Assert.False(device.IsLost);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_ComposerTakesTheGpuFrame_WithoutReadback(int index)
    {
        var card = Card(index);
        using var device = D3D12GpuDevice.Create(card);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);
        using var composer = new FrameComposer(new RateLimitedErrorLog(TimeProvider.System, _ => { }), gpu: device);
        var theme = Themes.ClassiqueDefinition;

        foreach (var second in new[] { 0.0, 1.0 })
        {
            gpu.Advance(TimeSpan.FromSeconds(second));
            using var frame = composer.Compose([], new Dictionary<Guid, IModule>(), theme, gpu, DateTimeOffset.UnixEpoch);
            Assert.True(composer.UsesGpu);
            using var reference = CpuFrameAt(fixture.VideoPath, TimeSpan.FromSeconds(second));
            var result = Report($"{card.Id.Name}, composition sur la carte à {second} s", reference, frame);
            Assert.True(result.Mean < 1.0, $"écart moyen {result.Mean:0.000}");
            Assert.True(result.ShareWithin >= 0.99, $"seulement {result.ShareWithin:P3} des pixels à 6/255 au plus");
        }

        Assert.Equal(0, gpu.CpuReadbacks);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_CpuDrawing_CurrentWorks(int index)
    {
        var card = Card(index);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);

        var first = gpu.Current;
        Assert.Equal(GridGeometry.Width, first.Width);
        Assert.Equal(GridGeometry.Height, first.Height);
        Assert.Same(first, gpu.Current); // la même instance tant que la version ne change pas
        Assert.Equal(1, gpu.CpuReadbacks);

        Assert.True(gpu.Advance(TimeSpan.FromSeconds(1)));
        var second = gpu.Current;
        Assert.Equal(GridGeometry.Width, second.Width);
        Assert.Equal(GridGeometry.Height, second.Height);
        Assert.Equal(2, gpu.CpuReadbacks); // une seule copie par image, pas une par lecture de Current
        _ = gpu.Current;
        Assert.Equal(2, gpu.CpuReadbacks);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuDecode_OtherCard_GivesNoGpuFrame(int index)
    {
        var card = Card(index);
        using var gpu = OpenOn(fixture.VideoPath, card, TimeSpan.Zero);
        using var warp = D3D12GpuDevice.CreateWarp();
        if (warp is null) return; // Direct3D 12 logiciel absent : rien à comparer

        Assert.Null(gpu.TryGetGpuFrame(warp)); // WARP n'est pas la carte qui décode : le compositeur prend Current
        Assert.Equal(0, gpu.CpuReadbacks);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void Reopen_ResumesAtPosition(int index)
    {
        var card = Card(index);
        var resume = TimeSpan.FromSeconds(1.5);
        using var start = CpuFrameAt(fixture.VideoPath, TimeSpan.Zero);
        using var before = CpuFrameAt(fixture.VideoPath, resume - TimeSpan.FromMilliseconds(FrameMs));
        using var at = CpuFrameAt(fixture.VideoPath, resume);
        using var after = CpuFrameAt(fixture.VideoPath, resume + TimeSpan.FromMilliseconds(FrameMs));

        foreach (var decodeOn in new GpuAdapterInfo?[] { null, card })
        {
            using var reopened = OpenOn(fixture.VideoPath, decodeOn, resume);
            using var first = SKBitmap.FromImage(reopened.Current);
            var label = decodeOn is null ? "processeur" : card.Id.Name;
            var best = new[] { before, at, after }.Select(b => Compare(b, first, 6).Mean).Min();
            var fromStart = Compare(start, first, 6).Mean;
            output.WriteLine($"Reprise à 1,5 s ({label}) : écart à l'image la plus proche {best:0.000}/255, écart à l'image 0 : {fromStart:0.000}/255, position {reopened.Position.TotalSeconds:0.000} s");
            Assert.True(best < 1.0, $"la première image n'est pas celle de 1,5 s (à une image près) : écart {best:0.000}");
            Assert.True(fromStart > 5.0, "la première image est celle du début : la reprise n'a pas eu lieu");
            Assert.InRange(reopened.Position.TotalSeconds, 1.4 - 0.001, 1.6 + 0.001);

            // La lecture continue de là : une seconde plus tard, c'est l'image de 2,5 s.
            Assert.True(reopened.Advance(TimeSpan.FromSeconds(1)));
            using var later = SKBitmap.FromImage(reopened.Current);
            using var expectedLater = CpuFrameAt(fixture.VideoPath, TimeSpan.FromSeconds(2.5));
            var shift = new[] { -1, 0, 1 }
                .Select(d => Compare(CpuFrameAt(fixture.VideoPath, TimeSpan.FromSeconds(2.5 + d * FrameMs / 1000.0)), later, 6).Mean).Min();
            Assert.True(shift < 1.0, $"une seconde après la reprise, l'image n'est pas celle de 2,5 s : écart {shift:0.000}");
        }
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void UnsupportedOnGpu_DecodesOnCpu(int index)
    {
        var card = Card(index);
        var warnings = new List<string>();
        var options = new VideoDecodeOptions(card, TimeSpan.Zero) { GpuFault = () => throw new InvalidOperationException("décodeur matériel refusé") };

        using var video = Assert.IsType<MediaFoundationBackground>(MediaFoundationBackground.TryOpen(fixture.VideoPath, warnings.Add, options));

        Assert.Null(video.DecoderLuid); // elle est décodée par le processeur
        Assert.True(video.IsAnimated);
        Assert.Equal(GridGeometry.Width, video.Current.Width);
        var warning = Assert.Single(warnings);
        Assert.Contains("carte graphique", warning);
        Assert.Contains("décodeur matériel refusé", warning);
        Assert.Contains("processeur", warning);
    }

    [Fact]
    public void Reopen_BeyondTheEnd_RestartsFromTheBeginning()
    {
        // Une reprise tout au bout du fichier (la vidéo allait boucler) : on repart de la première image, sans échec ni image noire.
        var warnings = new List<string>();
        var opened = MediaFoundationBackground.TryOpen(fixture.VideoPath, warnings.Add, new VideoDecodeOptions(null, TimeSpan.FromSeconds(30)));
        Assert.True(opened is not null, string.Join(" | ", warnings));
        using var video = opened;

        Assert.Empty(warnings);
        using var first = SKBitmap.FromImage(video.Current);
        using var start = CpuFrameAt(fixture.VideoPath, TimeSpan.Zero);
        Assert.True(Compare(start, first, 6).Mean < 1.0, "la première image n'est pas celle du début");
        Assert.True(video.Advance(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void UnknownCard_DecodesOnCpu()
    {
        // Une carte qui n'existe pas (retirée, LUID d'une autre séance) : l'appareil Direct3D 11 ne s'ouvre pas, la vidéo joue sur le processeur.
        var missing = new GpuAdapterInfo(new GpuAdapterId(0x10DE, 1, 1, "Carte absente"), 0x7FFF1234, 0);
        var warnings = new List<string>();

        using var video = Assert.IsType<MediaFoundationBackground>(
            MediaFoundationBackground.TryOpen(fixture.VideoPath, warnings.Add, new VideoDecodeOptions(missing, TimeSpan.Zero)));

        Assert.Null(video.DecoderLuid);
        Assert.Equal(GridGeometry.Width, video.Current.Width);
        Assert.Single(warnings);
        Assert.Contains("carte graphique", warnings[0]);
    }

    [HardwareGpuTheory, MemberData(nameof(CardIndexes))]
    public void GpuFailureWhilePlaying_ResumesOnCpuAtPosition(int index)
    {
        var card = Card(index);
        var warnings = new List<string>();
        using var video = OpenOn(fixture.VideoPath, card, TimeSpan.Zero, warnings);
        video.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(card.Luid, video.DecoderLuid);

        var fired = false;
        video.ReadFault = () =>
        {
            if (fired) return;
            fired = true;
            throw new InvalidOperationException("carte retirée"); // le décodeur lâche à la prochaine lecture, une fois
        };
        Assert.True(video.Advance(TimeSpan.FromSeconds(1.5)));
        video.ReadFault = null;

        Assert.Null(video.DecoderLuid);
        Assert.True(video.IsAnimated);
        var warning = Assert.Single(warnings);
        Assert.Contains("carte retirée", warning);
        Assert.Contains("processeur", warning);

        // Reprise à la position où elle en était : l'image de 1,5 s, puis la lecture continue.
        using var resumed = SKBitmap.FromImage(video.Current);
        var best = new[] { 1.4, 1.5, 1.6 }
            .Select(s => { using var reference = CpuFrameAt(fixture.VideoPath, TimeSpan.FromSeconds(s)); return Compare(reference, resumed, 6).Mean; })
            .Min();
        output.WriteLine($"Reprise sur le processeur après une panne de {card.Id.Name} : écart à l'image de 1,5 s {best:0.000}/255");
        Assert.True(best < 1.0, $"écart {best:0.000}");
        Assert.True(video.Advance(TimeSpan.FromSeconds(2.5)));
    }
}
