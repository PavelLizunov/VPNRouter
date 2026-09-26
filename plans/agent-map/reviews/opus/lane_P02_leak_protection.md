# Adversarial Review: Lane P02 — DNS, Firewall & Leak Protection

> **TL;DR:** Deep adversarial audit of `FirewallManager`, `LeakProtection`, `LinuxFirewallManager`, and `LinuxDnsHardening` uncovered **5 P0** (critical leak/crash/security), **6 P1** (major functional defect), and **6 P2** (edge-case/performance) findings. The most severe: fire-and-forget DNS lockdown teardown during app shutdown can orphan firewall rules that break all DNS on the host; the Linux nftables kill-switch omits CGNAT ranges causing Tailscale lockout on headless machines; and the Windows `FirewallManager` instance methods have zero thread synchronization while being called from concurrent `HealthMonitor` and `VpnEngine.Stop` paths.

---

## 1. Subsystem State Machines

### 1.1. Windows FirewallManager (per-process kill-switch)

**Implemented state machine:**

```
                        [CLEAN]
                           │
              CreateBlockRules(apps)
              ── CleanupOrphanedRules()
              ── _managedRules.Clear()
              ── ResolveProcessPath → netsh add rule enable=no
                           │
                           ▼
                     [DISABLED RULES]
                     _managedRules populated
                     _requestedNames stashed
                           │
              ┌────────────┴────────────┐
        sing-box crash         Clean VpnEngine.Stop
              │                         │
    EnableBlockRules()                  │
    ── late-resolve                     │
    ── netsh set rule enable=yes        │
              │                         │
              ▼                         │
        [ENABLED RULES]                 │
              │                         │
        sing-box restart                │
              │                         │
    DisableBlockRules()                 │
    ── netsh set rule enable=no         │
              │                         │
              └────────────┬────────────┘
                           │
              DeleteAllRules() / Dispose()
              ── netsh delete rule
              ── _managedRules.Clear()
                           │
                           ▼
                        [CLEAN]
```

**Desynchronization risk:** No synchronization primitive protects `_managedRules` or `_requestedNames`. `HealthMonitor` timer thread calls `EnableBlockRules()` while `VpnEngine.Stop()` concurrently calls `DeleteAllRules()` → `InvalidOperationException` on concurrent `List<string>` iteration/mutation.

### 1.2. Windows DNS Lockdown (DnsLockdownPolicy + WindowsDnsHardening)

**Implemented state machine:**

```
         ┌────────────────────────────────────────┐
         │          _lockdownEffective = false     │
         │               [UNLOCKED]               │
         └─────────────────┬──────────────────────┘
                           │
     EnableLockdownIfConfigured(true) / ReconcileLockdownForHealth(serving=true)
     ─── DnsLockdownPolicy.Decide → Enable
     ─── _lockdownEffective = true
     ─── Task.Run { EnableDnsLockdownAsync }  ← FIRE AND FORGET
                           │
                           ▼
         ┌────────────────────────────────────────┐
         │          _lockdownEffective = true      │
         │                [LOCKED]                │
         └─────────────────┬──────────────────────┘
                           │
     ReconcileLockdownForHealth(serving=false) / Restore()
     ─── _lockdownEffective = false
     ─── Task.Run { DisableDnsLockdownAsync }  ← FIRE AND FORGET
                           │
                           ▼
         ┌────────────────────────────────────────┐
         │          _lockdownEffective = false     │
         │              [UNLOCKED]                │
         │    (netsh may still be running!)        │
         └────────────────────────────────────────┘
```

**Critical desynchronization:** `_lockdownEffective` is updated *immediately* but the actual netsh firewall mutation happens *asynchronously*. There is a window where the logical state (unlocked) diverges from the physical state (rules still active). Multiple rapid transitions can queue conflicting Task.Run invocations that execute out of order.

### 1.3. Linux nftables Kill-Switch (LinuxFirewallManager)

**Implemented state machine:**

