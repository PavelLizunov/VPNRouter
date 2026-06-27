# Roblox realtime UDP reliability fixes (2026-06-27)

**Owner**: Codex session 2026-06-27
**Branch**: main
**Roadmap ref**: user request 2026-06-27; `plans/roblox-reliability-RB1-RB4-2026-06-27.md` RB4
**Effort**: 2-4 hours
**Risk**: MEDIUM
**Blast radius**: Core config generation, settings migration, HealthMonitor restart policy, focused tests
**Rollback**: `git revert <commit>`

## Why

Roblox and similar realtime UDP games can disconnect when full-tunnel mode sends latency-sensitive UDP through a distant proxy, when legacy configs keep a TUN MTU of 1500, or when a transient health blip hard-restarts sing-box during an active game session. The fix should preserve fail-closed defaults while making the game-direct leak explicit and user-controllable.

## What

- Add an `AppSettings` toggle, default-on, to route a curated list of realtime UDP game process names directly.
- Generate a `process_name -> direct` sing-box route rule after internal DNS/private/RU-direct rules and before catch-all proxy rules.
- Add schema v7 migration that clamps only the known legacy `Tun.Mtu == 1500` value to 1280.
- Debounce HealthMonitor auto-restarts so a single transient failed health probe does not restart sing-box.
- Cover each behavior with focused xUnit regression tests.

## How

1. Inspect current settings schema, ConfigGenerator route-rule ordering, migration style, and HealthMonitor tests.
2. Add the games-direct toggle and curated process list without mutating process-name casing.
3. Insert the games-direct route rule in the existing process-route machinery and verify JSON shape plus sing-box compatibility where available.
4. Add `Migrate_6_to_7` and bump `CurrentSchemaVersion`.
5. Add a two-consecutive-fail HealthMonitor restart debounce, keeping the existing restart path for sustained failures.
6. Run targeted tests, then full build and full test gates.

### Tests written

- `ConfigGeneratorRealtimeGamesDirectTests` - generated full-mode route JSON contains Roblox process names routed to `direct` when the toggle is enabled and omits them when disabled.
- `SettingsMigratorMtuSchemaV7Tests` - schema v7 migrates legacy MTU 1500 to 1280 and preserves explicit non-legacy MTU values.
- `HealthMonitorRestartDebounceTests` - a single transient health failure does not restart, while consecutive failures still restart.

### Verification approach

Run targeted suites while iterating, then `dotnet build VPNRouter.sln -c Release` and `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`. No UI surface is changed, so MCP UI verification is N/A for the implementation gates.

## Verification gate

- [ ] **Gate 1 - Build clean**: `dotnet build VPNRouter.sln -c Release` -> 0 errors.
- [ ] **Gate 2 - Tests green**: full suite passes. New tests included.
- [ ] **Gate 3 - Docs**: brief Outcome filled. README/CLAUDE only if user-facing docs need update.
- [ ] **Gate 4 - Self-review**: bug-hunt after non-trivial feature; simplify unavailable in this session if requested by methodology.
- [ ] **Gate 5 - MCP verify**: N/A - Core-only behavior, no UI surface.
- [ ] **Gate 6 - Characterization diff**: N/A - not a god-file split.

## Outcome (filled after merge)

**Status**: PENDING
**Commits**: TBD
**Pushed**: TBD
**Test deltas**: TBD
**Files changed**: TBD

**Gate results:**
- [ ] Gate 1: pending
- [ ] Gate 2: pending
- [ ] Gate 3: pending
- [ ] Gate 4: pending
- [-] Gate 5: N/A - Core-only change
- [-] Gate 6: N/A - not a god-file split

**Surprises encountered**:
- TBD

**Follow-ups spawned**:
- TBD

**Lessons for methodology doc**:
- TBD
