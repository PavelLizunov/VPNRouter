#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Headless.Lifecycle;

public enum TwoPhaseOutcome
{
    Connected,
    PhaseATimeout,
    PhaseBTimeout,
    ReadinessGuardFailed,
    Cancelled,
    Failed
}

public readonly record struct TwoPhaseResult(
    TwoPhaseOutcome Outcome,
    int? Pid = null,
    string? ErrorMessage = null,
    Func<bool>? ReadinessGuard = null);

/// <summary>
/// Coordinates the two-phase start lifecycle:
/// Phase A: Process launch (SingBoxStarted event) within budget.
/// Phase B: TUN warmup and typed routability confirmation (Connected event) within budget.
/// True typed Connected readiness is enforced via engine-scoped readiness guard evaluation.
/// </summary>
public static class TwoPhaseConnectCoordinator
{
    public static readonly TimeSpan DefaultPhaseABudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultPhaseBBudget = TimeSpan.FromSeconds(20);

    public static Task<TwoPhaseResult> RunAsync(
        Task startTask,
        ILifecycleEngine engine,
        CancellationToken cancellationToken,
        TimeSpan? phaseABudget = null,
        TimeSpan? phaseBBudget = null)
        => RunAsyncInternal(null, startTask, engine, cancellationToken, phaseABudget, phaseBBudget);

    public static Task<TwoPhaseResult> RunAsync(
        Func<Task> startAction,
        ILifecycleEngine engine,
        CancellationToken cancellationToken,
        TimeSpan? phaseABudget = null,
        TimeSpan? phaseBBudget = null)
        => RunAsyncInternal(startAction, null, engine, cancellationToken, phaseABudget, phaseBBudget);

    private static async Task<TwoPhaseResult> RunAsyncInternal(
        Func<Task>? startAction,
        Task? startTask,
        ILifecycleEngine engine,
        CancellationToken cancellationToken,
        TimeSpan? phaseABudget,
        TimeSpan? phaseBBudget)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (startAction == null && startTask == null)
            throw new ArgumentNullException(nameof(startAction));

        var budgetA = phaseABudget ?? DefaultPhaseABudget;
        var budgetB = phaseBBudget ?? DefaultPhaseBBudget;

        var startedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedTcs = new TaskCompletionSource<(int Pid, Func<bool>? Guard)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStarted(int pid) => startedTcs.TrySetResult(pid);

        void OnConnected(int pid)
        {
            Func<bool>? capturedGuard = null;
            try
            {
                var rawGuard = engine.CaptureReadinessGuard(pid);
                if (rawGuard != null)
                {
                    capturedGuard = () =>
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
                capturedGuard = null;
            }

            connectedTcs.TrySetResult((pid, capturedGuard));
        }

        // Subscribe BEFORE starting startTask to avoid event-before-subscription race
        engine.SingBoxStarted += OnStarted;
        engine.Connected += OnConnected;

        if (engine.SingBoxPid is { } existingPid && existingPid > 0)
        {
            startedTcs.TrySetResult(existingPid);
        }

        try
        {
            startTask ??= startAction!();

            // ─── Phase A: Wait for SingBoxStarted ─────────────────────────────
            using var phaseACts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            phaseACts.CancelAfter(budgetA);

            var phaseATimer = Task.Delay(budgetA, phaseACts.Token);
            while (!startedTcs.Task.IsCompleted)
            {
                var phaseACompleted = await Task.WhenAny(startedTcs.Task, startTask, phaseATimer).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);

                if (phaseACompleted == phaseATimer)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                    return new TwoPhaseResult(TwoPhaseOutcome.PhaseATimeout, null, "sing-box launch timed out");
                }

                if (phaseACompleted == startTask)
                {
                    if (startTask.IsFaulted)
                    {
                        var ex = startTask.Exception?.GetBaseException();
                        return new TwoPhaseResult(TwoPhaseOutcome.Failed, null, BoundedTeardown.SanitizeExceptionMessage(ex));
                    }
                    if (startTask.IsCanceled || cancellationToken.IsCancellationRequested)
                    {
                        return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                    }
                    // startTask completed cleanly without error; continue waiting for SingBoxStarted
                    var waitStarted = await Task.WhenAny(startedTcs.Task, phaseATimer).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                        return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                    if (waitStarted == phaseATimer)
                        return new TwoPhaseResult(TwoPhaseOutcome.PhaseATimeout, null, "sing-box launch timed out");
                }
            }

            // ─── Phase B: Wait for Connected ─────────────────────────────────
            using var phaseBCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            phaseBCts.CancelAfter(budgetB);

            var phaseBTimer = Task.Delay(budgetB, phaseBCts.Token);
            Task phaseBCompleted;
            if (!startTask.IsCompleted)
            {
                phaseBCompleted = await Task.WhenAny(connectedTcs.Task, startTask, phaseBTimer).ConfigureAwait(false);
            }
            else
            {
                phaseBCompleted = await Task.WhenAny(connectedTcs.Task, phaseBTimer).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
                return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);

            if (phaseBCompleted == startTask)
            {
                if (startTask.IsFaulted)
                {
                    var ex = startTask.Exception?.GetBaseException();
                    return new TwoPhaseResult(TwoPhaseOutcome.Failed, null, BoundedTeardown.SanitizeExceptionMessage(ex));
                }
                if (startTask.IsCanceled || cancellationToken.IsCancellationRequested)
                {
                    return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                }
                // startTask completed cleanly; wait for connected or timer
                var waitConnected = await Task.WhenAny(connectedTcs.Task, phaseBTimer).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                if (waitConnected == phaseBTimer)
                {
                    return new TwoPhaseResult(TwoPhaseOutcome.PhaseBTimeout, null, "TUN warmup and route confirmation timed out");
                }
            }

            if (phaseBCompleted == phaseBTimer)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);
                return new TwoPhaseResult(TwoPhaseOutcome.PhaseBTimeout, null, "TUN warmup and route confirmation timed out");
            }

            if (connectedTcs.Task.IsCompletedSuccessfully)
            {
                var (pid, guard) = await connectedTcs.Task.ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    return new TwoPhaseResult(TwoPhaseOutcome.Cancelled);

                // Enforce true typed readiness via engine-scoped guard captured at typed event (fails closed on null or false)
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

                if (!guardPassed)
                {
                    return new TwoPhaseResult(TwoPhaseOutcome.ReadinessGuardFailed, pid, "Engine readiness guard validation failed", guard);
                }

                return new TwoPhaseResult(TwoPhaseOutcome.Connected, pid, null, guard);
            }

            return new TwoPhaseResult(TwoPhaseOutcome.Failed, null, "Start completed without Connected signal");
        }
        finally
        {
            engine.SingBoxStarted -= OnStarted;
            engine.Connected -= OnConnected;
        }
    }
}
