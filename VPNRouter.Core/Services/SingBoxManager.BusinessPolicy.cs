namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    private BusinessPolicySnapshot? _businessPolicy;
    private BusinessPolicySnapshot? _acknowledgedBusinessPolicy;
    private CancellationToken _businessPolicyLifetime;
    private CancellationToken _policyOperationCancellation;
    private BusinessPolicyLaunch? _policyLaunch;
    private string? _sourceConfigJson;
    private bool _policySpawnAttempted;
    private static BusinessPolicyLaunch? s_diagnosticPolicyLaunch;
    private static readonly object s_policyDiagnosticGate = new();

    internal bool HasBusinessPolicy => Volatile.Read(ref _businessPolicy) != null;
    internal static string DiagnosticLogPath => Volatile.Read(ref s_diagnosticPolicyLaunch)?.LogPath ?? AppPaths.SingBoxLogPath;

    internal static void PreservePolicyDiagnosticLog()
    {
        lock (s_policyDiagnosticGate)
        {
            var launch = s_diagnosticPolicyLaunch;
            if (launch == null) return;
            try { AppPaths.WritePrivateText(AppPaths.SingBoxLogPath, launch.ReadRedactedDiagnosticTail()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { /* Diagnostics remain best effort; never substitute raw policy output. */ }
        }
    }

    internal event Action<BusinessPolicyLaunch>? PolicyConfigurationAccepted;

    internal void StartWithPolicyJson(string configJson, BusinessPolicySnapshot policy, CancellationToken ct)
    {
        var runtime = EffectivePolicy;
        using var scope = SingBoxRuntimePolicy.EnterScope(runtime);
        runtime?.Authorize(SingBoxRuntimeOperation.Start);
        lock (_lifecycleGate)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) return;
            if (State == SingBoxState.Starting || _handle is { HasExited: false }) return;
            if (!CanApplyBusinessPolicy(policy))
                throw new InvalidOperationException("A newer Business policy was already applied.");
            _businessPolicy = policy;
            _businessPolicyLifetime = ct;
            StartWithJsonCore(configJson);
        }
    }

    internal bool ReloadPolicyJsonWithResult(string configJson, BusinessPolicySnapshot policy,
        CancellationToken sessionToken, CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            ct.ThrowIfCancellationRequested();
            sessionToken.ThrowIfCancellationRequested();
            if (!CanApplyBusinessPolicy(policy) || Volatile.Read(ref _disposed) != 0 ||
                !_ownsTunLock || _exactStopUnconfirmed) return false;
            _businessPolicy = policy;
            _businessPolicyLifetime = sessionToken;
            _policyOperationCancellation = ct;
            try { return ReloadConfigJsonCore(configJson, forceRestart: true); }
            finally { _policyOperationCancellation = default; }
        }
    }

    internal bool IsPolicyLaunchCurrent(BusinessPolicyLaunch launch) =>
        Volatile.Read(ref _disposed) == 0 && !_businessPolicyLifetime.IsCancellationRequested &&
        ReferenceEquals(Volatile.Read(ref _policyLaunch), launch) &&
        State == SingBoxState.Running && launch.Acknowledged && launch.IsOwnedLive(_handle);

    private void PreparePolicyLaunch()
    {
        if (_businessPolicy == null) return;
        ThrowIfPolicyCanceled();
        if (_sourceConfigJson == null) throw new InvalidOperationException("Business launch has no configuration.");
        var launch = new BusinessPolicyLaunch(_sourceConfigJson, _businessPolicy, AppPaths.ConfigDir);
        _policyLaunch = launch;
        _currentConfigPath = launch.ConfigPath;
        lock (s_policyDiagnosticGate) Volatile.Write(ref s_diagnosticPolicyLaunch, launch);
    }

    private void AcknowledgePolicyLaunch(IProcessHandle handle)
    {
        if (_businessPolicy == null) return;
        var launch = _policyLaunch ?? throw new InvalidOperationException("Business launch is missing.");
        launch.Bind(handle);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _businessPolicyLifetime, _policyOperationCancellation);
        launch.WaitForAcknowledgementAsync(() =>
                Volatile.Read(ref _disposed) == 0 && ReferenceEquals(_policyLaunch, launch) &&
                launch.IsOwnedLive(_handle), linked.Token)
            .GetAwaiter().GetResult();
        Volatile.Write(ref _acknowledgedBusinessPolicy, launch.Policy);
    }

    private bool CanApplyBusinessPolicy(BusinessPolicySnapshot policy) =>
        BusinessPolicySession.CanFollow(policy, Volatile.Read(ref _acknowledgedBusinessPolicy));

    private void NotifyPolicyAcceptance()
    {
        var launch = _policyLaunch;
        if (launch == null || !IsPolicyLaunchCurrent(launch)) return;
        try { PolicyConfigurationAccepted?.Invoke(launch); }
        catch (Exception ex) { _logger.Warning("[SingBoxManager] Business acceptance callback failed ({Kind})", ex.GetType().Name); }
    }

    private void CleanStoppedPolicyLaunch()
    {
        if (_policyLaunch == null || _exactStopUnconfirmed || _handle is { HasExited: false }) return;
        var launch = _policyLaunch;
        lock (s_policyDiagnosticGate)
        {
            if (ReferenceEquals(s_diagnosticPolicyLaunch, launch))
            {
                PreservePolicyDiagnosticLog();
                Volatile.Write(ref s_diagnosticPolicyLaunch, null);
            }
        }
        launch.Dispose();
        _policyLaunch = null;
    }

    private void HandleFailedPolicyLaunch()
    {
        if (!_policySpawnAttempted && (_handle == null || _handle.HasExited))
        {
            _exactStopUnconfirmed = false;
            State = SingBoxState.Stopped;
            CleanStoppedPolicyLaunch();
            ReleaseTunOwnership();
        }
        else if (_handle == null)
        {
            // The runner was entered but did not return ownership. It may have
            // failed after spawn; no exact-handle evidence authorizes release.
            _exactStopUnconfirmed = true;
            State = SingBoxState.Failed;
        }
        else StopInternal(releaseLock: true);
    }

    internal async Task<bool> WaitForPolicyReadyAsync(CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0) return false;
                if (Monitor.TryEnter(_lifecycleGate))
                {
                    try
                    {
                        if (State == SingBoxState.Running && _handle is { HasExited: false }) return true;
                        // A known crash retains the lease while health recovery
                        // waits its backoff. The captured session may replace
                        // that exited core once, without waiting for old rules.
                        return State == SingBoxState.Failed && _handle is { HasExited: true } &&
                            _ownsTunLock && !_exactStopUnconfirmed && !_businessPolicyLifetime.IsCancellationRequested;
                    }
                    finally { Monitor.Exit(_lifecycleGate); }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private void ThrowIfPolicyCanceled()
    {
        if (_businessPolicy == null) return;
        _businessPolicyLifetime.ThrowIfCancellationRequested();
        _policyOperationCancellation.ThrowIfCancellationRequested();
    }
}
