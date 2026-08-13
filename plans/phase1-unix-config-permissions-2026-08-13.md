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
- Make secret-bearing configuration files owner read/write (`0600`) after
  atomic creation/replacement.
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
3. Apply directory modes after creation and file modes after atomic rename.
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

- [ ] **Gate 1 - Build clean**: Release solution build, 0 errors.
- [ ] **Gate 2 - Tests green**: focused permission tests and full suite.
- [ ] **Gate 3 - Docs**: security/privacy documentation and Outcome updated.
- [ ] **Gate 4 - Self-review**: security-focused bug-hunt; no custom crypto or dependency added.
- [ ] **Gate 5 - Remote brat UI verify**: N/A - Unix Core-only change.
- [ ] **Gate 6 - Characterization diff**: N/A - not a god-file split.

## Outcome

Pending implementation and verification.

