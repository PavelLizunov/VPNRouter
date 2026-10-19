#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless;
using VPNRouter.Headless.Lifecycle;

namespace VPNRouter.Headless.Tests;

/// <summary>
/// Dependency-free lifecycle test suite for RouterSession and Omarchy Protocol v1 lifecycle.
/// Exercises true typed Connected readiness, two-phase timeouts, cancellation, bounded teardown,
/// secret sanitization, Linux ownership conflicts, and truthful capability gating.
/// </summary>
public static class LifecycleChecks
{
    public static async Task RunAsync()
    {
        Console.WriteLine("[LifecycleChecks] Starting dependency-free lifecycle verification...");

        await CheckInitialStateAsync();
        Console.WriteLine("  ✓ CheckInitialState passed");

        await CheckConnectSuccessWithTypedReadinessAsync();
        Console.WriteLine("  ✓ CheckConnectSuccessWithTypedReadiness passed");

        await CheckReadinessGuardFailureAsync();
        Console.WriteLine("  ✓ CheckReadinessGuardFailure passed");

        await CheckConnectCancellationAsync();
        Console.WriteLine("  ✓ CheckConnectCancellation passed");

        await CheckPhaseATimeoutAsync();
        Console.WriteLine("  ✓ CheckPhaseATimeout passed");

        await CheckPhaseBTimeoutAsync();
        Console.WriteLine("  ✓ CheckPhaseBTimeout passed");

        await CheckEngineErrorSecretSanitizationAsync();
        Console.WriteLine("  ✓ CheckEngineErrorSecretSanitization passed");

        await CheckDisconnectAsync();
        Console.WriteLine("  ✓ CheckDisconnect passed");

        await CheckApplyAsync();
        Console.WriteLine("  ✓ CheckApply passed");

        await CheckBusyConcurrencyGuardAsync();
        Console.WriteLine("  ✓ CheckBusyConcurrencyGuard passed");

        await CheckLinuxOwnershipGuardConflictAsync();
        Console.WriteLine("  ✓ CheckLinuxOwnershipGuardConflict passed");

        await CheckUnsupportedUnsafeStateRefusalAsync();
        Console.WriteLine("  ✓ CheckUnsupportedUnsafeStateRefusal passed");

        await CheckDisposeAsync();
        Console.WriteLine("  ✓ CheckDispose passed");

        await CheckBoundedStopCannotReturnDisconnectedWhileProcessRemainsAsync();
        Console.WriteLine("  ✓ CheckBoundedStopCannotReturnDisconnectedWhileProcessRemains passed");

        await CheckBoundedStopTimeoutThrowsAndDoesNotReturnDisconnectedAsync();
        Console.WriteLine("  ✓ CheckBoundedStopTimeoutThrowsAndDoesNotReturnDisconnected passed");

        await CheckNonOwnerDisposalDoesNotStopForeignSessionAsync();
        Console.WriteLine("  ✓ CheckNonOwnerDisposalDoesNotStopForeignSession passed");

        await CheckNonOwnerDisconnectDoesNotStopForeignSessionAsync();
        Console.WriteLine("  ✓ CheckNonOwnerDisconnectDoesNotStopForeignSession passed");

        await CheckUnknownOwnershipFailsClosedAsync();
        Console.WriteLine("  ✓ CheckUnknownOwnershipFailsClosed passed");

        await CheckPersistentEventSubscriptionsPreventStaleConnectedAsync();
        Console.WriteLine("  ✓ CheckPersistentEventSubscriptionsPreventStaleConnected passed");

        await CheckOngoingRecoveryRestoresConnectedStateAsync();
        Console.WriteLine("  ✓ CheckOngoingRecoveryRestoresConnectedState passed");

        await CheckLinuxCapabilityUnverifiedFailsClosedAsync();
        Console.WriteLine("  ✓ CheckLinuxCapabilityUnverifiedFailsClosed passed");

        await CheckReadinessGuardDirectCallWithoutFallbackAsync();
        Console.WriteLine("  ✓ CheckReadinessGuardDirectCallWithoutFallback passed");

        await CheckSeparateProcessLockContentionAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockContention passed");

        await CheckSeparateProcessLockReleaseAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockRelease passed");

        await CheckSeparateProcessLockCrashAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockCrash passed");

        await CheckSeparateProcessLockSymlinkAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockSymlink passed");

        await CheckSeparateProcessLockFifoAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockFifo passed");

        await CheckSeparateProcessLockInvalidPermissionsAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockInvalidPermissions passed");

        await CheckSeparateProcessLockDisposedAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockDisposed passed");

        await CheckSeparateProcessLockUnknownAsync();
        Console.WriteLine("  ✓ CheckSeparateProcessLockUnknown passed");

        await CheckLockNeverReachesProductionPathAsync();
        Console.WriteLine("  ✓ CheckLockNeverReachesProductionPath passed");

        await CheckRecoveryStringOnlyStaysErrorAsync();
        Console.WriteLine("  ✓ CheckRecoveryStringOnlyStaysError passed");

        await CheckRecoveryTypedNullAndFalseGuardStaysErrorAsync();
        Console.WriteLine("  ✓ CheckRecoveryTypedNullAndFalseGuardStaysError passed");

        await CheckRecoveryTypedTrueRestoresAsync();
        Console.WriteLine("  ✓ CheckRecoveryTypedTrueRestores passed");

        await CheckTypedEventNeverResurrectsDuringDisconnectedDisposedOrDisconnectingAsync();
        Console.WriteLine("  ✓ CheckTypedEventNeverResurrectsDuringDisconnectedDisposedOrDisconnecting passed");

        await CheckCrashRestartWithoutStoppedAndRecoveryAsync();
        Console.WriteLine("  ✓ CheckCrashRestartWithoutStoppedAndRecovery passed");

        await CheckDisconnectRaceDuringGuardCallbackCannotResurrectAsync();
        Console.WriteLine("  ✓ CheckDisconnectRaceDuringGuardCallbackCannotResurrect passed");

        await CheckInitialCaptureInvalidatedBetweenEventAndContinuationFailsAsync();
        Console.WriteLine("  ✓ CheckInitialCaptureInvalidatedBetweenEventAndContinuationFails passed");

        await CheckTwoPhaseCoordinatorInitialCaptureInvalidatedAsync();
        Console.WriteLine("  ✓ CheckTwoPhaseCoordinatorInitialCaptureInvalidated passed");

        await CheckGuardExceptionsFailClosedAsync();
        Console.WriteLine("  ✓ CheckGuardExceptionsFailClosed passed");

        await CheckErrorRetryRequiresCapabilityAsync();
        Console.WriteLine("  CheckErrorRetryRequiresCapability passed");
        Console.WriteLine("[LifecycleChecks] All 41 lifecycle checks passed successfully.");
    }

