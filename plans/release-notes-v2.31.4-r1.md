# VPNRouter v2.31.4-r1 — F-25 tail: Recheck button visibility on healed entries

MCP verification of v2.31.3 caught a subtle regression: after the
cache migration heals corrupted sub-5 ms `LatencyMs` to 0, the Saved
tab's "↻ Recheck (N)" button hides because the staleness predicate
is purely time-based (LastTestedAt > 24h or LastVerifyFailedAt). The
healed entries still have a recent `LastTestedAt` so they fail the
time gate, but their displayed value is "— ✓✓" — exactly the case
where the user wants to re-probe.

## Fix

Extended both staleness predicates (`StaleSavedCount` getter and
`RecheckAllStaleAsync` command) with a new branch:

```csharp
(c.Status == FreeConfigStatus.Verified && c.LatencyMs <= 0)
```

Verified entries that lost their LatencyMs (post-migration, or any
future case where it ends up at 0) now count as stale — the
"↻ Recheck (N)" button appears with the correct count, and clicking
it actually picks up these entries for re-probing.

Both predicates are kept in sync (label and command must agree).

## Tests (still 28/28 passing)

No new tests — the fix is a pure predicate extension. The end-to-end
verification path is the v2.31.3 migration itself: load cache with
sub-5 ms entries, observe count > 0 in `StaleSavedCount`, click
button, recheck runs, fresh probes write real values via the
v2.31.2 gate.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 28/28 regression + AU-9 + F-25 tests pass
- MCP: post-r1 install on a cache with healed (LatencyMs=0) entries
  → "↻ Recheck (N)" button visible with correct count

## v2.31 cycle

| Release | Scope |
|---|---|
| v2.31.0 | Stability + A11y (39+5) |
| v2.31.1 | AU-9 + F-4 + F-6 (3+2) |
| v2.31.2 | F-25 prevent-new (1+1) |
| v2.31.3 | F-25 heal-old + UI polish (1) |
| **v2.31.4** | **F-25 tail: Recheck button regression on healed entries (1)** |

**Total: 46 fixes + 8 unit tests across 9 iterations.**

## Cross-refs

- `plans/release-notes-v2.31.3.md` — heal-old fix
- `plans/release-notes-v2.31.2.md` — prevent-new fix
- `plans/vpnrouter-ux-audit-2026-05-01.md` — F-25 / UX-23 source
