# Spec: Exact Unix sing-box stop (SU-3-3)

## 1. Intent & Invariants
- **What:** Replace pattern-based Unix `pkill/pgrep -f sing-box` termination with one exact VPNRouter-owned process identity for interactive Stop and Linux update paths.
- **Why:** Current user, `pkexec`, `sudo`, and update-helper commands can signal unrelated developer or third-party sing-box processes and can misread their presence as VPNRouter liveness.
- **Invariants:** capability-mode Stop keeps its owned `IProcessHandle`; elevated paths require PID + start ticks + executable path; Linux opens a pidfd, re-reads identity after open, then signals through that pidfd; macOS revalidates identity immediately before exact-PID `/bin/kill`; absent/unreadable/mismatched identity fails closed; no pattern fallback; no public API, dependency, persisted-schema, merge, release, tag, deploy, or install.
- **Risk:** an unavailable privileged helper can leave the owned tunnel alive; that explicit failed stop is safer than collateral termination. macOS has no process descriptor, so the documented managed identity-check-to-exact-PID signal window remains platform-limited.
- **Rollback:** revert this PR; no migration is required, but the unsafe broad Unix sweep returns.

## 2. Interface / Data Contract
```text
OwnedProcessIdentity = { PID, StartedAtUtcTicks, ExecutablePath }

interactive Stop:
  capability Linux -> existing IProcessHandle Kill/wait (never pkill)
  elevated Linux   -> FindOwnedSingBox -> pkexec/current-host internal helper
                       helper: pidfd_open -> fresh exact identity compare
                               -> pidfd_send_signal -> bounded exact recheck
  elevated macOS   -> FindOwnedSingBox -> fresh exact identity compare
                       -> sudo -n /bin/kill -- PID -> bounded exact recheck
  unknown/mismatch -> preserve every process; State=Failed when target remains

Linux update:
  resolve exact owned identity once
  installed helper or legacy path receives only that optional identity
  privileged helper invokes current VPNRouter host internal pidfd mode before copy
  no target => no process signal; helper never performs name/pattern discovery
```

## 3. Verification Checklist (Definition of Done)
- [ ] Happy path: controlled Linux child is signaled only through a pidfd after exact identity comparison.
- [ ] Failure: wrong start ticks/path, missing target, unsupported syscall, malformed helper args, or unreadable identity never signals a process.
- [ ] Interactive Linux/macOS command construction contains one exact PID and no `pkill`, `pgrep`, `-f`, shell interpolation, or broad manual recommendation.
- [ ] Capability-mode exception path never falls back to name-based termination.
- [ ] Linux update code and packaged helper carry optional exact identity and contain no broad sing-box kill.
- [ ] Stop reports failure rather than “Stopped” when its exact target remains alive.
- [ ] Focused tests, full suite, release-solution/service/package gates, exact-head CI, and three independent reviews pass.
- [ ] Outcome records delta, counts, primary-contract limitations, QA, follow-ups, and rollback; owner alone decides merge/release.

Six gates: scope; exact identity; stable Linux signal; fail-closed privilege/compatibility; independent review; immutable-head handoff.

## Outcome

Pending implementation and verification.
