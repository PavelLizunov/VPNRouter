# FC — Public Configs (search / recheck / apply)

Contract for the Free / Public Configs feature. Source theory:
`plans/user-interaction-boundaries-and-edge-case-verification-framework-2026-06-02.md`.
Audit basis: `plans/public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md`.

**User intent:** find working public VLESS servers, keep a verified shortlist,
and adopt one as the active generated server + connect.

**Platform parity (B7):** `adapter` — same envelope on desktop and Android, with
platform-specific verifier (desktop spawns `sing-box.exe`; Android uses in-proc
`libbox`) and source resolution (desktop combines built-in + user sources;
Android consumes the server-aggregated `pool.json` only = `reduced`, by design).

## State model

```
page:   FC.page.closed | search | saved
search: idle -> fetching-pool -> validating-pool -> filtering
             -> fast-probing -> deep-verifying -> saving -> done -> idle
        any running phase -> cancelling -> idle
        any running phase -> failed     -> idle
candidate: discovered -> parsed -> fast-ok -> verified ;  any -> rejected
           (only `verified` is normal-connectable — B4)
saved:  fresh | stale | failed-last-check | policy-excluded | malformed-quarantined
```

## Resolved decisions (2026-06-02)

| # | Decision |
|---|---|
| 1 | Search **allowed** while main VPN connected (verifier is SOCKS-isolated). |
| 2 | Verifier = SOCKS inbound, no own TUN. Split tunnel → probe direct. Full tunnel → probe rides active TUN (measures reach-through-VPN; documented; P3 backlog to exclude verifier from TUN). |
| 3 | Partial **Verified** results **persisted** on cancel (no weak candidates). |
| 4 | **Connectable ⇔ Status==Verified**, uniform. No expert override. Stale / failed-last-check are advisory only. |
| 5 | Apply **blocked** while a search/recheck runs (B1). |
| 6 | Page close / tab-switch / Android background **cancels** the search. |
| 7 | target ∈ **[1, 50]**, maxPing ∈ **[50, 2000] ms**, both platforms. |
| 8 | No hard stale-pool block (deep-verify is the live gate). Optional > 7-day soft hint. |
| 9 | Custom sources **combined** with built-in (desktop). Android pool-only = `reduced`. |

## Action contracts

### `FC.SEARCH.START` — find working configs
- **Enabled when:** `search.idle`. **Rejected when:** another search/recheck or
  destructive Saved mutation owns the operation.
- **Inputs/limits:** target `[1,50]`, maxPing `[50,2000]` ms, RU policy (G5).
- **Durable effect:** add/refresh **Verified** Saved records; prior Saved preserved
  on failure (G2, G6). **Runtime:** pool fetch + parse + fast probe + transient
  verifier proc/libbox (SOCKS, no TUN).
