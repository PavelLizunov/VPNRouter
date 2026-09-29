# H-31: ship the rolling candidate v2.50.0-r10

## Why

Main carries about fifty commits after `v2.50.0-r9` (security hardening, the audit fixes of
2026-09-29, the refactor splits). The owner authorized shipping a new rolling candidate on 2026-09-29
("выпускаем"). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r10` (this PR).
2. After it is merged: tag `v2.50.0-r10` on the accepted commit, create the draft prerelease with notes.
3. Windows assets: no SignPath secret or variable exists, so the unsigned path applies. `build.ps1
   -Version 2.50.0-r10 -BundleSplitDriver` runs on `windows-worker` (private toolchain, Go 1.21.13) and the
   four files are uploaded to the draft without clobber.
4. macOS, Linux and Android come from the tag-triggered workflows; Windows update test likewise.
5. Prepublication gate: 16 assets, sidecars, integrity workflow, strict CI check; open P0/P1 lines
   (4, owner-gated) need the explicit `check-open-p0.ps1 -Waive` for this cut.
6. Publication as prerelease (`--latest=false`) only after the owner reconfirms; then integrity and APT runs.
7. Post-ship WINBRAT gate: NOT run without the owner's confirmation (the owner said the VM/VPN must not be
   restarted for now; the verifier deploys and cycles connections on WINBRAT).

## Not covered

Stable cut. A candidate is not verified until the post-ship gate passes.

## Verification

Exact-head CI on this PR; the release steps above are recorded in the Outcome as they complete.

## Outcome

Pending.
