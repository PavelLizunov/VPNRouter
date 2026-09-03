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
                               -> pidfd_send_signal; caller performs bounded exact recheck
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
- [x] Happy path: controlled Linux child is signaled only through a pidfd after exact identity comparison.
- [x] Failure: wrong start ticks/path, missing target, unsupported syscall, malformed helper args, or unreadable identity never signals a process.
- [x] Interactive Linux/macOS command construction contains one exact PID and no `pkill`, `pgrep`, `-f`, shell interpolation, or broad manual recommendation.
- [x] Capability-mode exception path never falls back to name-based termination.
- [x] Linux update code and packaged helper carry optional exact identity and contain no broad sing-box kill.
- [x] Stop reports failure rather than “Stopped” when its exact target remains alive.
- [x] Focused tests, full suite, release-solution/service/package gates, exact-head CI, and three independent reviews pass.
- [x] Outcome records delta, counts, primary-contract limitations, QA, follow-ups, and rollback; owner alone decides merge/release.

Six gates: scope; exact identity; stable Linux signal; fail-closed privilege/compatibility; independent review; immutable-head handoff.

## Outcome

**Status:** acceptance-ready in draft PR [#213](https://github.com/PavelLizunov/VPNRouter/pull/213); not merged or released. Verified implementation/test head: `faa879b9f75c81fed420ba198a59b7d314f62e6b`.

### Delivered delta
- Added one Linux helper contract whose target is `{ PID, StartedAtUtcTicks, ExecutablePath }`. It opens `pidfd`, freshly re-reads all identity fields, and signals only through that bound descriptor. App and CLI process the hidden helper request before ordinary initialization.
- Replaced interactive Linux/macOS and Linux updater pattern kills with tokenized exact-target paths. The packaged updater aborts before copy when an exact stop is refused; new code detects a missing/legacy installed helper and uses its current-host exact path instead of invoking legacy broad semantics.
- Serialized process lifecycle transitions. An unconfirmed exact stop now stays `Failed`, retains the capability handle and manager-scoped TUN lease, and blocks same-manager Start/Restart, new-manager Start/Restart, and stale-manager Dispose release.
- Narrowed the macOS sudoers rule from `pkill *` to `/bin/kill -KILL -- [0-9]*`; product code emits one positive numeric PID token.
- Added real-kernel pidfd success/mismatch/missing-target checks, malformed/token construction checks, lifecycle concurrency/failure/cross-manager regressions, and source-order/no-pattern guards.

### Verification
- Exact head [`33622243920`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622243920): **PASS**, 2,850 total / 2,793 passed / 57 skipped / 0 failed; Linux controlled-child pidfd and all failed-stop lifecycle regressions passed.
- PR checks [`33622221869`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622221869), updater [`33622221819`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622221819), and grep [`33622221808`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622221808): **PASS**.
- Exact Windows update [`33622248064`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622248064): **PASS**.
- Exact Linux package [`33622251570`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622251570): **PASS**; AppImage, `.deb`, tarball, checksums, and artifacts built; release upload skipped.
- Exact macOS package [`33622255073`](https://github.com/PavelLizunov/VPNRouter/actions/runs/33622255073): **PASS**; DMG/ZIP and smoke test passed; release upload and Homebrew trigger skipped.
- The real-kernel and lifecycle cases also passed on three preceding immutable heads, providing four green executions rather than one timing-sensitive pass. `git diff --check` and `bash -n packaging/linux/vpnrouter-update-helper` passed. No local `dotnet` exists, so no local compilation is claimed.
- Four independent final Opus review lanes returned `CLEAN` after earlier rounds found and drove repairs for fd-0 ownership, legacy-helper compatibility, false `Stopped`, lock/handle loss, concurrent replacement, stale-manager release, and non-owner Restart.
- Evidence-backed Ouroboros QA session `qa-710cb2c2`: **PASS 0.98**. Its initial 0.52 revision requested inspectable evidence while final workflows were still running; the completed source/evidence submission passed.

### Primary-contract limitations and follow-ups
- macOS has no pidfd-equivalent in this implementation: a small, documented window remains between fresh identity validation and exact-PID `/bin/kill`. Linux kernels without pidfd support fail closed and may leave the owned tunnel alive.
- An already-running old application cannot gain new semantics retroactively during its first update to this version. Once new code is installed, it detects and refuses legacy broad helper behavior.
- The macOS sudoers compatibility glob is retained because the supported macOS 12-era sudo does not provide the desired regex floor; only fixed token construction by VPNRouter is claimed.
- `packaging/linux/postrm` is uninstall-only and outside this approved interactive/update change. Its remaining pattern cleanup is recorded as a separate defect in `plans/OPEN-DEFECTS.md`.
- Rollback is a PR revert; there is no data migration. Reverting also restores the unsafe broad behavior. Merge, release, tag, deployment, installation, and VPN mutation remain owner-only actions.
