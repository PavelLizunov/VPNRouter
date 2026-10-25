# D1: Linux backend defects and contradictions

## Intent and scope

Review Linux additions against origin/main c3ebc46e at initial head ddf97f80:
Headless lifecycle/protocol/storage, Core runtime and ownership seams, Arch
packaging/CI, and the read-only Service.qml consumer contract. The accepted
base origin/dsh/omarchy-mvp-2026-10-22 (75ac570c) was already an ancestor.
Keep PR 622 stacked. No VPN operation, installation on the workstation, shell
change, release, tag, merge, or local SDK build/full test run.

## Corrections and invariants

- 98532b24: server handler disposal allowed only 3 s while session teardown
  allows a 3 s gate wait and 15 s stop. Allow 20 s; retain a short injectable
  hang deadline and add an EOF regression beyond the old deadline. The owning
  Headless README now describes bounded shutdown rather than guaranteeing
  cleanup of an unresponsive engine. Risk: longer shutdown for a hung handler.
- 408a51b7: lowercase base64 keys with short slash-separated runs bypassed
  CrashReporter redaction as apparent paths. Preserve only the fixed public
  package path; test a canonical synthetic 32-byte key and retain the existing
  public-path negative control. Risk: other long paths can be redacted.
- 74113e74: both deep verifiers wrote credentials to temp JSON with permissions
  inherited from umask. Reuse AppPaths.CreatePrivateFile(CreateNew), preserving
  asynchronous writes, cancellation and final cleanup. No new dependency or
  public protocol/command change. This shared defect also existed on main.
- 1c2e8e57: put the new permission/cleanup regression in
  DeepVerifierConfigPermissionsTests. The first attempt was in a Headless-named
  class excluded by the Linux CI filter; that existing test was restored.
  The dedicated test inspects mode 0600 before fake spawn and deletion after
  spawn failure. It starts no real process or network probe.
- Final test cleanup adds the platform guard inside the callback and passes
  TestContext cancellation, addressing its CA1416 and xUnit1051 warnings.

All confirmed findings were recorded in OPEN-DEFECTS before implementation.
Rollback is a revert of the scoped correction commits. Runtime trust policy,
package capabilities, protocol schema and other platform entry points are intact.

## Observed gates at source/test head 1c2e8e57

1. PASS (scoped builds): test.yml builds/publishes Core, tests, CLI and Headless;
   Arch builds/installs/upgrades and checks the normal-user offline consumer.
   No full VPNRouter.sln or release/all-platform build was run.
2. PASS (CI scopes): Linux xUnit 3294 tests; Windows 3385 passed / 20 skipped;
   Headless console suite 40 passed / 0 failed. Both new Core regression names
   appear as Passed in the Linux log. GUI/pixel suites remain excluded by CI.
3. PASS: scoped diff and whitespace checks; README matches the 20 s handler
   deadline. Documentation claims name observed evidence and its limits.
4. BLOCKED for independent acceptance: direct source/diff review completed in
   this worker round; Claude lead review follows handoff. No independent
   acceptance claim or recursive worker dispatch.
5. N/A: no UI change or live VPN acceptance in D1. Active-core SIGTERM/EOF,
   crash/SIGKILL orphan and route cleanup, egress/leaks and shell loading were
   not exercised. The Free writer uses the same private factory, but its
   process integration was inspected only in source.
6. PASS (bounded consumer checks): actual ProtocolServer teardown boundary and
   VLESS temp file at fake spawn; Arch offline install/upgrade. These checks do
   not establish live dataplane behavior or full Linux feature parity.

## CI and handoff

| SHA | test.yml | Arch headless package |
|---|---|---|
| 98532b24 | 40171223816 success | 40171225782 success |
| 408a51b7 | 40176568895 success | 40176570811 success |
| 74113e74 | 40177179288 success | 40177181002 success |
| 1c2e8e57 | 40177760288 success | 40177761981 success |

The repository CI verifier returned exit 0 at each completed correction head;
1c2e8e57 has eight green checks and no tolerated failures. PowerShell 7.6.7 was
already installed and invoked directly because the mise shim had no selection;
no tool installation or configuration change was made. Final exact-head CI and
remote SHA are recorded in the owner-requested external D1 handoff report.

Read-only triage of PRs 617-620, 624 and 625 confirms the existing main
LOG-REDACT-QUOTED-SPACE defect. Prefer #617 for that narrow scope; #620 duplicates
it with unrelated changes. The other variants have missing truncation/quote
cases, and none fully handles escaped delimiters. Native .NET Regex checks used
synthetic inputs and translated match evaluators, not full PR test suites.
No PR comment, closure or merge. No files were deleted; no package was deployed.
