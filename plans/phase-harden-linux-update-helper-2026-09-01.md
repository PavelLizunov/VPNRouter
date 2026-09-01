# Phase — require authentication for Linux update helper

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/harden-linux-update-helper`
**Accepted base**: `origin/main` at `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
**Audit ref**: matrix candidate `PN-2-4`
**Effort**: 30–60 minutes
**Risk**: MEDIUM
**Blast radius**: Linux `.deb` update authorization policy and its source contract test; no runtime VPN path
**Rollback**: revert the implementation commit; passwordless update behavior would return

## Why

The installed polkit policy currently grants an active local user implicit passwordless authorization to run `/usr/libexec/vpnrouter-update-helper`. Polkit constrains the executable path but not its arguments. The helper accepts a caller-controlled source directory, recursively copies it into a root-owned VPNRouter installation, and grants network capabilities to a supplied `sing-box`. Direct invocation therefore bypasses application-side release checksum validation and crosses a privilege boundary without administrator authentication.

## What

- Change the active-session polkit default from `yes` to `auth_admin`.
- Correct comments that currently claim the helper is safe to execute without a password.
- Pin the authentication requirement with a repository contract test.
- Preserve the helper executable path, destination allowlist, application-side checksum verification, and existing fallback behavior.

## Non-goals

- No custom signed receipt or root-owned staging protocol: administrator authentication is the existing native trust boundary and closes the passwordless confused-deputy path with the smallest change.
- No updater redesign, package build, release, tag, deployment, installation, or merge.

## How

1. Add a source contract that fails while `<allow_active>yes</allow_active>` remains.
2. Replace it with `<allow_active>auth_admin</allow_active>` and update policy/UpdateChecker comments.
3. Run XML parsing, the focused release-tooling contract tests, full suite, and independent security/correctness review.
4. Record exact-head CI and rollback in Outcome.

### Tests written

- `ReleaseToolingContractTests.LinuxUpdatePolicy_RequiresAdminAuthentication` — requires `auth_admin`, forbids implicit `yes`, and keeps the helper path annotation pinned.

### Verification approach

The policy is parsed as XML, its exact authorization values are contract-tested, and the existing full GitHub Actions suite remains the build/test oracle because the control plane has no .NET SDK or PowerShell.

## Verification gate

- [x] **Gate 1 — Scope**: only the Linux update policy, explanatory comments, one contract test, defect ledger, and task outcome change.
- [x] **Gate 2 — Security**: active users cannot invoke the privileged helper without administrator authentication; executable path and destination allowlist remain constrained.
- [x] **Gate 3 — Tests/build**: XML parsing, focused update workflow, Release build, and full discovered suite pass.
- [x] **Gate 4 — Documentation**: comments, defect status, PR body, and Outcome match actual policy behavior.
- [x] **Gate 5 — Independent review**: security, correctness, and test lenses leave no source-confirmed P0/P1.
- [x] **Gate 6 — Integration**: implementation-head GitHub `test`, `test-update`, `grep`, Windows Go, and characterization checks pass; UI/remote verification is N/A.

## Outcome

Implemented in PR #204 at code head `ce04b8d9404ac70448997ec92bce207d596b3587`. The packaged polkit action now requires `auth_admin` for active, inactive, and other sessions. The exact root-owned helper path remains pinned, and its destination allowlist and application-side release verification are unchanged. Stale passwordless-helper comments were removed from the policy, helper, and `UpdateChecker`.

The policy parsed successfully with the standard XML parser, `bash -n packaging/linux/vpnrouter-update-helper` passed, and `git diff --check` passed. Three independent security/correctness/test reviewers found no P0/P1; one stale helper comment was source-confirmed and fixed. GitHub Actions on the implementation head passed `test` (2,831 total: 2,774 passed, 57 platform/UI skips), `test-update`, `characterization-windows` (19/19), `go-test-windows`, and `grep`. This outcome-only commit must pass the same exact-head checks before merge.

Rollback is a revert of `ce04b8d9404ac70448997ec92bce207d596b3587`. No release, tag, deployment, installation, merge, or stable cut was performed; merge remains a separate owner decision.
