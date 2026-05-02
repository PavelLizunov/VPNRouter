# VPNRouter v2.31.1 — AU-9 handle leak + 2 deferred UX

Small follow-up to v2.31.0 (Stability + A11y, 39 fixes). Picks up the
three items that were deferred from that cycle: **AU-9 handle leak**
(deferred because investigation needed running-app diagnostics, not
deterministic in unit tests) and two visible UX polishes from
Pillar 5 (F-4, F-6).

Promoted from `v2.31.1-r1` after the verification gate went green and
MCP+UIA in-app testing confirmed both UX items end-to-end on the
running binary (auto-update from v2.31.0 → r1 succeeded).

## Fixes

| ID | Severity | What |
|---|---|---|
| **AU-9** | RISK | Root cause identified: `RuntimeStatusDetector.{IsVpnRunning,IsZapretRunning}` called `Process.GetProcessesByName(...)` and only inspected `.Length` — never disposed the returned `Process[]`. Each entry holds a kernel handle. The detector is polled every 1–2 seconds, so we accumulated ~120-240 orphan handles per VPN start/stop cycle until GC mopped them up — exactly matching the v2.30.7 audit's "+170/cycle" symptom. Routed both methods through a new `AnyProcessAlive(name)` helper that disposes every entry deterministically in a `finally` block. |
| **AU-9 follow-up** | LOW | `EtwProcessMonitor.Dispose()` now also disposes `_sessionReady` (the `ManualResetEventSlim` added in v2.31.0-r1's CO-6 fix). It lazily allocates a kernel `WaitHandle` on first `Wait(timeout)` and was leaking that handle once per app lifetime. |
| **F-4** (UX-6) | UX | Boot-autostart checkboxes (`AutostartVpn` / `AutostartZapret` / `AutostartTgProxy`) are greyed out when the Windows service isn't installed. Pre-fix the only install path was scrolling up to the master toggle. Added an inline **"Установить службу"** button right under the existing warning hint, both visible only when `!ServiceVm.IsInstalled`. New `InstallServiceForAutostartCommand` flips `ServiceVm.AutostartChecked = true` — same code path as clicking the master toggle, just discoverable from where the user is looking. |
| **F-6** (UX-33) | UX | Subscription card metadata renders as `URL · Ns · time` where `Ns` is the server count from last refresh. Pre-fix users read "7s" as seconds and the "—" placeholder (never refreshed) as opaque. Added `ToolTip.Tip` on the metadata `TextBlock` explaining each field: "URL · число серверов в последнем обновлении · когда был последний рефреш. «—» если ни разу не обновлялась." |

## Tests (+2, 27/27 passing total)

`RuntimeStatusDetectorHandleLeakTests` — invokes each detector method
5,000 times back-to-back and asserts no throw. The dispose pattern
itself is a code-review invariant; the test pins that the public
surface stays callable at any rate without crashing.

## Verification

- `dotnet build VPNRouter.sln -c Release` → **0 errors**
- **27/27 regression + AU-9 tests pass**
- Mac DMG / Linux AppImage+.deb / APT publish CI on r1 → all `success`
- 12 assets confirmed on r1 release
- MCP+UIA in-app verification:
  - F-4: inline "Установить службу" button visible below greyed checkboxes when service not installed; tooltip "Установит службу VPNRouter и активирует мастер-тумблер автозапуска выше." appears on hover
  - F-6: tooltip on subscription card metadata appears on hover, explains the cryptic format

## Cycle status

v2.31.0 (2026-05-02): 39 fixes + 5 unit tests across 5 iterations
v2.31.1 (2026-05-02): 4 fixes + 2 unit tests in 1 iteration
**Total v2.31 cycle: 43 fixes + 7 unit tests**

All deferred items from v2.31.0 closed. F-25 (1ms latency
investigation) remains as investigation-only and is moved to v2.32
backlog along with any new findings.

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md` — original cycle plan
- `plans/release-notes-v2.31.0.md` — last stable
- `plans/release-notes-v2.31.1-r1.md` — per-iteration notes
- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding audit (AU-9 source)
- `plans/vpnrouter-ux-audit-2026-05-01.md` — 72-finding audit (UX-6, UX-33)
