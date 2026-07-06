#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.Core.Services;

/// <summary>
/// Windows-only native surface for the Mullvad split-tunnel driver: the P/Invoke
/// declarations (kernel32 device + process, advapi32 SCM, fwpuclnt WFP sublayers)
/// and the <see cref="SafeDeviceHandle"/>. A faithful port of the live-verified W1.0
/// spike's <c>Native</c> class, plus the overlapped primitives the P2 event pump needs
/// (<see cref="CreateEventW"/>, <see cref="CancelIoEx"/>, <see cref="GetOverlappedResult"/>
/// and a <c>NativeOverlapped*</c> DeviceIoControl overload).
///
/// <para>P1 is compile-only for this file — it is exercised live in P3. It carries no
/// logic of its own; the byte-layout logic is in <see cref="SplitTunnelDriverProtocol"/>
/// (pure, cross-platform) and the I/O orchestration is in the manager. Every declaration
/// here is <c>SetLastError = true</c> so callers read <see cref="Marshal.GetLastWin32Error"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SplitTunnelDriverInterop
{
    // ── kernel32: device + process access rights / flags ──────────────────────
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_NAME_NATIVE = 0x00000001;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    // ── advapi32: service control ─────────────────────────────────────────────
    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint SERVICE_KERNEL_DRIVER = 0x00000001;
    public const uint SERVICE_DEMAND_START = 0x00000003;
    public const uint SERVICE_ERROR_NORMAL = 0x00000001;

    // ChangeServiceConfig "no change" sentinels (self-heal binPath, ABI §1.3 p.3).
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    public const int ERROR_SERVICE_EXISTS = 1073;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;   // QueryServiceConfig first-call sizing probe

    // ── fwpuclnt: WFP sublayer management ─────────────────────────────────────
    public const uint RPC_C_AUTHN_WINNT = 10;
    public const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;

    // ───────────────────────────────────────────────────────────────────────
    // kernel32 — device I/O
    // ───────────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeDeviceHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>Synchronous control IOCTL (lpOverlapped = <see cref="IntPtr.Zero"/>).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeDeviceHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>Overlapped control/DEQUEUE_EVENT IOCTL. Pass the address of a pinned
    /// <c>NativeOverlapped</c> via <paramref name="lpOverlapped"/> (an <see cref="IntPtr"/>,
    /// so callers don't need <c>unsafe</c> — P2 pins the OVERLAPPED and passes its address);
    /// returns false with <see cref="ERROR_IO_PENDING"/> while the request is queued — reap
    /// it with <see cref="GetOverlappedResult"/>. <paramref name="lpBytesReturned"/> is
    /// ignored for overlapped I/O (may be <see cref="IntPtr.Zero"/>).</summary>
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    public static extern bool DeviceIoControlOverlapped(
        SafeDeviceHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        IntPtr lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    /// <summary>Auto/manual-reset event for the overlapped OVERLAPPED.hEvent (the pump
    /// and each control IOCTL wait on this).</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeWaitHandle CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    /// <summary>Resets a manual-reset event to non-signaled. The control-IOCTL wrapper resets
    /// its event before every request so a prior op's leftover signal can't make
    /// <see cref="GetOverlappedResult"/> return prematurely on the next (event-reuse hazard).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ResetEvent(SafeWaitHandle hEvent);

    /// <summary>Cancels the pending overlapped IOCTL on <paramref name="hDevice"/> (pump
    /// shutdown / disengage). Pass the address of the same OVERLAPPED to cancel just that
    /// request, or <see cref="IntPtr.Zero"/> to cancel all I/O this thread issued.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeDeviceHandle hDevice, IntPtr lpOverlapped);

    /// <summary>Reaps a completed/queued overlapped IOCTL. <paramref name="bWait"/> = true
    /// blocks until completion (used by the ms-scale control IOCTLs on a worker thread).
    /// <paramref name="lpOverlapped"/> is the address of the pinned OVERLAPPED.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        SafeDeviceHandle hDevice, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    // ───────────────────────────────────────────────────────────────────────
    // kernel32 — process snapshot / image path / times
    // ───────────────────────────────────────────────────────────────────────

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

    // ───────────────────────────────────────────────────────────────────────
    // advapi32 — service control manager
    // ───────────────────────────────────────────────────────────────────────

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

    /// <summary>Self-heal path for a moved-but-ours install (ABI §1.3 p.3). Pass
    /// <see cref="SERVICE_NO_CHANGE"/> for every field except the new binPath.</summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ChangeServiceConfig(
        IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryServiceConfig(
        IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    // NB: no ControlService / DeleteService P/Invoke by design — the manager NEVER stops or deletes
    // the kernel service (Disengage = RESET-to-inert only). Service removal is uninstall.ps1's job
    // (sc.exe stop/delete, W1.4). Omitting the primitives makes "never stop the service" structural.

    // ───────────────────────────────────────────────────────────────────────
    // fwpuclnt — WFP sublayer management
    // ───────────────────────────────────────────────────────────────────────

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineOpen0(
        string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    // ───────────────────────────────────────────────────────────────────────
    // Native structs
    // ───────────────────────────────────────────────────────────────────────

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
        public IntPtr providerKey;      // GUID* — NULL (driver resolves by GUID, not provider)
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

    /// <summary>QUERY_SERVICE_CONFIGW — output of <see cref="QueryServiceConfig"/>, read by the
    /// collision guard (§3 #3) to compare the existing service's binPath against ours. The LPWStr
    /// fields point into the caller-supplied buffer; <see cref="Marshal.PtrToStructure{T}(IntPtr)"/>
    /// copies them out to managed strings.</summary>
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
}

/// <summary>Owns the device HANDLE returned by <see cref="SplitTunnelDriverInterop.CreateFileW"/>.
/// Invalid = 0 or -1; closed via CloseHandle.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeDeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDeviceHandle() : base(ownsHandle: true) { }
    protected override bool ReleaseHandle() => SplitTunnelDriverInterop.CloseHandle(handle);
}
