# VPNRouter v2.31.1-r1 — AU-9 handle leak fix + 2 deferred UX

Opens the v2.31.1 cycle. Picks up three items deferred from v2.31.0:
the **AU-9 handle leak** (deferred from r1 because investigation
needed running-app diagnostics), plus two visible UX polishes (F-4,
F-6) deferred from Pillar 5 of the v2.31.0 plan.

## Fixes

| ID | Severity | What |
|---|---|---|
| **AU-9** | RISK | Root cause identified: `RuntimeStatusDetector.{IsVpnRunning,IsZapretRunning}` called `Process.GetProcessesByName(...)` and only inspected `.Length` — never disposed the returned `Process[]`. Each entry holds a kernel handle. The detector is polled every 1–2 seconds (see class summary), so we accumulated ~120-240 orphan handles per VPN start/stop cycle until GC mopped them up — exactly matching the audit's "+170/cycle" symptom. Routed both methods through a new `AnyProcessAlive(name)` helper that disposes every entry deterministically in a `finally` block. |
| **AU-9 follow-up** | LOW | `EtwProcessMonitor.Dispose()` was calling `Stop()` only — the `ManualResetEventSlim _sessionReady` (added in r1's CO-6 fix) lazily allocates a kernel WaitHandle on the first `Wait(timeout)` call and was leaking that handle once per app lifetime. Added `_sessionReady.Dispose()` to the dispose path. |
| **F-4** (UX-6) | UX | Boot-autostart checkboxes (`AutostartVpn` / `AutostartZapret` / `AutostartTgProxy`) are greyed out when the Windows service isn't installed. Pre-fix the only install path was scrolling up to the master toggle, which wasn't obvious. Added an inline "Установить службу" button right under the existing warning hint, both visible only when `!ServiceVm.IsInstalled`. New `InstallServiceForAutostartCommand` flips the master `ServiceVm.AutostartChecked = true` — same code path as clicking the master toggle, just discoverable from where the user is looking. |
| **F-6** (UX-33) | UX | Subscription card metadata renders as `URL · Ns · time` where `Ns` is the server count from last refresh. Pre-fix users read "7s" as seconds and the "—" placeholder (never refreshed) as opaque. Added `ToolTip.Tip` on the metadata `TextBlock` explaining each field. |

## Tests (+2, 27/27 passing total)

`RuntimeStatusDetectorHandleLeakTests` — invokes each detector method
5,000 times back-to-back and asserts no throw. The dispose pattern
itself is a code-review invariant; the test pins that the public
surface stays callable at any rate without crashing.

## Cycle progress

v2.31.0 stable shipped on 2026-05-02 with 39 fixes + 5 unit tests.
v2.31.1 is the small follow-up with the deferred items:

| Item | Status |
|---|---|
| AU-9 (handle leak) | r1: fixed |
| F-4 (boot autostart CTA) | r1: fixed |
| F-6 (sub metadata tooltip) | r1: fixed |
| F-25 (1ms latency investigation) | not yet — still investigation-only |

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md` — original cycle plan
- `plans/release-notes-v2.31.0.md` — last stable
- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding audit (AU-9 source)
- `plans/vpnrouter-ux-audit-2026-05-01.md` — 72-finding audit (UX-6, UX-33)
