#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Headless.Lifecycle;

namespace VPNRouter.Headless;

/// <summary>
/// Production implementation of IRouterSession.
/// Coordinates Core VPN engine lifecycle, true typed Connected readiness,
/// Linux ownership verification, and bounded teardown without secret leakage.
/// </summary>
public sealed class RouterSession : IRouterSession
{
    private readonly ILifecycleEngine _engine;
    private readonly Func<OwnershipCheckResult>? _ownershipProbe;
    private readonly Func<bool>? _killSwitchProbe;
    private readonly Func<bool>? _capabilityReadinessProbe;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly TimeSpan? _stopTimeout;

    private CancellationTokenSource? _activeConnectCts;
    private string _state = SessionStates.Disconnected;
    private string? _errorCode;
    private bool _disposed;
    private bool _ownsConnection;
    private bool _disconnectStarted;
    private Func<bool>? _retainedReadinessGuard;
    private int? _connectedPid;

    /// <summary>
    /// Default constructor for production use.
    /// Wires Core PlatformServices.CreateVpnEngine without Avalonia or UI dependencies.
    /// </summary>
    public RouterSession()
        : this(new VpnEngineAdapter(PlatformServices.CreateVpnEngine(Log.Logger)), null, null, Log.Logger)
    {
    }

    /// <summary>
    /// Test seam constructor allowing injection of fake lifecycle engine, ownership probe, and logger.
    /// </summary>
    public RouterSession(
        ILifecycleEngine engine,
        Func<OwnershipCheckResult>? ownershipProbe = null,
        Func<bool>? killSwitchProbe = null,
        ILogger? logger = null,
        Func<bool>? capabilityReadinessProbe = null,
        TimeSpan? stopTimeout = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownershipProbe = ownershipProbe;
        _killSwitchProbe = killSwitchProbe;
        _logger = logger;
        _capabilityReadinessProbe = capabilityReadinessProbe ?? engine.CapabilityReadinessFunc;
        _stopTimeout = stopTimeout;

        // Persistent event subscriptions for ongoing failure and recovery
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.Connected += OnEngineConnected;
        _engine.Warning += OnEngineWarning;
    }

    /// <inheritdoc />
    public string State
    {
        get
        {
            lock (_stateLock)
            {
                // Prevent stale connected: if marked connected but retained readiness guard is not satisfied, report error
                if (_state == SessionStates.Connected && !EvaluateRetainedGuard())
                {
                    return SessionStates.Error;
                }

                // Never claim disconnected with live pid of an owned or non-foreign engine; preserve error state
                if (_state == SessionStates.Disconnected && (_engine.IsRunning || _engine.SingBoxPid != null))
                {
                    var isForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
                    if (!isForeign)
                    {
                        return SessionStates.Error;
                    }
                }

                return _state;
            }
        }
    }

    /// <inheritdoc />
    public string? ErrorCode
    {
        get
        {
            lock (_stateLock)
            {
                if (_state == SessionStates.Connected && !EvaluateRetainedGuard())
                {
                    return _errorCode ?? "connection_lost";
                }

                if (_state == SessionStates.Disconnected && (_engine.IsRunning || _engine.SingBoxPid != null))
                {
                    var isForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
                    if (!isForeign)
                    {
                        return _errorCode ?? "stop_failed";
                    }
                }

                return _errorCode;
            }
        }
    }

