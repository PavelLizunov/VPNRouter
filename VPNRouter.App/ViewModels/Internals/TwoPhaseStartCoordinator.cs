#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.App.ViewModels.Internals;

/// <summary>
/// Task #41 Stage 2 (PinkuDani 2026-05-21) — Two-phase start timer for the
/// App-side <c>ToggleConnectionAsync</c> / <c>ReconnectAsync</c> /
/// <c>ApplyFreeConfigAsync</c> Start flow.
///
/// <para>Replaces the pre-Stage-2 single 60s <see cref="CancellationTokenSource"/>
/// budget with two sequential budgets keyed off two
/// <see cref="Services.VpnEngine"/> events:</para>
///
/// <list type="bullet">
///   <item><b>Phase A</b> — from invocation to the engine's
///   <c>SingBoxStarted</c> event. Budget 60s (Win10 LTSC / missing-NetAdapter
///   class hardening from Fix #1, commit <c>2f2c1a8</c>).</item>
///   <item><b>Phase B</b> — from <c>SingBoxStarted</c> to <c>Connected</c>.
///   Budget 20s (TUN warm-up + gstatic probe — sub-5s on healthy installs,
///   20s is a generous backstop for slow networks).</item>
/// </list>
///
/// <para>Stage 1 (commit <c>b012fe6</c>) added the typed
/// <c>VpnEngine.Connected</c> event fired ONLY from the warmup probe's
/// success branch — that's the unambiguous Phase B completion signal this
/// helper subscribes to.</para>
///
/// <para>The helper is intentionally decoupled from <c>VpnEngine</c> itself
/// so it can be unit-tested without spinning up the engine: callers pass in
/// the actual subscribe/unsubscribe pairs as lambdas, plus the
/// <c>StartAsync</c> task. Tests substitute simple lambdas that fire the
/// "events" against in-test <see cref="TaskCompletionSource{TResult}"/>s.</para>
/// </summary>
internal enum TwoPhaseStartOutcome
{
    /// <summary>Phase A succeeded (<c>SingBoxStarted</c> fired in budget) AND
    /// Phase B succeeded (<c>Connected</c> fired in budget). Caller should
    /// flip <c>IsConnected = true</c>.</summary>
    Connected,

    /// <summary>The <c>StartAsync</c> task returned BEFORE either event fired.
    /// Caller must <c>await</c> the original task to surface any exception
    /// (or to confirm a no-op return). Used for instant-fail paths
    /// (e.g. <c>ConflictingVpnException</c>) and for engine variants that
    /// don't emit the typed events.</summary>
    StartTaskCompleted,

    /// <summary>Phase A timeout: <c>SingBoxStarted</c> never fired within the
    /// budget. sing-box failed to spawn (firewall / wintun / netsh hang).
    /// Caller should fire <c>Stop()</c> with the Phase A diagnostic.</summary>
    PhaseATimeout,

    /// <summary>Phase B timeout: <c>SingBoxStarted</c> fired, but
    /// <c>Connected</c> never did within the budget. sing-box is running but
    /// the TUN warmup probe never confirmed connectivity (wintun driver
    /// issue, network gone). Caller should fire <c>Stop()</c> with the
    /// Phase B diagnostic.</summary>
    PhaseBTimeout,

    /// <summary>The outer <see cref="CancellationToken"/> was cancelled
    /// before either phase completed. Caller should rethrow
    /// <see cref="OperationCanceledException"/> (or let it propagate from
    /// the awaited task).</summary>
    Cancelled,
}

