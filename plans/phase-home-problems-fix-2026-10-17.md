# Home problems card: show detected problems in plain words and fix them with one button

## Why

Support case of 2026-10-17 (Windows 10, stable 2.50.3, simple mode). The user switched the home toggle to "All traffic",
then reinstalled. From then on the game (Assetto Corsa, ACDrift Launcher) reached its server 202.49.176.110 through the
VLESS server in Sweden instead of directly: connection setup grew from about 1 ms to 400-800 ms, TCP and UDP. The user
reported "it broke after the update" and reconnected four times in ten minutes. The VPN itself was healthy. The health
check also warned that TUN MTU 1420 is above the measured IPv4 path (largest DF payload 1380).

The fix was one click on the home toggle, but the user could not know that. The owner asked (2026-10-17) for the user to
see the problems on the main screen right away, understand what is wrong, and fix them with one button, without picking
MTU numbers. GPT Sol (pi gpt-sol-latest) reviewed the existing wizard and proposed a routing-first repair with Undo; its
cited lines were verified.

## What

- `VPNRouter.Core/Services/ProblemDetector.cs` (new, pure, unit-tested): turns the current state into a list of problems
  and a fix plan.
  - `GameTrafficThroughFullTunnel`: full tunnel is on and, since the last core start, the sing-box log shows proxied TCP
    or UDP flows to at least 3 distinct destination ports outside ordinary web and mail ports. Fix: back to split
    tunnel (the simple-mode default app set), so apps outside the VPN list go direct again.
  - `MtuAbovePath` (Windows): the configured TUN MTU is above the IPv4 DF-ping result, the same rule as the existing
    health check. Fix: the same conservative value the existing "Pick from IPv4 ping" button writes (largest payload,
    clamped to 1332..1420); not offered when the probe failed or the payload is below 1332.
  - `ServerTimeouts`: at least 3 dial timeouts to the active VPN endpoint since the last core start. Fix: reconnect.
  - `PlanFix`: switch to split, new MTU, reconnect when connected.
- `MainWindowViewModel.Problems.cs` (new): runs the detector in the background 40 s after a connect and on demand; the
  MTU probe runs at most once per app session. `FixProblemsAsync` snapshots routing mode, active profile and MTU, applies
  the plan, then restarts the tunnel once through the existing `ApplyPendingChangesInternalAsync(forceRestart: true)`
  (the setter-triggered auto-restart is suppressed during the repair), re-runs the detector and shows the result.
  `UndoProblemFixAsync` restores the snapshot the same way. "Hide" dismisses the card until the next connect.
- `SimplePage.axaml`: a warning card under the hero, in the style of the existing warning card: title, one line per
  problem (what it means and what the fix does), buttons Fix / Hide, then the result line with Undo.
- `Strings.Problems.cs` (new): Russian and English texts.

Not touched: servers, subscriptions, credentials, profile definitions and app lists, DNS, firewall, kill switch,
drivers, the setup wizard.

## Risk and rollback

A user who wants full tunnel on purpose sees the card while game traffic is detected; the card says what the fix changes
and offers Hide and Undo. Repair never runs by itself. Rollback: revert the commit; no settings schema change.

## Verification

1. Release build of the solution, zero errors (CI).
2. Unit tests for `ProblemDetector` with lines from the real 2026-10-17 log (full tunnel game flows detected; split
   tunnel, web-only flows and flows before the last core start ignored; MTU rule; timeouts; fix plan), plus the full
   discovered suite (CI).
3. Headless UI render of the home page with the card in Russian and English, light and dark, 360 and 520 px.
4. Brief Outcome filled.
5. Independent review of the diff by GPT Sol.
6. Live check on fixed WINBRAT before a release candidate.

## Outcome

Pending.
