using System.Runtime.InteropServices;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;
using Trofeo.Display.Video;
using Vortice.MediaFoundation;

namespace Trofeo.Display.Tests;

/// <summary>
/// Lit de vraies vidéos MP4 par Media Foundation. Chaque vidéo est fabriquée au début du test par l'encodeur H.264
/// de Windows (64 × 36, 10 images de 100 ms) : pas de fichier binaire dans le dépôt. Le codec est avec perte,
/// donc les couleurs se comparent avec une tolérance.
/// </summary>
public sealed class MediaFoundationBackgroundTests : IDisposable
{
    private const int FrameCount = 10, FrameMs = 100, VideoWidth = 64, VideoHeight = 36;

    // Une couleur pure par image, toutes bien séparées (B, V, R en 0 ou 255) pour résister à la compression.
    private static readonly SKColor[] Palette =
    [
        new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0), new(255, 0, 255),
        new(0, 255, 255), new(255, 255, 255), new(255, 128, 0), new(0, 128, 255), new(128, 0, 255),
    ];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "trofeo-video-tests-" + Guid.NewGuid().ToString("N"));

    public MediaFoundationBackgroundTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Video(
        Func<int, int, int, SKColor>? pixel = null, int frameMs = FrameMs, bool withAudio = false, int width = VideoWidth,
        int height = VideoHeight, int frameCount = FrameCount)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".mp4");
        TestVideoWriter.Write(path, pixel ?? ((frame, _, _) => Palette[frame]), frameMs, withAudio, width, height, frameCount);
        return path;
    }

    /// <summary>Couleur au centre de l'écran (la vidéo 16:9 est recadrée en 1920 × 462 : le centre reste dans l'image).</summary>
    private static SKColor Center(IBackground background) => Pixel(background, GridGeometry.Width / 2, GridGeometry.Height / 2);

    private static SKColor Pixel(IBackground background, int x, int y)
    {
        using var bitmap = SKBitmap.FromImage(background.Current);
        return bitmap.GetPixel(x, y);
    }

    private static void AssertColor(SKColor expected, SKColor actual, string because = "")
    {
        const int tolerance = 70; // H.264 + conversions YUV : quelques dizaines de niveaux d'écart
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance &&
            Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"attendu {expected}, obtenu {actual} {because}");
    }

    private static MediaFoundationBackground Open(string path)
    {
        var warnings = new List<string>();
        var background = MediaFoundationBackground.TryOpen(path, warnings.Add);
        Assert.Empty(warnings);
        return Assert.IsType<MediaFoundationBackground>(background);
    }

    [Fact]
    public void Video_FollowsElapsedTime()
    {
        using var video = Open(Video());

        Assert.True(video.IsAnimated);
        Assert.Equal(GridGeometry.Width, video.Current.Width);
        Assert.Equal(GridGeometry.Height, video.Current.Height);
        AssertColor(Palette[0], Center(video), "(image 0 dès l'ouverture)");
        int version = video.Version;

        Assert.False(video.Advance(TimeSpan.FromMilliseconds(30))); // toujours l'image 0
        Assert.Equal(version, video.Version);
        Assert.InRange(video.UntilNextFrame!.Value.TotalMilliseconds, 1, FrameMs);

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(250))); // image 2 : l'image 1 est lue mais jamais montrée
        AssertColor(Palette[2], Center(video), "(250 ms)");
        Assert.True(video.Version > version);

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(950)));
        AssertColor(Palette[9], Center(video), "(950 ms)");
    }

    [Fact]
    public void Video_LoopsWithoutBlackFrame()
    {
        using var video = Open(Video());

        // Trois tours : à chaque pas (au milieu d'une image) l'image attendue est là, et jamais un écran noir,
        // y compris pendant la dernière image et juste après le retour au début.
        for (int step = 0; step < 30; step++)
        {
            video.Advance(TimeSpan.FromMilliseconds(step * FrameMs + FrameMs / 2));
            // La tolérance de AssertColor exclut déjà le noir : aucune couleur de la palette n'en est à moins de 70 niveaux.
            AssertColor(Palette[step % FrameCount], Center(video), $"(pas {step})");
        }

        // Au début du tour suivant (après les 83 ms mini entre deux recadrages) : l'image 0 revient aussitôt.
        video.Advance(TimeSpan.FromMilliseconds(3 * FrameCount * FrameMs + 60));
        AssertColor(Palette[0], Center(video), "(retour à l'image 0)");
    }

    [Fact]
    public void Video_LongAbsence_ResumesWhereItLeftOff_WithoutCatchingUp()
    {
        using var video = Open(Video());
        Assert.True(video.Advance(TimeSpan.FromMilliseconds(250))); // image 2 à l'écran
        AssertColor(Palette[2], Center(video));
        int before = video.SamplesRead;

        // Une heure sans Advance (écran débranché, veille) : la lecture reprend là où elle en était, comme une pause. Elle ne
        // décode pas une heure de vidéo d'un coup (3,6 images/s pendant 25 s mesurés sur l'écran après 4 min 48 d'absence).
        Assert.True(video.Advance(TimeSpan.FromHours(1)));

        Assert.InRange(video.SamplesRead - before, 1, 3);
        AssertColor(Palette[3], Center(video), "(l'image qui suivait)");
        Assert.True(video.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(120)));
        AssertColor(Palette[4], Center(video), "(la lecture continue au rythme normal)");
    }

    [Fact]
    public void Video_LongAbsence_AfterOneTour_ReadsAtMostOneTour()
    {
        using var video = Open(Video());
        video.Advance(TimeSpan.FromMilliseconds(950));
        video.Advance(TimeSpan.FromMilliseconds(1050)); // un tour fini : la durée du tour est connue

        int before = video.SamplesRead;
        video.Advance(TimeSpan.FromHours(10));

        Assert.True(video.SamplesRead - before <= FrameCount + 1, $"{video.SamplesRead - before} échantillons lus");
        Assert.True(video.IsAnimated);
    }

    [Fact]
    public void Video_ShortLag_IsStillCaughtUp()
    {
        // Un retard d'une demi-seconde (moins que le seuil) se rattrape : la vidéo reste calée sur le temps écoulé.
        using var video = Open(Video());

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(750)));

        AssertColor(Palette[7], Center(video));
    }

    [Fact]
    public void Video_SingleFrame_StopsWakingTheLoop()
    {
        // Une vidéo d'une seule image est une image fixe : plus de réveil à 12 images/s pour rien, la version ne bouge plus.
        using var video = Open(Video(frameCount: 1));
        int version = video.Version;

        Assert.False(video.Advance(TimeSpan.FromMilliseconds(250))); // la fin du fichier est atteinte : un seul échantillon

        Assert.False(video.IsAnimated);
        Assert.Null(video.UntilNextFrame);
        Assert.Equal(1, video.SamplesRead);
        for (int ms = 300; ms < 5000; ms += 250)
        {
            Assert.False(video.Advance(TimeSpan.FromMilliseconds(ms)));
        }

        Assert.Equal(version, video.Version);
        Assert.Equal(1, video.SamplesRead);
        AssertColor(Palette[0], Center(video));
    }

    [Fact]
    public void Video_TwoFrames_KeepsLooping()
    {
        using var video = Open(Video(frameCount: 2));

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(120)));
        AssertColor(Palette[1], Center(video));
        Assert.True(video.Advance(TimeSpan.FromMilliseconds(220)));
        AssertColor(Palette[0], Center(video), "(retour au début)");
        Assert.True(video.IsAnimated);
    }

    [Fact]
    public void Video_DecoderFailsWhilePlaying_WarnsOnceWithTheCause_AndKeepsTheLastImage()
    {
        var path = Video();
        var warnings = new List<string>();
        using var video = Assert.IsType<MediaFoundationBackground>(MediaFoundationBackground.TryOpen(path, warnings.Add));
        AssertColor(Palette[0], Center(video));
        int version = video.Version;
        video.ReadFault = () => throw new InvalidOperationException("décodeur en panne");

        Assert.False(video.Advance(TimeSpan.FromMilliseconds(450)));
        Assert.False(video.Advance(TimeSpan.FromMilliseconds(900)));

        var warning = Assert.Single(warnings); // un seul avertissement, malgré deux Advance
        Assert.Contains("décodeur en panne", warning);
        Assert.Contains(path, warning);
        Assert.Contains("figée", warning); // ce qui se passe vraiment : l'image reste, le fond du thème n'est pas repris
        Assert.DoesNotContain("Fond du thème utilisé", warning);
        Assert.False(video.IsAnimated);
        Assert.Null(video.UntilNextFrame);
        Assert.Equal(version, video.Version);
        AssertColor(Palette[0], Center(video), "(la dernière image décodée reste)");
    }

    [Fact]
    public void Video_DecoderFailsWithAnHResult_WarnsWithTheHResult()
    {
        var path = Video();
        var warnings = new List<string>();
        using var video = Assert.IsType<MediaFoundationBackground>(MediaFoundationBackground.TryOpen(path, warnings.Add));
        video.ReadFault = () => throw new COMException("accès refusé", unchecked((int)0x80070005));

        video.Advance(TimeSpan.FromMilliseconds(450));

        var warning = Assert.Single(warnings);
        Assert.Contains("accès refusé", warning);
        Assert.Contains("0x80070005", warning); // le HRESULT, utile quand le message est vide ou obscur
    }

    [Fact]
    public void Video_DefaultTuning_IsAcceptedByTheDecoder()
    {
        // Open() exige zéro avertissement : le décodeur de cette machine accepte bien le réglage par défaut (un fil de travail).
        Assert.Equal(1, DecoderTuning.Default.WorkerThreads);
        using var video = Open(Video());

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(250)));
        AssertColor(Palette[2], Center(video));
    }

    [Fact]
    public void Video_TuningRefused_StillPlays_AndWarnsWithTheCause()
    {
        var path = Video();
        var warnings = new List<string>();
        var refused = new DecoderTuning(1) { Fault = () => throw new COMException("réglage refusé", unchecked((int)0x80004001)) };

        using var video = Assert.IsType<MediaFoundationBackground>(MediaFoundationBackground.TryOpen(path, warnings.Add, refused));

        var warning = Assert.Single(warnings);
        Assert.Contains("réglage refusé", warning);
        Assert.Contains("0x80004001", warning);
        Assert.Contains(path, warning);
        Assert.Contains("par défaut", warning); // le décodeur garde ses réglages : la lecture marche quand même
        Assert.True(video.IsAnimated);
        AssertColor(Palette[0], Center(video));
        Assert.True(video.Advance(TimeSpan.FromMilliseconds(250)));
        AssertColor(Palette[2], Center(video));
    }

    [Fact]
    public void Video_DecoderKeepsItsDefault_WhenNoTuningIsAsked()
    {
        var warnings = new List<string>();

        using var video = Assert.IsType<MediaFoundationBackground>(MediaFoundationBackground.TryOpen(Video(), warnings.Add, new DecoderTuning(null)));

        Assert.Empty(warnings);
        Assert.True(video.Advance(TimeSpan.FromMilliseconds(250)));
        AssertColor(Palette[2], Center(video));
    }

    [Fact]
    public void Video_IsNotFlipped()
    {
        // Haut rouge, bas bleu : une vidéo lue à l'envers (images « bottom-up » de Media Foundation) se verrait ici.
        using var video = Open(Video((_, _, y) => y < VideoHeight / 2 ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255)));

        AssertColor(new SKColor(255, 0, 0), Pixel(video, 960, 40));
        AssertColor(new SKColor(0, 0, 255), Pixel(video, 960, 420));
    }

    [Fact]
    public void Video_PaddingRowsOfTheDecoder_AreNotShown()
    {
        // Le décodeur arrondit la hauteur à un multiple de 16 (116 → 128) : les lignes en trop ne doivent pas se voir.
        // Cette vidéo a presque le format de l'écran, donc son bord bas est visible à l'écran.
        using var video = Open(Video((_, _, _) => new SKColor(255, 0, 0), width: 480, height: 116));

        AssertColor(new SKColor(255, 0, 0), Pixel(video, 960, GridGeometry.Height - 3), "(bord bas)");
        AssertColor(new SKColor(255, 0, 0), Pixel(video, 960, 2), "(bord haut)");
    }

    [Fact]
    public void Video_RecropsAtMostTwelveTimesPerSecond()
    {
        // Vidéo à 50 images/s (20 ms par image), lue toutes les 10 ms pendant une seconde : l'image change, mais au plus 12 fois.
        using var video = Open(Video(frameMs: 20));

        int changes = 0;
        for (int ms = 10; ms <= 1000; ms += 10)
        {
            if (video.Advance(TimeSpan.FromMilliseconds(ms))) changes++;
            Assert.InRange(video.UntilNextFrame!.Value.TotalMilliseconds, 1, 1000);
        }

        Assert.InRange(changes, 8, 12);
    }

    [Fact]
    public void Video_WithAudioTrack_ReadsOnlyTheVideoStream()
    {
        // Pas de son, jamais : même si le fichier a une piste son, seul le flux vidéo est sélectionné.
        using var video = Open(Video(withAudio: true));

        Assert.Equal(2, video.StreamCount); // la piste son est bien dans le fichier…
        Assert.Equal(1, video.SelectedStreamCount); // …et n'est pas lue
        Assert.True(video.Advance(TimeSpan.FromMilliseconds(350)));
        AssertColor(Palette[3], Center(video));
    }

    [Fact]
    public void Video_BadFile_IsNull()
    {
        var path = Path.Combine(_folder, "abime.mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray());
        var warnings = new List<string>();

        var video = MediaFoundationBackground.TryOpen(path, warnings.Add);

        Assert.Null(video);
        var warning = Assert.Single(warnings);
        Assert.StartsWith($"Fond d'écran illisible : {path} (", warning);
        Assert.EndsWith("). Fond du thème utilisé.", warning);
        Assert.True(warning.Length > $"Fond d'écran illisible : {path} (). Fond du thème utilisé.".Length, "la cause doit être écrite");
    }

    [Fact]
    public void Video_MissingFile_IsNull()
    {
        var path = Path.Combine(_folder, "absent.mp4");
        var warnings = new List<string>();

        Assert.Null(MediaFoundationBackground.TryOpen(path, warnings.Add));

        Assert.Equal([$"Fond d'écran introuvable : {path}. Fond du thème utilisé."], warnings);
    }

    [Fact]
    public void Video_AudioOnlyFile_IsNull()
    {
        // Un fichier sans flux vidéo n'est pas un fond : illisible, et aucun son.
        var path = Path.Combine(_folder, "son.wav");
        File.WriteAllBytes(path, WavSilence());
        var warnings = new List<string>();

        Assert.Null(MediaFoundationBackground.TryOpen(path, warnings.Add));

        var warning = Assert.Single(warnings);
        Assert.StartsWith($"Fond d'écran illisible : {path} (", warning);
        Assert.EndsWith("). Fond du thème utilisé.", warning);
    }

    [Fact]
    public void Video_Dispose_IsIdempotent()
    {
        var video = Open(Video());
        video.Dispose();
        video.Dispose();
        Assert.False(video.Advance(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Video_StaysOpen_WhileFileIsReplacedOrDeleted()
    {
        // Comme les GIF : le fichier peut être remplacé ou supprimé pendant la lecture (sous Windows).
        var path = Video();
        using var video = Open(path);

        File.Delete(path);

        Assert.True(video.Advance(TimeSpan.FromMilliseconds(450)));
        AssertColor(Palette[4], Center(video));
    }

    private static byte[] WavSilence()
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        const int dataLength = 8000;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(8000);
        writer.Write(8000);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(Enumerable.Repeat((byte)128, dataLength).ToArray());
        writer.Flush();
        return memory.ToArray();
    }
}
