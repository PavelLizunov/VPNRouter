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

/// <summary>
/// Cross-platform seam over the Windows split-tunnel driver (mirror of
/// <see cref="IWindowsDnsHardening"/>): the single implementation is
/// <see cref="SplitTunnelDriverManager"/> (Windows), the test double is
/// <c>FakeSplitTunnelDriver</c> (W1.2). Kept un-attributed so the cross-platform
/// <c>VpnEngine</c> can hold an <c>ISplitTunnelDriver?</c> field without CA1416 soup;
/// on non-Windows the field is simply null.
///
/// <para><b>Fail-open contract:</b> <see cref="EngageAsync"/> returns <c>false</c> (never
/// throws) on any failure, and the excluded apps keep working via the post-capture
/// <c>process_name → direct</c> rules that stay in every generated config. The driver only
/// ever <i>adds</i> an OS-level bind redirect; losing it degrades to the prior behaviour,
/// never breaks the network.</para>
/// </summary>
// public (not internal) so VpnEngine's public ctor + the PlatformServices factory can take it —
// same reason IWindowsDnsHardening is public. The manager impl below stays internal.
public interface ISplitTunnelDriver : IDisposable
{
    /// <summary>True when the bundled driver payload is present and the engine may try to engage it.</summary>
    bool IsAvailable { get; }

    /// <summary>Last user-actionable failure from the driver manager, if any.</summary>
    string? LastFailureReason { get; }

    /// <summary>True while the driver is in the ENGAGED state (excluded sockets bind to the
    /// physical NIC past the TUN).</summary>
    bool IsEngaged { get; }

    /// <summary>Observability only — false when the P3 event pump has died. Does NOT imply the
    /// split stopped (splitting is in-kernel, independent of the pump). Feeds the badge tooltip / diag.</summary>
    bool IsPumpHealthy { get; }

    /// <summary>Raised on an engaged↔disengaged transition (for the W1.3 badge). Handlers run
    /// under the manager lock — keep them trivial and non-reentrant.</summary>
    event Action<bool>? EngagedChanged;

    /// <summary>(Re)engage the driver for the given excluded paths + TUN addresses. Idempotent —
    /// a second call reinitialises (RESET → INITIALIZE → REGISTER → SET). Returns false on any
    /// failure (fail-open); never throws.</summary>
    Task<bool> EngageAsync(SplitTunnelEngageRequest request, CancellationToken ct);

    /// <summary>RESET the driver to inert (kernel service is left running, per design). Idempotent;
    /// never throws.</summary>
    Task DisengageAsync(CancellationToken ct);

    /// <summary>Best-effort crash-recovery, called once at engine start: RESET a stale ENGAGED driver
    /// left by a crashed prior session (the kernel service is never stopped, so its stale config can
    /// bind a just-launched excluded app to a dead IP after an include-mode restart — the engage hook
    /// can't catch this because a fresh manager's <see cref="IsEngaged"/> is false). No-op when nothing
    /// is wired / the driver isn't loaded / we're already engaged. Never throws.</summary>
    Task SweepStaleStateAsync(CancellationToken ct);
}

/// <summary>Everything needed for a full (re)engage. <paramref name="ExcludedDosPaths"/> are the
/// already-resolved DOS paths (e.g. <c>C:\Program Files\Discord\Discord.exe</c>) — the manager
/// converts each to its NT device form. TUN addresses come from settings; the physical internet
/// NIC is auto-detected.</summary>
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

