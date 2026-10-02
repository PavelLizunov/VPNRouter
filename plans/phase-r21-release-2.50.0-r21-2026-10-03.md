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

Pending.