internal static class TwoPhaseStartCoordinator
{
    /// <summary>
    /// Production default: 60s Phase A budget. See
    /// <c>MainWindowViewModel.cs:3785</c> comment block for the rationale —
    /// Windows 10 LTSC / Server SKUs without the NetAdapter PowerShell
    /// module pay ~1s per <c>Remove-NetAdapter</c> CommandNotFoundException
    /// and the cycle has 5+ such call sites, so 30s wasn't enough.
    /// </summary>
    internal static readonly TimeSpan DefaultPhaseABudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Production default: 20s Phase B budget. The TUN warmup probe
    /// (<c>StartupPipeline.ScheduleWarmupProbe</c>) loops up to 15
    /// attempts with ~1s delay; happy-path completion is sub-5s on a
    /// normal install. 20s gives a generous backstop for slow connections
    /// without making a real driver hang feel locked-up.
    /// </summary>
    internal static readonly TimeSpan DefaultPhaseBBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Wait for the two-phase start sequence to complete, race against
    /// per-phase budgets and the outer cancellation token, and return an
    /// outcome enum that the caller maps to UI state / diagnostic logging.
    ///
    /// <para>Contract:</para>
    /// <list type="bullet">
    ///   <item>The <paramref name="startTask"/> represents the work
    ///   <c>_engine.StartAsync(...)</c> does. It may complete BEFORE either
    ///   event fires (instant-fail paths like
    ///   <c>ConflictingVpnException</c>) — that's the
    ///   <see cref="TwoPhaseStartOutcome.StartTaskCompleted"/> outcome.</item>
    ///   <item>Subscriptions are attached BEFORE awaiting anything, so an
    ///   ultra-fast event can't race the helper. Unsubscriptions happen in
    ///   <c>finally</c> regardless of outcome.</item>
    ///   <item>Phase B only begins after Phase A completes. If <c>Connected</c>
    ///   fires BEFORE <c>SingBoxStarted</c> (impossible per Stage 1's
    ///   contract, but defensive) we still report
    ///   <see cref="TwoPhaseStartOutcome.Connected"/>.</item>
    ///   <item>The helper does NOT call <c>_engine.Stop()</c> on timeout —
    ///   that's the caller's responsibility (so the caller can keep all
    ///   shutdown logic in one place).</item>
    /// </list>
    /// </summary>
    /// <param name="startTask">The fire-and-forget task representing
    /// <c>_engine.StartAsync(...)</c>. Helper does NOT await this task to
    /// completion when an event fires first — the caller does
    /// (in <c>finally</c> typically).</param>
    /// <param name="subscribeStarted">Wires a handler for the engine's
    /// <c>SingBoxStarted</c> event. The returned <see cref="Action"/> is the
    /// unsubscribe lambda invoked in <c>finally</c>.</param>
    /// <param name="subscribeConnected">Wires a handler for the engine's
    /// <c>Connected</c> event. The returned <see cref="Action"/> is the
    /// unsubscribe lambda.</param>
    /// <param name="phaseABudget">Maximum time to wait for
    /// <c>SingBoxStarted</c>. Default 60s.</param>
    /// <param name="phaseBBudget">Maximum time to wait for <c>Connected</c>
    /// after <c>SingBoxStarted</c> fires. Default 20s.</param>
    /// <param name="cancellationToken">Outer cancel — typically the cycle
    /// CTS's token. Cancellation maps to
    /// <see cref="TwoPhaseStartOutcome.Cancelled"/>.</param>
    public static async Task<TwoPhaseStartOutcome> RunAsync(
        Task startTask,
        Func<Action<int>, Action> subscribeStarted,
        Func<Action<int>, Action> subscribeConnected,
        TimeSpan? phaseABudget = null,
        TimeSpan? phaseBBudget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startTask);
        ArgumentNullException.ThrowIfNull(subscribeStarted);
        ArgumentNullException.ThrowIfNull(subscribeConnected);

        var aBudget = phaseABudget ?? DefaultPhaseABudget;
        var bBudget = phaseBBudget ?? DefaultPhaseBBudget;

        // TCS pair fired by the engine event handlers. RunContinuationsAsynchronously
        // so a hot subscriber doesn't reentrantly drive Phase B from the same
        // call frame that started Phase A's WhenAny.
        var startedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe BEFORE any wait. The caller's subscribe lambda is
        // expected to attach the engine event right now; the returned
        // Action unsubscribes. Tests use trivial closures.
        var unsubStarted = subscribeStarted(pid => startedTcs.TrySetResult(pid));
        var unsubConnected = subscribeConnected(pid => connectedTcs.TrySetResult(pid));

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return TwoPhaseStartOutcome.Cancelled;

