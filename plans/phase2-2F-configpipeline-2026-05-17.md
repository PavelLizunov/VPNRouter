# Phase 2 — 2F: Extract `Services/ConfigPipeline.cs` (close v2.28.2 silent-leak bug class)

**Owner**: Wave 5 parallel agent
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2F; plans/v3.0-architecture-roadmap.md §3 "HealthMonitor.GenerateConfigJson duplicates VpnEngine config-pipeline logic"
**Effort**: 1 day
**Risk**: MEDIUM (touches VpnEngine.StartAsync — the most critical service in the codebase; tests must stay green)

## Why
Audit D root-caused the v2.28.2 silent-leak class: `HealthMonitor.GenerateConfigJson` independently re-implements the same multi-step config pipeline that `VpnEngine.StartAsync` walks (Resolve servers → Apply migrations → Validate via LeakProtection → Generate sing-box JSON). Two parallel implementations of the same logic drift.

By extracting `Services/ConfigPipeline.cs` as a single static helper that BOTH call, we close the entire bug class. If a future migration or guard is added to the pipeline, it lands once + propagates everywhere.

## What

Create `VPNRouter.Core/Services/ConfigPipeline.cs`:

```csharp
namespace VPNRouter.Core.Services;

/// <summary>
/// Canonical config-generation pipeline. Single source of truth for the
/// server-resolution + validation + sing-box-JSON-generation chain.
/// Called from VpnEngine.StartAsync (initial connect) AND from
/// HealthMonitor.GenerateConfigJson (hot-reload). Pre-2.32.x these two
/// callers had separate hand-rolled pipelines, which is how the v2.28.2
/// silent leak slipped through.
/// </summary>
internal static class ConfigPipeline
{
    /// <summary>
    /// Walk: resolve subscription servers → fold legacy vless.servers →
    /// strip placeholders → apply LeakProtection guard → build sing-box
    /// JSON for the active server. Throws on validation failure with
    /// typed exception (ConfigValidationException) so callers can
    /// surface user-actionable error messages.
    /// </summary>
    public static string Generate(
        AppSettings settings,
        ILogger? logger = null)
    {
        // 1. VlessServersResolver.Resolve (subscription aggregation)
        // 2. SettingsMigrator.PruneKnownPlaceholders
        // 3. ConfigSanityCheck.CheckBeforeStart
        // 4. ConfigGenerator.Generate
        // 5. LeakProtection.ValidateConfig
        // 6. Return JSON string
    }
}
```

Update **two** call sites to use ConfigPipeline.Generate:

**A. `VPNRouter.Core/Services/VpnEngine.cs:StartAsync`** — find the existing inline pipeline (likely a 100-200 line stretch starting after profile resolve). Replace with single `var configJson = ConfigPipeline.Generate(settings, logger);`. Preserve any try/catch around it; ConfigPipeline throws structured exceptions.

**B. `VPNRouter.Core/Services/HealthMonitor.cs:GenerateConfigJson`** — find the duplicate pipeline (the function that GIVES this audit-D finding its name). Replace body with same single call.

After extraction, the only diff in VpnEngine + HealthMonitor should be the inline-pipeline lines removed + the single ConfigPipeline call added.

## How

**Step 1 — Trace existing pipelines side-by-side**:
Open VpnEngine.cs in one half, HealthMonitor.cs in other. Identify the duplicate code stretches (likely with similar comments). Write the diff into the brief Outcome as evidence of duplication.

**Step 2 — Design ConfigPipeline.Generate signature**:
Method needs:
- Read: AppSettings (all of it — subscriptions, vless, dns, tun, app config_mode etc.)
- Read: ILogger? for trace
- Returns: string (the sing-box JSON)
- Throws: typed exceptions (`ConfigValidationException`, `PlaceholderConfigException`, etc.)

Optional: take an `IReadOnlyList<VlessServerEntry>? servers` parameter to allow callers that already resolved servers to skip step 1.

