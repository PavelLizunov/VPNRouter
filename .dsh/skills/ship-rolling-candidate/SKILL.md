---
name: ship-rolling-candidate
description: Ship a rolling vX.Y.Z-rN candidate through branch/PR/CI, all platform assets and fixed-WINBRAT verification.
whenToUse: The user explicitly authorizes a rolling candidate release. Never tag, release, deploy, merge or cut stable autonomously.
---

# Ship a rolling release candidate

Read `docs/agent-contract.md` first. This skill grants no release authority: an
explicit owner command is required for every candidate, and stable cut is never
autonomous. Use `cut-stable` only after a separate explicit stable command.

## Hard preconditions

1. Work from the current clean task worktree, not an absolute path or another
   checkout. Verify the branch, repository root and exact `HEAD`.
2. `harness-test` is control plane only. Before any build, test, or package job,
   select an authorized build worker, verify the exact repository SHA, and run
   the read-only identity/active-job/CPU/RAM/disk/SDK preflight from
   `docs/test-workers.md`. Queue on conflict, allow one mutable scenario per
   worker, and STOP on a missing SDK; do not provision or clean shared caches.
3. Run `tools/verify-last-commit-ci.ps1`; any red or in-progress result is STOP.
4. Run the Release solution build, full tests, relevant visual tests and an
   independent review. Record every surviving finding in `plans/OPEN-DEFECTS.md`.
5. `tools/check-open-p0.ps1` must pass unless the owner explicitly records a
   waiver.
6. `AppVersion.Version` must exactly equal `X.Y.Z-rN`.

## Branch and accepted commit

Commit without bypassing hooks, push only the task branch with
`git push -u origin HEAD`, open/update its PR to `main`, and wait for all checks.
Do not push `HEAD:main`, do not use a `github` remote and do not treat `origin`
as Forgejo. Merge requires explicit owner authorization. After merge, continue
only from a clean checkout whose `HEAD` equals accepted `origin/main`; use
repo-relative scripts from that checkout.

## Build and publish

First inspect the configured SignPath secret names and
`SIGNPATH_EXPECTED_SUBJECT` repository variable.

- When signing is configured, create the immutable tag and a **draft** release
  at the accepted commit, then run `Sign Windows (SignPath)`. Do not run a local
  unsigned `build.ps1 -Upload`. The workflow builds from the exact tag, verifies
  every required signature and stages the signed ZIPs while the release remains
  draft. Publish only after the complete platform gate below.
- Until enrollment is configured, build the custom sing-box-lx binary and use
  the existing unsigned upload path from the accepted exact commit:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-singbox-lx.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z-rN" `
  -SingBoxPath "publish/sing-box-lx.exe" -Upload
```

Apply release notes, mark the new release prerelease, keep the previous stable
as Latest, and delete only the superseded rolling release page after the new
candidate is fully verified. Never force-update a published tag.

## Exact platform and artifact gate

Wait for exact-SHA tests plus macOS, Linux, Android, Windows update, APT and
release-integrity workflows. Require exactly 16 canonical assets: 4 Windows,
4 macOS, 6 Linux and 2 Android ARM64 files. Every sidecar must match. The full
and update Windows ZIPs must contain their expected True Split driver bundles.

## Mandatory post-ship gate

Immediately delegate to the canonical post-ship verifier:

```powershell
powershell -ExecutionPolicy Bypass -File tools/post-ship-verify.ps1 `
  -Version X.Y.Z-rN -Cycles 2
```

It must return exit 0 and `"Status":"PASS"`. This performs the fixed-WINBRAT
identity check, clean deploy, UIA/applicable headless checks, two complete
proxy HTTPS/UDP connection cycles, lifecycle/log classification and cleanup.
There is no developer-machine fallback. A Core-only change is labelled not
UI-testable but still runs every applicable binary/dataplane/log gate.

Only after this PASS may the report call the candidate verified. Report the
exact commit, 16 assets, workflow status, WINBRAT cycles, log scan, cleanup and
any owner-blocked external step. Candidate PASS is readiness evidence only; it
does not authorize stable.