            // ── Phase A: wait for SingBoxStarted, startTask completion, or budget expiry ──
            using (var phaseACts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var phaseADelay = Task.Delay(aBudget, phaseACts.Token);
                    var phaseAResult = await Task.WhenAny(startedTcs.Task, connectedTcs.Task, startTask, phaseADelay)
                        .ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                        return TwoPhaseStartOutcome.Cancelled;

                    if (connectedTcs.Task.IsCompletedSuccessfully)
                        return TwoPhaseStartOutcome.Connected;

                    // Race: if started already fired, prioritize event unless task fault/cancel
                    if (startedTcs.Task.IsCompletedSuccessfully)
                    {
                        if (startTask.IsFaulted || (startTask.IsCanceled && !cancellationToken.IsCancellationRequested))
                            return TwoPhaseStartOutcome.StartTaskCompleted;

                        // Phase A success; proceed to Phase B.
                    }
                    else if (phaseAResult == phaseADelay)
                    {
                        // Phase A timed out. SingBoxStarted never fired.
                        return TwoPhaseStartOutcome.PhaseATimeout;
                    }
                    else if (phaseAResult == startTask)
                    {
                        if (startTask.IsFaulted || (startTask.IsCanceled && !cancellationToken.IsCancellationRequested))
                            return TwoPhaseStartOutcome.StartTaskCompleted;

                        // startTask clean completion before Started shouldn't falsely green.
                        // clean-noStarted enters wait Phase A until original deadline or typedConnected / Started.
                        var secondAResult = await Task.WhenAny(startedTcs.Task, connectedTcs.Task, phaseADelay)
                            .ConfigureAwait(false);

                        if (cancellationToken.IsCancellationRequested)
                            return TwoPhaseStartOutcome.Cancelled;

                        if (connectedTcs.Task.IsCompletedSuccessfully)
                            return TwoPhaseStartOutcome.Connected;

                        if (startedTcs.Task.IsCompletedSuccessfully)
                        {
                            // Started fired in time; proceed to Phase B.
                        }
                        else if (secondAResult == phaseADelay)
                        {
                            return TwoPhaseStartOutcome.PhaseATimeout;
                        }
                    }
                }
                finally
                {
                    try { phaseACts.Cancel(); } catch { /* idempotent */ }
                }
            }

            // Phase A succeeded. Check if Connected also already fired.
            if (connectedTcs.Task.IsCompletedSuccessfully)
                return TwoPhaseStartOutcome.Connected;

            // ── Phase B: wait for Connected, startTask completion, or budget expiry ──
            using (var phaseBCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var phaseBDelay = Task.Delay(bBudget, phaseBCts.Token);
                    var phaseBResult = await Task.WhenAny(connectedTcs.Task, startTask, phaseBDelay)
                        .ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                        return TwoPhaseStartOutcome.Cancelled;

                    if (connectedTcs.Task.IsCompletedSuccessfully)
                        return TwoPhaseStartOutcome.Connected;

                    if (phaseBResult == phaseBDelay)
                        return TwoPhaseStartOutcome.PhaseBTimeout;

                    if (phaseBResult == startTask)
                    {
                        if (startTask.IsFaulted || (startTask.IsCanceled && !cancellationToken.IsCancellationRequested))
                            return TwoPhaseStartOutcome.StartTaskCompleted;

                        // If startTask completes SUCCESSFULLY before Connected, continue waiting
                        // connected vs SAME phaseBDelay without resetting timer (not loop busycompleted task).
                        var secondBResult = await Task.WhenAny(connectedTcs.Task, phaseBDelay)
                            .ConfigureAwait(false);

                        if (cancellationToken.IsCancellationRequested)
                            return TwoPhaseStartOutcome.Cancelled;

                        if (connectedTcs.Task.IsCompletedSuccessfully)
                            return TwoPhaseStartOutcome.Connected;

                        if (secondBResult == phaseBDelay)
                            return TwoPhaseStartOutcome.PhaseBTimeout;
                    }

                    return TwoPhaseStartOutcome.Connected;
                }
                finally
                {
                    // phaseBCts canceled only AFTER final outcome, not before second wait.
                    try { phaseBCts.Cancel(); } catch { /* idempotent */ }
                }
            }
        }
        finally
        {
            // Best-effort unsubscribe. Wrapped in try/catch so a misbehaving
            // engine event surface (e.g. concurrent modification) can't
            // leak out of the coordinator.
            try { unsubStarted?.Invoke(); } catch { /* swallow */ }
            try { unsubConnected?.Invoke(); } catch { /* swallow */ }
        }
    }
}
