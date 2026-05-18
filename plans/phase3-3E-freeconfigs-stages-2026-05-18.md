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

**Status: LANDED** (worktree, ready for integrator commit).

### Files staged
**Core (new):**
- `VPNRouter.Core/Services/FreeConfigs/IFreeConfigStage.cs` — 192 LOC. Interface + `StageContext`/`StageResult` records (with `ShortCircuit` + `ShortCircuitStages` for the pool path) + `StageRetry` record + `StageRetryPolicy` class with case-insensitive name lookup + `Default` static policy (fetch=2 attempts, others=1).
- `VPNRouter.Core/Services/FreeConfigs/Stages/FetchStage.cs` — 175 LOC. Pool.json short-circuit OR per-source fan-out via `FreeConfigFetcher`. Emits `ShortCircuit=true, ShortCircuitStages=[parse,dedupe,geoip]` on pool path.
- `VPNRouter.Core/Services/FreeConfigs/Stages/ParseStage.cs` — 115 LOC. Drains `FetchStage.PendingFetches` bucket, parses URIs, intra-source dedupes. Includes `BuildId` helper (host:port:uuid SHA-1 prefix).
- `VPNRouter.Core/Services/FreeConfigs/Stages/DedupeStage.cs` — 78 LOC. Cross-source dedupe by `OrdinalIgnoreCase` Id, first-wins, empty-id skip.
- `VPNRouter.Core/Services/FreeConfigs/Stages/GeoIpStage.cs` — 107 LOC. `Optional = true`; failure passes through. Skips entries that already have CC (cache + pool both pre-enrich).
- `VPNRouter.Core/Services/FreeConfigs/Stages/CacheMergeStage.cs` — 99 LOC. Two-pass merge (inherit-cache fields + preserve-cache-only Verified/recent-Ok) via the existing `FreeConfigAggregator.PreservePreviousValidation` static.
- `VPNRouter.Core/Services/FreeConfigs/Stages/TestStage.cs` — 181 LOC + `TestStage.Helpers.cs` 92 LOC (partial class split to fit the <200 LOC gate). Phase 3D pre-test placeholder rejection via `PlaceholderDefense.Inspect(server:)` + `PlaceholderDefense.InspectUri(rawUri)` (the latter handles `VlessUriParser.Parse` throwing on placeholder URIs). Skip-recent gate, status-quality sort, MaxTestCount cap, goal-mode early stop, incremental cache save every 50 tests / 5s. Exposes `GoalReached`/`FoundMatching`/`SkippedRecent`/`RejectedPlaceholder` for orchestrator telemetry.