```
    [INITIAL: _armed=false, _loaded=false]
                    │
    CreateBlockRules(isFullTunnel=true)
    ── ReadServerIps() from current.json
    ── _armed=true
                    │
                    ▼
    [ARMED: _armed=true, _loaded=false]
                    │
    EnableBlockRules()  (on VPN failure)
    ── WritePrivateText(ruleset)
    ── sudo -n nft -f .conf
    ── WriteMarker()
    ── _loaded=true
                    │
                    ▼
    [LOADED: _armed=true, _loaded=true]
          │                    │
    DisableBlockRules()   UpdateCommittedConfig(json, true)
    ── DeleteTable()      ── ParseServerIps(json)
    ── TryDeleteMarker()  ── WritePrivateText(newRuleset)
    ── _loaded=false      ── sudo -n nft -f .conf  ← ATOMIC REFRESH
          │                    │
          ▼                    ▼
    [ARMED]              [LOADED/REFRESHED]
          │
    DeleteAllRules() / Dispose()
    ── DeleteTable() → _armed=false, _loaded=false
          │
          ▼
    [TEARDOWN: _armed=false, _loaded=false]
```

**Incomplete transition on Dispose failure:** If `DeleteTable()` fails inside `Dispose()`, `_disposed` is NOT set to `true` and `_loaded` remains `true`. The object is logically leaked — GC won't retry, and the nft table survives.

### 1.4. LinuxDnsHardening

**Implemented state machine:** Stateless aside from a filesystem sentinel. No lock, no internal state enum.

```
    [IDLE]
      │
    Apply(dnsTarget)
    ── Check resolvectl available
    ── ip route get → TUN interface
    ── SaveState({Interface})   ← sentinel file
    ── resolvectl dns <iface> <target>
    ── resolvectl domain <iface> ~.
    ── resolvectl flush-caches
      │
      ▼
    [HARDENED]  (sentinel exists)
      │
    Restore() / RestoreStrandedIfAny()
    ── LoadState() → interface
    ── resolvectl revert <iface>
    ── resolvectl flush-caches
    ── TryDeleteState()
      │
      ▼
    [IDLE]
```

**Missing transition:** No state protects against concurrent `Apply()` + `Restore()` — interleaved file writes and resolvectl invocations can produce contradictory DNS states.

---

## 2. Concurrency Hazards & Race Conditions

### 2.1. Windows FirewallManager Instance Thread Safety (FINDING-P02-06)

`_managedRules` and `_requestedNames` are unsynchronized `List<string>`. `HealthMonitor` timer → `EnableBlockRules()` and `VpnEngine.Stop()` → `DeleteAllRules()` / `Dispose()` run on different threads. A concurrent `foreach` + `Clear()` on the same `List<string>` is undefined behavior in .NET — causes `InvalidOperationException` or silent corruption.

### 2.2. ReconcileLockdownForHealth TOCTOU (FINDING-P02-02)

The `volatile` on `_lockdownEffective` ensures visibility but not atomicity of the compound operation: `Decide(read _lockdownEffective) → set _lockdownEffective → spawn Task.Run`. Three concurrent callers (HealthMonitor tick, crash hook, warm-up callback) can all read `_lockdownEffective = true`, all decide `Disable`, all set `false`, and spawn three `DisableDnsLockdownAsync` tasks — followed by a fourth caller reading `false` and spawning `Enable`. The fire-and-forget tasks have no ordering guarantee; the final netsh state is nondeterministic.

### 2.3. LinuxFirewallManager Lock Contention (FINDING-P02-11)

`DefaultResolveHost` (3s timeout per host) and `RunSudo` (10s timeout) execute inside `lock(_gate)`. With 3 unresolvable hostnames, `_gate` is held for 9+ seconds. Any concurrent `Dispose()`, `DisableBlockRules()`, or property access blocks completely. If called from the UI thread, the entire GUI freezes.

### 2.4. LinuxDnsHardening Unsynchronized (FINDING-P02-09)

No synchronization whatsoever. Concurrent `Apply()` and `Restore()` (rapid connect/disconnect) produce interleaved `File.WriteAllText` / `File.Delete` and `resolvectl` commands. The sentinel file can be deleted mid-Apply, or Apply can overwrite a sentinel that Restore just read.

