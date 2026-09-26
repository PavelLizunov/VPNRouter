# Lane P01: Lifecycle & Concurrency — Adversarial Review

**Scope**: VpnEngine, StartupPipeline, SingBoxManager (Lifecycle, CrashDetect, Health, HotReload), LinuxTunOwnership, TunOwnershipLock, LinuxOwnershipGuard, VpnEngineAdapter, BoundedTeardown, TwoPhaseConnectCoordinator.

**Evidence Sheets**: C05\_B019\_C05, C07\_B020\_C07, C09\_B021\_C09, H06\_B063\_H06.

**Methodology**: Source-level adversarial analysis against inventory sheets, direct code inspection, and cross-module contract verification. Each finding is grounded in file:line evidence and reproduces through concrete scenarios, not speculation.

---

## 1. Subsystem State Machines — Reconstructed vs Intended

### 1.1 VpnEngine Implied State Machine

VpnEngine has no explicit state enum. Its effective state is an emergent composite of:
- `_singBox` (null / instance with its own `SingBoxState`)
- `_sessionCts` (null / active / cancelled)
- `_postStartPhase` (false / true)
- `_warmupConfirmed` (false / true)
- `_failoverGeneration` (monotonic counter)
- `_disposed` (bool)

**Reconstructed transitions:**

```
IDLE  ──(StartAsync)──►  STARTING
  │                          │
  │                    ┌─────┴─────┐
  │                    │ Pipeline  │
  │                    │ Phases    │
  │                    │ 0..4      │
  │                    └─────┬─────┘
  │                     _postStartPhase=false
  │                          │
  │                    ┌─────┴─────┐
  │                    │ Phase 5   │──(F-E dead)──► RE-ENTER StartAsyncInternal
  │                    │ PreStart  │                (EarlyReturn=true)
  │                    └─────┬─────┘
  │                          │
  │                    ┌─────┴─────┐
  │                    │ Phase 7   │
  │                    │ SingBox   │
  │                    │ Started   │
  │                    └─────┬─────┘
  │                     _postStartPhase=true
  │                          │
  │                    ┌─────┴─────┐
  │                    │ Phase 8   │
  │                    │ Monitors  │
  │                    └─────┬─────┘
  │                          │
  │                    RUNNING (warmup probe in background)
  │                          │
  │     ┌────────────────────┼──────────────────────┐
  │     │                    │                      │
  │  (ApplyAsync)    (post-start probe      (Stop)
  │  hotReload/      failover restart)        │
  │  forceRestart         │               cancel _sessionCts
  │     │                 │               _lifecycleGate.Wait
  │     │            TeardownInternal     TeardownInternal
  │     │            StartAsyncInternal        │
  │     │                 │                    │
  │     └─────────────────┘               IDLE (stopped)
  │
  └──(Dispose)──► DISPOSED
```

**Desynchronization risk**: The `_postStartPhase` volatile flag is the *only* mechanism distinguishing pre-start failover (gate already held, re-enter directly) from post-start failover (must acquire gate). If `OnSingBoxStarted` fires but Phase 7's polling loop then throws (sing-box dies within 5s), `_postStartPhase` is `true` but `StartAsync` catches the exception and calls `TeardownInternal`. A subsequent failover closure still holding the old `_postStartPhase=true` will attempt `ExecuteProbeFailoverRestartAsync` and wait on the gate — which is correct because `StartAsync` releases the gate in finally. However, the generation check should catch this. This path is safe but fragile; there is no reset of `_postStartPhase` in the exception handler of `StartAsync`.

### 1.2 SingBoxManager SingBoxState Machine

The `SingBoxState` enum has 5 values: `Stopped, Starting, Running, Restarting, Failed`.

**Unreachable transition**: There is no direct `Failed → Stopped` transition in user-initiated code. Once `StopInternal` sets `Failed` (unconfirmed stop), the only path back to `Stopped` is through a successful `RestartCore` → `StopInternal(releaseLock:false)` → `LaunchProcess` sequence. A manual `Stop()` on a `Failed` manager with `_ownsTunLock=true` and `_exactStopUnconfirmed=true` re-enters `StopInternal` but the CAS guard (`_stopState`) will proceed and attempt to kill again. If the process is now gone, `winTargetHandle==null`, and it sets `Stopped`. This path works but is not obvious from the state diagram.

