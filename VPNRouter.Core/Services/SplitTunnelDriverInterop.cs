#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.Core.Services;

[SupportedOSPlatform("windows")]
internal static class SplitTunnelDriverInterop
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_NAME_NATIVE = 0x00000001;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint SERVICE_KERNEL_DRIVER = 0x00000001;
    public const uint SERVICE_DEMAND_START = 0x00000003;
    public const uint SERVICE_ERROR_NORMAL = 0x00000001;

    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    public const int ERROR_SERVICE_EXISTS = 1073;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_DISABLED = 1058;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint SERVICE_STOPPED = 0x00000001;

    public const uint RPC_C_AUTHN_WINNT = 10;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeDeviceHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeDeviceHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    public static extern bool DeviceIoControlOverlapped(
        SafeDeviceHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        IntPtr lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeWaitHandle CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ResetEvent(SafeWaitHandle hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeDeviceHandle hDevice, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        SafeDeviceHandle hDevice, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(
        IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateService(
        IntPtr hSCManager, string lpServiceName, string lpDisplayName,
        uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ChangeServiceConfig(
        IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryServiceConfig(
        IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineOpen0(
        string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public ushort flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpBinaryPathName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLoadOrderGroup;
        public uint dwTagId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDependencies;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpServiceStartName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SafeDeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDeviceHandle() : base(ownsHandle: true) { }
    protected override bool ReleaseHandle() => SplitTunnelDriverInterop.CloseHandle(handle);
}
