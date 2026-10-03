# R-21: tester fixes, candidate v2.50.0-r21

## Why

The tester's report on r20 (nine items, analysed in the session artifact "Замечания тестера r20") and the diagnostics VPNRouter-diagnostics-20261003-011238.zip showed real connection bugs: after a routing change the window showed Not connected
while the tunnel ran and the next Connect press stopped it (F-1), Connect picked a server from probes that went through the live tunnel (F-2), split-tunnel connect took 22-29 s and stop 41 s because of one netsh process per firewall rule
(F-3), the home card named a forgotten single link instead of the subscription server (F-4), and the apps-mode selector lost its highlighted segment when pressed (F-5). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r21` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, a live run that switches the routing mode and presses Connect after each switch, and timings of the firewall rule log lines.

## Not covered

The interface items of the report (narrow window, minimum size, header chips, Change panel, setup wizard entry: F-6..F-8, r22) and the Android home (A-8). Stable cut, live-update gate, hover with a real cursor.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Published 2026-10-03 as prerelease `v2.50.0-r21` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `a60cf1a6`, 18 assets. The six Windows files (unsigned: no SignPath secret exists) were built on
`windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows. Before publication: tag-bound `dotnet test`, `test-update`, the platform builds
and the dispatched integrity run green, strict CI gate `OK` (8 green, 0 red), `check-open-p0.ps1` with the recorded waiver (same three owner-gated P1 lines).

Included PRs: F-1 #487 (window/tunnel state, red-green policy tests), F-2 #488 (silent UDP ports are unverified, switch note), F-3 #489 (COM firewall store, netsh fallback; the real-COM round trip test ran on the worker),
F-4 #490 (home screen keeps the mode, card names the connected server), F-5 #491 (radio segments; the headless real-click test failed on the old markup and also showed that re-announcing the property did not help, so the control was replaced).

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` on the first attempt (version 2.50.0-r21, commit `a60cf1a6`, 2 cold cycles). Live run of the tester's failing sequence on the deployed build (UI Automation, two runs):
connect, then six switches All traffic / Selected apps with the window checked for 20 s after each: the window never showed Connect and sing-box stayed alive in all 12 switches (r20 flipped it every time); connect in
Selected apps mode 7.8-8.6 s and stop 4.8-5.4 s on this machine (the tester's r20 log: 22-29 s and 41 s on a machine where netsh costs 3x more). One stop in the first run took 92.8 s and did not reproduce in the next three; the cause is
not known (the app log of the live session is not flushed to disk until exit, so it could not be read). The home card shows the subscription server and "Subscription - 13 servers".

Not run: the screenshot gate, the live-update gate, hover with a real cursor. Still open from the tester report: narrow window / minimum size / header chips / Change panel / setup wizard entry (F-6..F-8) and the Android home (A-8).
