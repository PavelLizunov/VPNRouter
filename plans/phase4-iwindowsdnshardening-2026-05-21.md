# Phase 4 (Task #36-A) — IWindowsDnsHardening extraction

**Owner**: Claude session
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase2G-vpnengine-startasync-seam-2026-05-21.md` (commit `c4f1ce5b6...`-ish — flagged this work as the highest-blocking-value seam for #36-C)
- `plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md` (Phase 3 IProcessRunner seam adoption that this builds on)
**Effort**: ~2-3 hours
**Risk**: LOW (additive seam; production code unchanged behaviour — defaults to back-compat wrapper)
**Blast radius**: 2 production files modified · 1 new interface file · 1 test fake · 1 new test file
**Rollback**: `git revert <commit>` — interface + fake are additive, wrappers default to existing static facade.

## Why

`VpnEngine.StartAsync` lifecycle happy-path tests (Task #36-C, sequential
next-agent work) are blocked by `StartupPipeline` phase 7 + phase 8
calling `WindowsDnsHardening.Apply` + `WindowsDnsHardening.EnableLockdownIfConfigured`
+ `WindowsDnsHardening.Restore` directly. Those static methods write to:

- `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DisableSmartNameResolution`
- `HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DisableParallelAandAAAA`
- `netsh interface set` (TUN metric)
- Firewall rules via `FirewallManager.EnableDnsLockdownAsync` / `DisableDnsLockdownAsync`

Running a full happy-path lifecycle test against the real static class
would silently mutate the dev/CI machine's machine-wide DNS policy.
Predecessor brief (`phase2G-vpnengine-startasync-seam-2026-05-21.md`)
identified IWindowsDnsHardening as the highest-blocking-value seam in the
deferred-tests table.

## What

### Production changes (3 files)

1. **`VPNRouter.Core/Services/IWindowsDnsHardening.cs`** (new, +146 LOC)
   - `IWindowsDnsHardening` interface with 3 methods (cross-platform, no #if):
     - `Apply(AppSettings?, ILogger?)`
     - `Restore(ILogger?)`
     - `EnableLockdownIfConfigured(AppSettings?, ILogger?)`
   - `WindowsDnsHardeningImpl` concrete singleton — wraps the existing
     static `WindowsDnsHardening` facade. On non-Windows the `#if PLATFORM_WINDOWS`
     guards inside each method collapse to empty bodies (no-op), keeping
     the impl class cross-platform.
   - `WindowsDnsHardeningImpl.Default` static singleton — back-compat
     default for ctor-injected consumers, mirrors `RealSettingsStore.Instance`
     pattern from Phase 3G-1.

2. **`VPNRouter.Core/Services/StartupPipeline.cs`** (~12 LOC modified)
   - Ctor now accepts `IWindowsDnsHardening? dnsHardening = null` (defaults
     to `WindowsDnsHardeningImpl.Default`).
   - Phase 7 (`ScheduleWarmupProbe` BR-7 deferred-lockdown branch) routes
     `EnableLockdownIfConfigured` through `_dnsHardening`. The prior
     `#if PLATFORM_WINDOWS` guard around the call site collapsed into the
     impl itself.
   - Phase 8 (`StartMonitorsPhase`) routes `Apply` through `_dnsHardening`.
     Same #if simplification.

3. **`VPNRouter.Core/Services/VpnEngine.cs`** (~10 LOC modified)
   - Ctor accepts `IWindowsDnsHardening? dnsHardening = null` as new
     trailing optional param (back-compat — every existing call site
     compiles unchanged).
   - `StartAsync` + `ApplyAsync` now pass `_dnsHardening` to
     `StartupPipeline` ctor so a Null* engine-construction-time injection
     propagates into the pipeline.
   - `Stop()` routes `Restore` through `_dnsHardening`. Prior
     `#if PLATFORM_WINDOWS try { WindowsDnsHardening.Restore(_logger); } catch { }`
     collapsed into `try { _dnsHardening.Restore(_logger); } catch { }`.

### Test infrastructure (2 files)

