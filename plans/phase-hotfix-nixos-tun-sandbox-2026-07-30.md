# Hotfix - NixOS sandboxed AppImage TUN permissions

**Owner**: Codex session 2026-07-30 with Qwen 3.8 read-only review
**Branch**: `codex/nixos-capability-sandbox-2026-07-30`
**Roadmap ref**: out-of-roadmap user hotfix; `plans/v3.0-execution-methodology.md` section 9
**Effort**: 1 day
**Risk**: MEDIUM
**Blast radius**: Linux sing-box launch/crash recovery, Linux helper lookup, two desktop-open actions, tests and Linux documentation
**Rollback**: revert the implementation commit or close the task branch

## Why

On NixOS 26.05 an official VPNRouter 2.47.0 AppImage wrapped by
`appimageTools.wrapType2` reports `CAP_NET_ADMIN` on the deployed sing-box
file, launches it without elevation, and then fails `TUNSETIFF` with
`operation not permitted`. The current check proves only that an xattr is
visible through `getcap`; it does not prove that `execve` may grant that
capability inside a user namespace or while `NoNewPrivs` is active. The same
failure is then treated as a Windows orphan-adapter crash and can enter the
normal restart/failover cycle even though changing servers cannot repair a
local privilege failure.

## What

- Add one internal Linux runtime helper that:
  - detects `NoNewPrivs` from `/proc/self/status`;
  - detects a non-initial user namespace from `/proc/self/uid_map`;
  - resolves `pkexec` only from trusted fixed locations:
    `/usr/bin/pkexec` and `/run/wrappers/bin/pkexec`.
- Reject a sandboxed Linux TUN launch synchronously before `getcap` or
  `pkexec`, allowing the existing App start error surface to show an
  actionable localized message.
- Resolve `getcap` and non-privileged desktop openers through `PATH`.
- Classify the exact Linux `TUNSETIFF: operation not permitted` crash as a
  permanent local permission failure, not a Windows TUN-orphan failure.
- Disarm HealthMonitor restart and failover recovery for that permanent
  failure until a manual Connect starts a new lifecycle.
- Use the shared trusted `pkexec` resolver in Linux start, stop, health-check
  and updater paths.
- Update English and Russian Linux documentation.
- Keep an official native Nix derivation/flake as a separate packaging task;
  this hotfix must not pretend an unprivileged bubblewrap AppImage can safely
  configure the host TUN.

## How

1. Parse the two `/proc/self` files with small pure methods. Positive evidence
   of `NoNewPrivs` or a remapped user namespace blocks TUN startup. Unreadable
   or malformed `/proc` data remains "unknown" to avoid breaking existing
   native Linux installations; the runtime EPERM classifier is the backstop.
2. Put the preflight at the single Linux process-launch chokepoint before the
   capability and elevation branches.
3. Scan the already-bounded stderr snapshot for both `TUNSETIFF` and
   `operation not permitted`, case-insensitively, and set the permanent flag
   before firing `Crashed`.
4. Gate both immediate and periodic HealthMonitor recovery on that flag. Set
   `_shouldBeRunning` false for the permanent failure so failover cannot burn
   through unrelated servers; `Start()` re-arms it after the user fixes the
   environment.
5. Add focused pure and fake-process regression tests. Do not add a general
   process-helper abstraction or change Windows/macOS launch behavior.

### Tests written

- `LinuxRuntimeEnvironmentTests`:
  - parses `NoNewPrivs` enabled, disabled and malformed status;
  - distinguishes the initial uid map from remapped, multi-range and malformed
    maps;
  - resolves the standard pkexec path first, then the NixOS wrapper, then none.
- `SingBoxManagerLinuxTunPermissionTests`:
  - matches the exact Linux EPERM signature case-insensitively;
  - rejects incomplete and unrelated TUN messages;
  - preserves Windows orphan classification behavior.
- `HealthMonitorLinuxTunPermissionTests`:
  - permanent TUN permission failure is non-retryable;
  - normal crashes still follow `RestartOnFailure`.

### Verification approach

The owner explicitly prohibited local builds, tests, application launches,
services, installers, VM runs and live VPN validation. Therefore Gate 1 and
Gate 2 are executed only by GitHub Actions after each pushed implementation
commit. Qwen 3.8 performs read-only design and final diff review with zero tool
calls. Codex validates every Qwen claim against the repository before editing.

## Verification gate

- [ ] **Gate 1 - Remote build clean**: GitHub `test` workflow build step exits
  with zero errors. No local build is run.
- [ ] **Gate 2 - Remote tests green**: GitHub `test` and Windows Go test jobs
  pass with the new regression tests. No local tests are run.
- [ ] **Gate 3 - Docs**: Outcome filled; README EN/RU and test inventory
  updated.
- [ ] **Gate 4 - Self-review**: Qwen 3.8 read-only security/simplification
  review plus Codex diff review. The repository has no callable
  `security-review` skill, so the Qwen security prompt is the recorded
  equivalent for this task.
- [ ] **Gate 5 - MCP verify**: N/A - no UI layout change and local/VM
  application execution is prohibited.
- [ ] **Gate 6 - Characterization diff**: N/A - not a god-file split.

## Outcome

Pending implementation and remote CI.
