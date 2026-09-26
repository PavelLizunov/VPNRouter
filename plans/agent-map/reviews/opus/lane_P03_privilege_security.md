# Lane P03: Security, Privilege & Trust Boundaries — Adversarial Review

> **TL;DR:** 15 concrete findings discovered (3 P0, 6 P1, 6 P2). The most critical are: (1) silent Windows ACL failure leaves `%ProgramData%\VPNRouter\bin\` world-writable, enabling local privilege escalation via sing-box binary replacement; (2) `PlatformCapabilityVerifier.VerifyCanConnect` bypasses the entire `SingBoxRuntimePolicy` trust system when called without an active scope, falling back to bare `File.Exists`; (3) kill-switch marker file is written with `File.WriteAllText` instead of `AppPaths.WritePrivateText`, allowing local users to manipulate orphan cleanup behavior.

---

## Scope & Methodology

**Files Reviewed (primary):**
- `VPNRouter.Core/AppPaths.cs` (328 LOC)
- `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs` (643 LOC)
- `VPNRouter.Core/Services/LinuxRuntimeEnvironment.cs` (79 LOC)
- `VPNRouter.Core/Services/SingBoxRuntimePolicy.cs` (788 LOC)
- `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs` (116 LOC)
- `VPNRouter.Headless/Lifecycle/VpnEngineAdapter.cs` (102 LOC)
- `VPNRouter.Headless/Lifecycle/BoundedTeardown.cs` (72 LOC)

**Files Reviewed (secondary — cross-referenced for call-site analysis):**
- `VPNRouter.Headless/RouterSession.cs`, `VPNRouter.Headless/RouterBackend.cs`
- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`, `MacDnsHardening.cs`
- `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs`, `SingBoxManager.LinuxStop.cs`
- `VPNRouter.Core/Services/VpnEngine.cs`, `HealthCheck.cs`
- Omarchy plugin `setup` script (via inventory sheet O06)

**Inventory Evidence Sheets:** C04_B018_C04, C33_B033_C33, H06_B063_H06, O06_B073_O06.

**Approach:** Source-level adversarial analysis. Every finding is anchored to a specific source location with a concrete exploitation or failure scenario. No claim is accepted from inventory sheets without independent verification against the source.

---

## 1. Subsystem State Machines: Actual vs. Intended

### 1.1 LinuxFirewallManager State Machine

The implemented state machine has five states driven by two boolean fields `_armed` and `_loaded`:

```
INITIAL (armed=F, loaded=F)
    │
    ├─ CreateBlockRules(isFullTunnel=true) ──→ ARMED (armed=T, loaded=F)
    ├─ CreateBlockRules(isFullTunnel=false) ──→ stays INITIAL (explicit no-op)
    │
ARMED (armed=T, loaded=F)
    │
    ├─ EnableBlockRules() ──→ LOADED (armed=T, loaded=T) [writes marker + nft table]
    │   └─ if sudo -n nft fails ──→ stays ARMED (fail-OPEN)
    │
LOADED (armed=T, loaded=T)
    │
    ├─ DisableBlockRules() ──→ ARMED (armed=T, loaded=F) [deletes nft table + marker]
    │   └─ if DeleteTable fails ──→ stays LOADED (retains state for retry)
    ├─ DeleteAllRules() ──→ INITIAL (armed=F, loaded=F) [unconditional teardown]
    │   └─ if DeleteTable fails ──→ stays current state (early return)
    ├─ UpdateCommittedConfig(enabled=true) ──→ stays LOADED [atomic ruleset refresh]
    ├─ UpdateCommittedConfig(enabled=false) ──→ calls DisableBlockRules → INITIAL
    │
    ├─ Dispose() ──→ DISPOSED
    │   └─ if DeleteTable succeeds: disposed=T, loaded=F
    │   └─ if DeleteTable fails: disposed STAYS FALSE ← [DEFECT: FINDING-08]
```

**Unreachable state:** There is no transition from `LOADED` directly to `INITIAL` that bypasses `ARMED`. `DeleteAllRules` clears both. `Dispose` only clears `_loaded` and sets `_disposed`. This means a Dispose-failure leaves the object in a zombie state (`loaded=T, disposed=F`) that accepts further mutations.