---

## 3. Fail-Closed & Leak Invariants

### 3.1. Process Exit During DNS Lockdown Teardown (FINDING-P02-01)

`WindowsDnsHardening.Restore()` (line 276) spawns `_ = Task.Run(async () => { await FirewallManager.DisableDnsLockdownAsync(log); })` — fire-and-forget. `Restore()` returns immediately. If the host process exits (which it does during `VpnEngine.Stop()` → app shutdown), the `Task.Run` is aborted by the CLR before the 9 sequential `RunNetshStatic` calls inside `DisableDnsLockdownAsync` complete. Result: **DNS rules remain active** (UDP/53, TCP/53, TCP/853 blocked on non-loopback), breaking all DNS resolution until the user either runs VPNRouter again (orphan cleanup fires) or manually deletes the firewall rules.

**Severity:** P0 — complete DNS failure on the host machine.

### 3.2. Cancellation Token Not Propagated (FINDING-P02-04)

`EnableDnsLockdownAsync` and `DisableDnsLockdownAsync` create a 5-second linked `CancellationTokenSource` passed to `Task.Run(..., timeoutCts.Token)`. The delegate body executes 7–9 sequential `RunNetshStatic` calls that **never check the cancellation token**. If the timeout fires after 3 slow calls, `OperationCanceledException` is thrown to the awaiter, but the thread pool worker **continues running the remaining 4–6 netsh commands**. During teardown, this means background enable commands race with disable commands.

### 3.3. Windows FirewallManager Fails Open Without Elevation (Documented but Unreported)

`RunNetsh` returns `false` on `netsh` exit code 1 (elevation required) and logs a warning but does not throw. `CreateBlockRules` silently produces zero rules. The kill-switch is completely inert when running without administrator privileges, but no error is surfaced to the user or upstream caller. `CreateBlockRules` returns normally, and `VpnEngine` logs "Created 0 block rules" — the user believes the kill-switch is active.

### 3.4. Linux Kill-Switch Fails Open Without sudoers Grant (Documented but Correct)

`LinuxFirewallManager` correctly logs and does not block if `sudo -n nft` fails. This is an explicit design decision (fail-open) matching the macOS `pfctl` pattern. The risk is that the user has `block_on_vpn_fail = true` in their profile but no NOPASSWD sudoers entry for `nft`, and the UI does not indicate the kill-switch could not arm.

---

## 4. Security & Privilege Boundaries

### 4.1. Bare Executable Names in LinuxDnsHardening (FINDING-P02-14)

`LinuxDnsHardening` uses `"resolvectl"` and `"ip"` (bare names resolved via ambient `PATH`), while `LinuxFirewallManager` correctly hardcodes `/usr/bin/sudo`. If `PATH` is manipulated (e.g., by a parent launcher, Flatpak, or a compromised environment), a rogue `resolvectl` or `ip` binary could be executed. Since `LinuxDnsHardening` runs with the user's privileges (not root), the blast radius is limited to DNS hijacking within the user's session — but that is precisely the attack surface DNS hardening is meant to close.

### 4.2. nftables Ruleset Injection Defense (Sound)

`LinuxFirewallManager.ParseServerIps` validates every candidate through `IPAddress.TryParse` and `IPAddress.ToString()` canonicalization before interpolating into the nft ruleset string. Malformed `current.json` payloads cannot inject arbitrary nftables syntax. The ruleset file is written via `AppPaths.WritePrivateText` (mode 0600). This defense is sound.

### 4.3. Windows netsh Argument Splitting (Sound with Limitation)

`SplitShellArgs` strips outer quotes and splits on whitespace. Embedded double quotes in process paths or rule names would cause unpredictable tokenization, but this is mitigated by `ProcessStartInfo.ArgumentList` re-quoting. The only untested edge case is paths containing literal `"` characters, which are rare on Windows.

### 4.4. Non-Atomic State Files (FINDING-P02-10)

