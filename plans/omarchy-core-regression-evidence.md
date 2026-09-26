# Omarchy Core Regression Evidence Report

**Date:** 2026-09-17 22:07 UTC  
**Target Worker:** `omarchy-test` / `omarchytest` (user `tester`)  
**Staged Snapshot:** `2d9331e5343ee835cd8c83c7a794b31aed3b4511` (staged at `/home/tester/vpnrouter-omarchy-checks/2d9331e5343ee835cd8c83c7a794b31aed3b4511`)  
**SDK Path:** `/home/tester/.local/share/vpnrouter-omarchy-sdk/10.0.301/dotnet` (.NET SDK 10.0.301, RID `linux-x64`)  

---

## 1. Remote Worker Preflight & Environment

- **Identity**: `tester@omarchytest` (UID 1000, GID 1000, groups `tester`, `wheel`).
- **OS / Kernel**: Omarchy 4.0.4 Quattro (Arch Linux x86_64, kernel `7.2.5-3-omarchy #1 SMP PREEMPT_DYNAMIC`).
- **Resources**:
  - Uptime: 3h 58m, Load average: `0.99, 1.18, 1.29`
  - RAM: 7.7 GiB total, 2.3 GiB used, 5.4 GiB available; Swap: 15 GiB total, 0 B used
  - Disk: `/dev/sda2` 48 GiB total, 15 GiB used, 33 GiB available (32% used)
- **SDK Verification**:
  - `dotnet --version`: `10.0.301`
  - Base Path: `/home/tester/.local/share/vpnrouter-omarchy-sdk/10.0.301/sdk/10.0.301/`
  - Runtimes: `Microsoft.AspNetCore.App 10.0.9`, `Microsoft.NETCore.App 10.0.9`
- **Conflicting Jobs / Processes**:
  - No active `sing-box`, `vpnrouter`, or live networking processes.
  - Only idle MSBuild and Roslyn VBCSCompiler daemon processes from background node reuse.

---

## 2. Test Fixture Review & Flock / Routing Safety Analysis

### 2.1 Canonical Focused Filter (Safe)
1. **`ConfigGeneratorTests`**:
   - Pure configuration generation unit tests.
   - Evaluates JSON serialization, outbound structures, DNS routing rules, and strict DNS modes in memory.
   - No process spawning, no network interfaces, no `TunOwnershipLock` interactions.
2. **`VpnEngineStartAsyncSeamTests`**:
   - Characterization tests for early-throw and guard paths in `VpnEngine.StartAsync`.
   - Settings configured with `FlushDnsOnStart = false`, `BypassRussianTraffic = false`, `skipVpnConflictCheck = true`.
   - Mocked/stubbed dependencies: `StubProcessScanner` (`IProcessScanner`), `TrackingFirewallManager` (`IFirewallManager`).
   - Aborts deterministically in early phases (phases 1-2) on empty servers or disabled subscriptions before any sing-box process is launched or network routing/firewall is mutated.
3. **`SingBoxManagerProcessRunnerTests`**:
   - Tests `IProcessRunner` abstraction on `SingBoxManager` using `FakeProcessRunner`.
   - Crucially, all methods executing `manager.StartWithJson` or `manager.Stop` include explicit Windows-only runtime guards (`if (!OperatingSystem.IsWindows()) return;`).
   - On Linux (`omarchy-test`), only two reflection/dependency-injection tests execute:
     - `Construction_Runner_Default_IsProductionProcessRunner`
     - `Construction_RunnerParameter_OverridesStatic`
   - Neither test touches `TunOwnershipLock`, TUN devices, or process tables.

### 2.2 Production Runtime Lock Risk Analysis (Risky Fixtures)
Per instruction: *"Review VPNRouter.Tests fixture setup before running for new flock production path concerns; tests must not create real VPN/process routing. Run dotnet test focused canonical filter ... plus TunOwnership/ServiceAppCoexistence/UnixOwnership tests if harmless fake ... Better report before executing risky tests, no unapproved changes."*

1. **`ServiceAppCoexistenceTests` (RISKY - NOT a harmless fake on Linux)**:
   - In method `TunOwnershipLock_IsOwnedByAnyone_NonOwnerProbeIsIdempotent`:
     - Calls `TunOwnershipLock.IsOwnedByAnyone()` in a loop.
     - Calls `using var fresh = new TunOwnershipLock(); var acquired = fresh.TryAcquire();`.
   - **Path Collision**: On Linux, `TunOwnershipLock` delegates to `LinuxTunOwnership`. When `LinuxTunOwnership.OverrideRuntimeDirectory` is `null` (which is the case throughout `VPNRouter.Tests`), `LinuxTunOwnership.ResolveRuntimeDirectory` targets `/run/user/<euid>/vpnrouter-tun.lock`.
   - **Host Mutation**: Running this test creates/opens `/run/user/1000/vpnrouter-tun.lock` and acquires an exclusive `flock(LOCK_EX | LOCK_NB)` directly on the user's live systemd runtime directory. This touches the production lock path and is NOT isolated.
