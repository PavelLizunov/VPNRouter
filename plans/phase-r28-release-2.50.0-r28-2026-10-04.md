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

Pending.
