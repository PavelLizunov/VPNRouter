# Exact-head Arch runtime package

## Intent and invariants

Implement the owner-approved package-manager trust model on PR #616 in this worktree only.
Keep the normal-user stdio entry point at `/usr/lib/vpnrouter-headless/VPNRouter.Headless`.
Trust only the fixed root-owned, non-writable packaged sing-box with effective/permitted
CAP_NET_ADMIN. No binary SHA pin in production. Linux kill switch and DNS lockdown stay false.
No local SDK, build, host installation, merge, tag or release. CI owns builds and installation.

## Interface and scope

Runtime policy, readiness diagnostics, existing exact-child stop, Arch driver/install script,
aligned desktop core pins, source SHA manifest, Ubuntu policy checks, Arch build/install CI.
Snapshot adds a safe `unavailableReason`; test-file policies retain SHA verification.
The package install script sets only sing-box file capabilities. The dedicated push workflow checks out github.sha and passes it as --source-sha;
the artifact therefore records the branch head, rather than the PR synthetic merge.

## Verification

Run Python packaging tests locally without bytecode, review the task diff, then push and watch
all PR checks. CI builds/inspects/installs the real package, verifies getcap, normal-user snapshot,
and bounded normal-user TUN creation/stop if the runner kernel permits it. Record exact source,
run and artifact identities in the external r2 report. Owner-host traffic remains a separate check.

## Outcomes

Local Python packaging suite: 123 passed; git diff --check passed.
Independent Gemini review was attempted with the fixed opencode-gemini helper, but
it exited 1 before a session was created (OpenCode emitted stderr). No independent
acceptance is claimed; direct source/diff review completed. CI acceptance pending. phase-task-launcher and bug-hunt are absent from the available
skill catalog; this record follows sdd and direct evidence-based review instead.
