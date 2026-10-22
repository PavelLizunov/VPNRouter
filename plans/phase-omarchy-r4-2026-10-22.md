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
