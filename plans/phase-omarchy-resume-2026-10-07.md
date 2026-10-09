# Omarchy resume 2026-10-07

One task record for the unattended Omarchy session. Update this file; do not add sibling reports.

Evidence levels used below: read in source; unit-tested; CI green at an exact SHA; verified on a worker; verified on a real Omarchy host. Nothing in this session is above "read in source" until a later section says otherwise. This workstation must not build, test, install, elevate, or touch the live VPN or shell.

## Owner decisions

None yet. Safest defaults already fixed by the owner stay in force: fail-closed runtime policy, killSwitch and dnsLockdown stay false, no privileged helper, no runtime download, no elevation, capabilities only when verified.

## Intended result

Port the frozen Omarchy headless backend from draft PR 296 onto current origin/main as small draft pull requests. First delivery is Track A1: the Linux runtime-policy and TUN-ownership seams, with tests, without changing Windows, macOS, or Android behavior for callers that do not enter the new policy scope.

## Scope

In:

- New `VPNRouter.Core/Services/SingBoxRuntimePolicy.cs` and `LinuxTunOwnership.cs`, ported and re-read against current Core.
- The smallest wiring those types need in current `TunOwnershipLock`, `ProcessOwnership`, `SingBoxManager`, `VpnEngine`, `StartupPipeline`, and the verifiers that today assume a local sing-box path.
- `VPNRouter.Tests/HeadlessRuntimePolicyTests.cs` retargeted at current Core, plus the contract test slices named for Core.
- `InternalsVisibleTo` for Headless only when Headless lands (Track A2). A1 may add the test friend if tests need it; Headless friend waits for A2.

Out:

- VPNRouter.Headless itself (Track A2, separate branch and PR).
- packaging/arch and licence notices (Track A3).
- Plugin repo, kill switch, DNS lockdown, privileged helper, package publish.
- PR 296, its branch, and any rebase or comment on it.
- Plans and review dumps from the old branch. Protocol text is ported later, next to the code, not as a dump.

## Invariants

- Callers that pass a null runtime policy keep today's behavior. Production default policy is unavailable; file presence is not readiness.
- No new sudo, pkexec, Polkit, nftables, or setcap path.
- Windows semaphore ownership lock stays the Windows path.
- No local compile or test on this workstation. Verification is GitHub Actions at the pushed SHA.
- No credentials, subscription URLs, or emoji.

## Verification

- `git diff --check` before commit.
- Draft PR to main. Required evidence: CI green at the exact head SHA via `gh pr checks` and `gh pr view --json headRefOid`.
- Relevant CI slices are the existing Core filters in `.github/workflows/test.yml`. A1 does not add a new job; A2 does.
- Claims in the PR body name their evidence level.

## Unknowns

- Most old Core hunks do not apply onto main (`9aeae15b`). Each call site is re-resolved by reading both sides. A clean apply is not treated as correctness.
- `HeadlessRuntimePolicyTests.cs` is 1013 lines against September Core. It may not compile against October Core. If so, fix the tests to the current seams; do not weaken assertions to go green.
- Whether `SingBoxManager` on main still has `EffectivePolicy` is unknown until that file is read. If the seam moved, follow the current file.

## State

