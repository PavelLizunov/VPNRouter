# Phase 2G follow-up (Task #22) — VpnEngine.StartAsync invoke-test seam

**Owner**: Claude session
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase2G-vpnengine-orchestrator-2026-05-21.md` (commit `14c512e`, 16 idle/static tests)
- `plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md` (commit `e9c31be`, FakeProcessRunner adoption — declared this task UNBLOCKED)
**Effort**: ~2 hours
**Risk**: LOW (additive tests only; no production-code change)
**Blast radius**: 1 new test file · 0 production files touched
**Rollback**: `git revert <commit>` — pure test addition.

## Why

`VpnEngine.StartAsync` is the lifecycle entrypoint (~25 LOC since Phase 3C
moved the 750-LOC body into `StartupPipeline`). The prior brief explicitly
documented the missing-coverage gap:

> The full StartAsync→Connected→Stop matrix is intentionally NOT covered here
> because VpnEngine.StartAsync requires (1) the sing-box binary on disk, (2)
> Windows-only firewall via netsh, (3) profiles JSON in %ProgramData%. Today
> there's no test seam that lets us stub those in-memory.

The Phase 3+ IProcessRunner adoption (`e9c31be`) was billed as unblocking
this task — SingBoxManager now spawns through `FakeProcessRunner`, so a
test can drive a real SingBoxManager without spawning the actual sing-box
binary.

**Status: PARTIALLY UNBLOCKED.** SingBoxManager itself is fully testable
through FakeProcessRunner. But the lifecycle BEFORE SingBoxManager is
launched — pipeline phases 6 (DeployAndSetupFirewall, runs `netsh` +
`TunAdapterDiagnostics.PreStartCleanupAsync`) and phase 8
(WindowsDnsHardening, mutates HKLM registry) — calls into static helpers
that mutate real Windows OS state with no abstraction seam.

That blocker is documented in detail in **Outcome → Surprises** below.
This brief delivers the achievable subset: characterization tests for the
**early-throw paths** of `VpnEngine.StartAsync` that abort cleanly before
reaching any destructive OS call.

## What

### Tests delivered (this batch — 10 tests)

All tests construct a `VpnEngine` via the deprecated `[Obsolete]` ctor
(test-friendly seam — Phase 4 will add a factory builder), wire stub
scanner / firewall / monitor, and invoke `StartAsync` with settings tuned
so the pipeline aborts in phases 1-2 before reaching destructive code:

- `FlushDnsOnStart = false` — skip the `ipconfig /flushdns` shell-out.
- `BypassRussianTraffic = false` — skip the geo-data HTTP download.
- `skipVpnConflictCheck: true` — skip the ConflictingVpnDetector probe.

Under these guards, ColdStart safely reaches `ResolveProfileAndServersAsync`
(StartupPipeline.cs phase 1+2). Any throw there propagates up cleanly
without touching phases 6-8.

**Tests:**

1. **`StartAsync_EmptyServers_SubscribeMode_ThrowsActionableMessage`** —
   Empty `App.Subscriptions` list in subscribe mode → throws
   `InvalidOperationException`; `IsRunning=false`; `SingBoxPid=null`.
2. **`StartAsync_SubscribeMode_AllSubscriptionsDisabled_Throws`** —
   Subscriptions configured but `Enabled=false` on all entries → same
   empty-case throw.
3. **`StartAsync_EmptyServers_GeneratedMode_Throws`** — Empty `Vless.Servers`
   in generated mode → throws; pin `ActiveProfileName` stays empty.
4. **`StartAsync_EmptyServers_DoesNotMutateState`** — Defence-in-depth: every
   readable getter (IsRunning, ActiveProfileName, ActiveServerAddress,
   MonitoredProcesses) is identical pre- and post-throw.
5. **`StartAsync_NoActiveProfile_SplitMode_Throws`** — `ActiveProfile=null`
   with valid servers in split mode → phase 1 throws with "profile" in the
   message.
6. **`StartAsync_CustomMode_MissingFile_Throws`** — `ConfigMode=custom` with
   non-existent file path → phase 1 throws with "Custom config not found".
7. **`StartAsync_CustomMode_InvalidJson_Throws`** — File exists with garbage
   content → `CustomConfigInjector.Validate` rejects → phase 1 throws with
   "validation" in the message.
8. **`StartAsync_PreCancelledToken_ThrowsOperationCanceled`** — Pre-cancelled
   CT propagates as `OperationCanceledException` (or subclass
   `TaskCanceledException`); engine stays inert.
9. **`ApplyAsync_OnIdleEngine_ReturnsFalseWithoutInvokingPipeline`** —
   ApplyAsync's idle-engine guard catches the case BEFORE entering the
   pipeline, so empty-servers settings don't surface as
   InvalidOperationException through Apply.
10. **`StartAsync_SkipVpnConflictCheck_DefaultFalse_StillRunsEmptyServersGuard`** —
    Bug-r10-B pin: default `skipVpnConflictCheck=false` still reaches the
    phase-2 empty-servers throw (assuming no real VPN conflicts on CI).

### Tests deferred (next brief — needs further refactoring)

These require new abstractions before they can be written as in-memory
tests:

- **Happy-path lifecycle** (Start → Connected event → Stop): blocked on
  phase 6 (`netsh` firewall rules, `TunAdapterDiagnostics.PreStartCleanupAsync`)
  + phase 8 (`WindowsDnsHardening.Apply` HKLM mutation).
- **Crash-then-restart** (HealthMonitor auto-restart loop): requires the
  happy path first.
- **Intentional stop suppression** (Stop after Start): requires happy path.
- **Apply on running engine** (hot-reload via Clash API + FakeHttpClient):
  requires `_singBox` to be live, which requires happy-path StartAsync to
  succeed.
- **Concurrent start guard / Stop-during-restart**: same as above.
- **DnsLeakLockdown ON/OFF symmetry**: requires reaching phase 8.

**Necessary new abstractions** (deferred to a follow-up brief):

| New seam | Replaces direct call to | Effort |
|---|---|---|
| `INetshRunner` (or route DnsFlusher's pattern through `WindowsDnsHardening`) | `netsh advfirewall` in `FirewallManager.CreateBlockRules`; `netsh interface set/show` in `TunAdapterDiagnostics.*` | M |
| `IRegistryStore` | `Registry.LocalMachine.SetValue / OpenSubKey` in `WindowsDnsHardening.Apply/Restore` | M |
| `INetworkInterfaceQuery` | `NetworkInterface.GetAllNetworkInterfaces` in `NetworkInterfaceDetector.DetectWireGuardSubnets` | S |
| `IPowerShellRunner` (or route through `IProcessRunner`) | `powershell -c "Get-NetAdapter ..."` in `TunAdapterDiagnostics.TryRemoveAdapterAsync` | S |

Once those land, the deferred test list above unblocks. The brief calls
out the gap explicitly so a future session can pick it up.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors (263 warnings, all
  pre-existing).
- [x] All existing tests stay green: 1304 pass / 4 skip / 0 fail (vs 1294
  baseline + 10 new tests = expected 1304).
- [x] 10 new VpnEngineStartAsyncSeamTests green.
- [x] Brief above filled, Outcome section completed.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Tests/VpnEngineStartAsyncSeamTests.cs` | +334 LOC (new file). 10 characterization tests for early-throw paths through `VpnEngine.StartAsync`. |
| `plans/phase2G-vpnengine-startasync-seam-2026-05-21.md` | This brief. |