Both `WindowsDnsHardening.SaveState` (line 446: `File.WriteAllText(StatePath, json)`) and `LinuxDnsHardening.SaveState` (line 244: `File.WriteAllText(_statePath, ...)`) write directly without atomic rename. A crash during write produces a truncated or zero-byte file. On next startup, `LoadState()` returns `null`, and `Restore()` cannot recover the user's original settings — they are permanently overwritten by the next `Apply()`.

---

## 5. Inconsistencies & Protocol Violations

### 5.1. LeakProtection NRE on settings.App (FINDING-P02-07)

`LeakProtection.ValidateConfig` (lines 264-270) accesses `settings.App.ConfigMode`, `settings.App.RoutingMode`, and `settings.App.RoutingAppsMode` without null-conditional operators. If `settings` is non-null but `settings.App` is null (possible from a partially deserialized or default-constructed `AppSettings`), `NullReferenceException` is thrown. This aborts validation entirely — a config that should have been rejected passes through unchecked.

### 5.2. Polarity Check Gap in LeakProtection

The polarity check (lines 264-277) only validates `Route.Final` against routing mode expectations but does not validate `Dns.Final` alignment in split-include mode. A split-include config with `Route.Final = "direct"` but `Dns.Final = "vpn-dns"` would pass validation despite sending all non-listed DNS through the VPN — a privacy-positive but functionally incorrect behavior that can cause resolution failures for direct-routed apps.

### 5.3. DnsLockdownPolicy Semantic Mismatch with Windows Firewall Behavior

`DnsLockdownPolicy.Decide` treats `_lockdownEffective` as tracking installed firewall state. But `_lockdownEffective` is set *before* the async netsh task runs — it tracks *intent*, not *physical state*. If the netsh call fails (timeout, permission), `_lockdownEffective` is `true` but no rules exist. Subsequent `Decide` calls return `None` (already effective), and the lockdown is never retried.

---

## 6. Prioritized Actionable Findings

### FINDING-P02-01 — P0: Fire-and-Forget DNS Lockdown Teardown Orphans Rules on Process Exit

- **Source Anchor:** `VPNRouter.Core/Services/WindowsDnsHardening.cs:276-286`
- **Mechanism:** `Restore()` spawns `_ = Task.Run(DisableDnsLockdownAsync)` and returns immediately. `VpnEngine.Stop()` calls `Restore()` then the process exits. The CLR aborts the `Task.Run` before the 9 netsh `delete rule` commands complete.
- **Threat/Failure Scenario:** User stops VPN → app exits → UDP/53 + TCP/53 + TCP/853 remain blocked on all non-loopback adapters → complete DNS failure → user cannot browse until next VPNRouter launch (orphan cleanup) or manual firewall rule deletion.
- **Fix:** Await the `DisableDnsLockdownAsync` task synchronously during `Restore()` (acceptable 5s block during shutdown), OR register a `ProcessExit` handler that blocks on the pending teardown task, OR use `CancellationTokenSource` + `Task.WhenAny` with a bounded wait.

### FINDING-P02-02 — P0: TOCTOU Race on _lockdownEffective During Rapid Tunnel Flapping

- **Source Anchor:** `VPNRouter.Core/Services/WindowsDnsHardening.cs:181-221`
- **Mechanism:** `ReconcileLockdownForHealth` is called from HealthMonitor timer, crash hook, and warm-up callback on different threads. `volatile bool` provides visibility but not atomicity. Multiple callers can read the same `_lockdownEffective` value, each decide to transition, and spawn overlapping `EnableDnsLockdownAsync` / `DisableDnsLockdownAsync` tasks that execute out of order.
- **Threat/Failure Scenario:** Rapid tunnel flap (crash → reconnect → health tick) leaves netsh in an inconsistent state — DNS port blocks remain enabled while `_lockdownEffective = false`, or vice versa.
- **Fix:** Guard the read-decide-set-spawn sequence with a `lock` or `SemaphoreSlim`. Alternatively, use an async channel/queue to serialize lockdown transitions. Set `_lockdownEffective` only after the netsh task completes, not before.