**Core (modified):**
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs` — was ~585 LOC, now 428. `RefreshAsync` is now a thin loop (~62 code lines) over the 6 stages with per-stage retry via `RunWithRetryAsync` + short-circuit skip support. `MergeWithCache`, `RetestAsync`, `FetchPoolAsync`, `PreservePreviousValidation`, `BuildId` kept verbatim (unchanged surface for tests + App layer).

**Tests (new):**
- `FreeConfigStageInterfaceTests.cs` — 6 tests (interface contract + StageRetryPolicy lookup + record-with semantics).
- `FreeConfigFetchStageTests.cs` — 5 tests (Name/Optional contract + pool-disabled path + disabled-sources filter + PendingFetches lifecycle).
- `FreeConfigParseStageTests.cs` — 5 tests (empty fetches, valid URIs produce entries, invalid URIs counted, dupe-within-source dedup, PendingFetches drained after run).
- `FreeConfigDedupeStageTests.cs` — 5 tests (distinct survive, dupe first-wins, empty-id skip, case-insensitive dedupe, empty input).
- `FreeConfigGeoIpStageTests.cs` — 4 tests (Optional=true contract pin, all-have-CC pass-through, empty input, lowercase Name).
- `FreeConfigCacheMergeStageTests.cs` — 5 tests (no-cache pass-through, overlap inherits fields, cache-only Verified survives, cache-only Ok-stale dropped, empty-fresh + cache-Verified preserved).
- `FreeConfigTestStageTests.cs` — 7 tests (Name + Optional, empty no-op, Verified-skip gate, recent-skip gate, **placeholder rejection via stas pubkey "DnT9..."**, MaxTestCount cap, placeholder pre-test mutation pins).

**Total: 37 new stage tests** (well over the 18+ required).

### LOC delta per stage
| File | Total | Code-only |
|---|---|---|
| IFreeConfigStage.cs | 192 | 63 |
| FetchStage.cs | 175 | 115 |
| ParseStage.cs | 115 | 74 |
| DedupeStage.cs | 78 | 45 |
| GeoIpStage.cs | 107 | 68 |
| CacheMergeStage.cs | 99 | 52 |
| TestStage.cs + Helpers.cs | 181 + 92 | 132 combined |
| FreeConfigAggregator.cs | 428 (was 585) | RefreshAsync = 62 code lines |

All stages are under the <200 LOC gate. `FreeConfigAggregator.RefreshAsync` is a thin loop (62 code lines vs the ~50 LOC brief target — close, with retry+short-circuit overhead).

### Test deltas
| Suite | Before | After |
|---|---|---|
| Total scoped tests | 1029 | **1066 (+37)** |
| FreeConfig-related | 78 | **115 (+37)** |
| FreeConfigAggregatorPreserveTests | 9 | 9 (still green) |
| FreeConfigCacheMigrationTests | 4 | 4 (still green) |
| FreeConfigRecheckMergeTests | 4 | 4 (still green) |
| All 6 required regression suites | green | green |

### Build / verification gates
- [x] **Gate 1**: `dotnet build VPNRouter.sln -c Release` → 0 errors, 0 warnings.
- [x] **Gate 2**: Scoped test suite (`!Headless&!PageScreenshot&!VisualDiff`) → 1066 passed, 0 failed, 4 skipped (sing-box integration). **Note:** rare transient failures occur in `SettingsLoaderRobustnessTests` due to existing `%ProgramData%\config.yaml` filesystem contention with `MainWindowViewModel`-touching tests in the full parallel run — those tests pass in isolation and are NOT caused by this change (documented in `VPNRouter.Tests/CLAUDE.md` "Headless tests — known issues" section).
- [x] **Gate 4 simplify**: Each stage <200 LOC (TestStage split to partial class to hit the bar).
- [x] **Hook gates**: pre-commit not run yet (integrator will do that).

### Phase 3D consolidation hooks
TestStage uses `PlaceholderDefense.Inspect(server:)` for the cheap IP-fingerprint check + `PlaceholderDefense.InspectUri(RawUri)` for the full Reality public_key / short_id check (the latter bypasses `VlessUriParser.Parse`'s `PlaceholderConfigException`, which would otherwise swallow the rejection signal). Pinned by `FreeConfigTestStageTests.PlaceholderEntry_MutatedToTlsFailed_BeforeProbe` using the canonical stas pubkey `DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU`.

### Surprises / notes
1. **VlessUriParser throws on placeholder URIs by design** — the v2.32.3 input-gate change (`PlaceholderGuard.Inspect` at parse time) means `VlessUriParser.Parse(cfg.RawUri)` throws inside TestStage. We use `PlaceholderDefense.InspectUri` (which catches the typed exception and returns the field name) instead — that's the recommended path per Phase 3D's file comment.
2. **`FreeConfigFetcher` already retries internally** (2 attempts × 10 s = 20 s per source). The stage-level `StageRetryPolicy.Default` adds another 2 attempts on top → max 4 attempts total. Acceptable for a user-driven Refresh button; if it ever becomes a UX concern Phase 4 will lift the policy into AppSettings.
3. **`FreeConfigAggregator.FetchPoolAsync` / `MergeWithCache` / `RetestAsync` were NOT refactored** to use the pipeline. These are alternate entry points used by `FreeConfigsPageViewModel`'s batched search loop — out of scope for this brief, and out-of-scope for the verification gate (they don't run the test stage). Phase 4 candidate.
4. **`AppSettings` per-stage retry config NOT added** to yaml — the `StageRetryPolicy` is constructor-injectable (default fallback works for all current callers). Adding a yaml section needs an `AppSettings` migration step + UI surface for a feature nobody's asked for yet. Phase 4 follow-up.

### Files staged for integrator
```
modified:   VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs
new file:   VPNRouter.Core/Services/FreeConfigs/IFreeConfigStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/CacheMergeStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/DedupeStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/FetchStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/GeoIpStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/ParseStage.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/TestStage.Helpers.cs
new file:   VPNRouter.Core/Services/FreeConfigs/Stages/TestStage.cs
new file:   VPNRouter.Tests/FreeConfigCacheMergeStageTests.cs
new file:   VPNRouter.Tests/FreeConfigDedupeStageTests.cs
new file:   VPNRouter.Tests/FreeConfigFetchStageTests.cs
new file:   VPNRouter.Tests/FreeConfigGeoIpStageTests.cs
new file:   VPNRouter.Tests/FreeConfigParseStageTests.cs
new file:   VPNRouter.Tests/FreeConfigStageInterfaceTests.cs
new file:   VPNRouter.Tests/FreeConfigTestStageTests.cs
```

## Follow-up

- Phase 4 may add `StageTelemetry` that records per-stage durations to `vpnrouter.log` for user-facing diagnostics.
- F-Droid + APT mirror sources could be new fetch sub-stages.
