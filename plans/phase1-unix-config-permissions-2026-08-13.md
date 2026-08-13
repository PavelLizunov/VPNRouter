# Phase 1 - Unix configuration permissions

**Owner**: Codex session 2026-08-13
**Branch**: `codex/quality-trust-hardening-249`
**Roadmap ref**: security hardening follow-up to P09 SEC-2
**Effort**: 2-3 hours
**Risk**: MEDIUM
**Blast radius**: `AppPaths`, settings persistence and focused tests on Linux/macOS; no schema or network behavior change
**Rollback**: revert the implementation commit

## Why

VPN credentials must be materialized for sing-box, so adding an encryption
framework would not remove the runtime plaintext boundary. The smaller real
gap is permissions: Windows already restricts ProgramData ACLs, while Unix
creation currently relies only on the process umask. On a multi-user Linux
host that can leave `config.yaml` and generated `current.json` readable by
other local users.

## What

- Reuse `File.SetUnixFileMode` and `Directory.SetUnixFileMode`; add no package or
  secret-store abstraction.
- Make the VPNRouter data/config directories owner-only (`0700`) on Unix.
- Create secret-bearing configuration files owner read/write (`0600`) before
  content is exposed, including atomic temporary files.
- Cover existing files as well as new saves; permission failure is logged or
  handled consistently without corrupting the configuration.
- Do not change Windows ACL behavior, YAML schema or serialized values.

```diff
- permissions inherited solely from the caller's umask
+ explicit owner-only modes on secret-bearing Unix paths
```

## How

1. Trace every production writer of `config.yaml` and `current.json`.
2. Add the smallest shared Unix-mode helpers at the existing path/persistence
   boundaries.
3. Create directories/files with private modes and verify the mode again on the
   opened file handle before writing.
4. Add platform-aware tests using a temporary overridden data directory.

### Tests written

- `AppPathsUnixPermissionsTests` - data/config directories resolve to owner-only modes.
- `SettingsLoaderUnixPermissionsTests` - a save leaves `config.yaml` at `0600`.
- A generated-current-config test, if its writer is a separate boundary.

### Verification approach

Focused permission tests on a Unix CI runner plus full solution tests. Static
Windows guard tests ensure no Unix API is invoked on Windows. This changes no
visible UI, so WINBRAT UI verification is not applicable.

## Verification gate

- [x] **Gate 1 - Build clean**: Release solution build, 0 errors.
- [x] **Gate 2 - Tests green**: 30 focused tests and full 2,772-test suite.
- [x] **Gate 3 - Docs**: security/privacy documentation and Outcome updated.
- [x] **Gate 4 - Self-review**: security-focused bug-hunt; no custom crypto or dependency added.
- [x] **Gate 5 - Remote brat UI verify**: N/A - Unix Core-only change.
- [x] **Gate 6 - Characterization diff**: N/A - not a god-file split.

## Outcome

**Status**: Complete locally; Linux CI is the final platform proof.

Implemented one shared, dependency-free boundary in `AppPaths`: Unix data
directories are created and verified as `0700`, while `config.yaml`, its
atomic temp file and generated `current.json` are created and handle-verified
as `0600`. Direct and dangling symlink paths fail closed. Windows behavior,
config contents and schemas are unchanged.

The adversarial review found and fixed an initial create-then-chmod exposure
window. The corrected implementation creates private paths immediately and
checks the actual open handle before writing. Two independent re-reviews found
no P0/P1 survivor.

Verification on Windows: solution build 0 errors; 30 focused tests passed;
full suite passed 2,768, skipped 4, failed 0. The six new platform-aware tests
will execute their Unix assertions in Linux CI (they intentionally no-op on
Windows/macOS-inapplicable hosts).
