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
*(filled by agent after impl)*

**Follow-up**: Phase 3C `StartupPipeline` extraction builds on this — once ConfigPipeline is the canonical config-stage helper, StartupPipeline orchestrates: profile resolve → ConfigPipeline.Generate → firewall setup → sing-box launch → monitor start.
