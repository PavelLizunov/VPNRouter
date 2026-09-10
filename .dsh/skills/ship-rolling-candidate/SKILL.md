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

## Create the immutable tag and draft

After explicit release authorization, record the accepted commit as `$sha`,
set `$version = 'X.Y.Z-rN'` and `$tag = "v$version"`. Require clean `HEAD` to
match accepted GitHub `main` and AppVersion to match the full version. Create
and push only this new tag; never replace an existing tag:

```powershell
git tag $tag $sha
git push origin "refs/tags/$tag"
$remoteSha = gh api "repos/PavelLizunov/VPNRouter/commits/$tag" --jq '.sha'
if ($LASTEXITCODE -ne 0 -or $remoteSha -ne $sha) { throw 'Remote tag SHA mismatch.' }
gh release create $tag --verify-tag --draft --prerelease --latest=false --title $tag --notes-file <notes-file>
```

Check every native command's exit code; stop on failure. Inspect an existing
tag/draft instead of blindly repeating creation. `build.ps1 -Upload` does not
create tags or releases and does not publish.

## Stage Windows and other platforms

Inspect the configured SignPath secret names and `SIGNPATH_EXPECTED_SUBJECT`
repository variable without exposing values. Any SignPath configuration,
including partial enrollment, forbids unsigned staging; finish enrollment or
STOP. There is no unsigned fallback when signing fails.

- Signed path: `gh workflow run sign-windows.yml --ref $tag -f version=$version`.
  Wait for the exact-tag source build, owner signing approval and signature
  verification before accepting its draft ZIPs and sidecars.
- Only when all SignPath settings are absent, build the custom sing-box-lx on
  the authorized exact-SHA worker and stage unsigned Windows assets:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-singbox-lx.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -Version $version `
  -SingBoxPath "publish/sing-box-lx.exe" -Upload
```

Unsigned staging requires `HEAD == accepted main == tag SHA`, an existing
correct-channel draft, and no conflicting assets. It never clobbers assets.
Wait for tag-triggered `build-mac.yml`, `build-linux.yml`, `build-android.yml`,
`test.yml` and `test-windows-update.yml`. Missing draft means staging fails
closed; after creating it, inspect run results and draft assets before retry.
For a missing platform set, dispatch its build workflow with `--ref $tag
-f version=$version` (and `-f upload_to_release=true` where offered). Test
workflows use `--ref $tag` without a version input. Legacy `sign-android.yml`
is disabled; use `build-android.yml`.

A partial upload is not permission to overwrite or delete assets. Inspect names,
hashes and provenance, then explicitly stage only missing files from the
verified exact-tag output without clobber. If consistency cannot be proven,
STOP; published corrections require a new version and immutable tag.

## Prepublication gate and authorized publication

Require all platform staging jobs and tag-bound tests/Windows update jobs green
at the exact tag SHA, exactly 16 canonical assets (4 Windows, 4 macOS, 6 Linux,
2 Android), every sidecar matching, and both Windows True Split driver bundles.
Then explicitly dispatch and await the completed-draft integrity gate:

```powershell
gh workflow run verify-release-integrity.yml --ref $tag -f tag=$tag -f auto_draft_on_failure=false
```

Run the prepublication strict gate with explicit requirements, because the
default strict requirements include postpublication APT:

```powershell
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1 `
  -Commit $sha -ReleaseTag $tag -Strict `
  -RequiredSuccess "build=3,verify=1,test-update=1,test=1,go-test-windows=1,characterization-windows=1" `
  -RequiredWorkflows "Build macOS DMG,Build Android APK,Build Linux AppImage + .deb,Verify Release Integrity,Auto-Update Integration Test (Windows),dotnet test"
```

Require canonical platform/test/update/integrity runs and jobs, not unrelated
same-SHA checks. Inspect the exact-tag Sign Windows run separately when signing
is configured. Do not require APT or post-ship verification before publication.
After inspecting the successful integrity run and reconfirming owner authority:

```powershell
gh release edit $tag --draft=false --prerelease --latest=false
```

Confirm the candidate is public/prerelease and the previous stable remains
Latest. Asset uploads are not `release: edited` completion signals. After
publication, await integrity and APT runs; if token-trigger suppression leaves
them absent, explicitly dispatch at the published tag:

```powershell
gh workflow run verify-release-integrity.yml --ref $tag -f tag=$tag -f auto_draft_on_failure=false
gh workflow run publish-apt.yml --ref $tag -f tag=$tag
```

Candidate APT runs verify provenance and reindex the latest published stable;
they do not add candidates to APT. Homebrew notification is suppressed during
draft staging and candidates must not notify the stable tap. Delete a
superseded candidate release page only after the new candidate passes the
post-ship gate; retain immutable tags.

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