**Desynchronization risk:** The `_loaded` field tracks the in-process belief that the nft table exists. If `sudo -n nft delete table` succeeds but the marker deletion fails (disk full, permissions), the next launch's orphan cleanup will find the marker but the table is already gone — this is handled correctly by `IsTableAbsent`. However, if the table is externally deleted (admin runs `sudo nft delete table inet vpnrouter_ks`) while the process is running, `_loaded` stays true and `DisableBlockRules` will attempt a redundant delete, which fails, and the manager retains `_loaded=true` incorrectly (since `IsTableAbsent` would return true but is only checked as a fallback after a failed delete).

### 1.2 SingBoxRuntimePolicy Scope Lifecycle

The `AsyncLocal<T>`-backed scope system has a critical design invariant: `DefaultProduction` is "sticky" — once latched by `Capture`, it can never be overridden by a fixture policy. The state transitions are:

```
null → (EnterScope(policy)) → policy active
policy → (EnterScope(DefaultProduction)) → DefaultProduction dominates
DefaultProduction → (EnterScope(fixture)) → DefaultProduction stays [cannot weaken]
```

This is correct and prevents privilege escalation within the policy system. However, the `VpnEngineAdapter` pattern of entering/exiting scope around non-awaited async calls is an anti-pattern (see FINDING-05), even though `AsyncLocal` copy-on-write semantics make it accidentally safe for async continuations.

### 1.3 PlatformCapabilityVerifier Decision Tree

```
VerifyCanConnect()
  ├─ capabilityReadinessFunc != null?
  │   └─ YES: return capabilityReadinessFunc()
  │   └─ NO: ↓
  ├─ SingBoxRuntimePolicy.Current != null?
  │   ├─ YES: return policy.IsAvailable
  │   └─ NO: ← [DEFECT: FINDING-02]
  │       └─ return File.Exists(AppPaths.SingBoxExePath)  ← BYPASSES TRUST CHAIN
  ├─ Check ownership (Free/HeldByAnother/Unavailable)
  └─ return true only if binary check passed AND ownership == Free
```

The `NO` branch at the policy check is the critical defect. When no scope is active, the entire SHA-256 verification, symlink rejection, statx validation, and root-refusal system is bypassed in favor of a bare `File.Exists`.

---

## 2. Concurrency Hazards & Race Conditions

### 2.1 Lock Contention During DNS Resolution (LinuxFirewallManager)

`CreateBlockRules` (line 96) and `UpdateCommittedConfig` (line 239) hold `lock (_gate)` for the entire duration of `ParseServerIps`, which calls `DefaultResolveHost` for each hostname-based server. `DefaultResolveHost` blocks synchronously for up to 3 seconds per host (`task.Wait(TimeSpan.FromSeconds(3))`). With N unresolvable hostnames, the lock is held for up to 3N seconds.

**Blocked operations during this window:**
- `DisableBlockRules()` — cannot lift the kill-switch during a lock-held DNS resolution
- `Dispose()` — cannot clean up on process exit
- `IsArmed` / `IsLoaded` — UI/telemetry property reads block
- `EnableBlockRules()` — cannot engage the kill-switch

This is a priority inversion: a network failure (unresolvable hostnames) prevents the kill-switch from being modified.

### 2.2 Orphaned Background Thread on BoundedTeardown Timeout

`BoundedTeardown.StopBoundedAsync` (line 29) runs `engine.Stop()` on a thread pool thread via `Task.Run`. If the 5-second budget expires, `StopBoundedAsync` returns `false`, but the `stopTask` continues executing indefinitely. When the orphaned `engine.Stop()` eventually completes or faults:
- It may mutate engine state concurrently with a new `StartAsync` call
- The faulted task's exception is unobserved (no `.ContinueWith` or observe handler)
- On .NET 6+, unobserved task exceptions are swallowed by default, but they still represent a correctness hazard

### 2.3 LinuxDnsHardening Thread-Safety Gap

`LinuxDnsHardening` has zero synchronization primitives. Methods `Apply`, `Restore`, and `RestoreStrandedIfAny` perform:
1. File existence checks (`File.Exists`)
2. File writes (`SaveState`)
3. File reads (`LoadState`)
4. Process launches (`RunResolvectl`)

