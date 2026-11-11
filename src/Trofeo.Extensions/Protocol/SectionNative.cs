using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.Extensions.Protocol;

/// <summary>Une section de mémoire (objet noyau « file mapping ») : se ferme avec <c>CloseHandle</c>.</summary>
internal sealed class SafeSectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSectionHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => SectionNative.CloseHandle(handle);
}

/// <summary>Les appels système des images partagées. Que des déclarations.</summary>
internal static partial class SectionNative
{
    public const uint PageReadWrite = 0x04;
    public const uint FileMapAllAccess = 0x000F001F;

    /// <summary>INVALID_HANDLE_VALUE : une section adossée au fichier d'échange, sans nom ni fichier.</summary>
    public static readonly nint InvalidHandleValue = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true)]
    public static partial SafeSectionHandle CreateFileMapping(nint file, nint attributes, uint protect, uint maximumSizeHigh,
        uint maximumSizeLow, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