### FINDING-P02-03 — P0: Linux nftables Kill-Switch Missing CGNAT (100.64.0.0/10) — Tailscale/Overlay Lockout

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:414`
- **Mechanism:** `BuildRuleset` allows only `10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16`. RFC 6598 CGNAT `100.64.0.0/10` is omitted. Tailscale uses `100.x.y.z` addresses; WireGuard mesh overlays often use this range.
- **Threat/Failure Scenario:** Kill-switch engages on a headless Linux server managed via Tailscale SSH → all Tailscale connectivity severed → operator locked out, requiring physical console access to run `sudo nft delete table inet vpnrouter_ks`.
- **Fix:** Add `100.64.0.0/10` to the IPv4 allow-list in `BuildRuleset`. Also add `fe80::/10` (IPv6 link-local) and `fc00::/7` (IPv6 ULA) to prevent NDP/RA breakage. Update corresponding tests.

### FINDING-P02-04 — P0: Cancellation Token Not Propagated in DNS Lockdown Async Methods

- **Source Anchor:** `VPNRouter.Core/Services/FirewallManager.cs:722-824, 871-891`
- **Mechanism:** The 5-second `timeoutCts` token is passed only to `Task.Run(..., token)`. Inside the delegate, 7–9 sequential `RunNetshStatic` calls proceed without checking `token.IsCancellationRequested`. After timeout, the caller's `await` throws `OperationCanceledException`, but the background thread **continues executing all remaining netsh commands**.
- **Threat/Failure Scenario:** (1) Timeout fires mid-enable → partial rule set installed (some ports blocked, others not). (2) Background enable commands from a timed-out task race with a subsequent disable call → inconsistent firewall state.
- **Fix:** Insert `timeoutCts.Token.ThrowIfCancellationRequested()` before each `RunNetshStatic` call inside the `Task.Run` delegate.

### FINDING-P02-05 — P0: LinuxFirewallManager.Dispose() Fails to Set _disposed on DeleteTable Failure

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:203-231`
- **Mechanism:** When `_loaded == true` and `DeleteTable()` returns `false`, the `if (DeleteTable())` branch is skipped entirely. `_disposed` remains `false`, `_loaded` remains `true`. `Dispose()` returns without throwing (correct for IDisposable), but:
  - The object is not marked disposed — subsequent calls to `EnableBlockRules()` or `UpdateCommittedConfig()` can proceed.
  - The nftables table `inet vpnrouter_ks` survives, blocking all egress indefinitely.
  - The marker file survives, but the orphan cleanup path also calls `DeleteTable()` — if the same sudo/nft issue persists, the user is permanently locked out.
- **Threat/Failure Scenario:** `sudo -n nft` fails (sudoers revoked, nft binary missing) → `Dispose()` silently fails → user's internet remains blocked until manual intervention.
- **Fix:** Set `_disposed = true` unconditionally in the `_loaded` branch (matching the standard IDisposable contract that Dispose is terminal). Log the failure prominently. Consider falling back to direct nft binary invocation without sudo as a last resort.

### FINDING-P02-06 — P1: Windows FirewallManager Instance Methods Not Thread-Safe

- **Source Anchor:** `VPNRouter.Core/Services/FirewallManager.cs:108,114,269-309,333-341`
- **Mechanism:** `_managedRules` (`List<string>`) and `_requestedNames` (`List<string>`) are accessed from multiple threads (HealthMonitor timer → `EnableBlockRules()`, VpnEngine → `CreateBlockRules()` / `DeleteAllRules()` / `Dispose()`). No lock, no `ConcurrentBag`, no synchronization.
- **Threat/Failure Scenario:** Concurrent `EnableBlockRules()` + `DeleteAllRules()` → `foreach` over `_managedRules` while `Clear()` modifies it → `InvalidOperationException` crash during kill-switch activation/teardown — the worst possible moment.
- **Fix:** Add a private `object _gate = new()` and wrap all `_managedRules` / `_requestedNames` access in `lock(_gate)`. Alternatively, use `ConcurrentBag<string>` and snapshot before iteration.

### FINDING-P02-07 — P1: LeakProtection NullReferenceException on settings.App