Concurrent execution of `Apply` and `Restore` (possible during rapid connect/disconnect cycles or failover) can interleave state file mutations and issue contradictory `resolvectl` commands. Mitigated in practice because `VpnEngine` serializes lifecycle calls, but the component contract offers no protection.

### 2.4 AppPaths._dataDir Non-Atomic Lazy Init

`DataDir => _dataDir ??= ResolveDataDir()` (line 23) is not thread-safe. The `??=` operator on a static field is not atomic. If multiple threads access `DataDir` concurrently during process startup before `_dataDir` is populated, `ResolveDataDir()` executes multiple times. While idempotent, this is a code smell and causes test parallelism flakiness via `OverrideDataDir` (over 25 test fixtures mutate this field concurrently under xUnit).

---

## 3. Fail-Closed & Leak Invariants

### 3.1 Kill-Switch Fail-Open Analysis

The Linux nftables kill-switch is explicitly fail-OPEN by design:

| Failure Mode | Behavior | Analysis |
|---|---|---|
| `sudo -n nft` fails (no NOPASSWD) | Log warning, traffic routes normally | **Fail-open.** User believes they are protected. |
| Ruleset file write fails (disk full) | Log warning, no blocking | **Fail-open.** |
| `nft -f` load fails (syntax/version) | Log warning, no blocking | **Fail-open.** |
| Split-tunnel mode | Explicitly disarmed | **Correct by design.** |
| Process crash (SIGKILL) | Marker survives; orphan cleanup on next launch | **Correct.** nft table persists (fail-closed for traffic until cleanup). |
| `Dispose()` with DeleteTable failure | `_disposed` stays false, table persists | **Accidentally fail-closed** (traffic stays blocked). |

The fail-open posture is documented and intentional, but there is no mechanism to surface the failure to the user. The `PlatformCapabilityVerifier.VerifyKillSwitchSupport` returns `false` on Linux without a verified adapter, but this only affects the snapshot capabilities report — it does not prevent a user from enabling `block_on_vpn_fail` in config.yaml, in which case the kill-switch silently does not engage.

### 3.2 DNS Leak Lockdown Fail-Open

Both Linux and macOS DNS hardening adapters are explicitly fail-open, never-throw. DNS queries can leak to ISP resolvers if `resolvectl`/`networksetup` commands fail silently. This is documented but not surfaced to the user beyond a log warning.

### 3.3 SIGTERM/SIGKILL Recovery

**LinuxFirewallManager:** The marker-based recovery is sound. The crash leaves the nft table active (fail-closed for traffic, which is correct for a kill-switch). Next launch detects the marker and removes the table.

**LinuxDnsHardening:** The sentinel-based recovery is sound. Crash leaves DNS pinned to the TUN gateway IP (which no longer exists). Next launch detects the sentinel and runs `resolvectl revert`.

**Neither component handles SIGKILL during recovery itself.** If the process is killed during orphan cleanup, the marker/sentinel survives, and the next launch will retry. This is correct.

---

## 4. Security & Privilege Boundary Analysis

### 4.1 Elevation Seam Map

| Component | Elevation Mechanism | Binary Path | Verification |
|---|---|---|---|
| LinuxFirewallManager | `sudo -n` | `/usr/bin/sudo` (hardcoded) | Hardcoded absolute path prevents PATH hijacking. `nft` is passed as argument, resolved by sudo. |
| LinuxDnsHardening | None (unprivileged) | `resolvectl`, `ip` (bare names) | **Vulnerable to PATH hijacking** — attacker can plant malicious `resolvectl` in PATH. |
| MacDnsHardening | `sudo -n` | `/usr/bin/sudo` (hardcoded) | Correct. |
| MacFirewallManager | `sudo -n` | `/usr/bin/sudo` (hardcoded) | Correct. |
| SingBoxManager (Linux) | `pkexec` or `sudo -n` | Resolved via `LinuxRuntimeEnvironment.ResolvePkexec()` | Checks `/usr/bin/pkexec` and `/run/wrappers/bin/pkexec` existence. |
| UpdateChecker (Linux) | `pkexec` | Resolved via `LinuxRuntimeEnvironment.ResolvePkexec()` | Correct. |
| SingBoxRuntimePolicy | None (verification only) | N/A | Validates sing-box binary integrity via SHA-256, statx, and symlink checks. Does not execute. |
| setup (Omarchy) | None (unprivileged) | N/A | By design: never calls sudo/pkexec. |