- Done: contract and zone reads; origin/main fetched at `9aeae15b`; worktree `/var/lib/dsh/Project/VPNRouter-omarchy-a` on `dsh/omarchy-headless-seams-2026-10-07`. Old branch read-only at `04bdff27`.
- Done in this worktree, not yet committed: `SingBoxRuntimePolicy.cs` and `LinuxTunOwnership.cs` copied from the old branch and re-read. `ProcessOwnership.cs` and `SingBoxManager.LinuxStop.cs` applied cleanly. Manual wiring, active only when a policy scope is entered: `SingBoxManager`, `VpnEngine.StartAsync`, `StartupPipeline` (skips binary deploy), `HealthCheck`, `DiagnosticsExporter`, `SingBoxFeatures`, `VlessDeepVerifier`, `FreeConfigDeepVerifier`. Tests copied as `HeadlessRuntimePolicyTests.cs`. `git diff --check` clean. No local build.
- Not done: TunOwnershipLock still uses the Windows semaphore on Linux. The old Linux flock branch does not apply. Headless, packaging, plugin are later PRs.
- A1 committed `2e248deb` and pushed. Draft PR https://github.com/PavelLizunov/VPNRouter/pull/558. At poll time: grep pass, go-test-windows pass, test pass, compile (android) pass. characterization-windows was still running. Head SHA matches `2e248deb`. No local build.
- A1 head moved to `53f8e532` after the state note. Draft PR https://github.com/PavelLizunov/VPNRouter/pull/558. At the later poll: compile pass, test pass, grep pass, go-test-windows pass. characterization-windows still running. Evidence level: CI green for those four jobs at `53f8e532`, not a full required-check rollup, not a worker, not Omarchy.
- A2 restacked onto `53f8e532` and force-pushed with lease. Head `d4d07de2`. Draft PR https://github.com/PavelLizunov/VPNRouter/pull/559. At that SHA: compile pass, test pass, grep pass, go-test-windows pass, headless-contract pass. characterization-windows still running. Same evidence limit. Not mergeable until 558 lands.
- A1 fix `7da5b48a`: deep verifiers honor the active policy. Run 37613416880 on that SHA passed characterization-windows, plus compile, test, grep, and go-test-windows. That is CI evidence for the fix, not a worker and not Omarchy.
- A2 restacked onto `7da5b48a`. Head `24b0a82b`. PR https://github.com/PavelLizunov/VPNRouter/pull/559. Previous head `b2a4cb42` was green, including headless-contract. The restack CI had not been read when this note was committed.
- A3 worktree `/var/lib/dsh/Project/VPNRouter-omarchy-c`, branch `dsh/omarchy-packaging-2026-10-07`. Packaging replay plus `packaging-arch` job. Head after restack `b32e0dbd`. PR https://github.com/PavelLizunov/VPNRouter/pull/560. Previous head `f0f856b4` passed packaging-arch, compile, test, grep, go-test-windows, and headless-contract. Restack CI unread at note time. Not a built package and not installed.
- Plugin repo, separate from VPNRouter: new branch `dsh/omarchy-plugin-resume-2026-10-07` at `2d9642c`, replayed onto plugin main `175d9da`. Draft https://github.com/PavelLizunov/omarchy-vpnrouter/pull/2. Both test jobs passed at that SHA. QML load was not run. Old plugin PR 1 was not touched.
- Follow-up on A1, not yet CI-proven: `TunOwnershipLock` now uses `LinuxTunOwnership` flock on Linux and keeps the semaphore elsewhere. The headless publish check is not on this branch, because the project does not exist here. It is added as its own job on the headless branch.
- Live heads when this note was written, before this commit: 558 `bc5bf9d2` (compile, test, grep, go-test pass; characterization was still running), 559 `b489b3f9` (those plus headless-contract pass; characterization still running), 560 `6466e92f` (those plus packaging-arch pass; characterization still running). Plugin PR 2 `2d9642c` both test jobs pass. QML was not loaded.
- Still out: kill switch, DNS lockdown, privileged helper, real Omarchy host, package install. Drafts stay draft. Merging needs the owner. No further force-push; later commits go on top.
- Next step: if characterization-windows fails, fix only that regression. Otherwise the owner decides whether 558 and 559 become reviewable non-draft. Packaging stays a separate draft and was not opened.

## Assumptions

- "Latest version" for this session is origin/main `9aeae15b`, not the local dirty Omarchy branch and not a binary drop.
- A1 ships before A2 because Headless cannot be reviewed against a Core that lacks the policy types.
- The old 67k of plan text is not ported. This file is the only new plan.
