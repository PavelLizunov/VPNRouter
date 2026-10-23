# Exact-head Arch runtime package

## Intent and invariants

Implement the owner-approved package-manager trust model on PR #616 in this worktree only.
Keep the normal-user stdio entry point at `/usr/lib/vpnrouter-headless/VPNRouter.Headless`.
Trust only the fixed root-owned, non-writable packaged sing-box with effective/permitted
CAP_NET_ADMIN. No binary SHA pin in production. Linux kill switch and DNS lockdown stay false.
CI owns builds and package installation. No local SDK is used; no merge, tag or release is requested.

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

Implementation receipt at `29dcca1a29a670f0a45cf44a72fa639231427040`:

- Arch build/install/upgrade/TUN acceptance succeeded in
  https://github.com/PavelLizunov/VPNRouter/actions/runs/39837132508.
  Artifact `vpnrouter-headless-arch`, ID `12450988334`.
- The installed normal-user backend reported connect=true. Missing capabilities,
  symlink, non-root owner, writable binary and writable ancestor each produced
  connect=false with a safe reason. NET_ADMIN alone sufficed for successful
  protocol connect, a real `vpnrouter-ci` TUN and disconnect removing that TUN.
- Package reinstall restored cap_net_admin,cap_net_raw=ep. The smoke test uses
  the distro runtime (no private DOTNET_ROOT), a pam_systemd-style 0700 user
  runtime directory and an isolated container network namespace.
- Downloaded artifact SHA256 and manifest source_commit verified locally:
  package SHA256 `91e95f28675b8ae13a3f5a444888f5984a5e70424d23dacc6e56e6d685a1120b`.
  Manifest SDK is 10.0.303, allowed by global.json latestPatch.
- Python packaging: 124 passed locally and in CI; headless runner: 37 passed.
  Linux test job succeeded; Windows: 3379 passed, 19 skipped, 0 failed.
  The dotnet workflow reports success; at receipt time its completed Windows
  steps were still propagating to the PR check-run status.
- The final documentation checkpoint is rebuilt and watched again. The external
  r2-backend.md report records that final head/run/artifact identity.

Direct source/diff review and git diff --check passed. Independent Gemini review
was attempted with the fixed opencode-gemini helper, but it exited 1 before a
session was created (OpenCode emitted stderr). No independent acceptance is claimed.
phase-task-launcher and bug-hunt are absent from the available skill catalog;
this record follows sdd and direct evidence-based review.

The package was installed only inside CI. No owner-host installation, Quattro
load or selected-app proxy egress was verified. Native notice/source coverage
remains partial in the manifest. Linux kill switch and DNS lockdown remain false.

Documentation delivery gate: PASS for grounded claims, direct prose and flow;
commands and paths were checked against source and the actual CI artifact layout.
