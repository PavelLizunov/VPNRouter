#nullable enable
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
#if PLATFORM_WINDOWS
using System.Management;
#endif
using Microsoft.Win32.SafeHandles;
using Serilog;

using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;
using Proto = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Core.Services;

internal sealed partial class SplitTunnelDriverManager
{
    private void StartPumpLocked()
    {
        if (_pumpThread is { IsAlive: true }) return;

        FreePumpResourcesLocked();
        _pumpThread = null;

        var evt = Native.CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        if (evt.IsInvalid)
        {
            _log.Warning("[SplitTunnel] Pump event create failed (err={Err}) — ENGAGED without the event pump (split still active, diag degraded)",
                Marshal.GetLastWin32Error());
            evt.Dispose();
            _pumpHealthy = false;
            return;
        }

        _pumpEvent = evt;
        _pumpBuffer = new byte[PumpBufferSize];
        _pumpBufferHandle = GCHandle.Alloc(_pumpBuffer, GCHandleType.Pinned);
        _pumpStop = false;
        _pumpErrorStreak = 0;
        _pumpHealthy = true;
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "split-tunnel-events" };
        _pumpThread.Start();
    }

    private void StopPumpLocked()
    {
        var thread = _pumpThread;
        if (thread is null) { FreePumpResourcesLocked(); return; }

        _pumpStop = true;
        if (_device is { IsInvalid: false })
            Native.CancelIoEx(_device, IntPtr.Zero);

        if (thread.Join(PumpJoinTimeout))
        {
            _pumpThread = null;
            FreePumpResourcesLocked();
        }
        else
        {
            _log.Warning("[SplitTunnel] Event pump did not join in {Sec}s — abandoning it (resources reclaimed when it exits)",
                PumpJoinTimeout.TotalSeconds);
        }
    }

    private void FreePumpResourcesLocked()
    {
        if (_pumpBufferHandle.IsAllocated) _pumpBufferHandle.Free();
        _pumpBuffer = null;
        _pumpEvent?.Dispose();
        _pumpEvent = null;
    }

    private void PumpLoop()
    {
        var dev = _device;
        var evt = _pumpEvent;
        if (dev is null || evt is null || _pumpBuffer is null) return;
        IntPtr outPtr = _pumpBufferHandle.AddrOfPinnedObject();
        uint outLen = (uint)_pumpBuffer.Length;

        try
        {
            while (!_pumpStop)
            {
                Native.ResetEvent(evt);
                var overlapped = new NativeOverlapped { EventHandle = evt.DangerousGetHandle() };
                var ovH = GCHandle.Alloc(overlapped, GCHandleType.Pinned);
                try
                {
                    IntPtr ovPtr = ovH.AddrOfPinnedObject();
                    bool started = Native.DeviceIoControlOverlapped(
                        dev, Proto.IoctlDequeueEvent, IntPtr.Zero, 0, outPtr, outLen, IntPtr.Zero, ovPtr);
                    if (!started)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != Native.ERROR_IO_PENDING)
                        {
                            if (!ContinueAfterPumpError(err)) return;
                            continue;
                        }
                    }
                    if (!Native.GetOverlappedResult(dev, ovPtr, out uint bytes, bWait: true))
                    {
                        if (!ContinueAfterPumpError(Marshal.GetLastWin32Error())) return;
                        continue;
                    }
                    _pumpErrorStreak = 0;
                    DispatchEvent(bytes);
                }
                finally { if (ovH.IsAllocated) ovH.Free(); }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[SplitTunnel] Event pump crashed — marking degraded (split stays active in-kernel)");
            _pumpHealthy = false;
        }
        GC.KeepAlive(evt);
    }

    private bool ContinueAfterPumpError(int err)
    {
        if (_pumpStop || err == Native.ERROR_OPERATION_ABORTED)
            return false;
        if (++_pumpErrorStreak >= PumpMaxErrorStreak)
        {
            _log.Warning("[SplitTunnel] Event pump: {N} consecutive DEQUEUE errors (last err={Err}) — degraded, stopping pump (split unaffected)",
                _pumpErrorStreak, err);
            _pumpHealthy = false;
            return false;
        }
        _log.Debug("[SplitTunnel] Event pump DEQUEUE error (err={Err}, streak={N})", err, _pumpErrorStreak);
        return true;
    }

    private void DispatchEvent(uint bytes)
    {
        if (_pumpBuffer is null || bytes == 0) return;
        int len = (int)Math.Min(bytes, (uint)_pumpBuffer.Length);
        var ev = Proto.ParseEventBuffer(_pumpBuffer.AsSpan(0, len));
        switch (ev.Kind)
        {
            case Proto.SplitTunnelEventKind.Splitting:
                _log.Information("[SplitTunnel] {Id} pid={Pid} reason={Reason} image={Image}",
                    ev.Id, ev.Pid, ev.Reason, ev.Image);
                break;
            case Proto.SplitTunnelEventKind.SplittingError:
                _log.Warning("[SplitTunnel] {Id} pid={Pid} image={Image}", ev.Id, ev.Pid, ev.Image);
                break;
            case Proto.SplitTunnelEventKind.ErrorMessage:
                _log.Warning("[SplitTunnel] driver error 0x{Status:X8}: {Msg}", ev.Status, ev.Image);
                break;
            case Proto.SplitTunnelEventKind.Unknown:
                _log.Debug("[SplitTunnel] unknown driver event id=0x{Id:X8} (forward-compat skip)", ev.UnknownId);
                break;
            case Proto.SplitTunnelEventKind.Malformed:
                _log.Debug("[SplitTunnel] malformed driver event ({Bytes}b) — skipped", bytes);
                break;
        }
    }
}