    private static async Task CheckErrorRetryRequiresCapabilityAsync()
    {
        bool available = true;
        int starts = 0;
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = _ =>
            {
                starts++;
                throw new InvalidOperationException("Simulated startup failure");
            }
        };
        await using var session = new RouterSession(fake, () => OwnershipCheckResult.Free(),
            () => true, capabilityReadinessProbe: () => available);
        try { await session.ConnectAsync(new AppSettings(), CancellationToken.None); }
        catch (RouterException ex) when (ex.Code == "connect_failed") { }
        AssertEqual(SessionStates.Error, session.State, "First failed start must establish error state");
        AssertEqual(1, starts, "First attempt must reach fake engine exactly once");
        available = false;
        bool refused = false;
        try { await session.ConnectAsync(new AppSettings(), CancellationToken.None); }
        catch (RouterException ex) when (ex.Code == "unavailable") { refused = true; }
        AssertTrue(refused, "Retry from error must refuse unavailable environment");
        AssertEqual(1, starts, "Unavailable retry must never invoke the engine");
    }

    private static Task CheckInitialStateAsync()
    {
        var fake = new FakeLifecycleEngine();
        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => false);

        AssertEqual(SessionStates.Disconnected, session.State, "Initial state must be disconnected");
        AssertEqual(null, session.ErrorCode, "Initial ErrorCode must be null");
        AssertTrue(session.CanConnect, "CanConnect must be true when free and disconnected");
        AssertFalse(session.SupportsKillSwitch, "SupportsKillSwitch must be false when unverified probe returns false");

        // Explicit injected capability readiness check: when readiness probe returns false, CanConnect must fail closed
        var unreadyFake = new FakeLifecycleEngine { CapabilityReadinessFunc = () => false };
        var unreadySession = new RouterSession(unreadyFake, () => OwnershipCheckResult.Free(), () => false);
        AssertFalse(unreadySession.CanConnect, "CanConnect must be false when injected capability readiness func returns false");

        // Direct constructor probe override
        var constructorUnreadySession = new RouterSession(
            fake,
            () => OwnershipCheckResult.Free(),
            () => false,
            capabilityReadinessProbe: () => false);
        AssertFalse(constructorUnreadySession.CanConnect, "CanConnect must be false when explicit readiness probe returns false");

        return Task.CompletedTask;
    }

    private static async Task CheckConnectSuccessWithTypedReadinessAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    f.FireSingBoxStarted(4321);
                    await Task.Delay(20);
                    f.FireConnected(4321);
                });
            },
            ReadinessGuardFactory = pid => () => pid == 4321
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        var stateChanges = new List<string>();
        session.Changed += () => stateChanges.Add(session.State);

        var settings = new AppSettings();
        await session.ConnectAsync(settings, CancellationToken.None);

        AssertEqual(SessionStates.Connected, session.State, "Session must transition to connected");
        AssertEqual(null, session.ErrorCode, "ErrorCode must be null after successful connect");
        AssertTrue(stateChanges.Contains(SessionStates.Connecting), "Must transition through connecting");
        AssertTrue(stateChanges.Contains(SessionStates.Connected), "Must transition to connected");
    }

    private static async Task CheckReadinessGuardFailureAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    f.FireSingBoxStarted(5555);
                    await Task.Delay(20);
                    f.FireConnected(5555);
                });
            },
            // Guard fails (e.g. process exited or warmup confirmed was false)
            ReadinessGuardFactory = pid => () => false
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        var settings = new AppSettings();

        bool threw = false;
        try
        {
            await session.ConnectAsync(settings, CancellationToken.None);
        }
        catch (RouterException ex)
        {
            threw = true;
            AssertEqual("connect_failed", ex.Code, "Expected connect_failed on guard failure");
        }

        AssertTrue(threw, "ConnectAsync must throw on readiness guard failure");
        AssertEqual(SessionStates.Error, session.State, "Session must transition to error state");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed");
        AssertTrue(fake.StopCalled, "Engine Stop must be called on readiness guard failure");
    }

    private static async Task CheckConnectCancellationAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    f.FireSingBoxStarted(1111);
                });
            }
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        bool threw = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or RouterException { Code: "cancelled" })
        {
            threw = true;
        }

        AssertTrue(threw, "ConnectAsync must abort on cancellation");
        AssertEqual(SessionStates.Disconnected, session.State, "Session state must return to disconnected");
        AssertTrue(fake.StopCalled, "Engine Stop must be called on cancellation");
    }

    private static async Task CheckPhaseATimeoutAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            // Hangs without firing SingBoxStarted
            OnStartAction = _ => { }
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150); // backstop

        bool threw = false;
        try
        {
            // Run coordinator directly with tiny budget for fast test execution
            var task = Task.Run(() => fake.StartAsync(new AppSettings(), CancellationToken.None));
            var outcome = await TwoPhaseConnectCoordinator.RunAsync(
                task,
                fake,
                CancellationToken.None,
                phaseABudget: TimeSpan.FromMilliseconds(40),
                phaseBBudget: TimeSpan.FromMilliseconds(40));

            AssertEqual(TwoPhaseOutcome.PhaseATimeout, outcome.Outcome, "Must yield PhaseATimeout");
            threw = true;
        }
        catch
        {
            threw = false;
        }

        AssertTrue(threw, "Coordinator must report PhaseATimeout when SingBoxStarted does not fire");
    }

    private static async Task CheckPhaseBTimeoutAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10);
                    f.FireSingBoxStarted(2222);
                    // Never fires Connected
                });
            }
        };

        var task = Task.Run(() => fake.StartAsync(new AppSettings(), CancellationToken.None));
        var outcome = await TwoPhaseConnectCoordinator.RunAsync(
            task,
            fake,
            CancellationToken.None,
            phaseABudget: TimeSpan.FromMilliseconds(50),
            phaseBBudget: TimeSpan.FromMilliseconds(30));

        AssertEqual(TwoPhaseOutcome.PhaseBTimeout, outcome.Outcome, "Must yield PhaseBTimeout when Connected does not fire");
    }

    private static async Task CheckEngineErrorSecretSanitizationAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = _ =>
            {
                throw new InvalidOperationException(
                    "Failure at vless://secret-token@secret-server.com:443?uuid=48b1fc50-9d04-4b5b-95eb-37e0ffbd8ef1#secret");
            }
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        bool threw = false;

        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex)
        {
            threw = true;
            AssertEqual("connect_failed", ex.Code, "Expected connect_failed code");
            AssertFalse(ex.Message.Contains("secret-token"), "Exception message must not contain secret token");
            AssertFalse(ex.Message.Contains("48b1fc50-9d04-4b5b-95eb-37e0ffbd8ef1"), "Exception message must not contain UUID");
        }

        AssertTrue(threw, "ConnectAsync must throw on engine failure");
        AssertEqual(SessionStates.Error, session.State, "State must be Error");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed");
    }

    private static async Task CheckDisconnectAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(3333);
                f.FireConnected(3333);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Should be connected before disconnect");

        fake.StopCalled = false;
        await session.DisconnectAsync(CancellationToken.None);

        AssertEqual(SessionStates.Disconnected, session.State, "State must be disconnected after DisconnectAsync");
        AssertTrue(fake.StopCalled, "Engine Stop must be called on disconnect");

        // Idempotent second call
        await session.DisconnectAsync(CancellationToken.None);
        AssertEqual(SessionStates.Disconnected, session.State, "State must remain disconnected");
    }

    private static async Task CheckApplyAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(7777);
                f.FireConnected(7777);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);

        // Apply when disconnected must fail
        bool threwWhenDisconnected = false;
        try
        {
            await session.ApplyAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex)
        {
            threwWhenDisconnected = true;
            AssertEqual("invalid_request", ex.Code, "Apply when disconnected must be invalid_request");
        }
        AssertTrue(threwWhenDisconnected, "ApplyAsync must throw when not connected");

        // Connect first
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);

        // Apply when connected must succeed
        await session.ApplyAsync(new AppSettings(), CancellationToken.None);
        AssertTrue(fake.ApplyCalled, "Engine ApplyAsync must be called");
    }

    private static async Task CheckBusyConcurrencyGuardAsync()
    {
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                entered.TrySetResult(true);
                startGate.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                f.FireSingBoxStarted(4321);
                f.FireConnected(4321);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);

        var firstConnect = Task.Run(() => session.ConnectAsync(new AppSettings(), CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bool secondThrewBusy = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "busy")
        {
            secondThrewBusy = true;
        }

        startGate.TrySetResult(true);
        try { await firstConnect; } catch { }

        AssertTrue(secondThrewBusy, "Concurrent ConnectAsync must throw RouterException('busy')");
    }

    private static async Task CheckLinuxOwnershipGuardConflictAsync()
    {
        var fake = new FakeLifecycleEngine();
        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.HeldByAnother(9876, "VPNRouter.App", "Active GUI session holds TUN"),
            () => true);

        AssertFalse(session.CanConnect, "CanConnect must be false when another owner holds the session");

        bool threwConflict = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            threwConflict = true;
        }

        AssertTrue(threwConflict, "ConnectAsync must reject with conflict when another owner is active");
        AssertEqual(SessionStates.Error, session.State, "Session must transition to error on conflict");
        AssertEqual("conflict", session.ErrorCode, "ErrorCode must be conflict");
    }

    private static async Task CheckUnsupportedUnsafeStateRefusalAsync()
    {
        var fake = new FakeLifecycleEngine();
        // Probe says killswitch is NOT supported
        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.Free(),
            () => false);

        var directory = Directory.CreateTempSubdirectory("vpnrouter-killswitch-test-");
        try
        {
            var profilePath = Path.Combine(directory.FullName, "profiles.json");
            await File.WriteAllTextAsync(profilePath,
                """{"profiles":[{"name":"RequiresKillSwitch","processes":[],"block_on_vpn_fail":true,"dns_mode":"vpn_only"}]}""");
            var settings = new AppSettings();
            settings.App.RoutingMode = "full";
            settings.ActiveProfile = "RequiresKillSwitch";
            settings.ProfileSources = new List<ProfileSource>
            {
                new() { Type = "local", Path = profilePath }
            };
            fake.OnStartAction = _ => throw new InvalidOperationException("Unsupported protection must be rejected before start");

            bool threwUnavailable = false;
            try
            {
                await session.ConnectAsync(settings, CancellationToken.None);
            }
            catch (RouterException ex) when (ex.Code == "unavailable")
            {
                threwUnavailable = true;
            }

            AssertTrue(threwUnavailable, "ConnectAsync must refuse unsupported unsafe killswitch configuration");
            AssertEqual(SessionStates.Error, session.State, "State must reflect error/refusal");
            AssertEqual("unavailable", session.ErrorCode, "ErrorCode must be unavailable");
        }
        finally
        {
            await session.DisposeAsync();
            directory.Delete(recursive: true);
        }
    }

    private static async Task CheckDisposeAsync()
    {
        var fake = new FakeLifecycleEngine();
        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);

        await session.DisposeAsync();

        AssertTrue(fake.DisposeCalled, "Engine must be disposed when session is disposed");
        AssertFalse(session.CanConnect, "CanConnect must be false when disposed");

        bool threwDisposed = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            threwDisposed = true;
        }

        AssertTrue(threwDisposed, "Connecting disposed session must throw ObjectDisposedException");
    }

    private static async Task CheckBoundedStopCannotReturnDisconnectedWhileProcessRemainsAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(5566);
                f.FireConnected(5566);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected before disconnect test");

        // Simulate engine stop failure: process remains active (e.g. sing-box hung or refused SIGTERM)
        fake.StopFailsToStop = true;

        bool threwStopFailed = false;
        try
        {
            await session.DisconnectAsync(CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "stop_failed")
        {
            threwStopFailed = true;
        }

        AssertTrue(threwStopFailed, "DisconnectAsync must throw RouterException('stop_failed') when engine stop fails");
        AssertEqual(SessionStates.Error, session.State, "Session must transition to Error, NEVER Disconnected while process remains");
        AssertEqual("stop_failed", session.ErrorCode, "ErrorCode must be stop_failed");
    }

    private static async Task CheckBoundedStopTimeoutThrowsAndDoesNotReturnDisconnectedAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            IsRunning = true,
            SingBoxPid = 7788,
            StopHangs = true
        };

        var stopped = await BoundedTeardown.StopBoundedAsync(fake, timeout: TimeSpan.FromMilliseconds(30));
        AssertFalse(stopped, "StopBoundedAsync must return false when stop exceeds budget");
        AssertTrue(fake.IsRunning, "Engine must still report IsRunning when stop hung");

        // 1. Session wrapping an unowned/local engine with live PID must report Error, NEVER Disconnected
        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.Free(),
            () => true,
            stopTimeout: TimeSpan.FromMilliseconds(30));

        AssertEqual(SessionStates.Error, session.State, "Session must report Error, never Disconnected with live PID");
        AssertEqual("stop_failed", session.ErrorCode, "ErrorCode must be stop_failed with live PID");

        // Disposing session with live PID must preserve Error state, never claim Disconnected
        await session.DisposeAsync();
        AssertEqual(SessionStates.Error, session.State, "Disposed session with live PID must preserve Error state, never claim Disconnected");
        AssertEqual("stop_failed", session.ErrorCode, "ErrorCode must remain stop_failed");

        // 2. An actively owned session where stop times out during disposal:
        //    Must transition to Error (stop_failed), must NOT claim Disconnected,
        //    and must NOT call Stop() twice overlapping!
        var ownedFake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(7799);
                f.FireConnected(7799);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var ownedSession = new RouterSession(
            ownedFake,
            () => OwnershipCheckResult.Free(),
            () => true,
            stopTimeout: TimeSpan.FromMilliseconds(30));

        await ownedSession.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, ownedSession.State, "Owned session must be connected");

        // Now simulate stop hang during disposal
        ownedFake.StopHangs = true;
        await ownedSession.DisposeAsync();

        AssertEqual(SessionStates.Error, ownedSession.State, "Disposed owned session on stop timeout must report Error, never Disconnected");
        AssertEqual("stop_failed", ownedSession.ErrorCode, "ErrorCode must be stop_failed on stop timeout");
        AssertEqual(1, ownedFake.StopCallCount, "Stop must NOT be called twice overlapping on timeout");
    }

    private static async Task CheckNonOwnerDisposalDoesNotStopForeignSessionAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            IsRunning = true,
            SingBoxPid = 9999
        };

        // Ownership probe says TUN/session is held by another process
        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.HeldByAnother(9999, "VPNRouter.App", "Foreign active GUI holds session"),
            () => true);

        // Disposing an unowned session must NOT call engine.Stop()
        await session.DisposeAsync();

        AssertFalse(fake.StopCalled, "Non-owner disposal must NOT call Stop on foreign session");
        AssertTrue(fake.DisposeCalled, "Engine.Dispose must still be invoked on session disposal");
        AssertTrue(fake.IsRunning, "Foreign engine must remain running untouched");
    }

    private static async Task CheckNonOwnerDisconnectDoesNotStopForeignSessionAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            IsRunning = true,
            SingBoxPid = 8888
        };

        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.HeldByAnother(8888, "VPNRouter.App", "Foreign active GUI holds session"),
            () => true);

        // Attempt connect -> rejected with conflict
        bool threwConflict = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            threwConflict = true;
        }
        AssertTrue(threwConflict, "ConnectAsync must reject with conflict on foreign ownership");
        AssertEqual(SessionStates.Error, session.State, "State must be Error");

        // Now attempt Disconnect on this unowned session
        fake.StopCalled = false;
        await session.DisconnectAsync(CancellationToken.None);

        AssertFalse(fake.StopCalled, "Non-owner disconnect must NOT call Stop on foreign session");
        AssertTrue(fake.IsRunning, "Foreign engine must remain running untouched");
        AssertEqual(SessionStates.Disconnected, session.State, "Session state can cleanly reset to disconnected");
    }

    private static async Task CheckUnknownOwnershipFailsClosedAsync()
    {
        var fake = new FakeLifecycleEngine();
        var session = new RouterSession(
            fake,
            () => OwnershipCheckResult.Unavailable("Corrupt or unverified runtime-owner.json record"),
            () => true);

        AssertFalse(session.CanConnect, "CanConnect must fail closed when ownership status is Unavailable");

        bool threwUnavailable = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "unavailable")
        {
            threwUnavailable = true;
        }

        AssertTrue(threwUnavailable, "ConnectAsync must throw RouterException('unavailable') on unknown ownership");
        AssertEqual(SessionStates.Unavailable, session.State, "Session state must be Unavailable");
        AssertFalse(fake.StopCalled, "Engine must not be started or stopped when ownership is unknown");
    }

    private static async Task CheckPersistentEventSubscriptionsPreventStaleConnectedAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(6677);
                f.FireConnected(6677);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Engine experiences unexpected crash or external stop
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");

        AssertEqual(SessionStates.Error, session.State, "Persistent event subscription must transition session to Error on crash; no stale connected");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must be connection_lost");
    }

    private static async Task CheckOngoingRecoveryRestoresConnectedStateAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(1212);
                f.FireConnected(1212);
            },
            ReadinessGuardFactory = pid => () => pid == 1212 || pid == 1213
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash occurs
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");

        // AutoFailover/Recovery fires Connected event for recovered child
        fake.FireSingBoxStarted(1213);
        fake.FireConnected(1213);

        AssertEqual(SessionStates.Connected, session.State, "Persistent subscription must handle ongoing recovery and restore Connected state");
        AssertEqual(null, session.ErrorCode, "ErrorCode must be cleared on recovery");
    }

    private static Task CheckLinuxCapabilityUnverifiedFailsClosedAsync()
    {
        // On Linux, unverified killswitch capability without checker must fail closed (no Environment.UserName root guess)
        if (OperatingSystem.IsLinux())
        {
            var unverified = PlatformCapabilityVerifier.VerifyKillSwitchSupport(null);
            AssertFalse(unverified, "PlatformCapabilityVerifier must fail closed for unverified Linux killswitch");

            var verifiedTrue = PlatformCapabilityVerifier.VerifyKillSwitchSupport(() => true);
            AssertTrue(verifiedTrue, "Verified Linux checker returning true must be honored");

            var faultyChecker = PlatformCapabilityVerifier.VerifyKillSwitchSupport(() => throw new InvalidOperationException("crash"));
            AssertFalse(faultyChecker, "Faulty Linux checker must fail closed");
        }

        return Task.CompletedTask;
    }

    private static Task CheckReadinessGuardDirectCallWithoutFallbackAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            IsRunning = true,
            SingBoxPid = 4455,
            ReadinessGuardFactory = pid => () => false
        };

        var guard = fake.CaptureReadinessGuard(4455);
        AssertTrue(guard != null, "Guard should not be null");
        AssertFalse(guard!(), "Readiness guard returning false must evaluate to false; no IsRunning fallback");

        // Factory returning null must yield null guard; fail closed, never bless as true
        fake.ReadinessGuardFactory = null;
        var nullGuard = fake.CaptureReadinessGuard(4455);
        AssertTrue(nullGuard == null, "Readiness guard must be null when factory is null; no default true blessing");

        return Task.CompletedTask;
    }

    private static async Task CheckRecoveryStringOnlyStaysErrorAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(8101);
                f.FireConnected(8101);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash occurs -> enters Error state
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must be connection_lost");

        // String-only status events arrive (must never promote to Connected)
        fake.FireStatusChanged("Connected");
        AssertEqual(SessionStates.Error, session.State, "String 'Connected' status must NOT promote to Connected; must stay Error");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must remain connection_lost");

        fake.FireStatusChanged("Connected (PID 8102)");
        AssertEqual(SessionStates.Error, session.State, "String 'Connected (PID ...)' status must NOT promote to Connected; must stay Error");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must remain connection_lost");
    }

    private static async Task CheckRecoveryTypedNullAndFalseGuardStaysErrorAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(8201);
                f.FireConnected(8201);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash occurs
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");

        // 1. Recovery attempt with null readiness guard: stays Error
        fake.FireSingBoxStarted(8202);
        fake.ReadinessGuardFactory = _ => null;
        fake.FireConnected(8202);

        AssertEqual(SessionStates.Error, session.State, "Typed Connected with null guard must stay Error; fail-closed");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed on null guard");

        // 2. Recovery attempt with false readiness guard: stays Error
        fake.FireSingBoxStarted(8203);
        fake.ReadinessGuardFactory = _ => () => false;
        fake.FireConnected(8203);

        AssertEqual(SessionStates.Error, session.State, "Typed Connected with false guard must stay Error; fail-closed");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed on false guard");
    }

    private static async Task CheckRecoveryTypedTrueRestoresAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(8301);
                f.FireConnected(8301);
            },
            ReadinessGuardFactory = pid => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash occurs
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");

        // Typed event with positive non-null readiness guard and exact owned identity restores
        fake.FireSingBoxStarted(8302);
        fake.ReadinessGuardFactory = pid => () => pid == 8302;
        fake.FireConnected(8302);

        AssertEqual(SessionStates.Connected, session.State, "Typed Connected with positive non-null guard must restore Connected");
        AssertEqual(null, session.ErrorCode, "ErrorCode must be cleared on successful typed recovery");
    }

    private static async Task CheckTypedEventNeverResurrectsDuringDisconnectedDisposedOrDisconnectingAsync()
    {
        // 1. While Disconnected: typed event must never resurrect to Connected
        var fake1 = new FakeLifecycleEngine { ReadinessGuardFactory = _ => () => true };
        var session1 = new RouterSession(fake1, () => OwnershipCheckResult.Free(), () => true);
        AssertEqual(SessionStates.Disconnected, session1.State, "Initial state must be Disconnected");

        fake1.FireSingBoxStarted(8401);
        fake1.FireConnected(8401);
        AssertTrue(session1.State != SessionStates.Connected, "Typed Connected while Disconnected must NEVER resurrect session to Connected");

        // 2. While Disposed: typed event must never resurrect
        await session1.DisposeAsync();
        fake1.FireSingBoxStarted(8402);
        fake1.FireConnected(8402);
        AssertTrue(session1.State != SessionStates.Connected, "Typed Connected while Disposed must NEVER resurrect session to Connected");

        // 3. While Disconnecting or after Disconnect started: typed event must never resurrect (avoid second recovery)
        var fake2 = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(8403);
                f.FireConnected(8403);
            },
            ReadinessGuardFactory = _ => () => true
        };
        var session2 = new RouterSession(fake2, () => OwnershipCheckResult.Free(), () => true);
        await session2.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session2.State, "Must be connected");

        fake2.IsRunning = false;
        fake2.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session2.State, "Must be in Error state");

        fake2.StopHangs = true;
        var disconnectTask = Task.Run(() => session2.DisconnectAsync(CancellationToken.None));
        await Task.Delay(20);

        // Attempt typed event while disconnect is in flight
        fake2.FireSingBoxStarted(8404);
        fake2.FireConnected(8404);
        AssertTrue(session2.State != SessionStates.Connected, "Typed Connected during disconnect must NEVER resurrect session");

        try { await disconnectTask; } catch { }
        AssertTrue(session2.State != SessionStates.Connected, "Session must remain disconnected/error after disconnect");

        // 4. While Start Cancellation is active: typed event must never promote to Connected
        using var startCts = new CancellationTokenSource();
        startCts.Cancel();
        var fake3 = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(8405);
                f.FireConnected(8405);
            },
            ReadinessGuardFactory = _ => () => true
        };
        var session3 = new RouterSession(fake3, () => OwnershipCheckResult.Free(), () => true);
        try
        {
            await session3.ConnectAsync(new AppSettings(), startCts.Token);
        }
        catch { }
        AssertTrue(session3.State != SessionStates.Connected, "Typed Connected during start cancellation must never promote to Connected");
    }

    private static async Task CheckCrashRestartWithoutStoppedAndRecoveryAsync()
    {
        int currentGeneration = 1;
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(9101);
                f.FireConnected(9101);
            }
        };
        fake.ReadinessGuardFactory = pid =>
        {
            int capturedGen = currentGeneration;
            int capturedPid = pid;
            return () => capturedGen == currentGeneration && fake.IsRunning && fake.SingBoxPid == capturedPid;
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // 1. Crash occurs WITHOUT "Stopped" event being fired (e.g. abrupt termination/kill)
        fake.IsRunning = false;
        fake.SingBoxPid = null;
        // Notice: NO fake.FireStatusChanged("Stopped")
        AssertEqual(SessionStates.Error, session.State, "Crash without Stopped must consult retained guard and report Error; fail-closed");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must be connection_lost on crash without Stopped");

        // 2. Replacement pid IsRunning true before typed signal stays error
        fake.IsRunning = true;
        fake.SingBoxPid = 9102;
        AssertEqual(SessionStates.Error, session.State, "Replacement pid with IsRunning=true before typed signal must stay Error; don't infer IsRunning means ready");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must remain connection_lost before typed Connected signal");

        // 3. Captured old guard false even same pid reuse
        fake.SingBoxPid = 9101; // Reused same PID as generation 1
        currentGeneration = 2;   // Generation incremented; old guard (tied to gen 1) returns false
        AssertEqual(SessionStates.Error, session.State, "Captured old guard must evaluate false even with same PID reuse; must stay Error");
        AssertEqual("connection_lost", session.ErrorCode, "ErrorCode must remain connection_lost on old guard invalidation");

        // 4. Good typed confirms new generation
        fake.SingBoxPid = 9102;
        fake.FireSingBoxStarted(9102);
        fake.FireConnected(9102); // Fires typed Connected with new generation
        AssertEqual(SessionStates.Connected, session.State, "Good typed Connected signal for new generation must restore Connected state");
        AssertEqual(null, session.ErrorCode, "ErrorCode must be cleared on successful typed recovery");
    }

    private static async Task CheckDisconnectRaceDuringGuardCallbackCannotResurrectAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(9201);
                f.FireConnected(9201);
            },
            ReadinessGuardFactory = _ => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash occurs
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");

        // Recovery signal arrives with guard callback that starts disconnect concurrently
        Task? disconnectTask = null;
        fake.FireSingBoxStarted(9202);
        fake.ReadinessGuardFactory = pid => () =>
        {
            // Concurrent disconnect initiated during guard evaluation callback
            disconnectTask = session.DisconnectAsync(CancellationToken.None);
            return true; // Guard itself returns true
        };

        fake.FireConnected(9202);

        if (disconnectTask != null)
        {
            await disconnectTask;
        }

        AssertEqual(SessionStates.Disconnected, session.State, "Session must NOT resurrect to Connected when disconnect was initiated during guard callback");
    }

    private static async Task CheckInitialCaptureInvalidatedBetweenEventAndContinuationFailsAsync()
    {
        bool engineValid = true;
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    f.FireSingBoxStarted(9301);
                    await Task.Delay(20);
                    f.FireConnected(9301);
                    // Invalidate state immediately after event but before coordinator continuation
                    engineValid = false;
                });
            },
            ReadinessGuardFactory = pid => () => engineValid
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        bool threw = false;
        try
        {
            await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        }
        catch (RouterException ex) when (ex.Code == "connect_failed")
        {
            threw = true;
        }

        AssertTrue(threw, "ConnectAsync must fail when readiness guard is invalidated between event and continuation");
        AssertEqual(SessionStates.Error, session.State, "Session must transition to Error when guard is invalidated between event and continuation");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed");
    }

    private static async Task CheckTwoPhaseCoordinatorInitialCaptureInvalidatedAsync()
    {
        bool engineValid = true;
        Func<bool>? capturedGuard = null;
        var fake = new FakeLifecycleEngine
        {
            ReadinessGuardFactory = pid =>
            {
                capturedGuard = () => engineValid;
                return capturedGuard;
            }
        };

        var startTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            fake.FireSingBoxStarted(9302);
            await Task.Delay(20);
            fake.FireConnected(9302);
            // Invalidate between event and continuation
            engineValid = false;
        });

        var result = await TwoPhaseConnectCoordinator.RunAsync(startTask, fake, CancellationToken.None);

        AssertEqual(TwoPhaseOutcome.ReadinessGuardFailed, result.Outcome, "Coordinator outcome must be ReadinessGuardFailed when guard invalidated between event and continuation");
        AssertTrue(result.ReadinessGuard != null, "Result must carry the captured readiness guard");
    }

    private static async Task CheckGuardExceptionsFailClosedAsync()
    {
        var fake = new FakeLifecycleEngine
        {
            OnStartAction = f =>
            {
                f.FireSingBoxStarted(9401);
                f.FireConnected(9401);
            },
            ReadinessGuardFactory = pid => () => true
        };

        var session = new RouterSession(fake, () => OwnershipCheckResult.Free(), () => true);
        await session.ConnectAsync(new AppSettings(), CancellationToken.None);
        AssertEqual(SessionStates.Connected, session.State, "Must be connected initially");

        // Crash
        fake.IsRunning = false;
        fake.FireStatusChanged("Stopped");
        AssertEqual(SessionStates.Error, session.State, "Must be Error after crash");

        // 1. Guard factory throws exception => fail closed
        fake.FireSingBoxStarted(9402);
        fake.ReadinessGuardFactory = pid => throw new InvalidOperationException("Guard capture blew up");
        fake.FireConnected(9402);

        AssertEqual(SessionStates.Error, session.State, "Guard capture exception must fail closed and stay in Error state");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed on guard exception");

        // 2. Guard evaluation throws exception => fail closed
        fake.FireSingBoxStarted(9403);
        fake.ReadinessGuardFactory = pid => () => throw new InvalidOperationException("Guard evaluation blew up");
        fake.FireConnected(9403);

        AssertEqual(SessionStates.Error, session.State, "Guard evaluation exception must fail closed and stay in Error state");
        AssertEqual("connect_failed", session.ErrorCode, "ErrorCode must be connect_failed on guard exception");
    }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void WorkerModuleInit()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tun-lock-worker" && i + 1 < args.Length)
            {
                string action = args[i + 1];
                string? testDir = i + 2 < args.Length ? args[i + 2] : null;
                int exitCode = RunWorkerAction(action, testDir);
                Environment.Exit(exitCode);
            }
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string pathname, uint mode);

    private static int RunWorkerAction(string action, string? testDir)
    {
        try
        {
            if (!string.IsNullOrEmpty(testDir))
            {
                LinuxTunOwnership.OverrideRuntimeDirectory = testDir;
            }

            switch (action)
            {
                case "try-acquire":
                {
                    using var lockInstance = new TunOwnershipLock();
                    bool acquired = lockInstance.TryAcquire();
                    Console.WriteLine(acquired ? "ACQUIRED" : "ACQUIRE_FAILED");
                    Console.Out.Flush();
                    return acquired ? 0 : 2;
                }

                case "hold-lock":
                {
                    var lockInstance = new TunOwnershipLock();
                    bool acquired = lockInstance.TryAcquire();
                    if (!acquired)
                    {
                        Console.WriteLine("ACQUIRE_FAILED");
                        Console.Out.Flush();
                        lockInstance.Dispose();
                        return 2;
                    }

                    Console.WriteLine("LOCKED");
                    Console.Out.Flush();

                    var input = Console.ReadLine();
                    lockInstance.Release();
                    lockInstance.Dispose();
                    Console.WriteLine("RELEASED");
                    Console.Out.Flush();
                    return 0;
                }

                case "test-disposed":
                {
                    var lockInstance = new TunOwnershipLock();
                    lockInstance.Dispose();
                    try
                    {
                        lockInstance.TryAcquire();
                        Console.WriteLine("FAIL_ACQUIRED_AFTER_DISPOSE");
                        Console.Out.Flush();
                        return 2;
                    }
                    catch (ObjectDisposedException)
                    {
                        Console.WriteLine("DISPOSED_REFUSED");
                        Console.Out.Flush();
                        return 0;
                    }
                }

                case "probe":
                {
                    var status = TunOwnershipLock.ProbeOwnership();
                    Console.WriteLine($"PROBE:{status}");
                    Console.Out.Flush();
                    return status switch
                    {
                        TunOwnershipStatus.Free => 0,
                        TunOwnershipStatus.Owned => 2,
                        TunOwnershipStatus.Unavailable => 4,
                        _ => 1
                    };
                }

                default:
                    Console.WriteLine($"UNKNOWN_ACTION:{action}");
                    Console.Out.Flush();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WORKER_EXCEPTION:{ex.Message}");
            Console.Out.Flush();
            return 3;
        }
    }

    private static Process StartWorkerProcess(string action, string testDir)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Environment.ProcessPath is not available.");

        var psi = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var asmLocation = typeof(LifecycleChecks).Assembly.Location;
        if (processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
            || processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.Arguments = $"\"{asmLocation}\" --tun-lock-worker {action} \"{testDir}\"";
        }
        else
        {
            psi.Arguments = $"--tun-lock-worker {action} \"{testDir}\"";
        }

        var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start child worker process.");
        return proc;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForExitWithTimeoutAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { }
            return false;
        }
    }

    private static void EnsurePrivateTestDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task CheckSeparateProcessLockContentionAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-contention-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            using var lockA = new TunOwnershipLock();
            bool acquiredA = lockA.TryAcquire();
            AssertTrue(acquiredA, "Process A must successfully acquire TunOwnershipLock");
            AssertEqual(TunOwnershipStatus.Owned, TunOwnershipLock.ProbeOwnership(), "ProbeOwnership must report Owned while held");

            // Process B attempts to acquire in separate process -> must fail
            using var procB = StartWorkerProcess("try-acquire", testDir);
            try
            {
                var line = await ReadLineWithTimeoutAsync(procB.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("ACQUIRE_FAILED", line, "Process B stdout must be ACQUIRE_FAILED");
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must exit within timeout");
                AssertEqual(2, procB.ExitCode, "Process B must fail to acquire lock while held by Process A (exit code 2)");
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }

            // Release lock in Process A
            lockA.Release();
            AssertEqual(TunOwnershipStatus.Free, TunOwnershipLock.ProbeOwnership(), "ProbeOwnership must report Free after release");

            // Process C can now acquire
            using var procC = StartWorkerProcess("try-acquire", testDir);
            try
            {
                var line = await ReadLineWithTimeoutAsync(procC.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("ACQUIRED", line, "Process C stdout must be ACQUIRED");
                var exited = await WaitForExitWithTimeoutAsync(procC, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process C must exit within timeout");
                AssertEqual(0, procC.ExitCode, "Process C must acquire lock and exit 0");
            }
            finally
            {
                if (!procC.HasExited) { try { procC.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockReleaseAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-release-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            using var procB = StartWorkerProcess("hold-lock", testDir);
            try
            {
                var line = await ReadLineWithTimeoutAsync(procB.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("LOCKED", line, "Process B must acquire lock and signal LOCKED");

                // Verify held from outside
                AssertEqual(TunOwnershipStatus.Owned, TunOwnershipLock.ProbeOwnership(), "ProbeOwnership must report Owned while Process B holds it");

                using var lockA = new TunOwnershipLock();
                AssertFalse(lockA.TryAcquire(), "Process A must fail to acquire while Process B holds lock");

                // Instruct Process B to release
                await procB.StandardInput.WriteLineAsync("release").ConfigureAwait(false);
                await procB.StandardInput.FlushAsync().ConfigureAwait(false);
                var released = await ReadLineWithTimeoutAsync(procB.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("RELEASED", released, "Process B must signal RELEASED");

                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must exit within timeout");
                AssertEqual(0, procB.ExitCode, "Process B must exit with code 0");

                // Now Process A can acquire
                AssertTrue(lockA.TryAcquire(), "Process A must acquire lock after Process B released");
                lockA.Release();
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockCrashAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-crash-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            using var procB = StartWorkerProcess("hold-lock", testDir);
            try
            {
                var line = await ReadLineWithTimeoutAsync(procB.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("LOCKED", line, "Process B must acquire lock and signal LOCKED");
                AssertEqual(TunOwnershipStatus.Owned, TunOwnershipLock.ProbeOwnership(), "ProbeOwnership must report Owned");

                // Kill process B simulating sudden crash / SIGKILL
                procB.Kill();
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must terminate upon Kill()");

                // Linux kernel must automatically release flock
                AssertEqual(TunOwnershipStatus.Free, TunOwnershipLock.ProbeOwnership(), "Kernel must automatically release flock on abnormal process crash");

                using var lockA = new TunOwnershipLock();
                AssertTrue(lockA.TryAcquire(), "Process A must successfully acquire lock after crash");
                lockA.Release();
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockSymlinkAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-symlink-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            var lockPath = Path.Combine(testDir, LinuxTunOwnership.LockFileName);
            var decoy = Path.Combine(testDir, "decoy.txt");
            await File.WriteAllTextAsync(decoy, "decoy").ConfigureAwait(false);
            File.CreateSymbolicLink(lockPath, decoy);

            // Probe must detect symlink and return Unavailable (fail closed)
            var probeStatus = TunOwnershipLock.ProbeOwnership();
            AssertEqual(TunOwnershipStatus.Unavailable, probeStatus, "ProbeOwnership must report Unavailable on symlink lock file");

            using var lockA = new TunOwnershipLock();
            AssertFalse(lockA.TryAcquire(), "TryAcquire must fail on symlink lock file");

            // Subprocess must also fail to acquire (exit code 2)
            using var procB = StartWorkerProcess("try-acquire", testDir);
            try
            {
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must exit within timeout on symlink lock");
                AssertEqual(2, procB.ExitCode, "Process B must fail to acquire symlink lock (exit code 2)");
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockFifoAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-fifo-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            var lockPath = Path.Combine(testDir, LinuxTunOwnership.LockFileName);
            int mkfifoRes = Mkfifo(lockPath, 0x0180); // mode 0600
            AssertTrue(mkfifoRes == 0, "mkfifo must succeed in creating FIFO");

            // Probe must open non-blocking, observe S_IFIFO via fstatx, and return Unavailable without hanging
            var probeStatus = TunOwnershipLock.ProbeOwnership();
            AssertEqual(TunOwnershipStatus.Unavailable, probeStatus, "ProbeOwnership on FIFO must report Unavailable and not hang");

            // In-process TryAcquire must also reject FIFO and not hang
            using var lockA = new TunOwnershipLock();
            AssertFalse(lockA.TryAcquire(), "TryAcquire on FIFO must reject and not hang");

            // Subprocess must also reject and exit 2 within timeout (no hang)
            using var procB = StartWorkerProcess("try-acquire", testDir);
            try
            {
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must exit within timeout on FIFO lock (did not hang)");
                AssertEqual(2, procB.ExitCode, "Process B must fail to acquire FIFO lock (exit code 2)");
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockInvalidPermissionsAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-perms-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            // 1. Lock file with non-private mode (0666)
            var lockPath = Path.Combine(testDir, LinuxTunOwnership.LockFileName);
            await File.WriteAllTextAsync(lockPath, "test").ConfigureAwait(false);
            File.SetUnixFileMode(lockPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

            var probeStatus = TunOwnershipLock.ProbeOwnership();
            AssertEqual(TunOwnershipStatus.Unavailable, probeStatus, "ProbeOwnership on lock file with lax permissions must report Unavailable");

            using var lockA = new TunOwnershipLock();
            AssertFalse(lockA.TryAcquire(), "TryAcquire on lock file with lax permissions must reject");

            using var procB = StartWorkerProcess("try-acquire", testDir);
            try
            {
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Process B must exit within timeout");
                AssertEqual(2, procB.ExitCode, "Process B must exit code 2 on lax permissions");
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }

            // 2. Directory with non-private mode (0777)
            File.Delete(lockPath);
            File.SetUnixFileMode(testDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var probeDirStatus = TunOwnershipLock.ProbeOwnership();
            AssertEqual(TunOwnershipStatus.Unavailable, probeDirStatus, "ProbeOwnership on directory with lax permissions must report Unavailable");

            using var lockDir = new TunOwnershipLock();
            AssertFalse(lockDir.TryAcquire(), "TryAcquire on directory with lax permissions must reject");
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckSeparateProcessLockDisposedAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-disposed-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            // In-process disposal refusal
            var lockInstance = new TunOwnershipLock();
            lockInstance.Dispose();
            bool threwDisposed = false;
            try
            {
                lockInstance.TryAcquire();
            }
            catch (ObjectDisposedException)
            {
                threwDisposed = true;
            }
            AssertTrue(threwDisposed, "TunOwnershipLock.TryAcquire must throw ObjectDisposedException after dispose");

            var linuxLock = new LinuxTunOwnership();
            linuxLock.Dispose();
            bool linuxThrewDisposed = false;
            try
            {
                linuxLock.TryAcquire();
            }
            catch (ObjectDisposedException)
            {
                linuxThrewDisposed = true;
            }
            AssertTrue(linuxThrewDisposed, "LinuxTunOwnership.TryAcquire must throw ObjectDisposedException after dispose");

            // Subprocess disposal refusal
            using var procB = StartWorkerProcess("test-disposed", testDir);
            try
            {
                var line = await ReadLineWithTimeoutAsync(procB.StandardOutput, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertEqual("DISPOSED_REFUSED", line, "Worker process must report DISPOSED_REFUSED");
                var exited = await WaitForExitWithTimeoutAsync(procB, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                AssertTrue(exited, "Worker process must exit cleanly");
                AssertEqual(0, procB.ExitCode, "Worker process must exit with code 0");
            }
            finally
            {
                if (!procB.HasExited) { try { procB.Kill(); } catch { } }
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static Task CheckLockNeverReachesProductionPathAsync()
    {
        if (!OperatingSystem.IsLinux()) return Task.CompletedTask;

        // Ensure override was cleanly reset after all tests
        AssertEqual(null, LinuxTunOwnership.OverrideRuntimeDirectory, "OverrideRuntimeDirectory must be null after all tests");

        // Verify resolution without override points to production /run/user/<uid>
        var (dir, _, _) = LinuxTunOwnership.ResolveRuntimeDirectory(forCreate: false);
        AssertTrue(dir != null && dir.StartsWith("/run/user/"), "Production directory must start with /run/user/");

        return Task.CompletedTask;
    }

    private static async Task CheckSeparateProcessLockUnknownAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), "vpnrouter-unknown-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateTestDirectory(testDir);
        try
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = testDir;

            // Create symlink pointing to another file to violate O_NOFOLLOW / symlink invariant
            var lockPath = Path.Combine(testDir, LinuxTunOwnership.LockFileName);
            var decoy = Path.Combine(testDir, "decoy.txt");
            await File.WriteAllTextAsync(decoy, "decoy").ConfigureAwait(false);
            File.CreateSymbolicLink(lockPath, decoy);

            // Probe must detect symlink and return Unavailable (fail closed)
            var probeStatus = TunOwnershipLock.ProbeOwnership();
            AssertEqual(TunOwnershipStatus.Unavailable, probeStatus, "ProbeOwnership must report Unavailable on symlink lock file");

            var guardResult = LinuxOwnershipGuard.CheckOwnership();
            AssertEqual(OwnershipStatus.Unavailable, guardResult.Status, "LinuxOwnershipGuard must return Unavailable on unverifiable lock file");

            var fake = new FakeLifecycleEngine();
            var session = new RouterSession(fake, null, () => true);
            AssertFalse(session.CanConnect, "CanConnect must fail closed on Unavailable ownership");

            bool threw = false;
            try
            {
                await session.ConnectAsync(new AppSettings(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (RouterException ex) when (ex.Code == "unavailable")
            {
                threw = true;
            }
            AssertTrue(threw, "ConnectAsync must throw RouterException('unavailable') on unverifiable lock file");
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = null;
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void AssertFalse(bool condition, string message)
    {
        if (condition)
            throw new InvalidOperationException($"Assertion failed (expected false): {message}");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: {message}. Expected '{expected}', got '{actual}'.");
    }

    // ─── Test Fake for ILifecycleEngine ─────────────────────────────────────

    private sealed class FakeLifecycleEngine : ILifecycleEngine
    {
        public bool IsRunning { get; set; }
        public string ActiveProfileName { get; set; } = "Default";
        public int? SingBoxPid { get; set; }
        public string ActiveConfigMode { get; set; } = "generated";
        public string ActiveRoutingMode { get; set; } = "split";
        public string ActiveServerAddress { get; set; } = "1.1.1.1";

        public bool StopCalled { get; set; }
        public bool StopFailsToStop { get; set; }
        public bool StopHangs { get; set; }
        public int StopCallCount { get; private set; }
        public bool ApplyCalled { get; set; }
        public bool DisposeCalled { get; set; }

        public Action<FakeLifecycleEngine>? OnStartAction { get; set; }
        public Func<int, Func<bool>?>? ReadinessGuardFactory { get; set; }
        public Func<bool>? CapabilityReadinessFunc { get; set; } = () => true;

        public event Action<int>? SingBoxStarted;
        public event Action<int>? Connected;
        public event Action<string>? StatusChanged;
        public event Action<string>? Warning;

        public void FireSingBoxStarted(int pid)
        {
            SingBoxPid = pid;
            SingBoxStarted?.Invoke(pid);
        }

        public void FireConnected(int pid)
        {
            IsRunning = true;
            Connected?.Invoke(pid);
        }

        public void FireStatusChanged(string status) => StatusChanged?.Invoke(status);
        public void FireWarning(string warning) => Warning?.Invoke(warning);

        public Task StartAsync(AppSettings settings, CancellationToken ct)
        {
            OnStartAction?.Invoke(this);
            return Task.CompletedTask;
        }

        public Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct)
        {
            ApplyCalled = true;
            return Task.FromResult(true);
        }

        public void Stop()
        {
            StopCallCount++;
            StopCalled = true;
            if (StopHangs)
            {
                Thread.Sleep(100);
            }
            if (!StopFailsToStop)
            {
                IsRunning = false;
                SingBoxPid = null;
            }
        }

        public Func<bool>? CaptureReadinessGuard(int pid)
        {
            return ReadinessGuardFactory?.Invoke(pid);
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
