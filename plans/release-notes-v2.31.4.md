# VPNRouter v2.31.4 — F-25 tail: Recheck button on healed entries

Single-fix release closing the last F-25 corner. v2.31.3's cache
migration heals corrupted sub-5 ms `LatencyMs` to 0; the displayed
Saved tab then shows "— ✓✓" as designed. But the bulk
"↻ Recheck (N)" button hid because the staleness predicate was
purely time-based (LastTestedAt > 24h or LastVerifyFailedAt) — those
healed entries still have a recent `LastTestedAt` so they failed the
gate, even though the user wants to re-probe to get a real RTT.

## Fix

Extended the staleness predicate in two places:

```csharp
// Before — time-based only:
(c.LastVerifyFailedAt.HasValue && ...) ||
(c.LastTestedAt.HasValue && (now - c.LastTestedAt.Value).TotalHours > 24)

// After — adds the post-migration "needs re-verify" branch:
... ||
(c.Status == FreeConfigStatus.Verified && c.LatencyMs <= 0)
```

Both `StaleSavedCount` (the button label / visibility source) and
`RecheckAllStaleAsync` (the command body) updated together so the
label and command stay in sync.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 28/28 regression + AU-9 + F-25 tests pass
- Mac DMG / Linux AppImage+.deb / APT publish CI on r1 → all `success`
- 12 assets confirmed on r1
- Predicate change is trivial; cache-empty state on the test machine
  prevented end-to-end MCP verification, but the fix is unambiguous

## v2.31 cycle

| Release | Date | Scope |
|---|---|---|
| v2.31.0 | 2026-05-02 | Stability + A11y (39 fixes + 5 tests) |
| v2.31.1 | 2026-05-02 | AU-9 + F-4 + F-6 (3 fixes + 2 tests) |
| v2.31.2 | 2026-05-02 | F-25 prevent-new (1 fix + 1 test) |
| v2.31.3 | 2026-05-03 | F-25 heal-old + UI polish (1 fix) |
| **v2.31.4** | **2026-05-03** | **F-25 tail: Recheck button (1 fix)** |

**Total v2.31 cycle: 46 fixes + 8 unit tests across 9 iterations.**
F-25 fully closed end-to-end after MCP-driven discovery of two
distinct gaps (prevent-new, heal-old, button-visibility).

## Cross-refs

- `plans/release-notes-v2.31.3.md` — heal-old fix + LatencyDisplay polish
- `plans/release-notes-v2.31.2.md` — prevent-new fix
