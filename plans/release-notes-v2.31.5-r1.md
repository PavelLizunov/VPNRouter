# VPNRouter v2.31.5-r1 — test-infra hardening (no product changes)

Test-only release. No code changes in `VPNRouter.Core`, `VPNRouter.App`,
`VPNRouter.CLI`, `VPNRouter.Service` — only `VPNRouter.Tests` and
documentation.

## Why

The v2.31 cycle exposed two distinct failure modes that pure
build/CI green couldn't catch:
- **v2.31.2 partial F-25 fix** — caught only because we MCP-retested
  AFTER cut and noticed Saved tab still showed 1 ms.
- **v2.31.4 Recheck button regression** — caught only because we
  walked the UI manually after migration.

Both bugs were in App-layer data / converter / collection paths that
DON'T require a real Avalonia dispatcher to exercise. They could
have been caught by [Fact] tests if they existed.

## What

Added 6 regression-pin tests in `VPNRouter.Tests/UnitTest1.cs`:

| Test class | Pins | What |
|---|---|---|
| `FreeConfigCacheMigrationTests` | v2.31.3-r1 (F-25 heal-old) | Sub-5ms LatencyMs reset to 0 by `HealCorruptedSubThresholdLatencies` |
| `AvailableRuleTypesSurfaceTests` | v2.31.0-r4 (AU-10) | Cards-mode ComboBox lists `domain_regex` + `process_path` |
| `FreeConfigItemViewModelDisplayTests` | v2.31.3-r1 (F-25 polish) | `Verified + LatencyMs<=0` renders as "— ✓✓"; plausible RTT still as "42 ms ✓✓" |
| `BoolToChevronConverterTests` | v2.31.0-r4 (F-3) | Default param returns ▲/▼; "▽\|›" param returns chevron-card glyphs |

Mix of plain `[Fact]` (data-only paths) and `[AvaloniaFact]` (where
`MainWindowViewModel` construction needs the dispatcher).

The pre-existing headless harness (`TestAppBuilder`,
`HeadlessGuiTests`, `PageScreenshotTests`) was already wired but
under-used. This release pins it as the active path forward and
documents the patterns in `VPNRouter.Tests/CLAUDE.md`.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 34/34 regression tests pass (28 existing + 6 new)
- Build artifacts identical to v2.31.4 (same product code)

## Cycle status

| Release | Date | Scope |
|---|---|---|
| v2.31.0..v2.31.4 | 2026-05-02..03 | F-25 cycle (45 fixes + 8 tests) |
| **v2.31.5** | **2026-05-03** | **Test-infra hardening (+6 tests)** |

**Total v2.31 cycle: 45 fixes + 14 unit tests across 10 iterations.**

## New release-cut policy (effective from this release)

`CLAUDE.md` golden rule #1 + #6 updated 2026-05-03 (commit
`ca451c7`): stable cut requires explicit user "cut" / "ok" /
"promote" command. Verification gate (build + tests + Mac/Linux CI +
12 assets + MCP+UIA verify где testable) is "READY", not "AUTO-CUT".
See `CLAUDE.local.md` "Урок v2.31.2 → v2.31.3 → v2.31.4" for full
rationale.

This release ships as `-r1` and waits for explicit cut.

## Cross-refs

- `plans/release-notes-v2.31.4.md` — last F-25 tail fix
- `VPNRouter.Tests/CLAUDE.md` — updated harness documentation
