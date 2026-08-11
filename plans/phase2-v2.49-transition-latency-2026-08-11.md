# Phase 2 — v2.49 common transition latency

**Owner**: Codex root session 019ff00b-d54a-78b0-b3d9-bbc136e487ec  
**Branch**: `codex/v2.49-transition-profiling`  
**Prerequisite**: PR #131 / commit `7874d0d2` (v2.49 Apply stability baseline)  
**Effort**: 3-5 hours  
**Risk**: MEDIUM — the task measures and may change live process transitions; all UI and runtime checks run only on WINBRAT  
**Blast radius**: common desktop Connect/Apply flows plus Windows-only Telegram Proxy and Zapret toggles; Free Configs and server discovery are excluded  
**Rollback**: revert the implementation commit; measurement leaves WINBRAT disconnected and restores any temporary application-routing selection

## Why

The sing-box transport itself is an upstream boundary, but VPNRouter owns the
time spent before and after it: configuration generation, unnecessary process
restarts, fixed settle delays and UI state reconciliation. Optimizing an
unverified Free Configs path would be speculative because current usage is
unknown. The next v2.49 step therefore measures four frequent transitions and
changes only a bottleneck demonstrated by code and WINBRAT evidence.

The initial source audit already found one strong candidate. Manual Telegram
Proxy start waits up to two seconds inside `TgProxyManager.Start`, then the UI
unconditionally waits another two seconds before checking the same process and
listening port. Telegram Proxy and Zapret are independent sidecars and do not
restart sing-box or the VPN in their normal toggle paths.

## What

- Record action-to-ready timing for:
  - cold VPN Connect;
  - Apply after a reversible built-in application-routing change;
  - Telegram Proxy start and stop;
  - Zapret start and stop with the already-selected installed strategy.
- Record whether sing-box PID changes and whether VPN connectivity remains up
  during the two sidecar toggles.
- Use existing structured logs and typed UI/runtime signals where possible.
  Add no telemetry framework and collect no endpoint, credential or raw config
  data in committed evidence.
- Implement only the smallest confirmed improvement. The current leading
  candidate is removal of the duplicate Telegram Proxy settle wait while
  retaining the manager's early-exit safety probe.

## How

1. Verify WINBRAT identity and capture the installed baseline without touching
   the local development desktop.
2. Run at least three bounded samples for the candidate transition. Use one
   bounded sample for the other flows to classify them and confirm restart
   ownership; expand only if their result is ambiguous.
3. Snapshot the remote configuration before an Apply measurement and restore
   it in `finally`. End every run with VPN, Telegram Proxy and Zapret in their
   pre-measurement state.
4. For Telegram Proxy, preserve the existing two-second early-exit watchdog in
   `TgProxyManager.Start`; remove only the UI's second unconditional settle
   delay if the baseline confirms it is duplicated.
5. Keep the UI responsive by moving the synchronous manager start off the
   Avalonia dispatcher if that is required by the measured call path.
6. Add a focused regression test to pin that manual start has one bounded
   readiness window rather than two sequential fixed waits.
7. Re-run the same WINBRAT scenario and compare action-to-ready timing, process
   ownership and recent error logs.

### Tests planned

- Manual Telegram Proxy start does not contain a second fixed settle delay
  after `TgProxyManager.Start` returns.
- Existing `TgProxyManagerProcessRunnerTests` continue to pin early exit,
  running-process and stop behavior.
- Existing MainWindowViewModel characterization is updated only if the intended
  MainWindowViewModel edit changes its pinned source hash.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` has 0 errors.
- [ ] **Gate 2 — Tests green**: focused tests and the full suite pass; GitHub CI is authoritative for environment-bound cases.
- [ ] **Gate 3 — Docs**: this Outcome records sanitized before/after evidence and exact platform impact.
- [ ] **Gate 4 — Self-review**: Ponytail simplification review; bug-hunt for any non-trivial product change; security review fallback if process handling changes materially.
- [ ] **Gate 5 — Remote WINBRAT verify**: complete user flow, final status, process ownership and recent logs pass on WINBRAT only.
- [ ] **Gate 6 — Characterization**: MainWindowViewModel hash updated only for the intended diff.

## Acceptance

- Free Configs code, sources and UI remain unchanged.
- Telegram Proxy and Zapret toggles do not restart sing-box and do not drop an
  already-connected VPN session.
- A normal application-list Apply performs at most the restart required by the
  active routing policy; no duplicate Stop/Start cycle is introduced.
- The chosen optimization removes at least one second of deterministic waiting
  or demonstrates a material measured reduction without weakening readiness or
  early-exit detection.
- WINBRAT is restored to its initial component states after measurement.
- No release, tag, version bump, merge or deployment to user machines occurs in
  this task.

## Outcome

**Status**: IN PROGRESS

**Baseline evidence**: pending.

**Implementation**: pending measurement gate.

**Platform impact**: pending final scope.

**Verification**: pending.

**Surprises and follow-ups**: pending.