- **Cancel:** preserve prior Saved; keep already-final Verified (decision #3).
- **Relaunch:** recover Saved cache; clean verifier temp artifacts; no stuck busy (G7).
- **Invariants:** G2, G3, G4, G5, G6, G7, G9, G10.

### `FC.SEARCH.CANCEL`
- **Enabled when:** a search/recheck is running and supports cooperative cancel.
- **Durable:** no deletion; save already-final Verified only. **Status** must
  distinguish cancelled from failed/timeout (G3). **Relaunch:** no stuck busy.

### `FC.SAVED.RECHECK.ONE / .STALE`
- **Enabled when:** Saved row(s) exist, operation idle.
- **Success:** refresh the successful checkpoint. **Failure:** keep the prior
  usable row, mark **failed-last-check**, and **do not report fresh success**
  (the #146 fix — keyed on a fresh `LastDeepVerifyAt` stamp, not residual
  `Verified`). **Cancel:** restore prior state, no failure marker.
- **Invariants:** G2, G3, G4, G10.

### `FC.APPLY.SELECTED` — adopt + connect
- **Enabled when:** selected row is **Verified** AND apply-owner is idle.
- **Rejected when:** weak / malformed / no selection / **another lifecycle
  mutation or search is running** (decision #5).
- **Durable:** add/update the adopted server + set generated/manual mode WITHOUT
  deleting unrelated servers (G2, #145 class). **Runtime:** stop prior VPN if
  needed; start via the guarded two-phase lifecycle. **Warning:** one-time
  public-proxy risk acknowledgement (both platforms).
- **Failure:** preserve the server list; surface actionable status.
- **Invariants:** G1, G2, G3, G4, G6, G9, G10.

### `FC.SAVED.CLEAR.ALL`
- **Enabled when:** Saved non-empty, no conflicting op. **Confirmation required**
  (B5). **Must not change** manual Servers / active config / custom sources.
- **Invariants:** G2, G3, G5.

## Implementation state vs contract

| Contract point | Desktop | Android | Gap → Phase 2 |
|---|---|---|---|
| Connectable ⇔ Verified (#4, B4) | Apply NOT status-gated | **gated** (#148 `ApplyFcConnectGate` + click backstop) | desktop defensive Verified-gate (P1) |
| Apply blocked during search (#5, B1) | not gated | not gated | gate both on op-idle (P1) |
| target/maxPing bounds (#7, G5) | **no clamp** (10/400 default) | clamped 1-50 / 50-2000 (#148) | desktop clamps (P2) |
| Cancel on close/bg (#6) | `Dispose` cancels | `StopFreeConfigsBackgroundWork` on close/tab | verify Android `OnPause` hook (P2) |
| Partial-Verified-on-cancel (#3) | done | done | — |
| Saved-recheck truthful (#146) | done | done | — |
| Servers preserved on apply-fail (#145) | done | **done** | — |
| Verifier SOCKS-isolated (#2) | done (no TUN) | done (no TUN) | P3: exclude verifier from active TUN |
| Pool gz fetch + last-known-good (B6) | done (#4 r4, bounded+atomic) | done | — |
| Custom sources combined (#9) | done | pool-only (`reduced`, intended) | — (document) |

## Scenario matrix (seed — pin at the cheapest layer)

| ID | Action | Boundary tuple | Expected | Layer |
|---|---|---|---|---|
| `FC-001` | Search | `D.empty + E.online-fast + L.none` | target Verified rows saved | L2 |
| `FC-002` | Search | `D.stale + E.offline` | fallback explained; old Saved preserved | L2 |
| `FC-003` | Search | `D.one-good-cache + E.malformed` | corrupt pool rejected; old pool retained | L2 |
| `FC-004` | Search | `E.online-slow + L.cancel@download` | prompt cancel; cache intact | L2 |
| `FC-005` | Search | `L.double-click` | one owner only (G4) | L1/L3 |
| `FC-006` | Search | `P.android + weak candidates` | weak not connectable; scan to Verified target | L1 (done #148) |
| `FC-007` | Recheck | `prior Verified + E.process-missing` | prior row kept, failed-last-check | L1 (done #146) |
| `FC-008` | Recheck | `prior Verified + L.cancel@deep` | prior row restored; no false failure | L1 (done #146) |
| `FC-009` | Apply | `Verified + Servers many + E.disk-full` | Servers unchanged | L1 (done #145 android) / desktop TBD |
| `FC-010` | Apply | `weak Ok` | rejected both platforms | L3 (android done; desktop P1) |
| `FC-011` | Apply | `VPN.running + phase-A timeout` | prior runtime converges; status truthful | L2 |
| `FC-012` | Clear all | `Saved many + confirm=no` | no mutation | L1/L3 |
| `FC-013` | Filter RU | `cached Verified RU` | absent from eligible result | L1 (done #4 r5) |
| `FC-014` | Search | `P.android + L.background@deep` | bounded stop; no stuck busy | L4 (P2 verify) |
| `FC-015` | Relaunch | `L.process-kill@verifier` | temp cleaned; no stuck busy | L4 |
| `FC-016` | Apply | `apply during running search` | Apply rejected until idle (decision #5) | L3 (P1) |