- **Source Anchor:** `VPNRouter.Core/Services/LeakProtection.cs:265,267,269`
- **Mechanism:** `settings.App.ConfigMode`, `settings.App.RoutingMode`, `settings.App.RoutingAppsMode` accessed without null-conditional. If `settings != null && settings.App == null`, NRE is thrown.
- **Threat/Failure Scenario:** Validation aborts with unhandled exception → config that should have been rejected is not validated → potential routing inversion or polarity leak.
- **Fix:** Use `settings.App?.ConfigMode`, `settings.App?.RoutingMode`, `settings.App?.RoutingAppsMode`. Guard the entire block with `if (settings?.App != null)`.

### FINDING-P02-08 — P1: Rigid IsTableAbsent() JSON Parsing Rejects Unknown nftables Metadata

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:331-386`
- **Mechanism:** `IsTableAbsent()` rejects any nft JSON array element with `propCount > 1` or an unrecognized single property name (not `"table"` or `"metainfo"`). A newer nftables version adding metadata fields (e.g., `"comment"`, `"version"`) causes `return false`.
- **Threat/Failure Scenario:** After an nftables package update, `DeleteTable()` fails → orphan cleanup cannot confirm table absence → marker file retained → kill-switch believed to be still engaged → repeated futile cleanup attempts on every boot, and if the table truly exists, the user's internet stays blocked.
- **Fix:** Whitelist known property names (`"table"`, `"metainfo"`) and *skip* (not reject) unrecognized properties. Only return `false` when an element explicitly matches `inet vpnrouter_ks`.

### FINDING-P02-09 — P1: LinuxDnsHardening Has Zero Thread Synchronization

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs:57-173`
- **Mechanism:** `Apply()`, `Restore()`, and `RestoreStrandedIfAny()` perform multi-step file I/O and `resolvectl` invocations with no locking. Concurrent calls from rapid connect/disconnect cycles produce interleaved operations.
- **Threat/Failure Scenario:** `Apply()` writes sentinel → concurrent `Restore()` deletes sentinel → `Apply()` continues with `resolvectl dns` but sentinel is gone → crash recovery impossible on next launch.
- **Fix:** Add a private `object _gate = new()` and wrap `Apply()`, `Restore()`, and `RestoreStrandedIfAny()` in `lock(_gate)`.

### FINDING-P02-10 — P1: Non-Atomic State File Writes in WindowsDnsHardening and LinuxDnsHardening

- **Source Anchor:** `VPNRouter.Core/Services/WindowsDnsHardening.cs:446`, `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs:244`
- **Mechanism:** `File.WriteAllText(path, json)` is not atomic on any OS. A crash or SIGKILL during write produces a truncated file. `LoadState()` catches the deserialization error and returns `null`.
- **Threat/Failure Scenario:** On next startup, `Apply()` detects the state file exists, calls `Restore()`, but `LoadState()` returns `null` → original registry values (Windows) or interface name (Linux) lost → `Apply()` saves the *already-modified* values as "original" → user's baseline DNS settings permanently overwritten.
- **Fix:** Write to a temporary file (e.g., `path + ".tmp"`), then `File.Move(tmp, path, overwrite: true)` for an atomic rename on the same filesystem. Or use `AppPaths.WritePrivateText` which should provide this guarantee.