/// <summary>
/// Sealed manager driving the <c>mullvad-split-tunnel</c> kernel driver: owns the SCM service
/// (create/adopt, never stopped), the one exclusive overlapped device handle, the two WFP
/// sublayers the driver installs filters into, and the engage state machine. A production
/// reshape of the live-verified W1.0 spike; the byte-exact protocol + pure decisions live in
/// <see cref="SplitTunnelDriverProtocol"/> and are golden-tested on CI. This class is the thin
/// I/O orchestration — every public method fails open (§3 of the arch plan) and never throws.
///
/// <para><b>P2 scope:</b> SCM + collision guard, overlapped-IOCTL wrapper, engage/disengage,
/// sublayers, crash-sweep, <see cref="NetworkChange"/> re-register, fail-open. The inverted-call
/// event pump is P3 — <see cref="IsPumpHealthy"/> reports healthy until then.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SplitTunnelDriverManager : ISplitTunnelDriver
{
    private const string DriverFileName = "mullvad-split-tunnel.sys";
    private const string ServiceDisplayName = "Mullvad Split Tunnel (VPNRouter)";
    private static readonly TimeSpan NetChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReRegisterRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);
    // ImageNameLength is a USHORT (<= 65535 b) and the headers are fixed, so a DEQUEUE_EVENT
    // payload can't exceed this — overflow is impossible by construction (arch §2.2).
    private const int PumpBufferSize = 64 * 1024 + 64;
    private const int PumpMaxErrorStreak = 3;   // consecutive DEQUEUE errors → pump degraded, stops

    private readonly string _sysPath;
    private readonly string _ownTunName;
    private readonly ILogger _log;
    private readonly Func<string, string?> _queryDosDevice;

    // Serialises the entire control plane (engage / disengage / re-register / sweep). The P3
    // event pump uses its own OVERLAPPED on the same handle — legal, different OVERLAPPED.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SafeDeviceHandle? _device;
    private SafeWaitHandle? _controlEvent;   // per control-IOCTL wait (its own event, distinct from the pump's)

    private volatile bool _engaged;
    private volatile bool _pumpHealthy = true;
    private bool _sublayersCreated;
    private bool _netChangeSubscribed;
    private bool _disposed;

    // Event pump (P3): a dedicated bg thread draining DEQUEUE_EVENT. Observability only — its death
    // never touches _engaged (splitting is in-kernel, arch §2.1). Its OVERLAPPED needs its OWN event
    // (the control plane and the pump run concurrent overlapped I/O on the same handle).
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

    /// <param name="driverDir">Directory holding <c>mullvad-split-tunnel.sys</c>; defaults to the
    /// bundled <c>driver/</c> beside the app (like sing-box). Injectable for tests.</param>
    /// <param name="ownTunName">Our TUN adapter name, filtered out of the internet-NIC pick.</param>
    /// <param name="queryDosDevice">Seam over <c>QueryDosDeviceW</c> (default wraps the native call).</param>
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

    // ─── Public API (all fail-open, never throw) ────────────────────────────────

    public async Task<bool> EngageAsync(SplitTunnelEngageRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;

        bool before = _engaged, ok;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }   // cancelled → not engaged (fail-open), never throw
        try
        {
            ok = EngageLocked(request);
        }
        catch (Exception ex)   // fail-path #13 — no exception ever reaches the caller
        {
            _log.Warning(ex, "[SplitTunnel] Engage threw (non-fatal) — RESET + fall back to post-capture routing");
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
        // Cancel any in-flight NIC-debounce task BEFORE _gate.Dispose() below — else a task waking from
        // its 5 s retry-delay would re-acquire a disposed _gate (bug-hunt P1-1). Its own finally disposes
        // the CTS. Unconditional here so it fires even when a prior Disengage already unsubscribed.
        // The Task no longer disposes its own CTS (see OnNetworkAddressChanged), so cancel + dispose the
        // last one here. Guard the cancel so Dispose() itself never throws on a teardown race.
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

    /// <summary>
    /// Crash-recovery sweep (§1.3, fail-path #12): if the demand-started driver survived a prior
    /// session in a <c>&gt; STARTED</c> state (we never stop the service) while we are NOT engaged,
    /// its stale IP/config would keep splitting excluded apps to a dead address — RESET it. Opens
    /// the device only if the driver is already loaded (never creates the service just to sweep);
    /// a device held by a real Mullvad daemon is skipped. Wired at <c>VpnEngine.StartAsyncInternal</c>
    /// start (W1.2). Never throws.
    /// </summary>
    public async Task SweepStaleStateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_sysPath)) return;

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }   // never-throw contract (matches DisengageAsync)
        try
        {
            if (_engaged) return;                    // active this session — nothing stale
            if (!EnsureDeviceOpenLocked()) return;   // driver not loaded / held elsewhere → nothing to sweep
            try
            {
                var state = GetStateLocked();
                if (state > Proto.DriverState.Started)
                {
                    _log.Information("[SplitTunnel] Stale driver state {State} from a prior session — RESET", state);
                    TryResetLocked();
                }
            }
            finally { CloseDeviceLocked(); }         // we didn't engage; release the exclusive handle
        }
        catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] Stale-state sweep failed (ignored)"); }
        finally { _gate.Release(); }
    }

    // ─── Engage / disengage flow (under _gate) ──────────────────────────────────

    private bool EngageLocked(SplitTunnelEngageRequest request)
    {
        LastFailureReason = null;

        // #1 — driver file present? (build-time sha256 pin is W1.4; runtime just needs the .sys.)
        if (!IsAvailable)
        {
            LastFailureReason = $"True-split driver file is missing at {_sysPath}.";
            _log.Warning("[SplitTunnel] Driver file missing at {Path} — feature off, post-capture routing stands", _sysPath);
            return false;
        }

        // #2/#3 — ensure the kernel service (create / adopt-moved / start), collision-guarded.
        if (!EnsureServiceLocked()) return false;

        // Micro-invariant: sublayers BEFORE any driver IOCTL, so the driver's filters never land
        // in a sublayer we later delete (#6, and avoids FWP_E_IN_USE junk on teardown).
        if (!EnsureSublayersLocked()) return false;

        // #4 — the single exclusive overlapped handle.
        if (!EnsureDeviceOpenLocked()) return false;

        var addrs = ResolveAddresses(request);

        // #10 — no physical internet NIC resolved. BuildAddresses would zero the internet slot, so
        // excluded sockets bind to 0.0.0.0 and break. Guard BEFORE the cheap-skip: a null inet means we
        // must not stay engaged regardless of prior state. Full cleanup → fail-open to post-capture.
        if (addrs.inetV4 is null)
        {
            LastFailureReason = "True-split could not find a physical internet adapter with an IPv4 gateway.";
            _log.Warning("[SplitTunnel] no internet NIC resolved — cannot bind excluded apps to a real address; fail-open to post-capture");
            BestEffortResetAndCloseLocked();
            return false;
        }

        var initial = GetStateLocked();

        // Idempotent cheap-skip (bug-hunt P1-3): a re-engage from a hot-apply that changed neither the
        // excluded set nor the addresses is a no-op — skip the RESET→re-init so we don't briefly un-split
        // excluded apps for nothing. Only when the driver is already ENGAGED for this exact config.
        if (initial == Proto.DriverState.Engaged && _engaged && _lastRequest is not null
            && _lastRequest.ExcludedDosPaths.SequenceEqual(request.ExcludedDosPaths, StringComparer.OrdinalIgnoreCase)
            && !SplitTunnelPolicy.ShouldReRegister(_lastAddrs, addrs))
        {
            return true;
        }

        // Engage state machine (ABI §"State machine"). A non-STARTED state here is either a stale
        // prior-session tail or a re-engage — RESET brings the driver back to STARTED to re-init.
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
        if (state != Proto.DriverState.Engaged)   // #7
        {
            // Full cleanup, not just RESET: a failed RE-engage (after a prior success) must clear
            // _engaged + close the handle, else IsEngaged stays true and the W1.3 badge lies while the
            // driver is inert. BestEffortResetAndCloseLocked mirrors the exception path (#13); the
            // EngageAsync wrapper's before/after check then raises EngagedChanged(false) → badge off.
            _log.Warning("[SplitTunnel] Engage did not reach ENGAGED (state={State}) — RESET + fall back", state);
            BestEffortResetAndCloseLocked();
            return false;
        }

        _lastRequest = request;
        _lastAddrs = addrs;
        _engaged = true;
        SubscribeNetworkChangeLocked();
        StartPumpLocked();   // observability only — never gates engaged state
        _log.Information("[SplitTunnel] ENGAGED — {N} excluded path(s) bind to internet NIC {Inet}",
            request.ExcludedDosPaths.Count, addrs.inetV4);
        return true;
    }

    private void DisengageLocked()
    {
        // Order (arch §2.2): stop the pump FIRST (cancel its pended DEQUEUE + join) so it isn't
        // holding I/O on the handle when we RESET and close it.
        StopPumpLocked();
        UnsubscribeNetworkChangeLocked();
        if (_device is { IsInvalid: false })
            TryResetLocked();          // driver → inert STARTED; service is LEFT running (design decision #3)
        CloseDeviceLocked();
        DeleteSublayersLocked();
        if (_engaged)
        {
            _engaged = false;
            _log.Information("[SplitTunnel] Disengaged (driver inert, kernel service left running)");
        }
    }

    // #5 — mid-flow engage failure returns the driver to inert. Deliberately does NOT unsubscribe
    // NetworkChange (we only subscribe on the success tail, so there's nothing to undo) nor delete
    // the sublayers (a retry reuses them via tolerated ALREADY_EXISTS, and deleting one that just
    // took a driver filter would leave FWP_E_IN_USE junk). That's why this isn't DisengageLocked.
    private void BestEffortResetAndCloseLocked()
    {
        StopPumpLocked();   // a prior engage's pump could be live if this is a failed re-engage
        try { if (_device is { IsInvalid: false }) TryResetLocked(); } catch { /* best effort */ }
        CloseDeviceLocked();
        _engaged = false;
    }

    // ─── SCM: create / adopt / start (never stop) ───────────────────────────────

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
                // #3 — collision guard: what does the existing service point at?
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
                return TryRepairOwnStoppedServiceLocked(scm, ref svc, action, startErr);
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
            if (err == Native.ERROR_SERVICE_EXISTS)   // race: created between our OpenService and here
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
        LastFailureReason = err == Native.ERROR_SERVICE_MARKED_FOR_DELETE
            ? "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split."
            : $"True-split driver service could not start (StartService err={err}).";
        return false;
    }

    private bool TryRepairOwnStoppedServiceLocked(
        IntPtr scm,
        ref IntPtr svc,
        Proto.ServiceCollisionAction action,
        int startErr)
    {
        uint? state = QueryServiceState(svc);
        if (!SplitTunnelPolicy.CanRepairOwnStoppedServiceStartFailure(action, startErr, state))
            return false;

        if (DescribeRunningForeignSplitDriverOwner() is { } foreignOwner)
        {
            LastFailureReason = foreignOwner;
            _log.Warning("[SplitTunnel] Not repairing own stopped service: a foreign mullvad-split-tunnel driver is running");
            return false;
        }

        _log.Warning("[SplitTunnel] Repairing stopped stale own service after StartService err={Err}: delete + recreate", startErr);
        if (!Native.DeleteService(svc))
        {
            int err = Marshal.GetLastWin32Error();
            LastFailureReason = err == Native.ERROR_SERVICE_MARKED_FOR_DELETE
                ? "True-split driver service is being deleted by Windows; reboot Windows, then retry True Split."
                : $"True-split driver service repair failed (DeleteService err={err}).";
            _log.Warning("[SplitTunnel] DeleteService repair failed (err={Err}) - post-capture stands", err);
            return false;
        }

        Native.CloseServiceHandle(svc);
        svc = IntPtr.Zero;
        if (CreateAndStartServiceLocked(scm))
            return true;

        if (SplitTunnelPolicy.IsStaleDriverObjectAfterRepairFailure(startErr, LastFailureReason))
        {
            LastFailureReason = DescribeRunningForeignSplitDriverOwner() ??
                "True Split tried to repair the VPNRouter split driver service, but Windows still reports " +
                "that the old driver object already exists (StartService err=183). Reboot Windows, then retry True Split.";
            _log.Warning("[SplitTunnel] Repair delete+recreate completed, but StartService still returned ERROR_ALREADY_EXISTS - reboot required");
        }

        return false;
    }

    private string? DescribeRunningForeignSplitDriverOwner()
    {
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
                return SplitTunnelPolicy.FormatForeignSplitDriverOwner(name, displayName, path);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SplitTunnel] Failed to inspect Win32_SystemDriver for foreign split driver owner");
        }
