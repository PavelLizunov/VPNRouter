# Phase 3 — 3C: `VpnEngine.StartAsync` → `StartupPipeline` extraction

**Owner**: Wave 10 parallel agent (2 of 4)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3C
**Effort**: 1 week
**Risk**: MEDIUM (security-critical path — VPN start sequence)

## Why

Audit B+D: `VpnEngine.StartAsync` is 880 LOC, touches 16 services. Mirror of what Phase 2F did for ConfigPipeline — extract a `StartupPipeline` orchestrator. Adds the same single-source-of-truth invariant: any new pre-start step propagates to every caller of `StartAsync` automatically.

Also addresses Phase 2 follow-up 2F-A: the third inline pipeline in `VpnEngine.Apply` (lines ~1030-1095) is a sibling. Both paths should call `StartupPipeline.ExecuteAsync` with different `StartupContext` modes.

## What

Create `VPNRouter.Core/Services/StartupPipeline.cs`:

```csharp
public sealed class StartupPipeline
{
    public async Task<StartupResult> ExecuteAsync(StartupContext ctx, CancellationToken ct);
}

public sealed record StartupContext(
    AppSettings Settings,
    Profile Profile,
    StartupMode Mode,
    ISingBoxApi? ApiOverride = null);

public enum StartupMode { ColdStart, HotReload, AutoFailover }

public sealed record StartupResult(
    bool Success,
    string? FailureReason,
    int? ProcessId,
    TimeSpan Duration);
```

Phases inside `ExecuteAsync`:
1. **ResolveProfile** — merge profile sources, validate
2. **ResolveServers** — VlessServersResolver (delegated to ConfigPipeline from 2F)
3. **GenerateConfig** — ConfigPipeline.Generate
4. **ValidateConfig** — LeakProtection.ValidateConfig (already inside ConfigPipeline; redundant but kept as guard)
5. **PreStartChecks** — ConfigSanityCheck.CheckBeforeStart + PlaceholderDefense (depends on 3D — if 3D lands first use new namespace)
6. **SetupFirewall** — FirewallManager.Apply if profile.block_on_vpn_fail
7. **StartSingBox** — SingBoxManager.StartWithJson
8. **StartMonitors** — HealthMonitor + EtwProcessMonitor.Start

Each phase is a static helper, testable in isolation against fake collaborators (use Wave 6 abstractions — IProcessRunner, IFileSystem, IHttpClient, ISingBoxApi).

## How

**Step 1** — Read current `VpnEngine.StartAsync` (line ~150-1030) + `VpnEngine.Apply` (line ~1030-1095). Catalog the 8 phases above with their exact line ranges.

**Step 2** — Build `StartupPipeline.cs` as a parallel implementation. Pre-implement all 8 phases as private static helpers. The Run method orchestrates them.

**Step 3** — Add 8 contract tests in `VPNRouter.Tests/StartupPipelineTests.cs` — one per phase:
- `ResolveServers_EmptyConfig_ReturnsDescriptiveFailure`
- `GenerateConfig_PlaceholderCreds_RejectedByPreStartChecks`
- `SetupFirewall_NoBlockOnFail_Skipped`
- `StartSingBox_BinaryMissing_ReturnsFailure`
- ... etc

**Step 4** — Refactor `VpnEngine.StartAsync` to call `StartupPipeline.ExecuteAsync`. Single-line replacement of the inline 880 LOC.

**Step 5** — Refactor `VpnEngine.Apply` similarly. Use `StartupMode.HotReload` for that path.

**Step 6** — Verify HealthMonitor's auto-restart path uses the pipeline (or extracts a smaller sub-set if recovery doesn't need all phases).

**Step 7** — Run existing HealthMonitorRecoveryGapTests + AutoFailoverTests — must stay green.

## Verification gate
- [ ] StartupPipeline.cs ~300 LOC (orchestrator + 8 phase helpers + records)
- [ ] VpnEngine.StartAsync shrinks 880 → ~50 LOC (single pipeline call + result handling)
- [ ] VpnEngine.Apply hot-reload path migrated
- [ ] 8 phase contract tests added
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite green + 8 new
- [ ] **Gate 4 simplify**: per-phase helper <50 LOC
- [ ] **Gate 4 security-review**: VPN start is security-critical — verify firewall + placeholder + leak invariants all run in the new path
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 2F-A (third inline pipeline in `VpnEngine.Apply`) — CLOSES with this task.
- Phase 3E (FreeConfigs pipeline stages) reuses the same `IStage` pattern.
- Phase 4 could add a `StartupTelemetry` collector that aggregates phase durations for diagnostics.
