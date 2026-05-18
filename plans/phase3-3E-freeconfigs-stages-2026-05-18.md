# Phase 3 — 3E: Free Configs pipeline stages

**Owner**: Wave 11 (single agent — depends on 3D for PlaceholderDefense namespace)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3E
**Depends on**: Wave 10's 3D PlaceholderDefense consolidation landed
**Effort**: 1 week
**Risk**: MEDIUM (Free Configs is the user-facing feature — pipeline integrity matters)

## Why

Audit B: the 6-stage Free Configs pipeline (fetch → parse → dedupe → GeoIP → test → cache) lives in one orchestrator (`FreeConfigAggregator`). Splitting into composable stages with explicit contracts enables:
- Per-stage retry policy (e.g. fetch retries, test doesn't)
- Stage replay for debugging (replay GeoIP from cached fetch output)
- Optional stages (skip GeoIP when offline)
- Per-stage testability (unit-test parse without spinning network)

Mirror of the Phase 2F ConfigPipeline + Phase 3C StartupPipeline pattern.

## What

Create `VPNRouter.Core/Services/FreeConfigs/Stages/` directory + stage interface:

```csharp
public interface IFreeConfigStage
{
    string Name { get; }
    Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct);
}

public sealed record StageContext(
    IReadOnlyList<FreeConfigEntry> Input,
    AppSettings Settings,
    IFreeConfigCache Cache,
    ILogger Logger);

public sealed record StageResult(
    bool Success,
    IReadOnlyList<FreeConfigEntry> Output,
    string? FailureReason,
    TimeSpan Duration);
```

6 concrete stages in `Stages/`:
1. `FetchStage.cs` — pulls from 14 built-in sources + user-added sources via IHttpClient (uses Phase 2D-3 abstraction)
2. `ParseStage.cs` — converts raw bodies to VlessServerEntry list, supports 3 body formats (JSON wrapper / raw base64 / plain URIs)
3. `DedupeStage.cs` — `StringComparer.OrdinalIgnoreCase` dedup on `Server:Port:UUID:Flow` (NOT mutating case per CLAUDE.md GR #7)
4. `GeoIpStage.cs` — MaxMind lookup; SKIPPABLE if offline (returns Input as-is with annotation)
5. `TestStage.cs` — TCP + TLS probe; calls IFreeConfigDeepVerifier if `DeepVerifyEnabled`
6. `CacheMergeStage.cs` — preserves Verified + recent Ok entries from cache (current `PreservePreviousValidation` logic)

`FreeConfigAggregator.Refresh` becomes a thin loop over `IFreeConfigStage[]`. Per-stage retry policy configurable in AppSettings.

## How

**Step 1** — Read `FreeConfigAggregator.cs` (~600 LOC). Catalog the 6 stages with exact line ranges.

**Step 2** — Build interface + records in `VPNRouter.Core/Services/FreeConfigs/IFreeConfigStage.cs`.

**Step 3** — Extract one stage at a time: start with FetchStage (smallest), end with TestStage (largest). Each extraction:
- Move logic to `Stages/<Name>Stage.cs`
- Add 3-5 unit tests in `VPNRouter.Tests/<Name>StageTests.cs` using FakeHttpClient / InMemoryFileSystem
- Build + scoped tests green after each

**Step 4** — Refactor `FreeConfigAggregator.Refresh` to a thin loop:

```csharp
public async Task RefreshAsync(CancellationToken ct)
{
    var ctx = new StageContext(_seedEntries, _settings, _cache, _logger);
    foreach (var stage in _stages)
    {
        var result = await ExecuteWithRetryAsync(stage, ctx, ct);
        if (!result.Success && !stage.Optional) break;
        ctx = ctx with { Input = result.Output };
    }
}
```

**Step 5** — Verify existing FreeConfigAggregatorPreserveTests + FreeConfigCacheMigrationTests + FreeConfigRecheckMergeTests stay green.

**Step 6** — Stage-level retry policy in AppSettings:
```yaml
free_configs:
  retry:
    fetch: { count: 3, base_delay_ms: 500 }
    test: { count: 1, base_delay_ms: 0 }
```

**Step 7** — Use Phase 3D's PlaceholderDefense namespace in TestStage's pre-test rejection logic (depends on 3D landing first).

## Verification gate
- [ ] IFreeConfigStage interface + 6 concrete stages
- [ ] FreeConfigAggregator.Refresh becomes thin loop (~50 LOC)
- [ ] 18+ new stage tests (3 per stage minimum)
- [ ] Existing FreeConfig regression tests stay green
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite +18 new, all green
- [ ] **Gate 4 simplify**: each stage <200 LOC
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 4 may add `StageTelemetry` that records per-stage durations to `vpnrouter.log` for user-facing diagnostics.
- F-Droid + APT mirror sources could be new fetch sub-stages.