### 4.2 Command Injection Analysis

All process invocations use discrete argument arrays via `IProcessRunner` (never `sh -c`). This eliminates shell metacharacter injection.

The nftables ruleset builder (`BuildRuleset`) interpolates IP addresses into string templates. IP addresses are validated through `IPAddress.TryParse` and canonicalized via `parsedIp.ToString()`, ensuring only valid IP literals appear in the ruleset. Hostnames are resolved to IPs before insertion. **No injection vector exists in the current implementation.**

### 4.3 Symlink and Path Traversal Defense

**AppPaths (Unix):** Multi-layer defense:
1. `DirectoryInfo.LinkTarget` check before creation
2. `FileAttributes.ReparsePoint` check after creation
3. `File.SetUnixFileMode` + verification

TOCTOU window exists between steps 1 and 2, but exploitation requires write access to the parent directory, which is mitigated by the parent itself being `0700`.

**SingBoxRuntimePolicy (Linux):** Robust defense:
1. `O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC` open flags
2. `statx` with `AT_EMPTY_PATH` on the open fd (not the path)
3. Ancestor directory traversal checking each component for symlinks
4. Before/after inode comparison detecting replacement during hash computation

The acknowledged TOCTOU limitation (ancestor check is point-in-time) cannot be eliminated without `fexecve` or descriptor-based execution.

**Omarchy setup script:** Rigorous defense:
1. `O_NOFOLLOW | O_CLOEXEC` for profile copies with `dir_fd`
2. Rejects all symlinks in build materials
3. Post-install full-tree symlink scan
4. Leading hyphen and trailing dot filename rejection

### 4.4 Data Directory Permission Model

