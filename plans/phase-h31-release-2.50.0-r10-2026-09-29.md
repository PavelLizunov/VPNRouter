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

Published 2026-09-29 as prerelease `v2.50.0-r10` (`--latest=false`; Latest stayed `v2.49.3`), tag on
`6bae0234` (AppVersion bump #366; #336, #331 and #332 merged just before, #335 not merged because the
Spectre.Console.Cli 0.55 API breaks the CLI build, #337 closed).

- Assets: exactly 16. Android, macOS and Linux from the tag-triggered workflows; the two Windows archives
  and sidecars built on `windows-worker` (`build.ps1 -Version 2.50.0-r10 -BundleSplitDriver`, unsigned
  path, no SignPath settings exist) and uploaded to the draft without clobber. Both archives contain the
  split-tunnel driver and their sidecars match.
- Tag CI green: macOS, Linux, Android, Windows update test and `dotnet test`.
- The draft-time run of `Verify Release Integrity` failed with HTTP 404 because it read the release through
  `releases/tags/<tag>`, which does not return drafts (r9 failed the same way at draft time). The workflow's
  own Python check, run locally on the downloaded draft assets, passed (0 errors; warnings only for tools
  missing on the local machine). After publication the `release: published` integrity run and the APT run both
  succeeded.
- `check-open-p0.ps1` reported 4 open owner-gated P1 lines; the owner waived them for this cut (2026-09-29).
- Published Windows archive smoke check on `windows-worker` (not an install): hash equals the sidecar, the CLI
  reports `2.50.0-r10`, sing-box `1.14.0-vpnctl.5`, driver files present, unsigned as expected.
- Post-ship gate (`tools/post-ship-verify.ps1`): NOT run. It needs the owner's Windows PC (WinRM credential
  file, local .NET SDK) which is offline, and its visual gate runs the six `PageScreenshotTests` that fail on
  `windows-worker`. The candidate is therefore published but not verified; run
  `tools/post-ship-verify.ps1 -Version 2.50.0-r10 -Cycles 2` from that PC.
- Follow-up in H-32: the integrity workflow now finds draft releases.
