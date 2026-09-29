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
internal sealed partial class SplitTunnelDriverManager : ISplitTunnelDriver
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
        // Cancel the NIC-debounce task before disposing _gate, or it re-acquires a disposed gate.
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
        try { if (_device is { IsInvalid: false }) TryResetLocked(); } catch { }
        CloseDeviceLocked();
        _engaged = false;
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

    private (IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) ResolveAddresses(SplitTunnelEngageRequest request)
    {
        var (inetV4, inetV6) = NetworkInterfaceDetector.GetInternetInterfaceAddresses(_ownTunName, _log);
        return (ParseAddr(request.TunnelIpv4), inetV4, ParseAddr(request.TunnelIpv6), inetV6);
    }

    private void RaiseEngagedChanged(bool engaged)
    {
        var handler = EngagedChanged;
        if (handler is null) return;
        try { handler(engaged); }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] EngagedChanged handler threw (ignored)"); }
    }
}