**Missing transition**: `Restarting → Stopped` is reachable only through `RestartCore` when `StopInternal` succeeds but `LaunchProcess` throws. In that case the catch block sets `Failed`, not `Stopped`. The `State = Stopped` assignment from `StopInternal` is overwritten by `State = Restarting` on line 651 of Lifecycle.cs, then `LaunchProcess` failure sets `Failed`. This is correct but means a restart failure always results in `Failed` even when the old process was cleanly stopped — a minor semantic gap that could mislead health monitoring.

### 1.3 Headless Session State Machine

The `SessionStates` enum defines 6 canonical states. The `TwoPhaseConnectCoordinator` enforces typed event transitions (not string-based). The `ReadinessGuardFailed` outcome is correctly mapped to `Error`. No unreachable states were found in the headless state machine; it is the most well-specified of the three.

---

## 2. Concurrency Hazards & Race Conditions

### 2.1 Lock Ordering Inventory

| Lock | Type | Scope | Acquired by |
|------|------|-------|-------------|
| `VpnEngine._lifecycleGate` | `SemaphoreSlim(1,1)` | Per-engine | StartAsync, ApplyAsync, Stop, ExecuteProbeFailoverRestartAsync |
| `SingBoxManager._lifecycleGate` | `object` (Monitor) | Per-manager | StartWithJson, Stop, Restart, ReloadConfigJson, TryReloadConfigJson, KillWedgedForRecovery |
| `SingBoxManager._stopState` | `Interlocked CAS` | Per-manager | StopInternal |
| `SingBoxManager._capturedStderrLock` | `object` (Monitor) | Per-manager | ErrorLine handler, DetectTunOrphanCrashSignature |
| `TunOwnershipLock.InstanceGate` | `static object` (Monitor) | Process-wide | TryAcquire (Linux), TryAcquireExclusive, Dispose, RegisterExecutablePath |
| `TunOwnershipLock._ownerRecordMonitorGate` | `object` (Monitor) | Per-instance | StartOwnerRecordMonitor, StopOwnerRecordMonitor |
| `LinuxTunOwnership._gate` | `object` (Monitor) | Per-instance | TryAcquire, Release, Dispose |
| `s_tunRemovalGate` | `static object` (Monitor) | Class-wide | QueueTunAdapterRemoval, WaitForQueuedTunAdapterRemoval |

**Lock ordering**: VpnEngine._lifecycleGate → SingBoxManager._lifecycleGate (via Stop → _singBox.Dispose → Stop → lock). This ordering is consistent across all paths (Start, Stop, Apply). No inversion detected.

**Potential deadlock in TunOwnershipLock**: `TryAcquireExclusive` acquires `InstanceGate` then calls `TryAcquire` which (on Linux) also acquires `InstanceGate`. However, `Monitor` is reentrant, so this is safe. On Windows, `TryAcquire` does not take `InstanceGate`, so no issue.

### 2.2 Specific Race Conditions Identified

See Findings section for concrete race conditions with file:line anchors.

---

## 3. Fail-Closed & Leak Invariants

### 3.1 Crash / SIGTERM Behavior

**Traffic fail-open window during teardown** (`VpnEngine.TeardownInternal`, lines 1002-1097):

The teardown order is: `_probeCts.Cancel → _healthMonitor.Dispose → _singBox.Dispose → _slipstream.Dispose → _splitDriver.Disengage → _dnsHardening.Restore → _unixDns.Restore → _firewall.Disable/Delete/Dispose`.

Critically, the **firewall rules are removed AFTER sing-box is stopped**. Between sing-box dying and firewall rule deletion, traffic from previously-tunneled applications routes directly over the physical NIC **without firewall kill-switch protection**. The kill-switch (`BlockOnVpnFail`) rules exist precisely to prevent this — but they are disabled in the same teardown sequence. On a user-initiated Stop, this window is expected (user wants internet). On a **crash**, however, the ordering matters: `OnProcessExited` fires `Crashed`, which triggers `HealthMonitor` restart — the firewall rules remain armed during the restart attempt, which is correct. The issue arises only in `TeardownInternal` called from `Stop()` or `Dispose()`.

**Assessment**: The current ordering is intentional for user-initiated disconnects (BR-5 comment). For crash recovery, `HealthMonitor` handles it. This is acceptable but should be documented as a design decision.

