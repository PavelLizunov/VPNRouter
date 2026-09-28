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

public interface ISplitTunnelDriver : IDisposable
{
    bool IsAvailable { get; }

    string? LastFailureReason { get; }

    bool IsEngaged { get; }

    bool IsPumpHealthy { get; }

    event Action<bool>? EngagedChanged;

    Task<bool> EngageAsync(SplitTunnelEngageRequest request, CancellationToken ct);

    Task DisengageAsync(CancellationToken ct);

    Task SweepStaleStateAsync(CancellationToken ct);
}

public sealed record SplitTunnelEngageRequest(
    IReadOnlyList<string> ExcludedDosPaths,
    string? TunnelIpv4,
    string? TunnelIpv6);

public enum TrueSplitState
{
    NotApplicable,
    DriverMissing,
    Starting,
    Active,
    Fallback
}

[SupportedOSPlatform("windows")]
internal sealed class SplitTunnelDriverManager : ISplitTunnelDriver
{
    private const string DriverFileName = "mullvad-split-tunnel.sys";
    private const string ServiceDisplayName = "Mullvad Split Tunnel (VPNRouter)";
    private static readonly TimeSpan NetChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReRegisterRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);
    private const int PumpBufferSize = 64 * 1024 + 64;
    private const int PumpMaxErrorStreak = 3;

    private readonly string _sysPath;
    private readonly string _ownTunName;
    private readonly ILogger _log;
    private readonly Func<string, string?> _queryDosDevice;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private SafeDeviceHandle? _device;
    private SafeWaitHandle? _controlEvent;

    private volatile bool _engaged;
    private volatile bool _pumpHealthy = true;
    private bool _sublayersCreated;
    private bool _netChangeSubscribed;
    private bool _disposed;

    private Thread? _pumpThread;
    private SafeWaitHandle? _pumpEvent;
    private byte[]? _pumpBuffer;
    private GCHandle _pumpBufferHandle;
    private volatile bool _pumpStop;
    private int _pumpErrorStreak;

    private SplitTunnelEngageRequest? _lastRequest;
    private (IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) _lastAddrs;
    private CancellationTokenSource? _debounceCts;

    public event Action<bool>? EngagedChanged;

    public string? LastFailureReason { get; private set; }

    public SplitTunnelDriverManager(
        string? driverDir = null,
        string ownTunName = "VPNRouter-TUN",
        ILogger? logger = null,
        Func<string, string?>? queryDosDevice = null)
    {
        driverDir ??= Path.Combine(AppContext.BaseDirectory, "driver");
        _sysPath = Path.Combine(driverDir, DriverFileName);
        _ownTunName = ownTunName;
        _log = logger ?? Log.Logger;
        _queryDosDevice = queryDosDevice ?? DefaultQueryDosDevice;
    }

    public bool IsEngaged => _engaged;
    public bool IsAvailable => File.Exists(_sysPath);
    public bool IsPumpHealthy => _pumpHealthy;

    public async Task<bool> EngageAsync(SplitTunnelEngageRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;

        bool before = _engaged, ok;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try
        {
            ok = EngageLocked(request);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[SplitTunnel] Engage threw (non-fatal) — RESET + fall back to post-capture routing");
            if (ex is Win32Exception win32
                && SplitTunnelPolicy.FormatDriverStartFailure(unchecked((uint)win32.NativeErrorCode)) is { } reason)
                LastFailureReason = reason;

            BestEffortResetAndCloseLocked();
            ok = false;
        }
        finally { _gate.Release(); }

        if (_engaged != before) RaiseEngagedChanged(_engaged);
        return ok;
    }

    public async Task DisengageAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        bool before = _engaged;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try { DisengageLocked(); }
        catch (Exception ex) { _log.Warning(ex, "[SplitTunnel] Disengage threw (non-fatal)"); }
        finally { _gate.Release(); }

        if (_engaged != before) RaiseEngagedChanged(_engaged);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var lastCts = Interlocked.Exchange(ref _debounceCts, null);
        if (lastCts is not null)
        {
            try { lastCts.Cancel(); } catch (ObjectDisposedException) { }
            lastCts.Dispose();
        }

        if (!OperatingSystem.IsWindows()) { _gate.Dispose(); return; }

        try
        {
            if (_gate.Wait(DisposeGateTimeout))
            {
                try { DisengageLocked(); }
                finally { _gate.Release(); }
            }
        }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] Dispose cleanup issue (ignored)"); }

        _gate.Dispose();
    }

    public async Task SweepStaleStateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_sysPath)) return;

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_engaged) return;
            if (!EnsureDeviceOpenLocked()) return;
            try
            {
                var state = GetStateLocked();
                if (state > Proto.DriverState.Started)
                {
                    _log.Information("[SplitTunnel] Stale driver state {State} from a prior session — RESET", state);
                    TryResetLocked();
                }
            }
            finally { CloseDeviceLocked(); }
        }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] Stale-state sweep failed (ignored)"); }
        finally { _gate.Release(); }
    }

    private bool EngageLocked(SplitTunnelEngageRequest request)
    {
        LastFailureReason = null;

        if (!IsAvailable)
        {
            LastFailureReason = $"True-split driver file is missing at {_sysPath}.";
            _log.Warning("[SplitTunnel] Driver file missing at {Path} — feature off, post-capture routing stands", _sysPath);
            return false;
        }

        VerifySysIntegrityLocked();

        if (DescribeRunningForeignSplitDriverOwner() is { } foreignOwner)
        {
            LastFailureReason = foreignOwner;
            _log.Warning("[SplitTunnel] Foreign split-tunnel kernel driver is running before our start path; not touching SCM");
            return false;
        }

        if (!EnsureServiceLocked()) return false;

        if (!EnsureSublayersLocked()) return false;

        if (!EnsureDeviceOpenLocked()) return false;

        var addrs = ResolveAddresses(request);

        if (addrs.inetV4 is null)
        {
            LastFailureReason = "True-split could not find a physical internet adapter with an IPv4 gateway.";
            _log.Warning("[SplitTunnel] no internet NIC resolved — cannot bind excluded apps to a real address; fail-open to post-capture");
            BestEffortResetAndCloseLocked();
            return false;
        }

        var initial = GetStateLocked();

        if (initial == Proto.DriverState.Engaged && _engaged && _lastRequest is not null
            && _lastRequest.ExcludedDosPaths.SequenceEqual(request.ExcludedDosPaths, StringComparer.OrdinalIgnoreCase)
            && !SplitTunnelPolicy.ShouldReRegister(_lastAddrs, addrs))
        {
            return true;
        }

        if (initial != Proto.DriverState.Started)
        {
            _log.Information("[SplitTunnel] Driver state {State} — RESET before (re)initialise", initial);
            ResetLocked();
        }

        IoctlLocked(Proto.IoctlInitialize, Proto.BuildSublayerGuids(Proto.SublayerBaseline, Proto.SublayerDns), null);
        IoctlLocked(Proto.IoctlRegisterProcesses, BuildProcessSnapshotBuffer(), null);
        IoctlLocked(Proto.IoctlRegisterIpAddresses,
            Proto.BuildAddresses(addrs.tunV4, addrs.inetV4, addrs.tunV6, addrs.inetV6), null);
        IoctlLocked(Proto.IoctlSetConfiguration, BuildConfigBuffer(request.ExcludedDosPaths), null);

        var state = GetStateLocked();
        if (state != Proto.DriverState.Engaged)
        {
            _log.Warning("[SplitTunnel] Engage did not reach ENGAGED (state={State}) — RESET + fall back", state);
            BestEffortResetAndCloseLocked();
            return false;
        }

        _lastRequest = request;
        _lastAddrs = addrs;
        _engaged = true;
        SubscribeNetworkChangeLocked();
        StartPumpLocked();
        _log.Information("[SplitTunnel] ENGAGED — {N} excluded path(s) bind to internet NIC {Inet}",
            request.ExcludedDosPaths.Count, addrs.inetV4);
        return true;
    }

    private void DisengageLocked()
    {
        StopPumpLocked();
        UnsubscribeNetworkChangeLocked();
        if (_device is { IsInvalid: false })
            TryResetLocked();
        CloseDeviceLocked();
        DeleteSublayersLocked();
        if (_engaged)
        {
            _engaged = false;
            _log.Information("[SplitTunnel] Disengaged (driver inert, kernel service left running)");
        }
    }

    private void BestEffortResetAndCloseLocked()
    {
        StopPumpLocked();
        try { if (_device is { IsInvalid: false }) TryResetLocked(); } catch {  }
        CloseDeviceLocked();
        _engaged = false;
    }

    private void VerifySysIntegrityLocked()
    {
        try
        {
            var sidecarPath = Path.Combine(Path.GetDirectoryName(_sysPath) ?? string.Empty, "checksums.sha256");
            if (!File.Exists(sidecarPath))
            {
                _log.Debug("[SplitTunnel] No driver checksums.sha256 sidecar — skipping integrity diagnostic");
                return;
            }
            var expected = SplitTunnelPolicy.ParseSidecarHashFor(File.ReadAllText(sidecarPath), DriverFileName);
            if (expected == null)
            {
                _log.Debug("[SplitTunnel] {File} not listed in checksums.sha256 — skipping integrity diagnostic", DriverFileName);
                return;
            }
            using var fs = File.OpenRead(_sysPath);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
            if (actual == expected)
                _log.Debug("[SplitTunnel] Driver integrity OK (sha256 matches sidecar)");
            else
                _log.Warning("[SplitTunnel] Driver sha256 MISMATCH vs checksums.sha256 (expected {Exp}, got {Act}) — " +
                    "stale/partial .sys? ABI mismatch possible; engaging anyway (Windows signature check is the tamper gate)",
                    expected, actual);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SplitTunnel] Driver integrity diagnostic failed (ignored)");
        }
    }

    private bool EnsureServiceLocked()
    {
        IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LastFailureReason = $"True-split needs administrator access to Service Control Manager (OpenSCManager err={err}).";
            _log.Warning("[SplitTunnel] OpenSCManager failed (err={Err}) — need admin; post-capture stands",
                err);
            return false;
        }
        try
        {
            IntPtr svc = Native.OpenService(scm, Proto.ServiceName, Native.SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Native.ERROR_SERVICE_DOES_NOT_EXIST)
                    return CreateAndStartServiceLocked(scm);
                if (err == Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                    LastFailureReason = "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.";
                else
                    LastFailureReason = $"True-split driver service could not be opened (OpenService err={err}).";
                _log.Warning("[SplitTunnel] OpenService failed (err={Err}) — post-capture stands", err);
                return false;
            }
            try
            {
                string? existing = QueryServiceBinPath(svc);
                var action = SplitTunnelPolicy.ClassifyServiceBinPath(existing ?? string.Empty, _sysPath);
                switch (action)
                {
                    case Proto.ServiceCollisionAction.BailForeign:
                        LastFailureReason =
                            $"True-split driver service '{Proto.ServiceName}' is owned by another install ({existing ?? "unknown path"}).";
                        _log.Warning("[SplitTunnel] '{Svc}' exists with a foreign binPath ({Path}) — not touching it " +
                            "(real Mullvad or unknown); post-capture stands", Proto.ServiceName, existing);
                        return false;

                    case Proto.ServiceCollisionAction.AdoptMovedInstall:
                        _log.Information("[SplitTunnel] Adopting our relocated service — ChangeServiceConfig binPath → {Path}", _sysPath);
                        if (!Native.ChangeServiceConfig(svc, Native.SERVICE_NO_CHANGE, Native.SERVICE_NO_CHANGE,
                                Native.SERVICE_NO_CHANGE, _sysPath, null, IntPtr.Zero, null, null, null, null))
                            _log.Warning("[SplitTunnel] ChangeServiceConfig failed (err={Err}) — trying start anyway",
                                Marshal.GetLastWin32Error());
                        break;

                    case Proto.ServiceCollisionAction.StartExisting:
                        break;
                }
                if (StartServiceLocked(svc, out int startErr)) return true;
                _log.Warning("[SplitTunnel] StartService failed (err={Err}); not deleting/recreating kernel service at runtime", startErr);
                return false;
            }
            finally
            {
                if (svc != IntPtr.Zero)
                    Native.CloseServiceHandle(svc);
            }
        }
        finally { Native.CloseServiceHandle(scm); }
    }

    private bool CreateAndStartServiceLocked(IntPtr scm)
    {
        IntPtr svc = Native.CreateService(scm, Proto.ServiceName, ServiceDisplayName,
            Native.SERVICE_ALL_ACCESS, Native.SERVICE_KERNEL_DRIVER, Native.SERVICE_DEMAND_START,
            Native.SERVICE_ERROR_NORMAL, _sysPath, null, IntPtr.Zero, null, null, null);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_SERVICE_EXISTS)
            {
                svc = Native.OpenService(scm, Proto.ServiceName, Native.SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    _log.Warning("[SplitTunnel] Service appeared then vanished (err={Err})", Marshal.GetLastWin32Error());
                    return false;
                }
            }
            else
            {
                _log.Warning("[SplitTunnel] CreateService failed (err={Err}) — post-capture stands", err);
                if (err == Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                    LastFailureReason = "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.";
                else
                    LastFailureReason = $"True-split driver service could not be created (CreateService err={err}).";
                return false;
            }
        }
        try { return StartServiceLocked(svc, out _); }
        finally { Native.CloseServiceHandle(svc); }
    }

    private bool StartServiceLocked(IntPtr svc, out int err)
    {
        err = 0;
        if (Native.StartService(svc, 0, null)) return true;
        err = Marshal.GetLastWin32Error();
        if (err == Native.ERROR_SERVICE_ALREADY_RUNNING) return true;
        if (err == Native.ERROR_ALREADY_EXISTS)
        {
            if (Native.QueryServiceStatus(svc, out var status)
                && status.dwCurrentState == Native.SERVICE_STOPPED)
            {
                var path = QueryServiceBinPath(svc) ?? "unknown";
                LastFailureReason = DescribeRunningForeignSplitDriverOwner() ??
                    $"True-split driver service '{Proto.ServiceName}' is stopped after StartService err=183 " +
                    $"(Win32ExitCode={status.dwWin32ExitCode}, Path={path}). Windows says the driver object already exists; " +
                    "close Mullvad/other VPN using mullvad-split-tunnel or reboot Windows, then retry True Split.";
                _log.Warning(
                    "[SplitTunnel] StartService returned ERROR_ALREADY_EXISTS but service is STOPPED " +
                    "(Win32ExitCode={Exit}, Path={Path}) - stale/foreign driver object; trying safe repair if service is ours",
                    status.dwWin32ExitCode, path);
                return false;
            }
            _log.Information("[SplitTunnel] StartService returned ERROR_ALREADY_EXISTS — continuing; device open will verify driver usability");
            return true;
        }
        _log.Warning("[SplitTunnel] StartService failed (err={Err}) — post-capture stands", err);
        LastFailureReason = err switch
        {
            Native.ERROR_SERVICE_MARKED_FOR_DELETE =>
                "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split.",
            Native.ERROR_SERVICE_DISABLED =>
                "True-split driver service is disabled; re-run the VPNRouter installer, or in an admin console " +
                "`sc config mullvad-split-tunnel start= demand`, then retry True Split.",
            _ => $"True-split driver service could not start (StartService err={err}).",
        };
        return false;
    }

    private string? DescribeRunningForeignSplitDriverOwner()
    {
        foreach (var foreign in FindRunningForeignSplitDrivers())
            return SplitTunnelPolicy.FormatForeignSplitDriverOwner(foreign.ServiceName, foreign.DisplayName, foreign.PathName);
        return null;
    }

    private List<ForeignSplitDriverService> FindRunningForeignSplitDrivers()
    {
        var result = new List<ForeignSplitDriverService>();
#if PLATFORM_WINDOWS
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, PathName FROM Win32_SystemDriver WHERE State = 'Running'");
            foreach (ManagementObject driver in searcher.Get())
            {
                string name = Convert.ToString(driver["Name"]) ?? "";
                string path = Convert.ToString(driver["PathName"]) ?? "";
                if (!SplitTunnelPolicy.IsForeignSplitDriverService(name, path))
                    continue;

                string displayName = Convert.ToString(driver["DisplayName"]) ?? "";
                result.Add(new ForeignSplitDriverService(name, displayName, path));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SplitTunnel] Failed to inspect Win32_SystemDriver for foreign split driver owner");
        }
