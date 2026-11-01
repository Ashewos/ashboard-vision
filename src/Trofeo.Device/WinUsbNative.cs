using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.Device;

/// <summary>Appels natifs setupapi, kernel32 et winusb utilisés par <see cref="WinUsbTransportFactory"/> et <see cref="WinUsbTransport"/>.</summary>
internal static partial class WinUsbNative
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorSemTimeout = 121;
    public const int ErrorInsufficientBuffer = 122;

    public const uint DigcfPresent = 0x02;
    public const uint DigcfAllClasses = 0x04;
    public const uint DigcfDeviceInterface = 0x10;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    public const uint PipeTransferTimeout = 0x03;

    public const uint SpdrpHardwareId = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    public static partial SafeDeviceInfoSetHandle SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    /// <summary>Variante sans classe (<c>DIGCF_ALLCLASSES</c>) qui limite la liste à un énumérateur, par exemple « USB ».</summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeDeviceInfoSetHandle SetupDiGetClassDevs(nint classGuid, string enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(SafeDeviceInfoSetHandle deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceRegistryProperty(
        SafeDeviceInfoSetHandle deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property, out uint propertyRegDataType, ref byte propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(
        SafeDeviceInfoSetHandle deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(
        SafeDeviceInfoSetHandle deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, ref byte detailData, uint detailDataSize, out uint requiredSize, nint deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out SafeWinUsbHandle interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(nint interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_WritePipe(SafeWinUsbHandle interfaceHandle, byte pipeId, ref byte buffer, uint bufferLength, out uint lengthTransferred, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ReadPipe(SafeWinUsbHandle interfaceHandle, byte pipeId, ref byte buffer, uint bufferLength, out uint lengthTransferred, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_AbortPipe(SafeWinUsbHandle interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ResetPipe(SafeWinUsbHandle interfaceHandle, byte pipeId);
}

/// <summary>Liste d'informations de périphériques (SetupDi), détruite à la libération.</summary>
internal sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDeviceInfoSetHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => WinUsbNative.SetupDiDestroyDeviceInfoList(handle);
}

/// <summary>Handle d'interface WinUSB, libéré par <c>WinUsb_Free</c> (avant la fermeture du fichier).</summary>
internal sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeWinUsbHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => WinUsbNative.WinUsb_Free(handle);
}