### 3.2 Pipe Stalls

`BoundedTeardown.StopBoundedAsync` wraps `engine.Stop()` in `Task.Run` with a timeout. If `Stop()` hangs (kernel mutex deadlock, unkillable process), the `Task.Run` thread is orphaned permanently. There is no mechanism to observe or reap this thread. See FINDING-P01-09.

### 3.3 DNS Hardening Fail-Open on Warmup Failure

`StartupPipeline.ScheduleWarmupProbe` (line 1322): If the warmup probe fails all 15 attempts, `_dnsHardening.EnableLockdownIfConfigured` is **intentionally skipped** and `_host.OnConnected` is **not called**. The DNS lockdown remains unarmed. This is a deliberate fail-open to preserve internet availability, but it means DNS queries leak to the ISP's resolvers on the physical NIC when the tunnel is nominally "up" but warmup never succeeded. See FINDING-P01-06.

---

## 4. Security & Privilege Boundaries

### 4.1 Binary Deployment — Size-Only Check

`StartupPipeline.DeploySingBoxBinary` (inventory C05 line 1143-1156): The bundled sing-box binary is overwritten at the target path based solely on file size comparison (`installedSize != bundledSize`). No cryptographic hash or code signature is verified. A modified binary of identical size would not be detected. This is tracked as `OMARCHY-RUNTIME-TRUST-CONTRACT` in OPEN-DEFECTS.md. See FINDING-P01-10.

### 4.2 Windows TunOwnershipLock Fails Open

`TunOwnershipLock.TryAcquire` (lines 104-125): On Windows, if the named semaphore creation throws (sandboxed environment, `UnauthorizedAccessException`), the method catches the exception and returns `true` — **fail-open**. Both competing processes believe they own the TUN. The second sing-box instance crashes with `ERROR_FILE_EXISTS`. See FINDING-P01-07.

### 4.3 VpnEngineAdapter Synchronous Scope Disposal

`VpnEngineAdapter.StartAsync` and `ApplyAsync` (lines 62-72) enter a `SingBoxRuntimePolicy` scope with `using var _`, but return the `Task` without awaiting it. The scope is disposed synchronously when the method frame returns, **before** the async engine operation completes. Any policy check inside the engine that reads `SingBoxRuntimePolicy.Current` from `AsyncLocal` on a continuation thread may see the scope already exited. See FINDING-P01-05.

### 4.4 `PlatformCapabilityVerifier.VerifyCanConnect` Falls Back to `File.Exists`

When `SingBoxRuntimePolicy.Current` is `null` (no active scope), the verifier falls back to `File.Exists(AppPaths.SingBoxExePath)` (H06 inventory, section 6.4). On Linux Headless, this violates Rule #7: mere file existence does not establish launch authority. The `VpnEngineAdapter` constructor now captures `DefaultProduction` (line 38), which partially mitigates this — but only if `VerifyCanConnect` is called within that adapter's scope. See FINDING-P01-11.

---

## 5. Inconsistencies & Protocol Violations

### 5.1 `IsHealthy()` vs `IsRunning()` Linux Divergence

`SingBoxManager.Health.cs` lines 11-52:
- `IsRunning()` uses `IsClashApiAlive()` on **both** macOS and Linux (line 26).
- `IsHealthy()` uses `IsClashApiAlive()` on **macOS only** (line 35); on Linux, it falls through to `_handle.HasExited` (line 38).

When `_linuxUsedPkexec == true`, `_handle` is the short-lived pkexec wrapper which exits immediately. `IsHealthy()` returns `false` perpetually on Linux pkexec systems even when sing-box is alive and routing traffic. `HealthMonitor` uses `IsHealthy()` for its health checks, potentially triggering unnecessary restart loops. See FINDING-P01-01.

### 5.2 Warmup Probe CTS Mismatch

`StartupPipeline.ScheduleWarmupProbe` uses `ct` (the `_sessionCts.Token` from `StartAsync`) but does **not** link to `_probeCts`. `SchedulePostStartProbe` **does** link to `_probeCts`. During a post-start failover restart, `TeardownInternal` cancels `_probeCts` but not `_sessionCts`. The old warmup probe task continues running and can emit stale `OnStatus("Connected (PID ...)")` and call `_dnsHardening.EnableLockdownIfConfigured` — both of which bypass the `OnConnected` staleness guard. See FINDING-P01-02.

