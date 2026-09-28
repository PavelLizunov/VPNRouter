#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.App.ViewModels.Internals;

namespace VPNRouter.Tests;

public sealed class MvmTwoPhaseStartTimerTests
{
    private sealed class FakeEngineEvents
    {
        public int StartedSubscriptions { get; private set; }
        public int StartedUnsubscriptions { get; private set; }
        public int ConnectedSubscriptions { get; private set; }
        public int ConnectedUnsubscriptions { get; private set; }

        private Action<int>? _startedHandler;
        private Action<int>? _connectedHandler;

        public Action SubscribeStarted(Action<int> handler)
        {
            StartedSubscriptions++;
            _startedHandler = handler;
            return () =>
            {
                StartedUnsubscriptions++;
                _startedHandler = null;
            };
        }

        public Action SubscribeConnected(Action<int> handler)
        {
            ConnectedSubscriptions++;
            _connectedHandler = handler;
            return () =>
            {
                ConnectedUnsubscriptions++;
                _connectedHandler = null;
            };
        }

        public void FireStarted(int pid) => _startedHandler?.Invoke(pid);
        public void FireConnected(int pid) => _connectedHandler?.Invoke(pid);
    }

    private static (Task task, TaskCompletionSource<bool> control) ControlledTask()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (tcs.Task, tcs);
    }

    [Fact]
    public async Task PhaseA_SingBoxStartsBefore60s_ProceedsToPhaseB()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);
        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task PhaseA_NoSingBoxIn60s_PhaseATimeout()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(200);
        var phaseB = TimeSpan.FromMilliseconds(2000);

        var start = DateTime.UtcNow;
        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(TwoPhaseStartOutcome.PhaseATimeout, outcome);
        var lowerBound = phaseA - TimeSpan.FromMilliseconds(20);
        Assert.True(elapsed >= lowerBound,
            $"Expected at least {lowerBound} elapsed (= phaseA {phaseA} - 20ms grace), got {elapsed}");
        Assert.True(elapsed < phaseA + TimeSpan.FromSeconds(2),
            $"Expected at most {phaseA + TimeSpan.FromSeconds(2)} elapsed, got {elapsed}");
    }

    [Fact]
    public async Task PhaseB_ConnectedFiresBefore20s_ReturnsConnected()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(400);
        var phaseB = TimeSpan.FromMilliseconds(400);
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(100, ct);
        fake.FireStarted(54321);

        await Task.Delay(100, ct);
        fake.FireConnected(54321);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);
    }

    [Fact]
    public async Task PhaseB_NoConnectedIn20s_PhaseBTimeout()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(300);
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(99999);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);
    }

    [Fact]
    public async Task PreCancelled_ReturnsCancelled()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);

        Assert.Equal(TwoPhaseStartOutcome.Cancelled, outcome);

        Assert.Equal(fake.StartedSubscriptions, fake.StartedUnsubscriptions);
        Assert.Equal(fake.ConnectedSubscriptions, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task StartTaskFaultsBeforeEvents_ReturnsStartTaskCompleted()
    {
        var fake = new FakeEngineEvents();
        var startTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var ct = TestContext.Current.CancellationToken;
        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTcs.Task,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        startTcs.TrySetException(new InvalidOperationException("conflicting VPN"));

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await startTcs.Task);
        Assert.Equal("conflicting VPN", ex.Message);
    }

    [Fact]
    public async Task SubscriptionsUnhookedOnTimeout()
    {
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromMilliseconds(100),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public void DefaultBudgets_Are60sPhaseA_And20sPhaseB()
    {
        Assert.Equal(60, (int)TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
        Assert.Equal(20, (int)TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
    }

    [Fact]
    public async Task Started_ThenCleanStartCompletion_LaterConnectedSucceeds()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        await Task.Delay(50, ct);
        startTcs.TrySetResult(true);

        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task CleanStartCompletion_ThenStarted_LaterConnectedSucceeds()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        startTcs.TrySetResult(true);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task CleanCompleted_NoStarted_TimesOutPhaseA_NotSuccess()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();

        startTcs.TrySetResult(true);

        var phaseA = TimeSpan.FromMilliseconds(200);
        var phaseB = TimeSpan.FromMilliseconds(2000);

        var start = DateTime.UtcNow;
        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(TwoPhaseStartOutcome.PhaseATimeout, outcome);
        var lowerBound = phaseA - TimeSpan.FromMilliseconds(20);
        Assert.True(elapsed >= lowerBound,
            $"Expected at least {lowerBound} elapsed, got {elapsed}");

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task NoConnected_PhaseBTimeout_OriginalDeadlineNotReset()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(300);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        var phaseBStart = DateTime.UtcNow;

        await Task.Delay(100, ct);
        startTcs.TrySetResult(true);

        var outcome = await coordinatorTask;
        var phaseBElapsed = DateTime.UtcNow - phaseBStart;

        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);

        var lowerBound = phaseB - TimeSpan.FromMilliseconds(30);
        Assert.True(phaseBElapsed >= lowerBound,
            $"Expected Phase B elapsed at least {lowerBound}, got {phaseBElapsed}");
        Assert.True(phaseBElapsed < phaseB + TimeSpan.FromMilliseconds(180),
            $"Timer must not reset upon clean completion; expected < 480ms, got {phaseBElapsed}");

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task NIGHT07_DeterministicRegression_PhaseBZeroBudget_ReturnsPhaseBTimeout()
    {
        var startedSubscriptions = 0;
        var startedUnsubscriptions = 0;
        var connectedSubscriptions = 0;
        var connectedUnsubscriptions = 0;

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: Task.CompletedTask,
            subscribeStarted: handler =>
            {
                startedSubscriptions++;
                handler(12345);
                return () => startedUnsubscriptions++;
            },
            subscribeConnected: _ =>
            {
                connectedSubscriptions++;
                return () => connectedUnsubscriptions++;
            },
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);
        Assert.Equal(1, startedSubscriptions);
        Assert.Equal(1, startedUnsubscriptions);
        Assert.Equal(1, connectedSubscriptions);
        Assert.Equal(1, connectedUnsubscriptions);
    }

    [Fact]
    public async Task FaultCancelPrompt_PhaseB_ReturnsStartTaskCompletedPromptly()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(10),
            phaseBBudget: TimeSpan.FromSeconds(10),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        await Task.Delay(50, ct);
        var faultStart = DateTime.UtcNow;
        startTcs.TrySetException(new InvalidOperationException("warmup failed"));

        var outcome = await coordinatorTask;
        var faultElapsed = DateTime.UtcNow - faultStart;

        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        Assert.True(faultElapsed < TimeSpan.FromSeconds(2),
            $"Expected prompt return after fault, took {faultElapsed}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await startTask);
        Assert.Equal("warmup failed", ex.Message);
    }

    [Fact]
    public async Task InternalCancellation_Prompt_ReturnsStartTaskCompleted()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(10),
            phaseBBudget: TimeSpan.FromSeconds(10),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        await Task.Delay(50, ct);
        var cancelStart = DateTime.UtcNow;
        startTcs.TrySetCanceled();

        var outcome = await coordinatorTask;
        var cancelElapsed = DateTime.UtcNow - cancelStart;

        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        Assert.True(cancelElapsed < TimeSpan.FromSeconds(2),
            $"Expected prompt return after internal cancel, took {cancelElapsed}");
    }

    [Fact]
    public async Task ExternalCancellation_PhaseB_ReturnsCancelled()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();

        using var cts = new CancellationTokenSource();

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);

        await Task.Delay(50);
        fake.FireStarted(12345);

        await Task.Delay(50);
        startTcs.TrySetResult(true);

        await Task.Delay(50);
        cts.Cancel();

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Cancelled, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    [Fact]
    public async Task Race_StartedAlreadyFired_PrioritizesEvent_WhenClean()
    {
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        startTcs.TrySetResult(true);

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: handler =>
            {
                handler(12345);
                return () => { };
            },
            subscribeConnected: handler =>
            {
                handler(12345);
                return () => { };
            },
            phaseABudget: TimeSpan.FromSeconds(1),
            phaseBBudget: TimeSpan.FromSeconds(1),
            cancellationToken: ct);

        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);
    }

    [Fact]
    public async Task Race_StartedAlreadyFired_PrioritizesFault_WhenTaskFaulted()
    {
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        startTcs.TrySetException(new InvalidOperationException("race fault"));

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: handler =>
            {
                handler(12345);
                return () => { };
            },
            subscribeConnected: _ => () => { },
            phaseABudget: TimeSpan.FromSeconds(1),
            phaseBBudget: TimeSpan.FromSeconds(1),
            cancellationToken: ct);

        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await startTask);
        Assert.Equal("race fault", ex.Message);
    }

    [Theory]
    [InlineData("Connected")]
    [InlineData("PhaseATimeout")]
    [InlineData("PhaseBTimeout")]
    [InlineData("StartTaskFault")]
    [InlineData("Cancelled")]
    public async Task SubscriptionsRemoved_AllOutcomes(string scenario)
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        using var cts = new CancellationTokenSource();

        Task<TwoPhaseStartOutcome> coordinatorTask;

        switch (scenario)
        {
            case "Connected":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), cts.Token);
                fake.FireStarted(123);
                fake.FireConnected(123);
                await coordinatorTask;
                break;

            case "PhaseATimeout":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), cts.Token);
                await coordinatorTask;
                break;

            case "PhaseBTimeout":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), cts.Token);
                fake.FireStarted(123);
                await coordinatorTask;
                break;

            case "StartTaskFault":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token);
                startTcs.TrySetException(new InvalidOperationException("fault"));
                await coordinatorTask;
                break;

            case "Cancelled":
                cts.Cancel();
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token);
                await coordinatorTask;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }
}
