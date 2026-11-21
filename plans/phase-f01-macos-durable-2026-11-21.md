# F-01 macOS durable privileged helper adaptation (2026-11-21)

- Owner: implementation sub-agent for the Standard 2.51.0-r1 F-01 task
- Base: `d8675207093b748cb015b919787bcad426637482` (PR #713 head)
- Imported prototype: `/home/slovn/Work/VPNRouter-f01-defensive-20261120`,
  base `6066dd593f47f82a45eae4d55be0d5e4ca429a28`, working diff
  `git diff --binary` SHA-256
  `c8ffa9656223f9469947d8e056d6b110306a2884ea304093a7a9238b1fe799d3`.
- Worktree: `/home/slovn/Work/_worktrees/VPNRouter/f01-durable-20261121`
- Risk: HIGH. This owns the macOS root boundary, first-install migration and
  the privileged runtime contract.
- Rollback: revert the task branch. No tag, release or public artifact is
  created by this change.

## Why

The prior askpass patch was rejected as closure: it left the invoking user
handling the password, blocked standard accounts without a sudoers grant, and
still executed user-writable code/config as root. The prototype already has the
right shape: one root-owned helper, one exact sudoers rule, root-owned runtime
and staged config. This task imports that shape on the current #713 tree and
closes the decisive authority gaps found in review.

## What

Imported from the prototype (strict JSON parser included):

- `packaging/macos/vpnrouter-privileged.c`, `jsmn.h`, installer and template.
- `MacPrivilegePaths`, `MacPrivilegePolicy`, `MacPrivilegedClient`,
  `MacPrivilegedConfigContract`, `MacPrivilegedSetup`.
- macOS DNS, pf, DNS flush, sing-box launch/stop integration.
- Root-owned config staging and exact-PID stop request.

Adaptations in this task:

1. Protected directory chain: the installer and helper verify root ownership,
   non-symlink status and exact modes for `/Library`,
   `/Library/Application Support`, `/Library/PrivilegedHelperTools`, `/etc` and
   `/etc/sudoers.d`; the installer also rejects linked/group-writable source
   files and source directory chains.
2. Exact session ownership: `start` writes a root-owned per-uid session record
   (`pid` + Unix start seconds) after validating the config and before
   `execve`; `stop` requires that record, the invoking `SUDO_UID`, the root
   runtime path, root ownership and the exact start time. A live session cannot
   be replaced by a concurrent start.
3. Legacy migration is tri-state: `probe` reports `legacy_state` as `absent`,
   `present` or `unknown`; `unknown` and `present` are not ready. The installer
   removes the legacy file and verifies its absence before publishing the new
   policy.
4. Policy publication is exact: the installer accepts only one non-comment line
   equal to `<user> ALL=(root) NOPASSWD: /Library/PrivilegedHelperTools/com.vpnrouter.privileged`;
   the app renders and validates the same template before elevation.
5. Root artifact provenance: the helper verifies the root-owned runtime against
   the root-owned manifest before `start`; `probe` already verifies helper,
   runtime, manifest and policy.
6. Controller narrowing: the config contract adds a validated per-config Clash
   secret before the app reads the endpoint; the helper requires it. Hot reload
   remains disabled on privileged macOS. `v2ray_api`, external-UI download keys
   and Clash cache mode keys are forbidden.
7. Native PR CI: `.github/workflows/macos-privileged.yml` compiles the helper and
   runs the typed C contract tests on `macos-latest` without tags or artifacts.

Preserved from #713: Android private readiness, desktop Simple/Stop status and
all existing platform behavior outside the macOS privilege boundary.

## Invariants

1. The only sudoers command is the root-owned helper path with no arguments.
2. Root executes only a root-owned runtime and a root-owned staged config that
   the helper validated from a private user-owned file.
3. Stop requires the per-uid session record, not only a PID/start-time guess.
4. An unknown legacy path blocks readiness; absence and presence are distinct.
5. Every controller key is loopback-only and secret-protected.
6. A failed install, migration, validation or authorization blocks Connect.
7. The app bundle is unsigned; the one-time administrator prompt remains the
   installer trust anchor.

## Verification

Record PASS, FAIL, BLOCKED or N/A with evidence. Do not run builds, privileged
commands or live-host tests on the development workstation.

1. Build: macOS hosted workflow compiles the helper and the app; full solution
   build on an authorized worker.
2. Tests: `packaging/macos/tests/run-tests.sh`; `MacPrivilege*`,
   `MacPrivileged*`, `MacDnsHardeningTests`, `MacFirewallManagerTests`,
   `SingBoxManagerProcessRunnerTests`, `UnixOwnedProcessSignalTests`; full
   applicable suite.
3. Docs: `packaging/macos/README.md`, `packaging/AGENTS.md`, platform map and the
   user guide describe the helper, migration and secret.
4. Review: independent review of this exact diff; F-01 stays open until then.
5. Live: disposable macOS 12+ standard-user Connect/Stop, legacy fixture,
   cancellation, DNS/pf restore. Not run here.
6. Characterization: no public ViewModel surface split; #713 desktop/Android
   tests remain the baseline.

## Evidence and limits

- Source import and static checks were performed in the isolated worktree.
- The pinned core release asset
  `sing-box-1.14.4-vpnctl.2-darwin-universal.zip` has GitHub digest
  `c3f7efa244d376017fb346c0bb8ffcfc77be36cdba170efc42dc9cd84d57a6f0`,
  matching `build-mac.sh`.
- Tag source `experimental/clashapi/configs.go` was read: `PUT /configs` is a
  no-op; `PATCH /configs` only changes mode. The other registered handlers do
  not accept a config path or reload a caller file.
- No local C/.NET build, no root install, no live macOS test, no tag, no push
  and no public artifact. These remain evidence limits.

## Outcome

The review of the original staged tree found a blocking gap in validation
of privileged runtime capabilities. The pinned core can register outbound
types outside the managed client profile. The helper must allow only reviewed
outbound, endpoint and DNS types, reject caller-directed execution and file
options, and reject noncanonical spellings of the structural keys. Match the
managed validator to the native boundary and use inert synthetic tests.

The same review identified outstanding session and installation work. The
installer now verifies copied artifacts before replacement and binds the
approved script and policy to the prompt. Publishing a managed grant still
needs an independent installed-state check and failure handling. The
installation authorization does not establish publisher identity for an
unsigned bundle. Concurrent start/stop, legacy process readiness, and
session ownership of host-global DNS/pf require additional changes before
live acceptance.

- Status: PARTIAL. Source adaptation complete; build, tests, independent
  review and live macOS acceptance are pending.
- Integration preflight observed `/etc` as a symlink to `private/etc` and
  `/Library/Application Support` as root:admin, mode 0755. The installer and
  helper now validate the real sudoers path and accept that protected
  Application Support directory. Installer cleanup is registered only after
  lock acquisition, and copied binaries are checked before publication.
- The app embeds the installer and sends its bytes in the authorization
  request. The elevated shell starts with an empty environment.
- On `mac-worker`, the native helper compiled under production warning flags
  and the unsanitized native contract tests passed for `ac9bb52f`. The
  sanitizer run was terminated after it stalled initializing shadow memory
  before test entry. Hosted sanitizer results and managed .NET tests remain
  pending. The worker only has .NET 8.
- Hosted native tests with sanitizers passed at `0de7b32` in run
  44858184386. The separately dispatched .NET run 44858134050 found one
  failure on both Linux and Windows. The managed controller check
  rejected the bracketed IPv6 loopback value `[::1]:9090`. The fix
  normalizes the parsed host using the same approach as `ClashSingBoxApi`.
- Exact-head .NET tests passed on Linux and Windows at `94754f0` in
  run 44859003172. IPv6 loopback passed on both platforms. The native and
  Android checks also passed.
- Follow-up review confirmed the canonical macOS paths, installer script
  snapshot, staging hashes, and lock ownership. It found that the policy
  template could still be reopened after approval and that a process killed
  during installation could leave a stale lock directory. The app now sends
  policy bytes in the authorization request. The installer accepts exactly
  the managed comments and rule and locks a file descriptor. A non-root
  macOS fixture confirmed that a concurrent process is refused and that
  another process can acquire the lock after the holder is killed.
- Read-only worker check on macOS 26.5.2 reported `root:wheel 0755` for
  `/Library`, `/private`, `/private/etc`, and `/private/etc/sudoers.d`.
  `/Library/Application Support` was `root:admin 0755`. `ls -lde` reported
  the sticky bit on `/Library/PrivilegedHelperTools` and no extended ACLs
  on the inspected directories. Native tests pin the uid, group, mode and
  sticky-bit acceptance logic. The checks assume no write-granting ACL
  on these system directories. Live installer and Connect/Stop tests are
  still pending.
- The C helper and managed contract now allow only the generated root sections
  plus `$schema`, with explicit outbound, endpoint and DNS server types.
  Canonical key checks guard the new selectors and reject privileged file
  or process options. The macOS import path refuses unsupported sections
  and types. Added inert fixtures for both validators, including the
  generated DNSTT and WireGuard components.
- On the macOS worker, BSD stat confirmed `%Lp` returns `755` for the
  sticky helper directory, while `%Op` returns `41755`. The installer now
  checks the full mode with the same mask as the helper. Native fixtures
  cover acceptance of sticky and rejection of setuid and setgid bits.
- Native worker preflight for the component validation delta: macOS 26.5.2,
  10 logical CPUs, load 1.80, 16 GiB RAM (14,964 free and 286,836 inactive
  16 KiB pages), 1,174 MiB free swap, 86 GiB available on the internal disk,
  and no compiler jobs. The Command Line Tools clang is available.
- At `dcd39bd2`, the native helper compiled and its contract fixtures
  passed on the worker and under sanitizers in run 44861491261. Android
  compile passed in run 44861491242. All six managed CI jobs passed in
  run 44861518880.
- The native session code now takes a per-user transition lock and a
  runtime lifetime lease before changing assets, staging config or
  publishing the session record. Stop uses the same transition lock and
  confirms process identity before signaling. The runtime starts with a
  private root-owned working directory and temporary directory.
- The root helper checks the legacy grant and passively looks for a root
  process at the invoking user's old runtime path before start. Probe
  reports this process separately. The app surfaces the dependency on
  stopping the old process when an installed helper detects it. Installer
  policy publication and host-wide DNS and pf ownership remain separate
  gates. The new fixtures cover the identity parser, exact legacy path,
  and kernel lock behavior on temporary files.
- Native session test preflight: macOS 26.5.2, 10 logical CPUs, load
  1.76, 16 GiB RAM (43,815 free and 285,954 inactive 16 KiB pages),
  1,174 MiB free swap, 86 GiB free disk, no active compiler jobs,
  and the Command Line Tools clang available.
- Native production compilation with warnings as errors, installer shell
  syntax, and all contract fixtures passed on the macOS worker for
  `packaging/macos` from tree `2bdfa17e724d6bedc8c08ed84d10c539d421ee59`.
- Installer transaction follow-up: check for a running legacy instance
  before removing its grant, verify the installed bytes and permissions
  against the authorization before publishing the policy, and remove the
  new grant on a failed readiness probe. Verify this on inert fixtures
  and keep the live macOS migration gate open.
- Installer verification preflight: macOS 26.5.2, 10 logical CPUs, load
  2.26, 16 GiB RAM (40,721 free and 285,958 inactive 16 KiB pages), 1,174
  MiB free swap, 86 GiB free disk, no compiler jobs, and Command Line
  Tools clang available. Checks and managed CI passed on `dca7329c` before
  this change, including run 44862528652.
- Worker native compile and contract tests passed with `packaging/macos`
  from tree `26449dd6bd578f1ea0d6e6ff6ddd501c4b242674`. An unprivileged
  shell fixture exercised those transaction functions with temporary
  files, real modes and link counts, simulated installed ownership and
  a fake helper probe. A failed probe and a simulated interruption after
  rename removed the new rule. Artifact hash, mode and hard-link failures
  prevented publication. A failed rename preserved the existing rule.
  No system paths or installed sudoers state were changed.
- Remaining defects: same-user access to the secret-protected Clash controller;
  app-bundle authenticity remains unsigned; the durable read-only typed status
  path is not implemented; CLI/Headless entry points do not run the one-time
  installer prompt and fail closed until the desktop app installs the helper;
  live migration and standard-user acceptance are unproven.