### 5.3 `SingBoxManager.Dispose` Revives `_disposed`

`SingBoxManager.cs` lines 399-406: When an exact stop is unconfirmed, `Dispose()` resets `Volatile.Write(ref _disposed, 0)`. This violates the .NET `IDisposable` contract. A subsequent `Dispose()` call re-runs the cleanup body. More critically, if the instance was in a `using` block, the scope exit called `Dispose()` (setting `_disposed=1`), then the recovery path reset it to 0, and the object appears undisposed to any concurrent reader. See FINDING-P01-03.

---

## 6. Prioritized Actionable Findings

---

### FINDING-P01-01 — P0: `IsHealthy()` Permanently False on Linux pkexec

**Source Anchor**: `VPNRouter.Core/Services/SingBoxManager.Health.cs:33-39`

**Mechanism**: `IsHealthy()` checks `OperatingSystem.IsMacOS()` for the Clash API path but omits `OperatingSystem.IsLinux()`. On Linux with pkexec, `_handle.HasExited` is always `true` (the pkexec wrapper exits immediately), so `IsHealthy()` returns `false` even when sing-box is alive.

**Failure Scenario**: On any Linux distribution where sing-box lacks `CAP_NET_ADMIN` (AppImage, manual tarball, non-deb installs), `HealthMonitor.IsHealthy()` returns `false` on every health tick, potentially triggering spurious restart attempts or degraded UI warnings.

**Fix**:
```csharp
public bool IsHealthy()
{
    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        return State == SingBoxState.Running && IsClashApiAlive();
    // ... existing Windows path
}
```

---

### FINDING-P01-02 — P0: Stale Warmup Probe Emits Status & Arms DNS Lockdown After Failover

**Source Anchor**: `VPNRouter.Core/Services/StartupPipeline.cs:1240-1352` (ScheduleWarmupProbe)

**Mechanism**: `ScheduleWarmupProbe` captures the raw `ct` parameter (which is `_sessionCts.Token`). Unlike `SchedulePostStartProbe` which links to `_probeCts`, the warmup probe is not cancelled by `TeardownInternal`'s `_probeCts.Cancel()`. During a post-start failover (server A → server B), `TeardownInternal` cancels `_probeCts` but `_sessionCts` remains active. Server A's warmup probe continues running for up to 15 seconds concurrently with server B's startup.

**Failure Scenario**: Server A's warmup probe succeeds late (attempt 12 of 15) and calls:
1. `_host.OnStatus("Connected (PID <old>)")` — misleading status for the UI (line 1293).
2. `_dnsHardening.EnableLockdownIfConfigured(settings, ...)` — arms DNS lockdown using the **old** server's settings (line 1322). This call is **outside** the `OnConnected` staleness guard.

If server B uses a different DNS strategy, this stale lockdown misconfigures the DNS firewall rules. On Windows, this can block DNS queries needed by the new tunnel.

**Fix**: Link the warmup probe to `_probeCts` the same way `SchedulePostStartProbe` does, so `TeardownInternal` cancels both:
```csharp
// In ScheduleWarmupProbe, replace direct ct usage with _probeCts-linked token:
// The host should create/link _probeCts before calling ScheduleWarmupProbe,
// or ScheduleWarmupProbe should accept the _probeCts-linked token instead of raw ct.
```

---

### FINDING-P01-03 — P1: `SingBoxManager.Dispose` Revives Disposed Object

**Source Anchor**: `VPNRouter.Core/Services/SingBoxManager.cs:399-406`

**Mechanism**: When `Dispose()` cannot confirm the process stopped (`_ownsTunLock` is true after `_exactStopUnconfirmed`), it resets `Volatile.Write(ref _disposed, 0)` and re-subscribes to `AppDomain.ProcessExit`. This violates `IDisposable` idempotency — subsequent `Dispose()` calls re-execute the cleanup body.

**Failure Scenario**: In a `using` block: `Dispose()` sets `_disposed=1` → `Stop()` fails confirmation → `_disposed` reset to 0 → scope exit completes. A `GC.SuppressFinalize` was already called. If another thread subsequently calls any method that checks `Volatile.Read(ref _disposed) == 0`, it sees an "alive" manager on a partially torn-down instance. More practically, if `Stop()` eventually succeeds asynchronously (e.g. the process dies seconds later via `OnAppDomainProcessExit`), the already-GC-suppressed object will never finalize, but the AppDomain handler will call release on a half-disposed state.