4. **`VPNRouter.Tests/Fakes/NullWindowsDnsHardening.cs`** (new, +71 LOC)
   - Capture-only `IWindowsDnsHardening` double mirroring `FakeHttpClient` /
     `FakeProcessRunner` capture pattern.
   - Records every invocation in `Calls` list (`(string Op, AppSettings? Settings)`
     tuples).
   - Helpers: `ApplyCount`, `RestoreCount`, `EnableLockdownCount`.

5. **`VPNRouter.Tests/WindowsDnsHardeningInjectionTests.cs`** (new, 4 tests)
   - `Stop_OnIdleEngine_InvokesRestoreThroughSeam` — pins that
     `VpnEngine.Stop` drives `Restore` via the injected interface, not the
     static class.
   - `ApplyAsync_OnIdleEngine_DoesNotInvokeHardening` — pins idle-Apply
     short-circuit doesn't touch the DNS-hardening layer.
   - `IWindowsDnsHardening_InterfaceShape_ThreeMethodsPresent` — reflection
     shape pin so a refactor that drops/renames a method surfaces here
     instead of breaking Task #36-C's NullWindowsDnsHardening captures.
   - `VpnEngine_NullCtorArg_UsesDefaultImpl` — back-compat pin that
     null/omitted `dnsHardening` ctor arg falls back to
     `WindowsDnsHardeningImpl.Default`.

### Tests deferred (Task #36-C work)

The brief's "deferred" test list now becomes writeable thanks to this
seam. Specifically the lifecycle happy-path tests that NullWindowsDnsHardening
unlocks once paired with the remaining seams from the predecessor's deferred
table (sing-box factory, TunAdapterDiagnostics seam, NetworkInterfaceDetector
seam):

- `StartAsync_HappyPath_AppliesHardening_OnColdStart` — phase 8 fires
  Apply exactly once after sing-box start.
- `Stop_AfterStart_FiresRestore` — Restore captured after Stop on a
  successfully-started engine.
- `BR-7_DeferredLockdown_FiresAfterWarmupSuccess` — phase 7 warm-up
  success branch calls `EnableLockdownIfConfigured`.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors (275 warnings,
  all pre-existing).
- [x] All existing tests stay green: 1326 pass / 4 skip / 0 fail
  (vs 1322 prior to this change; 1315 baseline + ~11 since dc507cf).
- [x] 4 new WindowsDnsHardeningInjectionTests green.
- [x] All 12 existing WindowsDnsHardeningTests (Windows-only) stay
  green when included in the run.
- [x] Brief above filled, Outcome section completed.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/IWindowsDnsHardening.cs` | +146 LOC new — interface + WindowsDnsHardeningImpl singleton |
| `VPNRouter.Core/Services/StartupPipeline.cs` | ~12 LOC mod — ctor accepts IWindowsDnsHardening; phase 7/8 route through it |
| `VPNRouter.Core/Services/VpnEngine.cs` | ~10 LOC mod — ctor accepts seam; StartAsync/ApplyAsync pass to pipeline; Stop routes Restore |
| `VPNRouter.Tests/Fakes/NullWindowsDnsHardening.cs` | +71 LOC new — capture-only test double |
| `VPNRouter.Tests/WindowsDnsHardeningInjectionTests.cs` | +194 LOC new — 4 tests pinning the seam wiring |
| `plans/phase4-iwindowsdnshardening-2026-05-21.md` | This brief. |

### Was the static→interface wrap clean, or did the registry checkpoint state cause issues?

**Clean.** The static `WindowsDnsHardening` class owns:
- `_originalValues` checkpoint via `dns-hardening-state.json` (read in `Restore`,
  written in `Apply.SaveAndSet`).
- `_runnerOverride` netsh test seam (used by `WindowsDnsHardeningTests`).
- `WindowsDnsHardeningJsonContext` for the sidecar JSON serialisation.

All of this state is process-scoped and the `WindowsDnsHardeningImpl`
wrapper just delegates each call to the static method — so the
checkpoint state lives in exactly one place (the static class) and the
impl is a stateless façade. The wrapper class is a singleton precisely
because multiple instances would be aliases for the same underlying
static state; making that explicit at the type level matches the
existing `RealSettingsStore.Instance` pattern from Phase 3G-1.

The existing 12 `WindowsDnsHardeningTests` (which mutate `_runnerOverride`
to inject a fake netsh) continue passing without any change — they
exercise the static class directly, not via the wrapper. New tests
exercise the wrapper via NullWindowsDnsHardening, which is a different
seam that doesn't even reach `_runnerOverride`.

### Public seam for the next agent (#36-C)

The exact API surface Task #36-C should consume in lifecycle tests:

```csharp
using VPNRouter.Tests.Fakes;
using VPNRouter.Core.Services;

