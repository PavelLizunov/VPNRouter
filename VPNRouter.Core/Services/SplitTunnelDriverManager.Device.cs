using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Management;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;
using Proto = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Core.Services;

internal sealed partial class SplitTunnelDriverManager
{
    private bool EnsureDeviceOpenLocked()
    {
        if (_device is { IsInvalid: false }) return true;

        var handle = Native.CreateFileW(Proto.DevicePath,
            Native.GENERIC_READ | Native.GENERIC_WRITE, dwShareMode: 0, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (err == Native.ERROR_ACCESS_DENIED)
            {
                LastFailureReason = DescribeRunningForeignSplitDriverOwner() ??
                    "True-split driver device \\\\.\\MULLVADSPLITTUNNEL is busy (CreateFile err=5). " +
                    "Another VPNRouter Service/App or Mullvad process may hold it.";
                _log.Warning("[SplitTunnel] CreateFile({Dev}) failed (err=5 access denied) — device held exclusively by another agent; post-capture stands", Proto.DevicePath);
            }
            else
            {
                LastFailureReason = $"True-split driver device {Proto.DevicePath} could not be opened (CreateFile err={err}).";
                _log.Warning("[SplitTunnel] CreateFile({Dev}) failed (err={Err}) — driver not loaded? post-capture stands", Proto.DevicePath, err);
            }
            return false;
        }

        var evt = Native.CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        if (evt.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            LastFailureReason = $"True-split control event could not be created (CreateEvent err={err}).";
            _log.Warning("[SplitTunnel] CreateEvent for control IOCTL failed (err={Err})", err);
            evt.Dispose();
            handle.Dispose();
            return false;
        }

        _device = handle;
        _controlEvent = evt;
        return true;
    }

    private void CloseDeviceLocked()
    {
        _device?.Dispose();
        _device = null;
        _controlEvent?.Dispose();
        _controlEvent = null;
    }

    private uint IoctlLocked(uint code, byte[]? input, byte[]? output)
    {
        var dev = _device ?? throw new InvalidOperationException("split-tunnel device not open");
        var evt = _controlEvent ?? throw new InvalidOperationException("split-tunnel control event not created");

        Native.ResetEvent(evt);

        GCHandle inH = default, outH = default, ovH = default;
        try
        {
            IntPtr inPtr = IntPtr.Zero; uint inLen = 0;
            if (input is { Length: > 0 })
            {
                inH = GCHandle.Alloc(input, GCHandleType.Pinned);
                inPtr = inH.AddrOfPinnedObject();
                inLen = (uint)input.Length;
            }
            IntPtr outPtr = IntPtr.Zero; uint outLen = 0;
            if (output is { Length: > 0 })
            {
                outH = GCHandle.Alloc(output, GCHandleType.Pinned);
                outPtr = outH.AddrOfPinnedObject();
                outLen = (uint)output.Length;
            }

            var overlapped = new NativeOverlapped { EventHandle = evt.DangerousGetHandle() };
            ovH = GCHandle.Alloc(overlapped, GCHandleType.Pinned);
            IntPtr ovPtr = ovH.AddrOfPinnedObject();

            bool started = Native.DeviceIoControlOverlapped(dev, code, inPtr, inLen, outPtr, outLen, IntPtr.Zero, ovPtr);
            if (!started)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING)
                    throw new Win32Exception(err, $"DeviceIoControl(0x{code:X8}) failed");
            }
            if (!Native.GetOverlappedResult(dev, ovPtr, out uint bytes, bWait: true))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetOverlappedResult(0x{code:X8}) failed");

            GC.KeepAlive(evt);
            return bytes;
        }
        finally
        {
            if (inH.IsAllocated) inH.Free();
            if (outH.IsAllocated) outH.Free();
            if (ovH.IsAllocated) ovH.Free();
        }
    }

    private Proto.DriverState GetStateLocked()
    {
        var outBuf = new byte[8];
        IoctlLocked(Proto.IoctlGetState, null, outBuf);
        return (Proto.DriverState)BitConverter.ToUInt64(outBuf, 0);
    }

    private void ResetLocked() => IoctlLocked(Proto.IoctlReset, null, null);

    private void TryResetLocked()
    {
        try { ResetLocked(); }
        catch (Exception ex) { _log.Warning(ex, "[SplitTunnel] RESET failed (driver wedged?) — continuing teardown"); }
    }

    private byte[] BuildProcessSnapshotBuffer()
    {
        var byPid = new Dictionary<uint, ProcInfo>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == Native.INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");
        try
        {
            var pe = new Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32>() };
            if (!Native.Process32First(snap, ref pe))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process32First failed");
            do
            {
                uint pid = pe.th32ProcessID;
                if (pid is 0 or 4) continue;
                var (ntPath, creation) = QueryProcessImageAndTime(pid);
                byPid[pid] = new ProcInfo(pid, pe.th32ParentProcessID, creation, ntPath ?? string.Empty);
            } while (Native.Process32Next(snap, ref pe));
        }
        finally { Native.CloseHandle(snap); }

        Proto.ApplyPidRecycleGuard(byPid);
        return Proto.BuildProcessRegistry(new List<ProcInfo>(byPid.Values));
    }

    private byte[] BuildConfigBuffer(IReadOnlyList<string> excludedDosPaths)
    {
        var ntPaths = new List<string>(excludedDosPaths.Count);
        foreach (var dos in excludedDosPaths)
        {
            var nt = Proto.DosPathToNtPath(dos, _queryDosDevice);
            if (nt is null)
            {
                _log.Warning("[SplitTunnel] Could not resolve excluded path to NT form: {Dos} — skipped (post-capture still covers it)", dos);
                continue;
            }
            ntPaths.Add(nt);
        }
        return Proto.BuildConfiguration(ntPaths);
    }

    private static IPAddress? ParseAddr(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s.Substring(0, slash);
        return IPAddress.TryParse(s.Trim(), out var a) ? a : null;
    }

    private static string? DefaultQueryDosDevice(string drive)
    {
        var sb = new StringBuilder(1024);
        uint len = Native.QueryDosDeviceW(drive, sb, sb.Capacity);
        return len == 0 ? null : sb.ToString();
    }
}