**Fix**: Instead of reviving `_disposed`, use a separate flag (`_tunLeaseRetained`) to track the exceptional lease-retention state. Never reset `_disposed` after the CAS.

---

### FINDING-P01-04 — P1: `RestartTrueSplitAsync` Races Stop/Apply Without Lifecycle Gate

**Source Anchor**: `VPNRouter.Core/Services/VpnEngine.cs:583-584`

**Mechanism**: `RestartTrueSplitAsync` is a public method that forwards directly to `TryEngageSplitDriverAsync` without acquiring `_lifecycleGate` or checking `_sessionCts`. It is called from the UI thread (`MainWindowViewModel.Connection.cs:164`) via `Task.Run`.

**Failure Scenario**: User changes true-split settings (triggering `RestartTrueSplitAsync`) and simultaneously disconnects (triggering `Stop()`). `Stop()` acquires `_lifecycleGate` and calls `TeardownInternal` which runs `_splitDriver.DisengageAsync()` on line 1048. Concurrently, `RestartTrueSplitAsync` calls `_splitDriver.EngageAsync()`. The split driver's internal state is corrupted — it may remain engaged after the VPN has stopped, or the engage/disengage calls interleave causing kernel driver errors.

**Fix**: Either acquire `_lifecycleGate` in `RestartTrueSplitAsync`, or check `_sessionCts?.IsCancellationRequested` at the top and coordinate with the teardown path.

---

### FINDING-P01-05 — P1: `VpnEngineAdapter.StartAsync/ApplyAsync` Dispose Policy Scope Before Async Completion

**Source Anchor**: `VPNRouter.Headless/Lifecycle/VpnEngineAdapter.cs:62-72`

**Mechanism**: `StartAsync` and `ApplyAsync` are non-async methods that return the engine's `Task` without awaiting. The `using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy)` is disposed when the synchronous method frame returns — **before** the async engine work completes.

**Failure Scenario**: `_engine.StartAsync` reaches an `await` point and resumes on a thread pool thread. `SingBoxRuntimePolicy.Current` (backed by `AsyncLocal`) no longer sees the adapter's scope because it was disposed. VpnEngine internally re-enters its own scope in `StartAsync` (line 352), so the core engine is protected. However, any code path between the adapter's scope exit and the engine's scope entry that reads `SingBoxRuntimePolicy.Current` will see `null`.

**Impact**: Currently mitigated by the engine's own `EnterScope` in `StartAsync`. But this is a latent defect — any future code added between the adapter return and the engine's internal scope entry will operate without policy protection.

**Fix**: Declare methods `async Task` and `await` the engine call:
```csharp
public async Task StartAsync(AppSettings settings, CancellationToken ct)
{
    using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
    await _engine.StartAsync(settings, ct).ConfigureAwait(false);
}
```

---

### FINDING-P01-06 — P1: DNS Lockdown Intentionally Unarmed on Warmup Failure (Fail-Open)

**Source Anchor**: `VPNRouter.Core/Services/StartupPipeline.cs:1337-1341`

**Mechanism**: When the warmup probe exhausts all 15 attempts without reaching `gstatic.com`, `EnableLockdownIfConfigured` is intentionally skipped (line 1337-1341). The tunnel is nominally "up" (sing-box is running, TUN is created) but the lockdown that prevents DNS queries from leaking to the ISP's resolvers on the physical NIC is never armed.

**Failure Scenario**: On a heavily censored or throttled network where the warmup probe times out but the tunnel is actually functional for non-HTTP traffic (e.g., DNS-over-TLS, gaming protocols), DNS queries leak to the physical NIC unencrypted and unprotected, defeating the purpose of `DnsLeakLockdown`.

**Impact**: This is a documented design decision (fail-open for internet availability). However, it means `DnsLeakLockdown=true` does not guarantee DNS leak protection — it is conditional on warmup success. Users relying on this for privacy are not informed.

**Fix**: Consider arming the DNS lockdown unconditionally when `DnsLeakLockdown=true`, with a timed rollback if warmup never succeeds. Alternatively, document this limitation prominently.

---

### FINDING-P01-07 — P1: Windows `TunOwnershipLock.TryAcquire` Fails Open on Semaphore Errors