### FINDING-P02-11 — P1: Lock Contention During DNS Resolution Inside _gate Critical Section

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:96,239,542-564`
- **Mechanism:** `CreateBlockRules` and `UpdateCommittedConfig` hold `lock(_gate)` while calling `ParseServerIps` → `_resolveHost(candidate)` → `DefaultResolveHost` (3s timeout per host). Multiple unresolvable hostnames cause up to 9+ seconds under lock.
- **Threat/Failure Scenario:** While the lock is held, `Dispose()` (app shutdown) blocks for the full DNS timeout duration. On UI thread callers querying `IsArmed` or `IsLoaded`, the GUI freezes.
- **Fix:** Perform DNS resolution and config parsing *outside* `lock(_gate)`. Acquire the lock only for the final state mutation (assigning `_serverIps`, `_armed`, `_loaded`).

### FINDING-P02-12 — P2: Kill-Switch Fails Open for Processes Launched After VPN Crash

- **Source Anchor:** `VPNRouter.Core/Services/FirewallManager.cs:278-293`
- **Mechanism:** `EnableBlockRules()` late-resolves processes in `_requestedNames` that were unresolvable at connect time. If a process is not running at both connect time AND crash time, no firewall rule exists. A user launching Discord 5 seconds after the crash has no protection.
- **Threat/Failure Scenario:** The app that the user specifically wanted to protect (Discord, Telegram) launches after the VPN dies → sends traffic with the user's real IP → complete kill-switch bypass.
- **Fix:** Maintain a persistent cache of previously resolved executable paths (e.g., `%ProgramData%\VPNRouter\exe-path-cache.json`). Scan common installation directories (`%LocalAppData%\Programs\*`, `%AppData%\*`) as fallback. Document this fundamental Windows per-app firewall limitation.

### FINDING-P02-13 — P2: Dead Code — ConsoleEncoding Resolved but Never Used

- **Source Anchor:** `VPNRouter.Core/Services/FirewallManager.cs:132-152`
- **Mechanism:** `ConsoleEncoding` is resolved at type init via `GetOEMCP()` P/Invoke + `CodePagesEncodingProvider.Instance` registration, but is never referenced. `IProcessRunner` does not expose a `StandardOutputEncoding` surface.
- **Threat/Failure Scenario:** Localized netsh error output (Cyrillic on RU Windows, umlauts on DE) decoded as UTF-8 → mojibake in `vpnrouter.log`. Not a functional defect but hampers debugging.
- **Fix:** Either (a) remove the dead code + P/Invoke, or (b) extend `ProcessRequest` with an optional `OutputEncoding` property and pass `ConsoleEncoding` to netsh invocations.

### FINDING-P02-14 — P2: Bare Executable Names in LinuxDnsHardening PATH-Dependent

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs:43-44`
- **Mechanism:** `"resolvectl"` and `"ip"` resolved via ambient `PATH`. `LinuxFirewallManager` hardcodes `/usr/bin/sudo`. Inconsistent privilege boundary treatment.
- **Threat/Failure Scenario:** In a manipulated PATH environment (Flatpak, snap, malicious parent process), a rogue binary could be executed instead of the real `resolvectl`/`ip`, hijacking DNS configuration within the user session.
- **Fix:** Resolve full paths at construction time (e.g., `/usr/bin/resolvectl`, `/usr/sbin/ip`, `/sbin/ip`) with fallback probing, matching the `LinuxFirewallManager` pattern.

### FINDING-P02-15 — P2: Fragile Netsh Output Parsing in FindRulesByPrefixes

- **Source Anchor:** `VPNRouter.Core/Services/FirewallManager.cs:470-494`
- **Mechanism:** Assumes the *first non-empty line* after a blank-line boundary contains the rule name after a colon. Windows locale-specific headers, banners, or AV-injected firewall metadata could shift the rule name line position, causing rules to be missed.
- **Threat/Failure Scenario:** Orphaned kill-switch block rules not discovered during `CleanupOrphanedRules()` → user's internet permanently blocked for specific applications after a crash.
- **Fix:** Instead of consuming only the first line per block, scan all lines in each block for a field whose label matches the expected "Rule Name" pattern (or more robustly, use `netsh advfirewall firewall show rule name=all format=csv` which produces machine-parseable output).

### FINDING-P02-16 — P2: Missing IPv6 Link-Local and ULA in nftables Kill-Switch Ruleset

- **Source Anchor:** `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:414`
- **Mechanism:** `BuildRuleset` does not include `fe80::/10` (link-local) or `fc00::/7` (ULA) in the allow-list. When the kill-switch engages, all IPv6 neighbor discovery (NDP), router advertisements (RA), and ULA traffic is dropped by the `policy drop` chain.
- **Threat/Failure Scenario:** IPv6-only local services and NDP break. Default gateway via RA is lost. On dual-stack networks, the IPv4 gateway may still work, but IPv6 routing is permanently broken until the kill-switch is lifted.
- **Fix:** Add `ip6 daddr { ::1, fe80::/10, fc00::/7 }` accept rule to `BuildRuleset`, before the server-specific IPv6 pass rules.