| OS | Directory | Mode | Defense |
|---|---|---|---|
| Linux | `~/.config/vpnrouter/` | `0700` | `EnsurePrivateUnixDirectory` with verify |
| macOS | `~/Library/Application Support/VPNRouter/` | `0700` | Same |
| Windows | `%ProgramData%\VPNRouter\` | ACL-restricted | `TryRestrictWindowsDataDirAcl` (best-effort, **silent failure**) |
| Windows | `%ProgramData%\VPNRouter\bin\` | ACL-restricted | `RestrictWindowsBinDirAcl` (best-effort, **silent failure**) |

The Windows ACL hardening functions are the weakest link. Both use `try { ... } catch { }` with zero logging or telemetry, creating a class of silent failures that leave the most sensitive directories unprotected.

---

## 5. Inconsistencies & Protocol Violations

### 5.1 Inconsistent Privilege Path Hardening

`LinuxFirewallManager` hardcodes `/usr/bin/sudo` for all nft operations. `LinuxDnsHardening` uses bare `"resolvectl"` and `"ip"` names, resolved via ambient `PATH`. Within the same platform layer, there are two different trust models for external binary invocation.

### 5.2 Inconsistent Marker File Permissions

`LinuxFirewallManager.WriteMarker()` uses `File.WriteAllText` (inherits umask permissions). `LinuxFirewallManager.EnableBlockRules()` writes the nft ruleset via `AppPaths.WritePrivateText` (enforced `0600`). Two files in the same component, managed by the same class, have different permission models. The marker file is the one that governs orphan cleanup behavior.

### 5.3 PlatformCapabilityVerifier vs. RouterSession Policy Handling

`RouterSession` (line 53) correctly defaults to `DefaultProduction` on Linux when no scope is active:
```csharp
var policy = SingBoxRuntimePolicy.Current ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);
```

`PlatformCapabilityVerifier.VerifyCanConnect` (line 77) does NOT apply this pattern:
```csharp
var policy = SingBoxRuntimePolicy.Current;
if (policy != null) { ... }
else { /* falls through to File.Exists */ }
```

This inconsistency means the fix has been applied in callers but not in the verifier itself, creating a latent vulnerability for any new caller.

### 5.4 setup Script sing-box Deployment Gap

The Omarchy `setup` script installs `sing-box` into `$plugin_dir/bin/backend/sing-box`. `VPNRouter.Core.AppPaths.SingBoxExePath` resolves to `~/.config/vpnrouter/bin/sing-box`. The binary is installed to a location the core engine never looks at, requiring manual copying. The setup script acknowledges this gap with a notice but does not resolve it.

---

## 6. Prioritized Actionable Findings

### FINDING-P03_privilege_security-01 — **P0** (Critical Security)
- **Source Anchor:** `VPNRouter.Core/AppPaths.cs:254-287` (`RestrictWindowsBinDirAcl`, `TryRestrictWindowsDataDirAcl`)
- **Mechanism:** Both Windows ACL hardening functions wrap their entire logic in `try { ... } catch { }` with zero logging, telemetry, or fallback behavior. If ACL lockdown fails (missing `SeSecurityPrivilege`, filesystem errors, anti-virus interference, domain policy overrides), `%ProgramData%\VPNRouter\bin\` retains its inherited `%ProgramData%` permissions, which grant `BUILTIN\Users` the ability to create and modify files.
- **Threat Scenario:** On a system without an MSI installer (portable/development deployment), a standard unprivileged user replaces `%ProgramData%\VPNRouter\bin\sing-box.exe` with a malicious binary. The Windows Service (`VPNRouter.Service`), running as `LocalSystem`, executes this binary on the next VPN connection, achieving **full local privilege escalation to SYSTEM**.
- **Fix:** 
  1. Log a `Warning` with the exception details when ACL hardening fails.
  2. On failure, mark the bin directory as untrusted and refuse to store or execute binaries from it until permissions are verified.
  3. Consider failing the startup path rather than silently continuing with an unprotected directory.

### FINDING-P03_privilege_security-02 — **P0** (Critical Security)
- **Source Anchor:** `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:77-88`
- **Mechanism:** When `SingBoxRuntimePolicy.Current` is `null` (no scope entered), `VerifyCanConnect` falls through to `File.Exists(AppPaths.SingBoxExePath)`, completely bypassing the SHA-256 hash verification, statx metadata validation, symlink rejection, root-refusal, and binary integrity checks implemented in `SingBoxRuntimePolicy`.
- **Threat Scenario:** On Linux Headless, if `VerifyCanConnect` is called from a code path that neglects to enter a runtime policy scope (or from a new feature that doesn't know about the scope requirement), any file at `~/.config/vpnrouter/bin/sing-box` — including a malicious replacement — will be treated as a valid, launchable binary. This directly violates Headless Invariant #7: "Mere file existence on disk does not establish readiness, integrity, or authority to launch code."
- **Current Mitigation:** `RouterSession` and other Headless callers enter scope before calling `VerifyCanConnect`. This is a caller-side mitigation, not a defense in depth.
- **Fix:** Replace line 77 with:
  ```csharp
  var policy = SingBoxRuntimePolicy.Current
      ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);
  ```
  This matches the pattern already used in `RouterSession`, `VpnEngineAdapter`, and all Feature classes.

### FINDING-P03_privilege_security-03 — **P0** (Critical Security)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:571`
- **Mechanism:** `WriteMarker()` uses `System.IO.File.WriteAllText(_markerPath, "engaged")`, which creates the file with process umask permissions (typically `0644` or `0622`). The marker path is in `AppPaths.DataDir` (which is `0700`), so parent directory permissions protect it. However, the *marker file itself* does not use `AppPaths.WritePrivateText` (which enforces `0600` and verifies permissions), creating a permission inconsistency within the same component. In contrast, the nft ruleset file written two lines earlier at line 144 correctly uses `AppPaths.WritePrivateText`.
- **Threat Scenario:** If `AppPaths.DataDir` permissions are loosened (e.g., by a concurrent test override, a packaging misconfiguration, or a directory traversal in `OverrideDataDir`), a local attacker can:
  1. **Plant a fake marker:** Next launch triggers orphan cleanup, which calls `DeleteTable()` — deleting an actively-engaged kill-switch and silently opening all traffic.
  2. **Delete the marker:** After a crash with the kill-switch engaged, orphan cleanup does not run, leaving the host permanently firewalled with no internet until manual `sudo nft delete table` intervention.
