using System.ComponentModel;

namespace Trofeo.Extensions.Protocol;

/// <summary>
/// Côté hôte : projette la section d'images partagée dans la mémoire du processus. Le tampon 0 commence au début de la
/// vue, le tampon 1 à <c>width × height × 4</c> octets (BGRA, alpha prémultiplié, pas = <c>width × 4</c>).
/// </summary>
public static class FrameView
{
    /// <summary>Plafond de sécurité d'une section (largeur × hauteur × 4 × 2), bien au-dessus de l'écran de 1920×462.</summary>
    internal const long MaxSectionBytes = 256L * 1024 * 1024;

    /// <summary>La taille d'un tampon en octets.</summary>
    public static int BufferBytes(int width, int height)
    {
        ValidateSize(width, height);
        return checked(width * height * 4);
    }

    /// <summary>
    /// Projette toute la section (deux tampons) et renvoie l'adresse du tampon 0.
    /// <paramref name="sectionHandle"/> est la valeur de la poignée dans <b>ce</b> processus.
    /// </summary>
    public static IntPtr Map(long sectionHandle, int width, int height)
    {
        var bytes = 2L * BufferBytes(width, height);
        var view = SectionNative.MapViewOfFile(new IntPtr(sectionHandle), SectionNative.FileMapAllAccess, 0, 0, (nuint)bytes);
        if (view == IntPtr.Zero)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Impossible de projeter les images partagées.");
        }

        return view;
    }

    /// <summary>Défait la projection faite par <see cref="Map"/>.</summary>
    public static void Unmap(IntPtr view)
    {
        if (view != IntPtr.Zero)
        {
            SectionNative.UnmapViewOfFile(view);
        }
    }

    internal static void ValidateSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (2L * width * height * 4 > MaxSectionBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image beaucoup trop grande.");
        }
    }
}
