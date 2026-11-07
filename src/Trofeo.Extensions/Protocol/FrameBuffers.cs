using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Trofeo.Extensions.Protocol;

/// <summary>
/// Côté service : la section de mémoire où l'hôte dessine, une seule, anonyme, de <c>2 × width × height × 4</c> octets. Le
/// tampon 0 est au décalage 0, le tampon 1 à <c>width × height × 4</c> (BGRA, alpha prémultiplié, pas = <c>width × 4</c>).
/// L'hôte reçoit la section par <see cref="Sandbox.SandboxedProcess.DuplicateInto"/> et la projette avec <see cref="FrameView"/>.
/// </summary>
public sealed class FrameBuffers : IDisposable
{
    private readonly object _gate = new();
    private readonly SafeSectionHandle _section;
    private readonly int _bufferBytes;
    private IntPtr _view;
    private bool _disposed;

    /// <summary>Crée la section et la projette dans ce processus.</summary>
    public FrameBuffers(int width, int height)
    {
        _bufferBytes = FrameView.BufferBytes(width, height);
        Width = width;
        Height = height;

        var bytes = 2L * _bufferBytes;
        _section = SectionNative.CreateFileMapping(SectionNative.InvalidHandleValue, IntPtr.Zero, SectionNative.PageReadWrite,
            (uint)(bytes >> 32), (uint)(bytes & 0xFFFFFFFF), IntPtr.Zero);
        if (_section.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _section.Dispose();
            throw new Win32Exception(error, "Impossible de créer les images partagées.");
        }

        try
        {
            _view = FrameView.Map(_section.DangerousGetHandle(), width, height);
        }
        catch
        {
            _section.Dispose();
            throw;
        }
    }

    /// <summary>Largeur d'une image, en pixels.</summary>
    public int Width { get; }

    /// <summary>Hauteur d'une image, en pixels.</summary>
    public int Height { get; }

    /// <summary>La section, à dupliquer dans l'hôte (<c>Open.Buffer0</c>).</summary>
    public SafeHandle Section => _section;

    /// <summary>
    /// Copie le tampon <paramref name="buffer"/> (0 ou 1) dans <paramref name="destination"/>, ligne par ligne selon le pas de
    /// la destination, qui doit faire exactement la taille de l'image, en BGRA 8 bits à alpha prémultiplié.
    /// </summary>
    public void CopyTo(int buffer, SKBitmap destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(buffer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(buffer, 1);
        if (destination.Width != Width || destination.Height != Height)
        {
            throw new ArgumentException($"La destination doit faire {Width}×{Height} (elle fait {destination.Width}×{destination.Height}).", nameof(destination));
        }

        if (destination.ColorType != SKColorType.Bgra8888 || destination.AlphaType != SKAlphaType.Premul)
        {
            throw new ArgumentException("La destination doit être en BGRA 8 bits, alpha prémultiplié.", nameof(destination));
        }

        var target = destination.GetPixels();
        if (target == IntPtr.Zero)
        {
            throw new ArgumentException("La destination n'a pas de pixels.", nameof(destination));
        }

        var rowBytes = Width * 4;
        var targetStride = destination.RowBytes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            unsafe
            {
                var source = (byte*)_view + (long)buffer * _bufferBytes;
                var dest = (byte*)target;
                for (var y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(source + (long)y * rowBytes, dest + (long)y * targetStride, targetStride, rowBytes);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FrameView.Unmap(_view);
            _view = IntPtr.Zero;
        }

        _section.Dispose();
    }
}
