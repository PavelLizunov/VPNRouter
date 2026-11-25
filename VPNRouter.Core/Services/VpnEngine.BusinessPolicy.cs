using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed class BusinessPolicySession
{
    private readonly object _gate = new();
    private BusinessPolicySnapshot? _acknowledged;
    private int _hasRun;
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal CancellationTokenSource Cancellation { get; }
    internal CancellationToken Token { get; }
    internal bool HasRun { get => Volatile.Read(ref _hasRun) != 0; set => Volatile.Write(ref _hasRun, value ? 1 : 0); }
    internal BusinessPolicySession(CancellationTokenSource cancellation)
    {
        Cancellation = cancellation;
        Token = cancellation.Token;
    }

    internal bool CanApply(BusinessPolicySnapshot? candidate)
    {
        lock (_gate) return CanFollow(candidate, _acknowledged);
    }

    internal bool TryAcknowledge(BusinessPolicySnapshot candidate)
    {
        lock (_gate)
        {
            if (!CanFollow(candidate, _acknowledged)) return false;
            _acknowledged = candidate;
            return true;
        }
    }

    internal static bool CanFollow(BusinessPolicySnapshot? candidate, BusinessPolicySnapshot? floor) =>
        floor == null || (candidate != null && candidate.PolicyId == floor.PolicyId &&
            (candidate.Revision > floor.Revision ||
             (candidate.Revision == floor.Revision && candidate.PayloadDigest == floor.PayloadDigest)) &&
            (floor.Phone == null || (candidate.Phone != null && candidate.Phone.PolicyId == floor.Phone.PolicyId &&
                (candidate.Phone.Revision > floor.Phone.Revision ||
                 (candidate.Phone.Revision == floor.Phone.Revision &&
                  candidate.Phone.PayloadDigest == floor.Phone.PayloadDigest)))));
}

internal sealed class BusinessPolicyAppliedReceipt
{
    internal string PolicyId => Launch.Policy.PolicyId;
    internal long Revision => Launch.Policy.Revision;
    internal string PayloadDigest => Launch.Policy.PayloadDigest;
    internal string EffectiveDigest => Launch.Policy.EffectiveDigest;
    internal string ConfigurationDigest => Launch.ConfigurationDigest;
    internal Guid SessionId => Session.SessionId;
    internal Guid LaunchId => Launch.LaunchId;
    internal int ProcessId { get; }
    internal BusinessPolicySession Session { get; }
    internal SingBoxManager Manager { get; }
    internal BusinessPolicyLaunch Launch { get; }

    internal BusinessPolicyAppliedReceipt(BusinessPolicySession session, SingBoxManager manager,
        BusinessPolicyLaunch launch)
    {
        Session = session;
        Manager = manager;
        Launch = launch;
        ProcessId = launch.Handle!.Pid;
    }
}

public partial class VpnEngine
{
    private BusinessPolicySession? _policySession;
    private BusinessPolicyAppliedReceipt? _policyReceipt;

    internal event Action<BusinessPolicyAppliedReceipt>? BusinessPolicyApplied;

    internal BusinessPolicySession? CapturePolicySession()
    {
        var session = Volatile.Read(ref _policySession);
        // A reconnect can temporarily have no core. Retain that same previously
        // running session, but never acquire a replacement after a user Stop.
        return session != null && session.HasRun && IsCurrentPolicySession(session) ? session : null;
    }

    private bool IsCurrentPolicySession(BusinessPolicySession session) => !_disposed &&
        ReferenceEquals(Volatile.Read(ref _policySession), session) &&
        ReferenceEquals(_sessionCts, session.Cancellation) && !session.Token.IsCancellationRequested;

    internal bool IsPolicyReceiptCurrent(BusinessPolicyAppliedReceipt receipt) =>
        IsCurrentPolicySession(receipt.Session) && ReferenceEquals(_singBox, receipt.Manager) &&
        ReferenceEquals(Volatile.Read(ref _policyReceipt), receipt) &&
        receipt.Manager.IsPolicyLaunchCurrent(receipt.Launch);

    private void ObservePolicyAcceptance(SingBoxManager manager, BusinessPolicyLaunch launch,
        CancellationTokenSource? sessionSource)
    {
        var session = Volatile.Read(ref _policySession);
        if (session == null || !IsCurrentPolicySession(session) ||
            !ReferenceEquals(session.Cancellation, sessionSource) || !ReferenceEquals(_singBox, manager) ||
            !manager.IsPolicyLaunchCurrent(launch) || !session.TryAcknowledge(launch.Policy)) return;
        session.HasRun = true;
        var receipt = new BusinessPolicyAppliedReceipt(session, manager, launch);
        Volatile.Write(ref _policyReceipt, receipt);
        if (IsPolicyReceiptCurrent(receipt)) BusinessPolicyApplied?.Invoke(receipt);
    }

    internal async Task<BusinessPolicyAppliedReceipt?> ApplyPolicyAsync(AppSettings settings,
        BusinessPolicySession expectedSession, CancellationToken ct = default)
    {
        if (settings.BusinessPolicy == null || !IsCurrentPolicySession(expectedSession)) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expectedSession.Token);
        var acquired = false;
        try
        {
            await _lifecycleGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            if (!IsCurrentPolicySession(expectedSession)) return null;
            linked.Token.ThrowIfCancellationRequested();
            var manager = _singBox;
            if (manager == null || !await manager.WaitForPolicyReadyAsync(linked.Token).ConfigureAwait(false) ||
                !IsCurrentPolicySession(expectedSession) || !ReferenceEquals(_singBox, manager)) return null;
            // The desktop retains its manual preference even when this session uses
            // a different, verified outbound. A policy refresh changes routing only.
            if (_failoverSettingsContext is { } running)
            {
                settings = CopySessionSettings(settings);
                settings.Vless = CopySessionSettings(running).Vless;
            }
            if (!await ApplyGatedAsync(settings, linked.Token, forceRestart: true).ConfigureAwait(false)) return null;
            var receipt = Volatile.Read(ref _policyReceipt);
            return receipt != null && ReferenceEquals(receipt.Session, expectedSession) &&
                receipt.PolicyId == settings.BusinessPolicy.PolicyId && receipt.Revision == settings.BusinessPolicy.Revision &&
                receipt.EffectiveDigest == settings.BusinessPolicy.EffectiveDigest && IsPolicyReceiptCurrent(receipt)
                ? receipt : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
        finally
        {
            if (acquired)
                try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void AbortFailedPolicyRestart()
    {
        try { _sessionCts?.Cancel(); } catch (ObjectDisposedException) { }
        _failoverGeneration++;
        _failover = null;
        Volatile.Write(ref _policyReceipt, null);
        TeardownInternal();
    }
}