### Real-SingBoxManager + FakeProcessRunner: did it work?

**Partially.** `SingBoxManager` itself migrated cleanly through the
IProcessRunner seam (Phase 3+, `e9c31be`) and is fully testable in
isolation via `FakeProcessRunner`. `SingBoxManagerProcessRunnerTests` (7
tests) confirms the spawn / Exited / Stop / Restart wire-shape works.

The blocker is **upstream** of SingBoxManager — `StartupPipeline.ExecuteAsync`
calls into a chain of static helpers BEFORE phase 7 (which is where
SingBoxManager is constructed):

- Phase 0: `AppPaths.EnsureDirectories()` — idempotent, safe.
  `ConflictingVpnDetector.DetectConflictingVpnProcesses()` — read-only, safe
  (or fully skipped via `skipVpnConflictCheck`). `DnsFlusher.Flush()` —
  callable via `IProcessRunner` but only if `FlushDnsOnStart=true`; tests
  can disable.
- Phase 2: `NetworkInterfaceDetector.DetectWireGuardSubnets()` — reads
  real OS adapter table.
- Phase 5: `ConfigSanityCheck.CheckBeforeStart()` — pure in-memory, safe.
- **Phase 6: `DeployAndSetupFirewallPhaseAsync`** — calls
  `TunAdapterDiagnostics.PreStartCleanupAsync()` which spawns `netsh` +
  `powershell -c "Get-NetAdapter ..."` against the LIVE adapter table.
  Also creates firewall rules via `netsh advfirewall` (gated by
  `profile.BlockOnVpnFail=true`; tests can set false).
