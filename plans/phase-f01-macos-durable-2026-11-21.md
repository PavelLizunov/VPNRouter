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

- Status: PARTIAL. Source adaptation complete; build, tests, independent
  review and live macOS acceptance are pending.
- Integration preflight observed `/etc` as a symlink to `private/etc` and
  `/Library/Application Support` as root:admin, mode 0755. The installer and
  helper now validate the real sudoers path and accept that protected
  Application Support directory. Installer cleanup is registered only after
  lock acquisition, and copied binaries are checked before publication.
- Remaining defects: same-user access to the secret-protected Clash controller;
  app-bundle authenticity remains unsigned; the durable read-only typed status
  path is not implemented; CLI/Headless entry points do not run the one-time
  installer prompt and fail closed until the desktop app installs the helper;
  live migration and standard-user acceptance are unproven.
