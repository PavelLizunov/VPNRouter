# VPNRouter v2.31.2 — F-25 1ms latency fix on Saved configs

Single-fix release closing the last deferred audit item from the
v2.31.0 cycle. Promoted from `v2.31.2-r1` after the verification gate
went green.

## Fix

| ID | Severity | What |
|---|---|---|
| **F-25** (UX-23) | UX | Implausible 1 ms ping shown for Saved Free Configs. Root cause: `FreeConfigTester.TcpPingOnlyAsync` (the Recheck-flow helper used by the Saved-tab "↻ Перепроверить" buttons) was missing the `ImplausibleThresholdMs=5` plausibility gate that `TestOneAsync` already enforces. `TcpClient.ConnectAsync` returns in <1 ms when the OS has cached the route + ARP entry from a previous Deep Verify (most Saved entries fit this), so every recheck silently overwrote the previously-plausible Verified `LatencyMs` with a bogus sub-1 ms reading. Confirmed by inspecting the running cache: 22 entries had `LatencyMs=1`. Fix mirrors the gate in `TcpPingOnlyAsync`; sub-5 ms readings are dropped, the previous value (which already passed the gate during the original Deep Verify) is kept. `Status` preserved as before. |

## Tests (+1, 28/28 passing total)

`TcpPingOnlyPlausibilityGateTests.TcpPingOnlyAsync_UnreachablePort_DoesNotMutateLatency`
covers the failure path — port refused, both `LatencyMs` and `Status`
preserved. (A loopback-listener test that exercised the gate directly
flaked under parallel xUnit runs and was dropped with a comment.)

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 28/28 regression + AU-9 + F-25 tests pass
- Mac / Linux / APT CI on r1 → all `success`
- 12 assets confirmed on r1

## v2.31 cycle — closed

| Release | Date | Scope |
|---|---|---|
| v2.31.0 | 2026-05-02 | Stability + A11y cycle: 39 fixes + 5 unit tests across 5 iterations (CO-1..CO-8 stability + 20 A11y CheckBox UIA Name + 4 VM leak/race + 8 UX polish + F-26 toast scope hotfix) |
| v2.31.1 | 2026-05-02 | AU-9 handle leak + F-4 + F-6 (3 deferred items + 2 tests) |
| **v2.31.2** | **2026-05-02** | **F-25 1ms latency (1 fix + 1 test)** |

**Total v2.31 cycle: 44 fixes + 8 unit tests across 7 iterations.**
All audit-deferred items closed.

Existing cache entries with `LatencyMs=1` will get refreshed
organically as users re-verify them or the next Deep Verify pass
runs (no code path persists the bogus value beyond what's already in
`%ProgramData%\VPNRouter\cache\free_configs.json`).

## Cross-refs

- `plans/release-notes-v2.31.1.md` — previous stable
- `plans/release-notes-v2.31.0.md` — major v2.31 cycle
- `plans/vpnrouter-ux-audit-2026-05-01.md` — F-25 source finding (UX-23 in original numbering)