- **Fix:** Replace `File.WriteAllText` with `AppPaths.WritePrivateText` at line 571.

### FINDING-P03_privilege_security-04 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:414`
- **Mechanism:** `BuildRuleset` allows only `10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16` for IPv4 and no IPv6 local ranges. Missing:
  - **RFC 6598 CGNAT `100.64.0.0/10`**: Used by Tailscale, WireGuard mesh overlays, and ISP carrier-grade NAT.
  - **IPv6 loopback `::1/128`**: Blocked by the `policy drop` on `output` chain (the `oif "lo"` rule passes by interface, but `::1` traffic not going through `lo` would be dropped).
  - **IPv6 link-local `fe80::/10`**: Required for IPv6 Neighbor Discovery Protocol and Router Advertisements.
  - **IPv6 ULA `fc00::/7`**: Used in private IPv6 networks.
- **Threat Scenario:** When the kill-switch engages on a headless Linux machine managed via Tailscale (which uses `100.64.0.0/10`), the administrator is immediately locked out. No remote recovery is possible without physical/console access or a pre-existing SSH session. IPv6-only local networks lose router discovery.
- **Fix:** Add to `BuildRuleset`:
  ```csharp
  sb.AppendLine($"add rule inet {TableName} output ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, 100.64.0.0/10 }} accept");
  sb.AppendLine($"add rule inet {TableName} output ip6 daddr {{ ::1/128, fe80::/10 }} accept");
  ```