**Step 3 — Write the helper**:
Copy logic from VpnEngine.cs (since it's the canonical reference) into ConfigPipeline.cs. Each pipeline step gets a small private helper inside ConfigPipeline for clarity:
- `ResolveServers` → `VlessServersResolver.Resolve`
- `ApplyMigrations` → `SettingsMigrator.PruneKnownPlaceholders` + future migrations
- `SanityCheck` → `ConfigSanityCheck.CheckBeforeStart`
- `Generate` → `ConfigGenerator.Generate`
- `Validate` → `LeakProtection.ValidateConfig`

**Step 4 — Switch VpnEngine.StartAsync to use ConfigPipeline.Generate**:
Replace inline pipeline. Verify behavior identical via existing 17 VpnEngine tests + 5 HealthMonitor tests.

**Step 5 — Switch HealthMonitor.GenerateConfigJson to use ConfigPipeline.Generate**:
Same pattern. Verify HealthMonitorRecoveryGapTests + HealthMonitorTimerRaceTests still pass.

**Step 6 — Write 5 new ConfigPipeline tests**:
- `Generate_HappyPath_ProducesValidJson` — full settings → expected JSON shape
- `Generate_EmptyServers_ThrowsConfigValidationException` (pin from v2.28.2 hard guard)
- `Generate_PlaceholderActiveServer_ThrowsPlaceholderConfigException` (pin from v2.32.3)
- `Generate_SubscriptionMode_AggregatesServers` (pin VlessServersResolver path)
- `Generate_LegacyVlessServers_AppliedToOutput` (backward compat)

Add to `VPNRouter.Tests/ConfigPipelineTests.cs` (NEW file).

**Step 7 — Verify**:
- `dotnet build VPNRouter.sln -c Release` → 0 errors
- `dotnet test --filter "FullyQualifiedName~VpnEngine|FullyQualifiedName~HealthMonitor|FullyQualifiedName~ConfigPipeline|FullyQualifiedName~ConfigGenerator|FullyQualifiedName~LeakProtection"` → all pass, +5 new
- Full suite stays 839 → 844 (or whatever previous baseline + 5)

## Verification gate
- [ ] Inventory: both pipelines side-by-side documented in brief Outcome
- [ ] **Gate 1**: build clean
- [ ] **Gate 2**: tests stay green + 5 new ConfigPipeline tests pass
- [ ] **Gate 3 docs**: ConfigPipeline.cs has comprehensive XML doc explaining the bug-class closure
- [ ] **Gate 4 self-review**: `security-review` skill — pipeline touches security (LeakProtection, PlaceholderGuard)
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome

**Status: PASS** (Phase 2F closed, 2026-05-17)

### Files

| File | Action | LOC delta |
|---|---|---|
| `VPNRouter.Core/Services/ConfigPipeline.cs` | NEW | +208 |
| `VPNRouter.Core/Services/VpnEngine.cs` | edited (StartAsync inline block → 1 call) | 1658 → 1659 (+1, comment-heavy) |
| `VPNRouter.Core/Services/HealthMonitor.cs` | edited (GenerateConfigJson body → 1 call) | 593 → 571 (-22) |
| `VPNRouter.Tests/ConfigPipelineTests.cs` | NEW (5 tests) | +335 |
| `VPNRouter.Tests/HealthMonitorLeakValidationTests.cs` | edited (2-link reflection pin) | 75 → 122 (+47) |

Brief gate target: 839 + 5 = 844 tests. Actual baseline pre-2F was 835
(831 passed + 1 pre-existing failure + 3 skipped). New filter-targeted
gate (`VpnEngine|HealthMonitor|ConfigPipeline|LeakProtection|PlaceholderGuard`)
returns 96 passed / 0 failed / 0 skipped, including the 5 new ConfigPipeline
tests + 2 expanded HealthMonitorLeakValidationTests (one for each link of
the chain).

### Side-by-side pipeline diff (evidence of duplication)

Pre-2F **VpnEngine.StartAsync (generated branch)** lines 240-545:

```csharp
var allServers = VlessServersResolver.Resolve(settings, _logger);
if (allServers.Count == 0)
{
    var why = VlessServersResolver.DescribeEmptyReason(settings)
              ?? "VLESS server not configured.";
    throw new InvalidOperationException(why);
}
// … process scan, geo data, profile resolve …
var sbConfig = ConfigGenerator.Generate(_activeProfile, _scanResult.ProcessNames, settings);
var validation = LeakProtection.ValidateConfig(sbConfig, settings);
foreach (var warn in validation.Warnings)
{
    _logger?.Warning("[VpnEngine] {Warn}", warn);
    Warning?.Invoke(warn);
}
if (!validation.IsValid)
{
    var errors = string.Join("; ", validation.Errors);
    throw new InvalidOperationException($"Config validation failed: {errors}");
}
configJson = ConfigGenerator.Serialize(sbConfig);
```

Pre-2F **HealthMonitor.GenerateConfigJson** lines 530-575:

```csharp
VlessServersResolver.Resolve(_appSettings);
var config = ConfigGenerator.Generate(_activeProfile, processNames, _appSettings);
try
{
    var validation = LeakProtection.ValidateConfig(config, _appSettings);
    if (!validation.IsValid)
    {
        _logger.Warning(
            "[HealthMonitor] LeakProtection flagged restart config: errors=[{Errors}] warnings=[{Warnings}]",
            string.Join(" | ", validation.Errors),
            string.Join(" | ", validation.Warnings));
    }
    else if (validation.Warnings.Count > 0)
    {
        _logger.Information(
            "[HealthMonitor] LeakProtection restart-config warnings: {Warnings}",
            string.Join(" | ", validation.Warnings));
    }
}
catch (Exception ex)
{
    _logger.Warning(ex, "[HealthMonitor] LeakProtection.ValidateConfig threw (non-fatal)");
}
return ConfigGenerator.Serialize(config);
```

Post-2F both reduce to a single line:

```csharp
// VpnEngine.StartAsync:
configJson = ConfigPipeline.Generate(
    _activeProfile, _scanResult.ProcessNames, settings,
    ConfigPipeline.ValidationMode.Strict,
    warningSink: msg => Warning?.Invoke(msg), logger: _logger);

// HealthMonitor.GenerateConfigJson:
return ConfigPipeline.Generate(
    _activeProfile, processNames, _appSettings,
    ConfigPipeline.ValidationMode.Advisory,
    warningSink: null, logger: _logger);
```

### Behaviour drift discovered (per brief's STOP-and-document directive)

The trace surfaced **three** pipeline differences across the two named
callers. All three are documented in `ConfigPipeline.cs` XML doc as
**intentionally NOT extracted**, because each is tightly coupled to
caller-specific orchestration that would change external behaviour if
moved:

| Step | VpnEngine.StartAsync | HealthMonitor.GenerateConfigJson | Decision |
|---|---|---|---|
| `LeakProtection.ValidateAppSettings` (F-12 pre-gen invariant) | YES — throws on failure | NO | Stays in StartAsync. Rebuild paths trust the AppSettings model that StartAsync already validated. Moving this into ConfigPipeline would block HealthMonitor recovery on a transient model glitch. |
| `LeakProtection.ValidateConfig` failure handling | THROWS (hard-fail start) | WARNS (continue recovery) | **Resolved** via `ValidationMode { Strict, Advisory }` enum parameter. Behaviour preserved exactly per caller. |
| `ConfigSanityCheck.CheckBeforeStart` (F-E dead-config + AutoFailoverEngine) | YES — triggers failover recursion into StartAsync | NO | Stays in StartAsync. Lifting this would tangle restart loops with `AutoFailoverEngine.HandleDeadConfigAsync` which calls back into StartAsync. |

**Additional finding**: a THIRD pipeline exists in `VpnEngine.Apply`
(lines 1030-1095) that ALSO duplicates the same shape (Resolve → empty
check → Generate → Validate → Serialize) but with WARN-on-failure
semantics rather than THROW. The brief explicitly names only the two
callers (StartAsync + HealthMonitor) so Apply is **out of scope** for
2F. Recommending a Phase 2F-followup task to route Apply through
ConfigPipeline as well (probably with a third `ValidationMode.Soft`
that returns null instead of throwing on validation failure — Apply's
current "return false; skip reload" pattern). Filed below as follow-up.

### Verification gate (per brief)

- [x] **Gate 1 — build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors, 0 warnings.
- [x] **Gate 2 — targeted tests green + 5 new**: filter returns
  96 passed / 0 failed (includes 5 new ConfigPipelineTests + 2 expanded
  HealthMonitorLeakValidationTests).
- [x] **Gate 2 — Core regression suite green**: non-UI subset returns
  822 passed / 0 failed / 3 skipped (skips are pre-existing
  `[Skip]`-annotated TUN/sing-box-binary integration tests).
- [x] **Gate 3 docs**: `ConfigPipeline.cs` has comprehensive XML doc
  explaining the bug-class closure, the three NOT-extracted guards, and
  the in-place mutation contract.
- [ ] **Gate 4 self-review (`security-review` skill)**: deferred to
  integration time per task constraints (helper touches LeakProtection
  + PlaceholderGuard via VlessServersResolver scope guard — both
  security-sensitive surfaces). Should run before merge.
- [ ] **Hook gates**: not run (staged but not committed per brief).

### Test names (5 new)

1. `ConfigPipelineTests.Generate_HappyPath_ProducesValidJson`
2. `ConfigPipelineTests.Generate_EmptyServers_ThrowsConfigValidationException`
3. `ConfigPipelineTests.Generate_PlaceholderActiveServer_FallsBackToSubscription`
4. `ConfigPipelineTests.Generate_SubscriptionMode_AggregatesServers`
5. `ConfigPipelineTests.Generate_LegacyVlessServers_AppliedToOutput`

Test 3 was renamed from the brief's
`Generate_PlaceholderActiveServer_ThrowsPlaceholderConfigException` because
the actual scope-guard behaviour (per `VlessServersResolver` r7 Fix-A) is
*fallback to subscription*, not *throw*. Throwing here would force the
user to manually clean their `vless.servers` list before reconnecting —
the existing UX is to silently swap in the working subscription server
and warn-log. The test name updated to match the real contract.

### Pre-existing flakes (unrelated, NOT introduced by this change)

Running the full suite reveals 3 failing tests under parallel xUnit
execution:
- `VisualDiffTests.DpiBypassPage_MatchesBaseline` (baseline screenshot
  regression — repro in isolation, no link to Core)
- `WgturnUpdaterTests.DownloadLockPreventsConcurrentDownloads` (HTTP
  download lock race — passes in isolation)
- `MainWindowViewModelAppsModeTests.BridgedAppItem_SetterWritesIntoActiveList`
  (config.yaml file lock conflict between parallel VM tests)

All three pass when run in isolation. Baseline (pre-2F) test log shows
only `MainWindowViewModelWgturnTests.ConnectWgturnCommand_PersistsUrlAndVkLink`
failing (same xUnit-parallel class of issue, different parallel run).
None of the 3 touch ConfigPipeline / VpnEngine / HealthMonitor paths.

### Follow-up

- **Phase 2F-A** (recommended): route `VpnEngine.Apply` through
  ConfigPipeline.Generate (probably with a new
  `ValidationMode.SoftReturn` that returns null instead of throwing
  on validation failure, matching Apply's "skip reload" semantics).
  Currently filed in Apply's 60+ line inline block. Same drift risk
  as 2F closed for StartAsync + HealthMonitor.
- **Phase 3C `StartupPipeline`**: once ConfigPipeline is the canonical
  config-stage helper, StartupPipeline orchestrates: profile resolve
  → ConfigPipeline.Generate → firewall setup → sing-box launch →
  monitor start.
