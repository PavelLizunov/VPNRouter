#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.App.ViewModels.Internals;

internal enum TwoPhaseStartOutcome
{
    Connected,

    StartTaskCompleted,

    PhaseATimeout,

    PhaseBTimeout,

    Cancelled,
}

internal static class TwoPhaseStartCoordinator
{
    internal static readonly TimeSpan DefaultPhaseABudget = TimeSpan.FromSeconds(60);

    internal static readonly TimeSpan DefaultPhaseBBudget = TimeSpan.FromSeconds(20);

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

        var startedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var unsubStarted = subscribeStarted(pid => startedTcs.TrySetResult(pid));
        var unsubConnected = subscribeConnected(pid => connectedTcs.TrySetResult(pid));

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return TwoPhaseStartOutcome.Cancelled;

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

                    if (startedTcs.Task.IsCompletedSuccessfully)
                    {
                        if (startTask.IsFaulted || (startTask.IsCanceled && !cancellationToken.IsCancellationRequested))
                            return TwoPhaseStartOutcome.StartTaskCompleted;

                    }
                    else if (phaseAResult == phaseADelay)
                    {
                        return TwoPhaseStartOutcome.PhaseATimeout;
                    }
                    else if (phaseAResult == startTask)
                    {
                        if (startTask.IsFaulted || (startTask.IsCanceled && !cancellationToken.IsCancellationRequested))
                            return TwoPhaseStartOutcome.StartTaskCompleted;

                        var secondAResult = await Task.WhenAny(startedTcs.Task, connectedTcs.Task, phaseADelay)
                            .ConfigureAwait(false);

                        if (cancellationToken.IsCancellationRequested)
                            return TwoPhaseStartOutcome.Cancelled;

                        if (connectedTcs.Task.IsCompletedSuccessfully)
                            return TwoPhaseStartOutcome.Connected;

                        if (startedTcs.Task.IsCompletedSuccessfully)
                        {
                        }
                        else if (secondAResult == phaseADelay)
                        {
                            return TwoPhaseStartOutcome.PhaseATimeout;
                        }
                    }
                }
                finally
                {
                    try { phaseACts.Cancel(); } catch {  }
                }
            }

            if (connectedTcs.Task.IsCompletedSuccessfully)
                return TwoPhaseStartOutcome.Connected;

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
                    try { phaseBCts.Cancel(); } catch {  }
                }
            }
        }
        finally
        {
            try { unsubStarted?.Invoke(); } catch {  }
            try { unsubConnected?.Invoke(); } catch {  }
        }
    }
}