2. **`SingBoxManagerRestartTunLockTests` (RISKY - Windows-only fake, leaks to Linux flock)**:
   - Tests exercising non-Windows paths (`Stop_LinuxCapabilityMode_ReleasesLockNormally_RegressionPin`, etc.) call `SetLockOwnedForTest`.
   - `SetLockOwnedForTest` only replaces the Windows named semaphore field (`_semaphore`) via reflection with `new Semaphore(0, 1)`.
   - It does NOT replace or isolate `_linuxLock`. When `managerB.StartWithJson` or `_tunLock.Release()` runs on Linux, it instantiates `LinuxTunOwnership` and performs real `flock` operations on `/run/user/1000/vpnrouter-tun.lock`.
3. **`UnixOwnership`**:
   - No test class with this name exists in `VPNRouter.Tests`.
   - Real Linux flock unit tests were authored in `VPNRouter.Headless.Tests/LifecycleChecks.cs`, where `LinuxTunOwnership.OverrideRuntimeDirectory = testDir;` was explicitly wired to avoid touching `/run/user/<euid>`.
4. **Conclusion**:
   - `ServiceAppCoexistenceTests` and `SingBoxManagerRestartTunLockTests` were **withheld from unisolated execution** on `omarchy-test` to prevent production `/run/user/1000/vpnrouter-tun.lock` mutation, complying strictly with the safety mandate.

---

## 3. Dotnet Test Execution (Canonical Focused Filter)

- **Command**:
  ```bash
  /home/tester/.local/share/vpnrouter-omarchy-sdk/10.0.301/dotnet test \
    VPNRouter.Tests/VPNRouter.Tests.csproj -c Release \
    --filter "FullyQualifiedName~ConfigGeneratorTests|FullyQualifiedName~VpnEngineStartAsyncSeamTests|FullyQualifiedName~SingBoxManagerProcessRunnerTests"
  ```
- **Outcome**: **PASSED (Exit Code: 0)**
- **Test Results**:
  - Total: **45**
  - Passed: **45**
  - Failed: **0**
  - Skipped: **0**
  - Duration: **526 ms**
- **Test Breakdown**:
  - `ConfigGeneratorTests` (28 tests): All 28 passed.
  - `VpnEngineStartAsyncSeamTests` (15 tests): All 15 passed.
  - `SingBoxManagerProcessRunnerTests` (2 active tests + 6 Windows-guarded no-ops): All 2 passed.

---

## 4. Build & Compiler Regression Analysis

### 4.1 Solution Build Status
- `dotnet build VPNRouter.sln -c Release`: **Succeeded (0 Errors, 0 Warnings)**.
- `dotnet build VPNRouter.Headless.Tests/VPNRouter.Headless.Tests.csproj -c Release`: **Succeeded (0 Errors, 0 Warnings)**.

### 4.2 Constructor Overload Null Ambiguity Regression (CS0121)
In commit `2d9331e5343ee835cd8c83c7a794b31aed3b4511`, `TunOwnershipLock.cs` introduced a second constructor to support testing directories:
```csharp
// Constructor 1:
public TunOwnershipLock(ILogger? logger = null)
    : this(null, logger)
{
}

// Constructor 2:
public TunOwnershipLock(string? testRuntimeDirectory, ILogger? logger = null)
{
    ...
}
```
- **The Defect**:
  Both constructors accept a single argument when optional defaults are evaluated:
  - `TunOwnershipLock(ILogger? logger)`
  - `TunOwnershipLock(string? testRuntimeDirectory, ILogger? logger = null)`
  Because `null` matches both `ILogger?` and `string?` equally (neither type is more specific than the other), calling `new TunOwnershipLock(null)` produces:
  ```
  error CS0121: The call is ambiguous between the following methods or properties:
  'VPNRouter.Core.Services.TunOwnershipLock.TunOwnershipLock(Serilog.ILogger?)' and
  'VPNRouter.Core.Services.TunOwnershipLock.TunOwnershipLock(string?, Serilog.ILogger?)'
  ```
- **Current Impact in Tree**:
  Existing call sites use either `new TunOwnershipLock()` (zero arguments, which unambiguously resolves to Constructor 1) or `new TunOwnershipLock(logger)` (where `logger` is a typed `ILogger?` reference). However, any future caller passing a literal `null` will encounter a compiler failure unless cast explicitly (e.g. `(ILogger?)null` or `(string?)null`).

### 4.3 Platform Compatibility Warnings (CA1416)
During `VPNRouter.Tests` compilation on Linux, multiple CA1416 analyzer warnings were emitted where tests invoke platform-restricted methods reachable on all platforms (e.g. `LinuxFirewallManager` Linux-only members called from cross-platform test methods, and `TunAdapterDiagnostics` Windows-only members).