#endif
        return null;
    }

    private static uint? QueryServiceState(IntPtr svc)
    {
        return Native.QueryServiceStatus(svc, out var status)
            ? status.dwCurrentState
            : null;
    }

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

    // ─── WFP sublayers (we create them; the driver only installs filters into them) ──

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
        IntPtr namePtr = Marshal.StringToHGlobalUni(name);   // FWPM_DISPLAY_DATA0.name must be non-null
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

    // ─── Device handle + overlapped control IOCTL ───────────────────────────────

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

    /// <summary>Issues one overlapped control IOCTL and blocks for its completion (control IOCTLs
    /// are ms-scale, per §1.5 we reap them synchronously — no IOCP). Buffers + the OVERLAPPED are
    /// pinned via <see cref="GCHandle"/> (no <c>unsafe</c>). Throws <see cref="Win32Exception"/> on
    /// failure; the caller's try/catch turns that into fail-open.</summary>
    private uint IoctlLocked(uint code, byte[]? input, byte[]? output)
    {
        var dev = _device ?? throw new InvalidOperationException("split-tunnel device not open");
        var evt = _controlEvent ?? throw new InvalidOperationException("split-tunnel control event not created");

        // Reset the manual-reset event so a prior op's signal can't complete this one early.
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

    private void ResetLocked() => IoctlLocked(Proto.IoctlReset, null, null);   // METHOD_NEITHER, null buffers

    private void TryResetLocked()   // #11 — a wedged driver must never block teardown / Stop()
    {
        try { ResetLocked(); }
        catch (Exception ex) { _log.Warning(ex, "[SplitTunnel] RESET failed (driver wedged?) — continuing teardown"); }
    }

    // ─── Buffer assembly (live process snapshot + DOS→NT config) ─────────────────

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
                if (pid is 0 or 4) continue;   // Idle / System — no queryable image
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
        if (h == IntPtr.Zero) return (null, 0);   // System-protected / gone — skip (empty path)
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

    /// <summary>Parses a settings TUN address that may be a bare IP or CIDR ("172.19.0.2/30").</summary>
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

    // ─── NetworkChange → re-register (§5.2, fail-path #10) ───────────────────────

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
        // The Task no longer disposes its own CTS (see OnNetworkAddressChanged), so cancel + dispose the
        // current one here and null it out. Guard the cancel against a teardown race.
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // NIC flaps fire a burst — debounce 2 s of quiet, then re-check under the gate.
        var fresh = new CancellationTokenSource();
        // The SUPERSEDER owns the prior CTS's lifetime: cancel it (so its Task unwinds), THEN dispose it.
        // The Task must NOT dispose its own CTS — that was the r2 daemon crash: the Task's finally-dispose
        // ran while _debounceCts still referenced the CTS, so the NEXT event's Cancel() hit a disposed CTS
        // -> ObjectDisposedException thrown synchronously on the NetworkChange callback thread, outside the
        // Task's try/catch -> whole-daemon crash (found live on brat, r2). With disposal owned here,
        // _debounceCts never references a disposed CTS. The try/catch stays as belt-and-suspenders.
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
            catch (OperationCanceledException) { /* superseded by a newer change */ }
            catch (ObjectDisposedException) { /* our CTS was disposed by a superseder/teardown — benign */ }
            catch (Exception ex) { _log.Debug(ex, "[SplitTunnel] NetworkChange handler error (ignored)"); }
            // NB: NO finally-dispose here — the next superseder (or Dispose / UnsubscribeNetworkChangeLocked)
            // disposes `fresh`. Disposing here (the old bug-hunt-P1-2 "leak fix") is what caused the r2
            // use-after-dispose crash. Every CTS is still disposed exactly once, so there is no leak.
        });
    }

    // Test seam (InternalsVisibleTo): synchronously fire the NetworkChange handler — the exact path that
    // threw ObjectDisposedException on a NIC-change burst in r2. Lets a unit test stress the CTS
    // supersede/dispose lifecycle without a real NIC event.
    internal void RaiseNetworkAddressChangedForTest() => OnNetworkAddressChanged(this, EventArgs.Empty);

    private async Task ReRegisterIfChangedAsync(CancellationToken ct)
    {
        if (_disposed) return;   // bug-hunt P1-1: don't touch a _gate that Dispose may be disposing
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var newAddrs = ResolveAddresses(_lastRequest);
            if (!SplitTunnelPolicy.ShouldReRegister(_lastAddrs, newAddrs)) return;
            _log.Information("[SplitTunnel] Internet address changed — re-registering (inet {Old} → {New})",
                _lastAddrs.inetV4, newAddrs.inetV4);
            if (TryReRegisterLocked(newAddrs)) { _lastAddrs = newAddrs; return; }
            // else: first attempt failed — fall through to the retry below (control flow guarantees it).
        }
        finally { _gate.Release(); }

        // Gate released during the retry wait so Engage/Disengage/Stop aren't blocked for 5 s.
        try { await Task.Delay(ReRegisterRetryDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_disposed) return;   // bug-hunt P1-1: Dispose may have raced the retry wait
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_engaged || _device is not { IsInvalid: false } || _lastRequest is null) return;
            var retryAddrs = ResolveAddresses(_lastRequest);
            if (TryReRegisterLocked(retryAddrs)) { _lastAddrs = retryAddrs; return; }
            // Never hold ENGAGED with a stale internet IP — that would bind excluded apps to a dead
            // address (anti-fail-open). Disengage so they return to post-capture via the live TUN.
            _log.Warning("[SplitTunnel] Re-register failed twice — disengaging (excluded fall back to post-capture)");
            bool before = _engaged;
            DisengageLocked();
            if (_engaged != before) RaiseEngagedChanged(_engaged);
        }
        finally { _gate.Release(); }
    }

    private bool TryReRegisterLocked((IPAddress? tunV4, IPAddress? inetV4, IPAddress? tunV6, IPAddress? inetV6) a)
    {
        // #10 — never register a zeroed internet slot (would bind excluded apps to 0.0.0.0). A null inet
        // is a re-register FAILURE, not a value to write: it falls into the retry, and on a persistent
        // null the caller disengages (excluded return to post-capture via the live TUN) rather than lie.
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

    // ─── Event pump (P3): inverted-call DEQUEUE_EVENT drain — observability only ─────

    private void StartPumpLocked()
    {
        if (_pumpThread is { IsAlive: true }) return;   // a re-engage keeps the running pump

        // Reclaim any orphaned event/buffer left by a PRIOR pump that exited on its own (the 3-error
        // degrade path never runs StopPumpLocked) or a timed-out-and-since-died one. Safe here because
        // the guard above proved no pump thread is alive — so we can't unpin a buffer still in use.
        // Without this we'd leak a pinned 64 KB GCHandle + an event handle per degrade→re-engage cycle.
        FreePumpResourcesLocked();
        _pumpThread = null;

        var evt = Native.CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        if (evt.IsInvalid)
        {
            // Pump is observability-only; failing to start it must NOT fail the engage (§2.1).
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
        // CancelIoEx(handle, NULL) aborts the pump's pended DEQUEUE (only I/O outstanding at this
        // point in Disengage) → its GetOverlappedResult returns ERROR_OPERATION_ABORTED → clean exit.
        if (_device is { IsInvalid: false })
            Native.CancelIoEx(_device, IntPtr.Zero);

        if (thread.Join(PumpJoinTimeout))
        {
            _pumpThread = null;
            FreePumpResourcesLocked();
        }
        else
        {
            // Vanishingly rare (CancelIoEx reliably aborts a simple pended IOCTL). Do NOT free the
            // buffer/event the abandoned thread may still be writing, and KEEP _pumpThread pointing at
            // it so a later StartPumpLocked's IsAlive guard won't repin/free those resources. They're
            // reclaimed once it finally dies (next StartPumpLocked) or when CloseDeviceLocked cancels
            // its IRP; the pump's own try/catch absorbs the closed-handle error.
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
            // Fail-open both ways: a dead pump is degraded telemetry, NOT a reason to drop the split.
            _log.Warning(ex, "[SplitTunnel] Event pump crashed — marking degraded (split stays active in-kernel)");
            _pumpHealthy = false;
        }
        GC.KeepAlive(evt);
    }

    /// <summary>After a DEQUEUE error: returns false to EXIT the loop (cancelled, or too many
    /// consecutive errors → degraded), true to keep pumping.</summary>
    private bool ContinueAfterPumpError(int err)
    {
        if (_pumpStop || err == Native.ERROR_OPERATION_ABORTED)
            return false;   // cancelled by StopPumpLocked / a RESET — clean exit, not a degrade
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
