# R-16: design candidate v2.50.0-r16

## Why

r15 fixed the two defects from the owner's r14 diagnostics. Since its tag, the PC half of the design work the owner asked for ("carry the bottom
navigation icons to the PC version", "finish all design fixes") landed on main: D-5 (#452, navigation icons in the tab strip), D-6 (#457, inner
tab strips use the main tab roles) and the post-ship verifier fix (#455). The owner gave a standing mandate to decide the rest without asking.
This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r16` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the
   tag workflows, prepublication gate (18 assets, integrity, strict CI), `check-open-p0.ps1` with the recorded waiver (same three owner-gated P1
   lines as r10 to r15), publish as a prerelease (`--latest=false`).
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`. It runs the verifier scripts of the tag, so this is the first release whose tag
   contains the lifecycle false-positive fix (#455).

## Not covered

Stable cut, the previous-stable to candidate live-update gate; hover states with a real cursor on the inner tab strips; the driver-lock fix
(D-4) is still covered by a contract test only, the owner's next update is its real check; Android on a Pixel with Android 16.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-02 as prerelease `v2.50.0-r16` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `0308b044`, 18 assets. The six Windows files (unsigned: no SignPath secret or variable exists) were built on `windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows. Before publication: tag-bound `test`, `test-update`, three platform builds and the dispatched integrity run green, strict CI gate `OK` (8 green, 0 red), `check-open-p0.ps1` run with the recorded waiver (same three owner-gated P1 lines as r10 to r15). After publication: the integrity and APT runs of the release event are green.

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` on the first attempt (version 2.50.0-r16, commit `0308b044`, 2 cold cycles), with the verifier scripts of this tag, which contain the lifecycle fix of #455.
