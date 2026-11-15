using System.ComponentModel;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using PolicyStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private BusinessPolicyCoordinator? _businessPolicy;
    private IReadOnlyList<CustomRule>? _businessRules;
    private bool _businessPolicyStartupBlocked;
    private bool _businessPolicyApplyActive;
    private bool _businessPolicyStopRequested;
    private CancellationTokenSource? _businessPolicyApplyCancellation;
    private Task _businessPolicyApplyTask = Task.CompletedTask;
    private (BusinessPolicySnapshot Policy, BusinessPolicySession Session)? _businessPolicyPendingApply;
    private BusinessPolicyAppliedReceipt? _businessPolicyApplied;
    private BusinessPolicyAppliedReceipt? _businessPolicyAppliedHistory;

    internal event Action? BusinessPolicyStatusChanged;
    internal long? AcceptedBusinessPolicyRevision => _businessPolicy?.Current?.Revision;
    internal BusinessPolicyAppliedReceipt? AppliedBusinessPolicy =>
        _businessPolicyApplied is { } receipt && _engine.IsPolicyReceiptCurrent(receipt) ? receipt : null;
    internal BusinessPolicyAppliedReceipt? HistoricalBusinessPolicy => _businessPolicyAppliedHistory;

    internal string BusinessPolicyStatusText
    {
        get
        {
            if (_businessPolicyStartupBlocked) return PolicyStrings.BusinessPolicyRecoveryRequired;
            if (_businessPolicy == null) return string.Empty;
            if (_businessPolicyApplyActive) return PolicyStrings.BusinessPolicyApplying;
            if (_businessPolicy.IsUnavailable) return PolicyStrings.BusinessPolicyRefreshUnavailable;
            if (_businessPolicy.Current is not { } accepted) return PolicyStrings.BusinessPolicyEmbedded;
            return AppliedBusinessPolicy is { } applied && applied.Revision == accepted.Revision &&
                applied.PayloadDigest == accepted.PayloadDigest
                ? PolicyStrings.BusinessPolicyApplied : PolicyStrings.BusinessPolicyPending;
        }
    }

    private void InitializeBusinessPolicy(BusinessPolicyCoordinator? policy)
    {
        _businessRules = BusinessBootstrap.ReadRules();
        _businessPolicy = policy;
        if (policy == null) return;
        policy.Accepted += OnBusinessPolicyAccepted;
        policy.AvailabilityChanged += OnBusinessPolicyAvailabilityChanged;
        _engine.BusinessPolicyApplied += OnBusinessPolicyApplied;
        PropertyChanged += OnBusinessPolicyLifecycleChanged;
    }

    // Called by bootstrap/save/start owners on the UI thread. No engine session
    // ever receives this mutable UI instance once Business routing is enabled.
    internal AppSettings CaptureBusinessConnectionSettings()
        => CaptureBusinessConnectionSettings(_businessPolicy?.Current);

    internal AppSettings CaptureBusinessConnectionSettings(BusinessPolicySnapshot? policy)
    {
        if (_businessPolicyStartupBlocked) throw new BusinessPolicyStartupException();
        return _businessRules == null ? _settings :
            BusinessPolicySettings.Capture(_settings, _businessRules, policy);
    }

    private void EnforceBusinessPolicySettings()
    {
        if (_businessRules != null)
            BusinessPolicySettings.EnforceRules(_settings, _businessRules, _businessPolicy?.Current);
    }

    private void OnBusinessPolicyAccepted(BusinessPolicySnapshot policy)
    {
        // Capture at receipt, before dispatch. A later connection must never
        // become the target of an update received while the VPN was stopped.
        var session = policy.ApplyNow ? _engine.CapturePolicySession() : null;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _businessPolicy?.Current is not { } latest ||
                latest.Revision != policy.Revision || latest.PayloadDigest != policy.PayloadDigest) return;
            _businessPolicyPendingApply = session == null ? null : (policy, session);
            EnforceBusinessPolicySettings();
            NotifyBusinessPolicyStatus();
            TryApplyPendingBusinessPolicy();
        });
    }

    private void OnBusinessPolicyAvailabilityChanged(bool unavailable) =>
        Dispatcher.UIThread.Post(() => { if (!_disposed) NotifyBusinessPolicyStatus(); });

    private void OnBusinessPolicyApplied(BusinessPolicyAppliedReceipt receipt) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !_engine.IsPolicyReceiptCurrent(receipt)) return;
            _businessPolicyApplied = _businessPolicyAppliedHistory = receipt;
            NotifyBusinessPolicyStatus();
        });

    private void OnBusinessPolicyLifecycleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IsConnected) or nameof(IsConnecting) or nameof(IsApplying))) return;
        if (_disposed) return;
        if (AppliedBusinessPolicy == null) _businessPolicyApplied = null;
        NotifyBusinessPolicyStatus();
        TryApplyPendingBusinessPolicy();
    }

    private void OnBusinessPolicyUpdateStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateVm.IsDownloading)) TryApplyPendingBusinessPolicy();
    }

    private void TryApplyPendingBusinessPolicy()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(TryApplyPendingBusinessPolicy);
            return;
        }
        if (_disposed || _businessPolicyPendingApply is not { } pending ||
            _businessPolicyApplyActive || _businessPolicyStopRequested ||
            IsConnecting || IsApplying || _isReconnecting || UpdateVm?.IsDownloading == true) return;
        if (_businessPolicy?.Current is not { } latest || latest.Revision != pending.Policy.Revision ||
            latest.PayloadDigest != pending.Policy.PayloadDigest)
        {
            _businessPolicyPendingApply = null;
            return;
        }
        if (AppliedBusinessPolicy is { } applied && applied.Revision == latest.Revision &&
            applied.PayloadDigest == latest.PayloadDigest)
        {
            _businessPolicyPendingApply = null;
            return;
        }

        // One attempt per received revision/session. A failed attempt remains
        // pending for an explicit connection instead of reconnecting in a loop.
        _businessPolicyPendingApply = null;
        AppSettings captured;
        try { captured = CaptureBusinessConnectionSettings(pending.Policy); }
        catch { NotifyBusinessPolicyStatus(); return; }
        _businessPolicyApplyActive = true;
        IsApplying = true;
        var cancellation = new CancellationTokenSource();
        _businessPolicyApplyCancellation = cancellation;
        var apply = Task.Run(() => _engine.ApplyPolicyAsync(captured, pending.Session, cancellation.Token));
        _businessPolicyApplyTask = apply;
        _ = CompleteBusinessPolicyApplyAsync(apply, cancellation);
        NotifyBusinessPolicyStatus();
    }

    private async Task CompleteBusinessPolicyApplyAsync(Task<BusinessPolicyAppliedReceipt?> apply,
        CancellationTokenSource cancellation)
    {
        BusinessPolicyAppliedReceipt? receipt = null;
        try { receipt = await apply.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { _logger.Warning("[Business] Policy application was not confirmed"); }
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_businessPolicyApplyCancellation, cancellation)) return;
            _businessPolicyApplyCancellation = null;
            var cancelled = cancellation.IsCancellationRequested;
            cancellation.Dispose();
            if (_disposed) return;
            _businessPolicyApplyActive = false;
            if (receipt != null && !_businessPolicyStopRequested && _engine.IsPolicyReceiptCurrent(receipt))
            {
                _businessPolicyApplied = _businessPolicyAppliedHistory = receipt;
                RestoreConnectedStatus();
                RefreshActiveIndicator();
            }
            else if (!cancelled && !_businessPolicyStopRequested)
                StatusText = PolicyStrings.BusinessPolicyApplyFailed;
            IsApplying = false;
            NotifyBusinessPolicyStatus();
            TryApplyPendingBusinessPolicy();
        });
    }

    private async Task StopBusinessPolicyApplicationAsync()
    {
        if (_businessPolicyStopRequested) return;
        _businessPolicyStopRequested = true;
        _businessPolicyPendingApply = null;
        _businessPolicyApplyCancellation?.Cancel();
        IsConnecting = true;
        StatusText = PolicyStrings.Stopping;
        try { await Task.Run(_engine.Stop); }
        finally
        {
            _businessPolicyStopRequested = false;
            IsConnected = false;
            IsConnecting = false;
            ConnectButtonText = PolicyStrings.StartVPN;
            StatusText = PolicyStrings.NotConnected;
            _lastSuccessfulConnectAt = DateTime.MinValue;
            NotifyBusinessPolicyStatus();
        }
    }

    private void NotifyBusinessPolicyStatus() => BusinessPolicyStatusChanged?.Invoke();

    private void DisposeBusinessPolicy()
    {
        PropertyChanged -= OnBusinessPolicyLifecycleChanged;
        UpdateVm.PropertyChanged -= OnBusinessPolicyUpdateStateChanged;
        _engine.BusinessPolicyApplied -= OnBusinessPolicyApplied;
        _businessPolicyPendingApply = null;
        _businessPolicyApplyCancellation?.Cancel();
        if (_businessPolicy != null)
        {
            _businessPolicy.Accepted -= OnBusinessPolicyAccepted;
            _businessPolicy.AvailabilityChanged -= OnBusinessPolicyAvailabilityChanged;
            _businessPolicy.Dispose();
        }
        // These tasks never wait for the UI dispatcher. Drain before disposing
        // transport/process-operation resources or letting app shutdown proceed.
        try { Task.WhenAll(_businessPolicyApplyTask, _businessPolicy?.Completion ?? Task.CompletedTask).GetAwaiter().GetResult(); }
        catch { _logger.Debug("[Business] Policy work cancelled during shutdown"); }
        _businessPolicyApplyCancellation?.Dispose();
        _businessPolicyApplyCancellation = null;
        BusinessPolicyStatusChanged = null;
    }
}
