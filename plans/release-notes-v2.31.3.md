# VPNRouter v2.31.3 — F-25 follow-up: heal old sub-5ms cache corruption

Single-fix release that closes the remaining tail of F-25. v2.31.2 added
the plausibility gate to `TcpPingOnlyAsync` (preventing new corrupt
writes), but cache entries that were already corrupted by the pre-fix
Recheck flow stayed at `LatencyMs=1..4`. The Saved tab sorts by ping
ascending so those bogus entries surfaced at the top — clicking
"↻ Recheck" couldn't heal them either, because the new gate now
correctly drops the sub-5 ms readings (keeping the prior bogus value
in place).

## Fix

`FreeConfigCache.Load()` now runs a one-shot in-memory migration that
walks the freshly-loaded entries and resets any `LatencyMs > 0 && < 5`
to 0. The UI side renders 0 as "— ✓✓" via an updated `LatencyDisplay`
case (Verified + LatencyMs <= 0). Sort key keeps real-RTT Verified
entries on top of the Saved tab, with healed "needs re-verify"
entries in mid-rank.

Idempotent — runs every load; subsequent writes go through the
v2.31.2 gate so they stay clean.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 28/28 regression + AU-9 + F-25 tests pass
- Mac DMG / Linux AppImage+.deb / APT publish CI on r1 → all `success`
- 12 assets confirmed on r1
- **MCP+UIA in-app verification**: pre-r1 cache had 30 sub-5 ms
  entries; after r1 install + opening Saved tab, every previously-
  bogus row displays "— ✓✓" instead of "1 ms ✓✓". Status preserved as
  Verified, `MeasuredBandwidthMbps` preserved.

## v2.31 cycle — fully closed

| Release | Date | Scope |
|---|---|---|
| v2.31.0 | 2026-05-02 | Stability + A11y: 39 fixes + 5 tests across 5 -rN |
| v2.31.1 | 2026-05-02 | AU-9 + F-4 + F-6: 3 fixes + 2 tests |
| v2.31.2 | 2026-05-02 | F-25 prevent-new: 1 fix + 1 test |
| **v2.31.3** | **2026-05-03** | **F-25 heal-old: 1 fix + UI polish** |

**Total v2.31 cycle: 45 fixes + 8 unit tests across 8 iterations.**
All deferred audit items closed.

## Cross-refs

- `plans/release-notes-v2.31.2.md` — F-25 prevent-new fix
- `plans/release-notes-v2.31.0.md` — major v2.31 cycle
- `plans/vpnrouter-ux-audit-2026-05-01.md` — F-25 / UX-23 source finding
