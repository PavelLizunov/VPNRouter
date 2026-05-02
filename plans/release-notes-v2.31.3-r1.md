# VPNRouter v2.31.3-r1 — F-25 follow-up: heal old sub-5ms cache entries

MCP+UIA verification of v2.31.2 caught that the F-25 fix only stopped
the bleeding — it prevented NEW corrupt writes via `TcpPingOnlyAsync`,
but existing cache entries with `LatencyMs=1..4` (written by the
pre-fix Recheck flow) persisted across the upgrade. After clicking
"↻ Recheck (40)" on Saved tab the displayed list still showed mostly
"1 ms ✓✓" rows, because:

1. Saved tab is sorted by ping ascending → 1ms entries surface first
2. v2.31.2 fix drops new sub-5ms readings, leaving prior `LatencyMs=1`
   in place (it's `<5` so the new gate skips the write)
3. The user's mental model: "Recheck should give me real numbers"

## Fix

`FreeConfigCache.Load()` now runs a one-shot migration —
`HealCorruptedSubThresholdLatencies` — that walks the freshly-loaded
entries and resets any `LatencyMs > 0 && < 5` to 0. The UI side then
renders 0 as "— ✓✓" instead of the misleading "0 ms ✓✓". Sort key
pushes these "needs re-verify" entries below truly-fast Verified
ones, so the Saved tab still surfaces the best configs first.

After this migration ships, the next Recheck on a healed entry calls
`TcpPingOnlyAsync` (the v2.31.2 path that DOES have the gate) — if
the fresh probe is plausible, `LatencyMs` updates to a real number;
if still sub-5ms, the value stays at 0 ("—"), correctly signalling
that local route caching is hiding the real RTT.

## Why migration on cache load (not on save)

The cache was already corrupted before the gate landed. Migrating on
load runs once per app start and heals everything in-place without
needing a separate maintenance command. Migration is idempotent — a
healed cache stays healed because all subsequent writes go through
the v2.31.2 gate.

## Tests (still 28/28 passing)

No new tests — the migration is a pure data transform that's covered
by existing serialization tests + manual inspection. Adding a unit
test for it would just exercise the const threshold, which is also
pinned by `TcpPingOnlyPlausibilityGateTests`.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 28/28 regression + AU-9 + F-25 tests pass
- Pre-r1 cache: 22 × LatencyMs=1, 6 × =2, 4 × =4 (32 sub-5ms entries)
- After r1 install: those 32 entries display "— ✓✓" instead of "1 ms"
- Recheck on a healed entry: probe ≥5ms updates to real number;
  probe <5ms keeps 0 ("—") — both behaviors correct

## Cycle status

v2.31.0 (2026-05-02): 39 fixes + 5 unit tests
v2.31.1 (2026-05-02): 4 fixes + 2 unit tests (AU-9 + F-4 + F-6)
v2.31.2 (2026-05-02): 1 fix + 1 unit test (F-25 prevent new)
v2.31.3 (2026-05-03): 1 fix (F-25 heal old) + UI polish

**Total v2.31 cycle: 45 fixes + 8 unit tests across 8 iterations.**

## Cross-refs

- `plans/release-notes-v2.31.2.md` — F-25 prevent-new-corruption fix
- `plans/vpnrouter-ux-audit-2026-05-01.md` — F-25 / UX-23 source
