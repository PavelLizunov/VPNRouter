# R-28: fixes found by running r27, candidate v2.50.0-r28

## Why

The live scenarios (`tools/live`) run on the installed r27 found four defects that no unit test could: a 2 s pre-connect probe that cannot finish for a selected Hysteria2/TUIC/AmneziaWG server (N-3e #528), the auto-select option being ignored for a selected Hysteria2/TUIC server although its tooltip promises a same-protocol group (N-3f #533), the candidate list of Other versions crowding out every stable release (N-1d #532), and an amber ping pill that was not re-notified (N-3d #526). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r28` (this PR).
2. Contents since r27: #526, #528, #532, #533 (product), #525, #529, #531, #534 (live harness), #527, #530 (docs).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, then `cycles` with a Hysteria2 server selected (expected connect about 4 s instead of 7.7 s), `versions` (stable releases present), `autoselect` with a Hysteria2 server selected (a urltest group of the Hysteria2 servers), `ping`, `modes`, `tabs`.

## Not covered

TUN adapter removal wait on stop, a scroll-capable all-servers scenario, Android, urltest switching under a degrading server, everything open in the r27 brief.

## Verification

Exact-head CI on this PR; the live evidence is recorded in the Outcome.

## Outcome

Published 2026-10-04 as prerelease `v2.50.0-r28` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `8f7e3e97`, 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows), integrity run green, strict CI gate `OK` with 8 green, waiver gate applied, `POSTSHIP-LOCAL: PASS` (2 cold cycles). Contents: N-3d #526, N-3e #528, N-1d #532, N-3f #533; tools #525, #529, #531, #534, #536; docs #527, #530.

Live evidence on WINBRAT with the installed r28 (r27 numbers from the same scenarios a few hours earlier):

| | r27 | r28 |
|---|---|---|
| connect from the home screen, Hysteria2 server selected | 7.65-7.71 s | 3.7-4.1 s |
| stop | 4.3-4.5 s | 4.6-4.9 s |
| auto-select, Hysteria2 server selected | no group (single outbound) | urltest of the 3 Hysteria2 servers, Clash API `URLTest`, member delays 59-166 ms, public IP = picked server |
| auto-select, VLESS server selected | group of 4, public IP = picked server | same |
| Other versions on the experimental channel | r26..r19 (8 candidates, no stable) | r27..r23 (5 candidates) + v2.49.3, v2.49.2, v2.49.1 |
| Test all, VPN off / on | 2.0 s / 2.1 s | 2.1 s / 2.1 s |

tabs 0 failures, modes 6 switches never showed "Connect", no warnings or errors in the log.

The first two post-ship runs FAILED ("One or more cold-cycle dataplane probes failed"): the worker config still had `auto_select_best_server: true` from the live `autoselect` scenario (the checkbox changes only the in-memory setting until the next save, so the scenario's "restore" had not reached the file), and `tools/brat-verify.ps1` requires the `proxy` outbound to be a plain proxy protocol ("The canonical HTTPS proxy does not resolve directly to a supported proxy protocol"); with an auto-select group it fails closed. After putting the file back the run passed. The data plane through the group was checked separately by the public IP. The verifier does not support an auto-select group; if the owner wants the release gate to run with the option on, `Test-ProxyCapableChain` use at that point needs a decision (not changed: it is a fail-closed gate script).

Not run: the screenshot gate, the live-update gate (stable channel to r28), switching of the urltest group under a degrading server, installing an older version from the list, macOS, Linux, Android on a device.