- Phase 7: `new SingBoxManager(settings.SingBox, _host.Logger)` — uses
  `SingBoxManager.Runner` static. Tests CAN swap this to FakeProcessRunner.
  But the 2-arg ctor is hard-wired; we'd swap globally and risk leaking
  the swap into parallel tests. Workable but fragile.
- **Phase 8: `WindowsDnsHardening.Apply(settings, _host.Logger)`** —
  writes `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DisableSmartNameResolution`
  + `HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DisableParallelAandAAAA`.
  THIS MUTATES THE DEV/CI MACHINE'S REGISTRY. No seam exists today; the
  helper is a static class.

The IProcessRunner adoption was a necessary but not sufficient condition.
Tasks #22-extended (NullDnsFlusher + NullWindowsDnsHardening +
NullTunAdapterDiagnostics, listed in the **Tests deferred** table above)
need to land before the full lifecycle invokes-tests are writable
in-memory.

### Surprises encountered

1. **Phase 8 WindowsDnsHardening writes HKLM.** Most surprising blocker —
   I expected the netsh shell-out to be the gating concern, but
   WindowsDnsHardening modifies machine-wide Windows policies. Running
   the full lifecycle in a test on a dev machine would silently change
   the user's DNS resolution behaviour. This is the strongest reason to
   defer rather than push through.

2. **Phase 6 `PreStartCleanupAsync` is not idempotent enough for testing.**
   It removes / disables the `VPNRouter-TUN` adapter if found, then sleeps
   500 ms. On a dev machine with a real running VPN, this would cause
   user-visible network drops. The test would have to gate on "no
   VPNRouter-TUN adapter present" — fragile.

3. **`AppPaths.OverrideDataDir` is one-way.** Once a test calls it, the
   static field is set for the rest of the process. To use it safely,
   ALL tests using it would need to share an xUnit collection (similar
   to `SafeModeStateCollection`). The early-throw-path tests in this
   brief don't need OverrideDataDir because they don't reach disk-write
   phases — but the deferred lifecycle tests will.

4. **`SingBoxManager.Runner` static seam vs. per-instance injection.**
   The Phase 7 line `new SingBoxManager(settings.SingBox, _host.Logger)`
   uses the 2-arg ctor — it doesn't take an IProcessRunner parameter.
   Tests that want to inject FakeProcessRunner must mutate the static
   `SingBoxManager.Runner` property and restore it afterwards. xUnit
   parallelism makes this race-prone unless serialized via a collection
   attribute. A future refactor could add a `SingBoxManagerFactory`
   seam to make this less fragile — out of scope for this brief.

### Follow-ups spawned

- **Tasks #22-extended (deferred lifecycle tests)**: requires NullDnsFlusher /
  NullWindowsDnsHardening / NullTunAdapterDiagnostics / NullNetworkInterfaceDetector
  abstractions. Effort estimate: 1 day per seam (4 seams × ~M each = ~M
  days). Plan to fold into Phase 4 / Phase 5 series.
- **`SingBoxManagerFactory` ctor injection seam**: would let
  `StartupPipeline.StartSingBoxPhaseAsync` accept a factory rather than
  hard-coding `new SingBoxManager(...)`. Same pattern as
  `Func<IFirewallManager>` already used by `VpnEngine`. Trivial; defer to
  the next lifecycle-test brief.

### Brief

`plans/phase2G-vpnengine-startasync-seam-2026-05-21.md` (this file).
