# Omarchy backend round 4

## Intent and invariants

Implement the owner's shared contract-r4.md on branch dsh/omarchy-mvp-2026-10-22,
starting at 2628f77ee09339113b2c5bbc955092479e05abe4, for draft PR #616.
No local SDK/build, VPN connection, TUN startup or live desktop shell changes.
The plugin is owned by a separate job. No merge, tag or release.

## Interface

Add snapshot.unavailableReasonCode with the fixed runtime-missing,
runtime-capabilities, runtime-untrusted, sandbox, ownership, other and null codes.
Add apps.list.runningApps (name, displayName, icon, desktopId, hasWindow), keeping
running. Linux reads current-UID /proc entries and a bounded, TTL-cached desktop
index. Preserve executable basename casing and apps.set round trips.
Add source, subscriptionId and removable to servers.list; reject subscription
and legacy non-stored entries with not_removable without persistence.
Report first-launch missing configuration/directories as info.

## Verification

CI executes hermetic fixture-directory process/desktop tests, protocol feature
checks and runtime/ownership mappings on Ubuntu. Existing Windows/macOS paths
remain unchanged. Arch CI builds the exact commit and runs installed offline
checks with no TUN lifecycle for this round. Record serialized test samples,
all PR checks and exact artifact/run IDs in the external r4-backend.md receipt.
Review the task diff and run git diff --check before commit and push.
phase-task-launcher and bug-hunt are absent from the available skill catalog;
use this task record, direct source review and existing CI instead.

## Outcome

Implementation and fixture tests added. Direct diff review and git diff --check
passed. No local .NET execution. Fixed-model OpenCode Gemini review attempt
exited 1 before creating a session (stderr diagnostic); no independent acceptance.
CI pending first push.

First CI at dcd81afc exposed CS0103: RouterBackend needed the Lifecycle namespace
for PlatformCapabilityVerifier. Add the missing using; rerun exact-head CI.

Caller review found the desktop setup wizard passes HealthCheck.RunAll as a
parameterless method group. Retain the original overload and add the explicit
bool overload, preserving that consumer without touching UI source.

## Implementation acceptance

At 865e3eaa60153e28ade2d271e7d45643ecd93ddc, all nine PR #616 checks passed;
`gh pr checks 616 --watch` exited 0 and PR headRefOid matched local HEAD.
Dotnet workflow 39852743312: Linux 3210 passed/77 skipped, Windows 3379 passed/
19 skipped, headless runner 38 passed/0 failed, Python packaging 124 passed.
Arch workflow 39852737249 passed installed offline protocol and runtime-policy
checks with no TUN startup, and produced vpnrouter-headless-arch artifact
12458980428. Fixture app discovery measured 0.93 ms cold and 10.80 ms warm with
211 proc directories; this is not owner-host performance acceptance.

Source review covered the new files, task-owned diff and affected callers.
README protocol/runtime statements and package CI description were updated.
No local .NET execution, owner-host install, shell interaction, VPN connection,
merge, tag or release. Independent review remains unavailable as recorded above.
The final documentation head, its CI/artifact IDs and JSON copied from actual
test output are recorded in the owner's requested external r4-backend.md receipt.