// Construction:
var dnsHardening = new NullWindowsDnsHardening();
using var engine = new VpnEngine(
    scanner: ...,
    firewallFactory: ...,
    monitorFactory: ...,
    logger: null,
    dnsHardening: dnsHardening);  // <-- inject here

// After ColdStart through to phase 8:
Assert.Equal(1, dnsHardening.ApplyCount);
Assert.Equal(0, dnsHardening.RestoreCount);

// After Stop:
engine.Stop();
Assert.Equal(1, dnsHardening.RestoreCount);

// After BR-7 deferred lockdown fires (post-warmup success):
Assert.Equal(1, dnsHardening.EnableLockdownCount);

// Full invocation log for ordering assertions:
foreach (var (op, settings) in dnsHardening.Calls)
    Console.WriteLine($"{op}({settings?.App.ConfigMode})");
```

The `VpnEngine` ctor's new `dnsHardening` parameter is the ONLY wiring
point #36-C needs. The seam flows automatically into the pipeline
(via `StartupPipeline(host, dnsHardening: _dnsHardening)` in
`StartAsync` / `ApplyAsync`) and back through `Stop()`.

### Cross-platform / CI matrix

The seam is fully cross-platform:

- `IWindowsDnsHardening` interface — no `#if PLATFORM_WINDOWS` gate.
- `WindowsDnsHardeningImpl` class — cross-platform; each method's body
  is `#if PLATFORM_WINDOWS`-guarded and collapses to empty (no-op) on
  Linux/macOS.
- `NullWindowsDnsHardening` — pure in-memory; no platform deps.
- 4 new tests — all cross-platform. No `OperatingSystem.IsWindows()`
  gates needed.

Existing `WindowsDnsHardeningTests` (12 tests) remain `#if PLATFORM_WINDOWS`-
gated because they exercise the static class's netsh path that only
makes sense on Windows. They are NOT migrated to the interface — they
pin a different invariant (the netsh wire-shape) that's complementary.

### Surprises encountered

None of significance. The `WindowsDnsHardeningImpl` wrapping pattern
mirrors `RealSettingsStore` from Phase 3G-1 closely enough that the
implementation was mechanical. The only judgement call was whether to
make the impl methods `#if PLATFORM_WINDOWS`-guarded internally vs
splitting the impl into Windows/non-Windows partial files. Chose
internal guards because:

1. The interface contract is identical on every platform (Apply is
   a no-op outside Windows — matches what the existing
   `#if PLATFORM_WINDOWS` callsite guards in StartupPipeline / VpnEngine
   already enforce).
2. Single-file impl keeps the test surface trivial (NullWindowsDnsHardening
   doesn't have to know about platform variants).
3. Cross-platform consumers (Linux/macOS service / CLI builds) get
   the no-op impl automatically — no DI re-wiring per platform.

### Follow-ups spawned

- **Task #36-C** (lifecycle happy-path tests) — now unblocked from the
  DNS-hardening angle. Still needs sing-box factory + TunAdapterDiagnostics
  seam (separate work, see predecessor brief).
- **Phase 5+** potentially extract a sibling `IRegistryStore` so the
  static class's `Registry.LocalMachine.*` calls can also be faked —
  not required for #36-C since NullWindowsDnsHardening short-circuits
  before reaching the registry. Deferred unless a future test needs to
  exercise the registry-write branch.

### Brief

`plans/phase4-iwindowsdnshardening-2026-05-21.md` (this file).