**Source Anchor**: `VPNRouter.Core/Services/TunOwnershipLock.cs:104-125`

**Mechanism**: Both the semaphore creation catch block (line 114) and the `WaitOne` catch block (line 124) return `true` (fail-open). If a security policy prevents named semaphore access, both `VPNRouter.App` and `VPNRouter.Service` will believe they acquired ownership and launch competing sing-box instances.

**Failure Scenario**: On a locked-down Windows environment (GPO restricting Global\\ kernel objects, or AppContainer sandbox), the semaphore fails. Both processes start sing-box → the second hits `ERROR_FILE_EXISTS` on the Wintun adapter → crash loop.

**Impact**: Mitigated by the practical requirement for admin privileges to create TUN adapters (the same environment that blocks semaphores likely blocks TUN creation). But the fail-open posture violates the safety contract.

**Fix**: Change the catch blocks to return `false` (fail-closed), or add a secondary file-based lock as a fallback (similar to the Linux `flock` implementation).

---

### FINDING-P01-08 — P1: `OnProcessExited` Unbounded Sync Wait

**Source Anchor**: `VPNRouter.Core/Services/SingBoxManager.CrashDetect.cs:36`

**Mechanism**: `h.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult()` blocks a ThreadPool thread with no timeout or cancellation. This is the fallback path when `eventExitCode` is null.

**Failure Scenario**: If `WaitForExitAsync` hangs (corrupt process handle, kernel deadlock, or the process is in a non-interruptible sleep), a ThreadPool thread is permanently wedged. Under sustained crash/restart cycles, multiple ThreadPool threads could be consumed.

**Fix**: Add a bounded timeout:
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
exitCode = h.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
```

---

### FINDING-P01-09 — P2: `BoundedTeardown.StopBoundedAsync` Orphans Thread on Timeout

**Source Anchor**: `VPNRouter.Headless/Lifecycle/BoundedTeardown.cs:29-36` (per H06 inventory)

**Mechanism**: `engine.Stop()` runs in `Task.Run`. If it exceeds the 5s budget, `StopBoundedAsync` returns `false` but the thread executing `Stop()` continues running indefinitely. There is no uncooperative kill fallback.

**Failure Scenario**: `Stop()` hangs in a kernel call (e.g., `_splitDriver.DisengageAsync().Wait(5s)` itself timed out, then `_firewall.DeleteAllRules()` hangs on netsh). The `Task.Run` thread is orphaned. If a subsequent connect attempt starts while the old `Stop()` is still executing, `VpnEngine.Stop()` and `VpnEngine.StartAsync` can interleave — `Stop()` will eventually modify fields that the new `StartAsync` is using.

Additionally, if `stopTask` eventually faults, the exception is unobserved (no continuation attached).

**Fix**: Attach an observe-continuation to `stopTask` to log and swallow late faults:
```csharp
if (completed != stopTask)
{
    _ = stopTask.ContinueWith(t => logger?.Warning(t.Exception,
        "[BoundedTeardown] Late stop fault"), TaskContinuationOptions.OnlyOnFaulted);
    return false;
}
```

---

### FINDING-P01-10 — P2: Binary Deployment Size-Only Check

**Source Anchor**: `VPNRouter.Core/Services/StartupPipeline.cs:1143-1156` (per C05 inventory)

**Mechanism**: `DeploySingBoxBinary` overwrites the installed sing-box binary only when file sizes differ. No hash or signature check.

**Failure Scenario**: An attacker with write access to `AppPaths.SingBoxExePath` replaces the binary with a same-size malicious binary. The next startup skips the copy and executes the attacker's binary with VPN tunnel privileges (CAP_NET_ADMIN on Linux, admin on Windows).

**Impact**: Requires local write access to the binary path. Tracked in OPEN-DEFECTS as `OMARCHY-RUNTIME-TRUST-CONTRACT`. Under `SingBoxRuntimePolicy` (headless), `DeploySingBoxBinary` is skipped entirely (line 1129), which mitigates this for the headless path.

**Fix**: Replace size comparison with SHA-256 hash comparison, or use the `SingBoxRuntimePolicy` pinning model for all paths.

---

### FINDING-P01-11 — P2: `VerifyCanConnect` Falls Back to `File.Exists` Without Policy

**Source Anchor**: `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:77-88` (per H06 inventory)

**Mechanism**: When `SingBoxRuntimePolicy.Current` is null (no active scope), the verifier falls back to `File.Exists(AppPaths.SingBoxExePath)`. On Linux Headless this violates the fail-closed policy.

**Failure Scenario**: If `VerifyCanConnect` is called from a code path that does not enter a `SingBoxRuntimePolicy` scope (e.g., a status probe without policy context), an unauthorized binary on disk makes the system report "can connect" when it should report "unavailable".

**Impact**: Partially mitigated by `VpnEngineAdapter`'s constructor now capturing `DefaultProduction` (line 38). However, direct callers of `PlatformCapabilityVerifier.VerifyCanConnect` outside the adapter's scope remain exposed.

**Fix**: Default to `SingBoxRuntimePolicy.DefaultProduction` on Linux when `Current` is null:
```csharp
var policy = SingBoxRuntimePolicy.Current
    ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);
