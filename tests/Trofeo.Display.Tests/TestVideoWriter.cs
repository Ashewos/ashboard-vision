using System.Runtime.InteropServices;
using SkiaSharp;
using Trofeo.Display.Video;
using Vortice.MediaFoundation;

namespace Trofeo.Display.Tests;

/// <summary>Fabrique de vraies vidéos MP4 pour les tests : l'encodeur H.264 de Windows écrit le fichier (pas de binaire dans le dépôt).</summary>
internal static class TestVideoWriter
{
    /// <summary>
    /// Encode un MP4 H.264 de <paramref name="frameCount"/> images de <paramref name="frameMs"/> ms ; avec <paramref name="withAudio"/>,
    /// une piste son AAC (du silence) accompagne les images.
    /// </summary>
    public static void Write(
        string path, Func<int, int, int, SKColor> pixel, int frameMs, bool withAudio, int width, int height, int frameCount)
    {
        Assert.True(MediaFoundationBackground.EnsureStarted());

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

        long frameDuration = frameMs * 10_000L; // unités de 100 ns
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, 400_000u);
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(1000 / frameMs, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            writer.AddStream(output);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(1000 / frameMs, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)); // positif : de haut en bas
            writer.SetInputMediaType(0, input, null);
        }

        const int SampleRate = 44100, AudioBytesPerSecond = SampleRate * 4; // PCM 16 bits stéréo
        if (withAudio)
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 16000u);
                output.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                writer.AddStream(output);
            }

            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AudioBytesPerSecond);
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
            writer.SetInputMediaType(1, input, null);
        }

        writer.BeginWriting();
        var bytes = new byte[width * height * 4];
        var silence = new byte[AudioBytesPerSecond * frameMs / 1000];
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var color = pixel(frame, x, y);
                    int i = (y * width + x) * 4;
                    bytes[i] = color.Blue;
                    bytes[i + 1] = color.Green;
                    bytes[i + 2] = color.Red;
                    bytes[i + 3] = 255;
                }
            }

            WriteSample(writer, 0, bytes, frame * frameDuration, frameDuration);
            if (withAudio) WriteSample(writer, 1, silence, frame * frameDuration, frameDuration);
        }

        writer.Finalize();
    }

    private static void WriteSample(IMFSinkWriter writer, int stream, byte[] bytes, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out var data, out _, out _);
        try { Marshal.Copy(bytes, 0, data, bytes.Length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = bytes.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