    private bool EvaluateRetainedGuard()
    {
        if (_retainedReadinessGuard == null) return false;
        if (_connectedPid == null || _connectedPid <= 0) return false;
        if (!_engine.IsRunning) return false;
        if (_engine.SingBoxPid != _connectedPid) return false;

        try
        {
            return _retainedReadinessGuard();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool CanConnect =>
        !_disposed &&
        (State == SessionStates.Disconnected || State == SessionStates.Error) &&
        PlatformCapabilityVerifier.VerifyCanConnect(_ownershipProbe, _logger, _capabilityReadinessProbe);

    /// <inheritdoc />
    public bool SupportsKillSwitch => PlatformCapabilityVerifier.VerifyKillSwitchSupport(_killSwitchProbe, _logger);

    /// <inheritdoc />
    public bool SupportsDnsLockdown => PlatformCapabilityVerifier.VerifyDnsLockdownSupport();

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task ConnectAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new RouterException("busy", "Another session operation is in progress.");
        }

        try
        {
            if (State == SessionStates.Connected || _state == SessionStates.Connecting)
            {
                throw new RouterException("busy", "Session is already connected or connecting.");
            }

            // 1. Cross-GUI and cross-process exclusive ownership verification
            var ownership = _ownershipProbe != null
                ? _ownershipProbe()
                : LinuxOwnershipGuard.CheckOwnership(_logger);

            if (ownership.Status == OwnershipStatus.HeldByAnother)
            {
                TransitionState(SessionStates.Error, "conflict");
                throw new RouterException(
                    "conflict",
                    ownership.Details ?? "Another VPNRouter instance is currently active.");
            }

            if (ownership.Status == OwnershipStatus.Unavailable)
            {
                TransitionState(SessionStates.Unavailable, "unavailable");
                throw new RouterException(
                    "unavailable",
                    ownership.Details ?? "Session ownership state could not be verified.");
            }

            if (ownership.Status != OwnershipStatus.Free)
            {
                TransitionState(SessionStates.Error, "ownership_unknown");
                throw new RouterException(
                    "ownership_unknown",
                    ownership.Details ?? "Session ownership status is unknown; failing closed.");
            }

            // 2. Refuse unsupported unsafe states truthfully rather than pretending parity
            if (settings.App.DnsLeakLockdown && !SupportsDnsLockdown)
            {
                TransitionState(SessionStates.Error, "unavailable");
                throw new RouterException("unavailable", "DNS leak lockdown is not supported on this platform.");
            }

            if (!SupportsKillSwitch && await RequiresKillSwitchAsync(settings, ct).ConfigureAwait(false))
            {
                TransitionState(SessionStates.Error, "unavailable");
                throw new RouterException("unavailable", "Firewall killswitch is not supported without verified privileges.");
            }

            // 3. Verify general environment readiness
            if (!CanConnect)
            {
                TransitionState(SessionStates.Unavailable, "unavailable");
                throw new RouterException("unavailable", "VPN connection cannot be initiated in current environment.");
            }

            // 4. Begin connecting — session claims ownership of this active connection attempt
            lock (_stateLock)
            {
                _ownsConnection = true;
                _disconnectStarted = false;
                _retainedReadinessGuard = null;
                _connectedPid = null;
            }
            TransitionState(SessionStates.Connecting, null);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeConnectCts = linkedCts;

            var outcome = await TwoPhaseConnectCoordinator.RunAsync(
                () => _engine.StartAsync(settings, linkedCts.Token),
                _engine,
                linkedCts.Token).ConfigureAwait(false);

            switch (outcome.Outcome)
            {
                case TwoPhaseOutcome.Connected:
                    // Authoritative typed Connected readiness confirmed
                    bool aborted = false;
                    lock (_stateLock)
                    {
                        if (_disposed || _disconnectStarted || linkedCts.IsCancellationRequested)
                        {
                            aborted = true;
                        }
                        else
                        {
                            _retainedReadinessGuard = outcome.ReadinessGuard;
                            _connectedPid = outcome.Pid;
                        }
                    }

                    if (aborted)
                    {
                        await TeardownOnCancelAsync(ct).ConfigureAwait(false);
                        break;
                    }

                    TransitionState(SessionStates.Connected, null);
                    break;

                case TwoPhaseOutcome.PhaseATimeout:
                    await TeardownAfterStartFailureAsync("timeout", "Connection startup timed out during sing-box initialization.").ConfigureAwait(false);
                    break;

                case TwoPhaseOutcome.PhaseBTimeout:
                    await TeardownAfterStartFailureAsync("timeout", "Connection startup timed out during TUN warmup probe.").ConfigureAwait(false);
                    break;

                case TwoPhaseOutcome.ReadinessGuardFailed:
                    await TeardownAfterStartFailureAsync("connect_failed", "Connected readiness guard verification failed.").ConfigureAwait(false);
                    break;

                case TwoPhaseOutcome.Cancelled:
                    await TeardownOnCancelAsync(ct).ConfigureAwait(false);
                    break;

                case TwoPhaseOutcome.Failed:
                default:
                    await TeardownAfterStartFailureAsync("connect_failed", outcome.ErrorMessage ?? "Connection startup failed.").ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (_ownsConnection)
            {
                var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);
                if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
                {
                    TransitionState(SessionStates.Error, "stop_failed");
                    throw;
                }
                _ownsConnection = false;
            }
            TransitionState(SessionStates.Disconnected, null);
            throw;
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_ownsConnection)
            {
                var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);
                if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
                {
                    TransitionState(SessionStates.Error, "stop_failed");
                }
                else
                {
                    _ownsConnection = false;
                    TransitionState(SessionStates.Error, "connect_failed");
                }
            }
            else
            {
                TransitionState(SessionStates.Error, "connect_failed");
            }
            var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
            throw new RouterException("connect_failed", scrubbed);
        }
        finally
        {
            _activeConnectCts = null;
            _gate.Release();
        }
    }

    private static async Task<bool> RequiresKillSwitchAsync(AppSettings settings, CancellationToken ct)
    {
        if (!string.Equals(settings.App.RoutingMode, "full", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.ActiveProfile))
            return false;
        var manager = new VPNRouter.Core.Services.ProfileManager(
            VPNRouter.Core.Services.VpnEngine.BuildProfileSources(settings));
        await manager.LoadAsync(ct).ConfigureAwait(false);
        var names = settings.ActiveProfile.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return manager.MergeProfilesTolerant(names, out _)?.BlockOnVpnFail == true;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != SessionStates.Connected)
        {
            throw new RouterException("invalid_request", "Cannot apply settings when session is not connected.");
        }

        if (settings.App.DnsLeakLockdown && !SupportsDnsLockdown)
        {
            throw new RouterException("unavailable", "DNS leak lockdown is not supported on this platform.");
        }

        if (!SupportsKillSwitch && await RequiresKillSwitchAsync(settings, ct).ConfigureAwait(false))
        {
            throw new RouterException("unavailable", "Firewall killswitch is not supported without verified privileges.");
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new RouterException("busy", "Another session operation is in progress.");
        }

        try
        {
            var success = await _engine.ApplyAsync(settings, ct).ConfigureAwait(false);
            if (!success)
            {
                throw new RouterException("internal_error", "Failed to apply configuration changes to live engine.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
            _logger?.Warning("[RouterSession] Error applying configuration: {Message}", scrubbed);
            throw new RouterException("internal_error", scrubbed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            _disconnectStarted = true;
            _retainedReadinessGuard = null;
            _connectedPid = null;
        }
        // Cancel any active in-flight connection attempt first to unblock the gate
        _activeConnectCts?.Cancel();

        if (_state == SessionStates.Disconnected)
        {
            return;
        }

        // Ensure no non-owner stop can stop foreign session
        if (!_ownsConnection)
        {
            _logger?.Information("[RouterSession] Disconnect called on unowned session; leaving foreign engine untouched.");
            TransitionState(SessionStates.Disconnected, null);
            return;
        }

        // Refuse to stop if ownership is currently held by another process
        var ownershipCheck = _ownershipProbe != null ? _ownershipProbe() : LinuxOwnershipGuard.CheckOwnership(_logger);
        var isHeldByForeign = ownershipCheck.Status == OwnershipStatus.HeldByAnother;
        if (isHeldByForeign)
        {
            _logger?.Warning("[RouterSession] Disconnect: session ownership held by foreign process; refusing to stop foreign session.");
            _ownsConnection = false;
            TransitionState(SessionStates.Error, "conflict");
            throw new RouterException("conflict", "Cannot disconnect: ownership is held by another process.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == SessionStates.Disconnected)
            {
                return;
            }

            TransitionState(SessionStates.Disconnecting, null);
            var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);

            // Bounded stop cannot return disconnected while process remains
            if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
            {
                TransitionState(SessionStates.Error, "stop_failed");
                throw new RouterException("stop_failed", "Engine stop failed or timed out; process remains active.");
            }

            _ownsConnection = false;
            TransitionState(SessionStates.Disconnected, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
            _logger?.Warning("[RouterSession] Disconnect failed: {Message}", scrubbed);
            TransitionState(SessionStates.Error, "stop_failed");
            throw new RouterException("stop_failed", scrubbed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _retainedReadinessGuard = null;
            _connectedPid = null;
        }

        try
        {
            _activeConnectCts?.Cancel();
            _engine.StatusChanged -= OnEngineStatusChanged;
            _engine.Connected -= OnEngineConnected;
            _engine.Warning -= OnEngineWarning;

            // Ensure no non-owner disposal can stop foreign session
            if (_ownsConnection)
            {
                var ownershipCheck = _ownershipProbe != null ? _ownershipProbe() : LinuxOwnershipGuard.CheckOwnership(_logger);
                var isHeldByForeign = ownershipCheck.Status == OwnershipStatus.HeldByAnother;
                if (!isHeldByForeign)
                {
                    var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);
                    if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
                    {
                        TransitionState(SessionStates.Error, "stop_failed");
                        // Do not call _engine.DisposeAsync() when BoundedTeardown timed out / failed,
                        // to avoid running Stop() twice overlapping.
                    }
                    else
                    {
                        _ownsConnection = false;
                        TransitionState(SessionStates.Disconnected, null);
                        await _engine.DisposeAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger?.Information("[RouterSession] DisposeAsync: Session is held by another process; avoiding stop of foreign engine.");
                    _ownsConnection = false;
                    await _engine.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning("[RouterSession] Error during DisposeAsync: {Message}", BoundedTeardown.SanitizeExceptionMessage(ex));
        }
        finally
        {
            var hasLivePid = _engine.IsRunning || _engine.SingBoxPid != null;
            var isForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
            if (hasLivePid && !isForeign)
            {
                TransitionState(SessionStates.Error, _errorCode ?? "stop_failed");
            }
            else if (_state != SessionStates.Error)
            {
                TransitionState(SessionStates.Disconnected, null);
            }
            _gate.Dispose();
        }
    }

    private async Task TeardownAfterStartFailureAsync(string errorCode, string errorMessage)
    {
        var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);
        if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
        {
            TransitionState(SessionStates.Error, "stop_failed");
            throw new RouterException("stop_failed", "Teardown failed after startup failure; process remains active.");
        }

        _ownsConnection = false;
        TransitionState(SessionStates.Error, errorCode);
        throw new RouterException(errorCode, errorMessage);
    }

    private async Task TeardownOnCancelAsync(CancellationToken ct)
    {
        var stopped = await BoundedTeardown.StopBoundedAsync(_engine, timeout: _stopTimeout, logger: _logger).ConfigureAwait(false);
        if (!stopped || _engine.IsRunning || _engine.SingBoxPid != null)
        {
            TransitionState(SessionStates.Error, "stop_failed");
            throw new RouterException("stop_failed", "Teardown failed after cancellation; process remains active.");
        }

        _ownsConnection = false;
        TransitionState(SessionStates.Disconnected, null);
        ct.ThrowIfCancellationRequested();
        throw new RouterException("cancelled", "Connection attempt was cancelled.");
    }

    private void OnEngineStatusChanged(string status)
    {
        _logger?.Debug("[RouterSession] Engine status changed: {Status}", status);

        if (string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            if (_state == SessionStates.Connected)
            {
                // Retain _ownsConnection so ongoing recovery or subsequent disconnect/teardown can manage the owned session
                TransitionState(SessionStates.Error, "connection_lost");
            }
            else if (_state == SessionStates.Disconnecting)
            {
                _ownsConnection = false;
                TransitionState(SessionStates.Disconnected, null);
            }
        }
        else if (status.StartsWith("Apply failed:", StringComparison.OrdinalIgnoreCase))
        {
            if (_state == SessionStates.Connected)
            {
                TransitionState(SessionStates.Error, "apply_failed");
            }
        }
    }

    private void OnEngineConnected(int pid)
    {
        _logger?.Debug("[RouterSession] Engine Connected event received for PID {Pid}", pid);

        // 1. No promotion during disposed
        if (_disposed)
        {
            return;
        }

        // 2. Never resurrect during disconnected or disconnecting; avoid second recovery after disconnect started
        if (_state == SessionStates.Disconnected || _state == SessionStates.Disconnecting || _disconnectStarted)
        {
            return;
        }

        // 3. No promotion during start cancellation or active connecting
        if (_activeConnectCts?.IsCancellationRequested == true || _state == SessionStates.Connecting)
        {
            return;
        }

        // 4. Recovery promotion is only valid for an owned session
        if (!_ownsConnection)
        {
            return;
        }

        // Recovery typed events may revalidate from Error OR internal Connected whose prior guard invalidated
        bool canRevalidate;
        lock (_stateLock)
        {
            canRevalidate = _state == SessionStates.Error || (_state == SessionStates.Connected && !EvaluateRetainedGuard());
        }
        if (!canRevalidate)
        {
            return;
        }

        // 5. Foreign sessions are not owned by this instance
        var isForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
        if (isForeign)
        {
            return;
        }

        // 6. Typed event promotes ONLY:
        //    - pid > 0
        //    - engine running
        //    - exact owned identity (matching SingBoxPid)
        if (pid <= 0 || !_engine.IsRunning || _engine.SingBoxPid != pid)
        {
            return;
        }

        // 7. Event-time readiness guard capture and evaluation fail-closed (guard exceptions => false)
        Func<bool>? guard = null;
        try
        {
            var rawGuard = _engine.CaptureReadinessGuard(pid);
            if (rawGuard != null)
            {
                guard = () =>
                {
                    try
                    {
                        return rawGuard();
                    }
                    catch
                    {
                        return false;
                    }
                };
            }
        }
        catch
        {
            guard = null;
        }

        bool guardPassed = false;
        if (guard != null)
        {
            try
            {
                guardPassed = guard();
            }
            catch
            {
                guardPassed = false;
            }
        }

        // 8. Commit with flag synchronization and race prevention:
        // Recheck all flags under lock before committing state.
        // Disconnect race during guard callback cannot resurrect.
        bool commitConnected = false;
        lock (_stateLock)
        {
            if (_disposed || _disconnectStarted || _state == SessionStates.Disconnecting || _state == SessionStates.Disconnected || _activeConnectCts?.IsCancellationRequested == true || !_ownsConnection)
            {
                return;
            }

            if (guardPassed)
            {
                _retainedReadinessGuard = guard;
                _connectedPid = pid;
                commitConnected = true;
            }
            else
            {
                _retainedReadinessGuard = null;
                _connectedPid = null;
            }
        }

        if (commitConnected)
        {
            TransitionState(SessionStates.Connected, null);
        }
        else
        {
            TransitionState(SessionStates.Error, "connect_failed");
        }
    }

    private void OnEngineWarning(string warning)
    {
        _logger?.Warning("[RouterSession] Engine warning: {Warning}", BoundedTeardown.SanitizeExceptionMessage(new Exception(warning)));
    }

    private void TransitionState(string newState, string? errorCode)
    {
        Action? changedHandler = null;
        lock (_stateLock)
        {
            // Never claim disconnected with live pid of an owned or non-foreign engine; preserve error state
            if (newState == SessionStates.Disconnected && (_engine.IsRunning || _engine.SingBoxPid != null))
            {
                var isForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
                if (!isForeign)
                {
                    newState = SessionStates.Error;
                    errorCode ??= _errorCode ?? "stop_failed";
                }
            }

            if (newState == SessionStates.Connected)
            {
                if (_disposed || _disconnectStarted || _state == SessionStates.Disconnecting || _state == SessionStates.Disconnected || _activeConnectCts?.IsCancellationRequested == true || !_ownsConnection)
                {
                    // Disconnect or disposal started concurrent with promotion commit; fail closed
                    _retainedReadinessGuard = null;
                    _connectedPid = null;
                    return;
                }
            }
            else
            {
                _retainedReadinessGuard = null;
                _connectedPid = null;
            }

            if (_state != newState || _errorCode != errorCode)
            {
                _state = newState;
                _errorCode = errorCode;
                changedHandler = Changed;
            }
            else if (newState == SessionStates.Connected)
            {
                // Revalidation into Connected even if internal _state was already Connected
                changedHandler = Changed;
            }
        }

        changedHandler?.Invoke();
    }
}