```

---

### FINDING-P01-12 — P2: `IsOwnedByAnyone()` Treats `Unavailable` as `Free`

**Source Anchor**: `VPNRouter.Core/Services/TunOwnershipLock.cs:327-328`

**Mechanism**: `IsOwnedByAnyone()` returns `ProbeOwnership() == TunOwnershipStatus.Owned`. When `ProbeOwnership()` returns `Unavailable` (inspection failed), `IsOwnedByAnyone()` returns `false` ("assume free").

**Failure Scenario**: Callers like `OrphanCleanup.KillOrphans` rely on `IsOwnedByAnyone()` to gate destructive operations. If the semaphore/flock inspection fails (e.g., permission error, runtime directory missing), the method reports "free" and the orphan cleanup may terminate a process that is actually owned by another instance.

**Fix**: Callers performing destructive operations should use `ProbeOwnership()` directly and treat `Unavailable` as a stop signal (fail-closed).

---

### FINDING-P01-13 — P2: Incomplete JSON Escaping in Hot-Reload Body

**Source Anchor**: `VPNRouter.Core/Services/SingBoxManager.HotReload.cs:75`

**Mechanism**: The Clash API hot-reload body is constructed via string interpolation with only backslash escaping: `_currentConfigPath.Replace("\\", "\\\\")`. Double quotes, control characters, or Unicode in the path are not escaped.

**Failure Scenario**: A user profile directory containing `"` (rare but valid on Linux) causes invalid JSON, a Clash API 400 response, and an unnecessary sing-box restart via `RestartCore()`.

**Fix**: Use `System.Text.Json.JsonSerializer.Serialize(_currentConfigPath)` for proper JSON string escaping.

---

### FINDING-P01-14 — P2: Timed-Out Process Scan Task Runs Unbounded in Background

**Source Anchor**: `VPNRouter.Core/Services/StartupPipeline.cs:862-888` (per C05 inventory)

**Mechanism**: `ScanProcessesPhaseAsync` runs `_host.Scanner.ScanForProfile(profile)` in `Task.Run` with a 30s `Task.WhenAny` timeout. If the scan exceeds 30s, the pipeline continues with an empty `ScanResult`, but the scan task keeps running on a ThreadPool thread because `IProcessScanner.ScanForProfile` is synchronous and takes no `CancellationToken`.

**Failure Scenario**: On a heavily loaded system with thousands of processes, the scan could take minutes. The orphaned task continues consuming CPU and holding process handles while the VPN is already running. On repeated Start/Stop cycles, multiple orphaned scans accumulate.

**Fix**: Add `CancellationToken` support to `IProcessScanner.ScanForProfile`, or track and observe the orphaned task.

---

## Summary

| Severity | Count | IDs |
|----------|-------|-----|
| P0 | 2 | FINDING-P01-01, FINDING-P01-02 |
| P1 | 6 | FINDING-P01-03, FINDING-P01-04, FINDING-P01-05, FINDING-P01-06, FINDING-P01-07, FINDING-P01-08 |
| P2 | 6 | FINDING-P01-09, FINDING-P01-10, FINDING-P01-11, FINDING-P01-12, FINDING-P01-13, FINDING-P01-14 |

**Total: 14 findings (2 P0, 6 P1, 6 P2)**

---

*Lane P01 adversarial review completed 2026-09-26. Evidence anchored to source files and inventory sheets C05/C07/C09/H06.*
