using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.Extensions.Sandbox;

/// <summary>Les appels système de l'enfermement (AppContainer, processus, job object). Que des déclarations.</summary>
internal static partial class Native
{
    // --- Constantes -------------------------------------------------------------------------------------------------

    public const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7); // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
    public const int ErrorNotFoundHResult = unchecked((int)0x80070490);      // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)

    public const uint SeGroupEnabled = 0x00000004;

    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNoWindow = 0x08000000;
    public const uint CreateUnicodeEnvironment = 0x00000400;

    public const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    public const nuint ProcThreadAttributeHandleList = 0x00020002;

    public const uint HandleFlagInherit = 0x00000001;
    public const uint DuplicateSameAccess = 0x00000002;

    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectAssociateCompletionPortInformation = 7;
    public const int JobObjectExtendedLimitInformation = 9;
    public const int JobObjectCpuRateControlInformation = 15;

    public const uint JobObjectLimitActiveProcess = 0x00000008;
    public const uint JobObjectLimitProcessMemory = 0x00000100;
    public const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    public const uint JobObjectCpuRateControlEnable = 0x1;
    public const uint JobObjectCpuRateControlHardCap = 0x4;

    public const uint JobObjectMsgProcessMemoryLimit = 9;
    public const uint JobObjectMsgJobMemoryLimit = 10;

    // --- Structures -------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public nint Reserved2Ptr;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobAssociateCompletionPort
    {
        public nint CompletionKey;
        public nint CompletionPort;
    }

    // --- userenv.dll : profils AppContainer ---------------------------------------------------------------------------

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateAppContainerProfile(string name, string displayName, string description,
        nint capabilities, uint capabilityCount, out nint sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DeleteAppContainerProfile(string name);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetAppContainerFolderPath(string sidString, out nint path);

    // --- advapi32.dll -----------------------------------------------------------------------------------------------

    [LibraryImport("advapi32.dll")]
    public static partial nint FreeSid(nint sid);

    // --- kernel32.dll : processus ---------------------------------------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, nint value, nuint size,
        nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcess(string applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, SafeHandle sourceHandle, SafeProcessHandle targetProcess, out nint targetHandle,
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    // --- kernel32.dll : job objects et port de complétion ---------------------------------------------------------------

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial SafeFileHandle CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobExtendedLimitInformation info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobCpuRateControlInformation info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobAssociateCompletionPort info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobExtendedLimitInformation info,
        uint size, nint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobCpuRateControlInformation info,
        uint size, nint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobBasicAccountingInformation info,
        uint size, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle CreateIoCompletionPort(nint fileHandle, nint existingPort, nuint completionKey, uint concurrentThreads);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetQueuedCompletionStatus(SafeFileHandle port, out uint bytesTransferred, out nuint completionKey,
        out nint overlapped, uint milliseconds);
}