### FINDING-P02-17 — P2: Unchecked Null Elements in LeakProtection Collections

- **Source Anchor:** `VPNRouter.Core/Services/LeakProtection.cs:349,564-566`
- **Mechanism:** `ValidateProxyEndpoint` accesses `ep.Peers[i].PublicKey` without null-checking `ep.Peers[i]`. `ValidateOutboundServersScopeAware` calls `IsProxyLikeOutbound(ob)` which accesses `ob.Type` — throws NRE if `ob` is null. Malformed deserialized configs with null collection elements cause unhandled exceptions instead of structured validation errors.
- **Threat/Failure Scenario:** A malformed `current.json` or custom config with null array elements crashes the validation pipeline, potentially allowing an invalid config through unchecked.
- **Fix:** Add null guards: `if (ep.Peers[i] == null) { errors.Add(...); continue; }` and `if (ob == null) continue;` before accessing properties.

---

## 7. Summary Matrix

| ID | Severity | Component | Summary |
|---|---|---|---|
| FINDING-P02-01 | **P0** | WindowsDnsHardening | Fire-and-forget teardown orphans DNS firewall rules on exit |
| FINDING-P02-02 | **P0** | WindowsDnsHardening | TOCTOU race on `_lockdownEffective` during tunnel flapping |
| FINDING-P02-03 | **P0** | LinuxFirewallManager | Missing CGNAT `100.64.0.0/10` causes Tailscale lockout |
| FINDING-P02-04 | **P0** | FirewallManager | Cancellation token not propagated in DNS lockdown async |
| FINDING-P02-05 | **P0** | LinuxFirewallManager | `Dispose()` fails to set `_disposed` on `DeleteTable` failure |
| FINDING-P02-06 | **P1** | FirewallManager | Instance methods not thread-safe (`_managedRules` race) |
| FINDING-P02-07 | **P1** | LeakProtection | NRE on `settings.App` null access |
| FINDING-P02-08 | **P1** | LinuxFirewallManager | Rigid `IsTableAbsent()` rejects unknown nft metadata |
| FINDING-P02-09 | **P1** | LinuxDnsHardening | Zero thread synchronization on file I/O + resolvectl |
| FINDING-P02-10 | **P1** | WindowsDnsHardening, LinuxDnsHardening | Non-atomic state file writes risk permanent setting loss |
| FINDING-P02-11 | **P1** | LinuxFirewallManager | Lock held during synchronous DNS resolution (up to 9s) |
| FINDING-P02-12 | **P2** | FirewallManager | Kill-switch fails open for processes launched post-crash |
| FINDING-P02-13 | **P2** | FirewallManager | Dead code — `ConsoleEncoding` resolved but never used |
| FINDING-P02-14 | **P2** | LinuxDnsHardening | Bare executable names PATH-dependent |
| FINDING-P02-15 | **P2** | FirewallManager | Fragile netsh output parsing locale-sensitive |
| FINDING-P02-16 | **P2** | LinuxFirewallManager | Missing IPv6 link-local/ULA in nft allow-list |
| FINDING-P02-17 | **P2** | LeakProtection | Unchecked null elements in outbound/peer collections |

---

**Reviewed by:** Opus adversarial worker  
**Lane:** P02_leak_protection  
**Date:** 2026-09-26  
**Files reviewed:** `FirewallManager.cs` (1060 lines), `LeakProtection.cs` (892 lines), `LinuxFirewallManager.cs` (643 lines), `LinuxDnsHardening.cs` (266 lines), `WindowsDnsHardening.cs` (509 lines), `DnsLockdownPolicy.cs` (60 lines), plus 3 inventory evidence sheets (C16, C33, C35)  
**Total findings:** 5 P0 · 6 P1 · 6 P2