#endif
        return result;
    }

    private readonly record struct ForeignSplitDriverService(string ServiceName, string DisplayName, string PathName);

    private string? QueryServiceBinPath(IntPtr svc)
    {
        Native.QueryServiceConfig(svc, IntPtr.Zero, 0, out uint needed);
        if (needed == 0 || Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER)
            return null;
        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.QueryServiceConfig(svc, buf, needed, out _)) return null;
            return Marshal.PtrToStructure<Native.QUERY_SERVICE_CONFIGW>(buf).lpBinaryPathName;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool EnsureSublayersLocked()
    {
        if (_sublayersCreated) return true;
        uint status = Native.FwpmEngineOpen0(null, Native.RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out IntPtr engine);
        if (status != 0)
        {
            LastFailureReason = $"Windows Filtering Platform is unavailable (FwpmEngineOpen0 status=0x{status:X8}).";
            _log.Warning("[SplitTunnel] FwpmEngineOpen0 failed (0x{S:X8}) — BFE stopped? post-capture stands", status);
            return false;
        }
        try
        {
            if (!AddSublayerLocked(engine, Proto.SublayerBaseline, Proto.SublayerWeightBaseline, "VPNRouter split baseline")) return false;
            if (!AddSublayerLocked(engine, Proto.SublayerDns, Proto.SublayerWeightDns, "VPNRouter split dns")) return false;
            _sublayersCreated = true;
            return true;
        }
        finally { Native.FwpmEngineClose0(engine); }
    }

    private bool AddSublayerLocked(IntPtr engine, Guid key, ushort weight, string name)
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var sub = new Native.FWPM_SUBLAYER0 { subLayerKey = key, weight = weight };
            sub.displayData.name = namePtr;
            uint status = Native.FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero);
            if (status != 0 && status != Native.FWP_E_ALREADY_EXISTS)
            {
                LastFailureReason = $"True-split WFP sublayer '{name}' could not be created (status=0x{status:X8}).";
                _log.Warning("[SplitTunnel] FwpmSubLayerAdd0({Name}) failed (0x{S:X8})", name, status);
                return false;
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(namePtr); }
    }

    private void DeleteSublayersLocked()
    {
        if (!_sublayersCreated) return;
        uint status = Native.FwpmEngineOpen0(null, Native.RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out IntPtr engine);
        if (status != 0) { _log.Debug("[SplitTunnel] FwpmEngineOpen0 (delete) failed 0x{S:X8}", status); return; }
        try
        {
            Guid baseline = Proto.SublayerBaseline, dns = Proto.SublayerDns;
            uint s1 = Native.FwpmSubLayerDeleteByKey0(engine, ref baseline);
            uint s2 = Native.FwpmSubLayerDeleteByKey0(engine, ref dns);
            if (s1 != 0) _log.Debug("[SplitTunnel] delete baseline sublayer → 0x{S:X8} (tolerated)", s1);
            if (s2 != 0) _log.Debug("[SplitTunnel] delete dns sublayer → 0x{S:X8} (tolerated)", s2);
            _sublayersCreated = false;
        }
        finally { Native.FwpmEngineClose0(engine); }
    }

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

    private static (string? ntPath, ulong creation) QueryProcessImageAndTime(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, 0);
        try
        {
            string? ntPath = null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (Native.QueryFullProcessImageNameW(h, Native.PROCESS_NAME_NATIVE, sb, ref size))
                ntPath = sb.ToString();
            ulong creation = Native.GetProcessTimes(h, out long ct, out _, out _, out _) ? (ulong)ct : 0;
            return (ntPath, creation);
        }
        finally { Native.CloseHandle(h); }
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

    private (IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) ResolveAddresses(SplitTunnelEngageRequest request)
    {
        var (inetV4, inetV6) = NetworkInterfaceDetector.GetInternetInterfaceAddresses(_ownTunName, _log);
        return (ParseAddr(request.TunnelIpv4), inetV4, ParseAddr(request.TunnelIpv6), inetV6);
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

    private void SubscribeNetworkChangeLocked()
    {
        if (_netChangeSubscribed) return;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _netChangeSubscribed = true;
    }

    private void UnsubscribeNetworkChangeLocked()
    {
        if (!_netChangeSubscribed) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _netChangeSubscribed = false;
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var fresh = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _debounceCts, fresh);
        if (prior is not null)
        {
            try { prior.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { prior.Dispose(); }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NetChangeDebounce, fresh.Token).ConfigureAwait(false);
                await ReRegisterIfChangedAsync(fresh.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {  }
            catch (ObjectDisposedException) {  }
            catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] NetworkChange handler error (ignored)"); }
        });
    }

    internal void RaiseNetworkAddressChangedForTest() => OnNetworkAddressChanged(this, EventArgs.Empty);

    private async Task ReRegisterIfChangedAsync(CancellationToken ct)
    {
        if (_disposed) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var newAddrs = ResolveAddresses(_lastRequest);
            if (!SplitTunnelPolicy.ShouldReRegister(_lastAddrs, newAddrs)) return;
            _log.Information("[SplitTunnel] Internet address changed — re-registering (inet {Old} → {New})",
                _lastAddrs.inetV4, newAddrs.inetV4);
            if (TryReRegisterLocked(newAddrs)) { _lastAddrs = newAddrs; return; }
        }
        finally { _gate.Release(); }

        try { await Task.Delay(ReRegisterRetryDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_disposed) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var retryAddrs = ResolveAddresses(_lastRequest);
            if (TryReRegisterLocked(retryAddrs)) { _lastAddrs = retryAddrs; return; }
            _log.Warning("[SplitTunnel] Re-register failed twice — disengaging (excluded fall back to post-capture)");
            bool before = _engaged;
            DisengageLocked();
            if (_engaged != before) RaiseEngagedChanged(_engaged);
        }
        finally { _gate.Release(); }
    }

    private bool TryReRegisterLocked((IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) a)
    {
        if (a.inetV4 is null)
        {
            _log.Warning("[SplitTunnel] Re-register skipped — no internet NIC resolved (won't bind excluded apps to 0.0.0.0)");
            return false;
        }
        try
        {
            IoctlLocked(Proto.IoctlRegisterIpAddresses, Proto.BuildAddresses(a.tunV4, a.inetV4, a.tunV6, a.inetV6), null);
            return true;
        }
        catch (Exception ex) { _log.Warning(ex, "[SplitTunnel] REGISTER_IP_ADDRESSES re-register failed"); return false; }
    }

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

    private void RaiseEngagedChanged(bool engaged)
    {
        var handler = EngagedChanged;
        if (handler is null) return;
        try { handler(engaged); }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] EngagedChanged handler threw (ignored)"); }
    }
}