### FINDING-P03_privilege_security-05 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Headless/Lifecycle/VpnEngineAdapter.cs:62-66, 68-72`
- **Mechanism:** `StartAsync` and `ApplyAsync` are non-`async` methods that enter a `SingBoxRuntimePolicy` scope with `using var _`, then return the pending `Task` from `_engine.StartAsync`/`_engine.ApplyAsync` without awaiting it. The `using` disposes the scope synchronously upon method return, **before** the engine's async pipeline completes.
- **Threat/Failure Scenario:** While `AsyncLocal<T>` copy-on-write semantics preserve the scoped value for async continuations (making this accidentally safe for the engine's internal await chains), any synchronous callback invoked by the engine on the original calling thread after the task is returned — or any code that reads `SingBoxRuntimePolicy.Current` on the calling thread after `StartAsync` returns — will see the reverted (pre-scope) policy value. This is a structural violation of the adapter's contract to ensure policy coverage throughout the operation.
- **Fix:** Declare the methods `async` and await the engine call:
  ```csharp
  public async Task StartAsync(AppSettings settings, CancellationToken ct)
  {
      using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
      await _engine.StartAsync(settings, ct).ConfigureAwait(false);
  }
  ```

### FINDING-P03_privilege_security-06 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Core/AppPaths.cs:314`
- **Mechanism:** `ResolveDataDir()` on Windows uses `Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter")` instead of `Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)`. The `%ProgramData%` environment variable can be overridden in a child process's environment block.
- **Threat Scenario:** A parent process launching VPNRouter with a modified environment (`set ProgramData=C:\Users\attacker\data`) causes the application to read configuration, execute binaries, and write secrets to an attacker-controlled directory. If `%ProgramData%` is unset, `ExpandEnvironmentVariables` returns the literal string `%ProgramData%\VPNRouter`, which could be a writable relative path.
- **Fix:** Replace with `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VPNRouter")`.

### FINDING-P03_privilege_security-07 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:96, 239, 542-564`
- **Mechanism:** `CreateBlockRules` and `UpdateCommittedConfig` hold `lock (_gate)` while `ParseServerIps` resolves hostnames via `DefaultResolveHost` (3-second `task.Wait` timeout per host). With N unresolvable hostnames, the lock is held for up to 3N seconds.
- **Threat Scenario:** A VPN profile with 3 hostname-based servers, where DNS is failing (precisely when a kill-switch is most needed), holds `_gate` for up to 9 seconds. During this window, `DisableBlockRules()` and `Dispose()` are blocked, preventing the kill-switch from being lifted even when the VPN successfully reconnects. The UI thread querying `IsArmed`/`IsLoaded` freezes.
- **Fix:** Resolve hostnames outside the lock. Acquire `_gate` only for the state mutation:
  ```csharp
  var candidateIps = ParseServerIps(configJson); // outside lock
  lock (_gate)
  {
      _serverIps = candidateIps;
      _armed = true;
  }
  ```

### FINDING-P03_privilege_security-08 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:203-231`
- **Mechanism:** In `Dispose()`, if `DeleteTable()` returns `false`, the `_disposed` field remains `false`, but the method returns without throwing. The standard `IDisposable` contract requires that `Dispose` is idempotent and that the object is considered disposed after the call returns, regardless of internal cleanup success.
- **Threat Scenario:** A caller disposes the manager, receives no error, creates a new manager instance, and attempts to arm a new kill-switch. Meanwhile, the old table is still loaded in the kernel (the old manager didn't clean it up). Two `vpnrouter_ks` tables cannot coexist (nft uses `add table` which is idempotent), but the orphaned marker file from the undisposed instance interferes with the new instance's lifecycle.
- **Fix:** Always set `_disposed = true` in `Dispose()` regardless of cleanup success. Attempt cleanup best-effort:
  ```csharp
  if (_disposed) return;
  _disposed = true; // unconditional
  if (_loaded)
  {
      try { if (DeleteTable()) { TryDeleteMarker(); _loaded = false; } }
      catch { }
  }
  ```

### FINDING-P03_privilege_security-09 — **P1** (Major Functional Defect)
- **Source Anchor:** `VPNRouter.Headless/Lifecycle/BoundedTeardown.cs:29-36`
- **Mechanism:** `StopBoundedAsync` runs `engine.Stop()` on a thread pool thread. If the 5-second budget expires, the method returns `false`, but the background `stopTask` continues executing indefinitely. No continuation observes the task's eventual completion or fault.
- **Threat Scenario:** The orphaned `engine.Stop()` eventually completes 30 seconds later, mutating engine fields (`IsRunning`, `SingBoxPid`, event handlers) concurrently with a new `ConnectAsync` that was initiated after the timeout. This causes state corruption in `RouterSession`, which may believe it's connected when the delayed stop tears down the new connection.
- **Fix:** Attach an observe-continuation to catch and log late faults; consider a forceful process kill escalation on timeout.

### FINDING-P03_privilege_security-10 — **P2** (Edge Case)
- **Source Anchor:** `VPNRouter.Core/AppPaths.cs:127-141`
- **Mechanism:** `EnsurePrivateUnixDirectory` checks `info.LinkTarget` before `Directory.CreateDirectory`, then checks `FileAttributes.ReparsePoint` after. A TOCTOU window exists between the two checks where an attacker could replace the path with a symlink.
- **Mitigation:** The parent directory is itself `0700`, so only the owning user can write to it. Exploitation requires the owning user's credentials, at which point the attack is moot.
- **Fix:** No fix needed given the `0700` parent invariant, but document the TOCTOU limitation.

### FINDING-P03_privilege_security-11 — **P2** (Edge Case)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs:43-44`
- **Mechanism:** Uses bare executable names `"resolvectl"` and `"ip"` resolved via ambient `PATH`, unlike `LinuxFirewallManager` which hardcodes `/usr/bin/sudo`. A malicious binary planted in `PATH` ahead of `/usr/bin` would be executed.
- **Mitigation:** `LinuxDnsHardening` runs unprivileged (no sudo wrapper), limiting the impact of a hijacked binary to the current user's privilege level.
- **Fix:** Use `/usr/bin/resolvectl` and `/usr/sbin/ip` (or `/sbin/ip` with fallback probe).

### FINDING-P03_privilege_security-12 — **P2** (Edge Case)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:331-386` (`IsTableAbsent`)
- **Mechanism:** The JSON parser for `nft -j list tables` output rejects any array element that is not a single-property object with key `"table"` or `"metainfo"`. If a future nftables version adds new metadata element types (e.g., `"comment"`, `"version"`), `IsTableAbsent` returns `false`, causing `DeleteTable` to return `false`, which prevents the recovery marker from being deleted.
- **Threat Scenario:** After a nftables upgrade, orphan cleanup permanently fails. The marker persists, and every launch logs a warning and attempts (and fails) to clean up a table that no longer exists, creating a persistent but non-blocking noise issue.
- **Fix:** Skip unknown single-property elements with `continue` instead of `return false` at line 385.

### FINDING-P03_privilege_security-13 — **P2** (Edge Case)
- **Source Anchor:** `VPNRouter.Core/AppPaths.cs:232`
- **Mechanism:** `WindowsIdentity.GetCurrent().User!` uses the null-forgiving operator. In restricted impersonation tokens, anonymous tokens, or certain sandbox environments, `.User` can be null, triggering a `NullReferenceException`.
- **Mitigation:** The exception is caught by the empty `catch { }` block, but this causes the entire ACL lockdown for DataDir to silently fail (compounding FINDING-01).
- **Fix:** Guard with `var currentUser = WindowsIdentity.GetCurrent().User; if (currentUser == null) { _logger.Warning(...); return; }`.

### FINDING-P03_privilege_security-14 — **P2** (Edge Case)
- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs` (entire class)
- **Mechanism:** No synchronization lock. `Apply()`, `Restore()`, and `RestoreStrandedIfAny()` perform non-atomic file I/O and multi-step process executions without any concurrency protection.
- **Mitigation:** `VpnEngine` serializes lifecycle calls in practice, and `LinuxDnsHardening` is fail-open/never-throw by design.
- **Fix:** Add a private `object _gate` and wrap `Apply`/`Restore`/`RestoreStrandedIfAny` in `lock (_gate)` for defense in depth.

### FINDING-P03_privilege_security-15 — **P2** (Edge Case)
- **Source Anchor:** Omarchy `setup` script, lines 580-698 (per inventory O06)
- **Mechanism:** No `flock` advisory lock serializes concurrent `./setup` invocations. Two parallel installs race during the directory swap phase. Additionally, `SIGHUP` and `SIGQUIT` are not caught by the cleanup trap (line 49: `trap cleanup EXIT INT TERM`).
- **Threat Scenario:** SSH disconnect during installation leaves the plugin in an uncommitted state with no automatic rollback.
- **Fix:** Acquire `flock` on `$plugin_dir/bin/.setup.lock` before mutation; add `HUP QUIT` to the trap specification.

---

## 7. Summary

| Severity | Count | Key Themes |
|---|---|---|
| **P0** | 3 | Silent ACL failure enabling LPE; RuntimePolicy bypass via File.Exists; marker file permission inconsistency |
| **P1** | 6 | Missing CGNAT/IPv6 in kill-switch; async scope disposal anti-pattern; environment variable path spoofing; DNS resolution lock contention; incomplete Dispose; orphaned background thread |
| **P2** | 6 | TOCTOU in symlink checks; bare executable names in PATH; rigid nft JSON parsing; WindowsIdentity null; DNS hardening threading; setup script concurrency |
| **Total** | **15** | |

**Cross-cutting observations:**
1. The codebase consistently uses fail-open semantics for privilege-dependent operations (firewall, DNS hardening) but does not surface these failures to end users, creating a false sense of security.
2. The `SingBoxRuntimePolicy` trust system is well-designed and thoroughly tested, but `PlatformCapabilityVerifier` — the single central capability gate — has a policy-bypassing fallback that undermines the entire system.
3. Windows ACL hardening is the most critical gap: the `catch { }` pattern means the application cannot distinguish between "permissions are locked down" and "permissions are wide open" — it treats both identically and proceeds.
4. The Omarchy `setup` script has a notably strong security posture (zero privilege escalation, bounded protocol handshake, symlink rejection, O_NOFOLLOW profile copies) that exceeds the core platform code in several areas.

---

**Review completed: 2026-09-26**  
**Reviewer: Opus adversarial worker (lane P03_privilege_security)**  
**Verdict: SUCCESS — 3 P0, 6 P1, 6 P2 findings discovered.**
