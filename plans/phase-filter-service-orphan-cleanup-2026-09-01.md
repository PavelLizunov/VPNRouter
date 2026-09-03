# Phase: Filter Windows service orphan cleanup

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/filter-service-orphan-cleanup`
Audit ID: `SU-3-2`

## 1. Intent & Invariants

- **What:** stop the LocalSystem service startup sweep from killing every process named `sing-box` when the TUN lock is free. Only a process proven VPNRouter-owned may be terminated.
- **Invariants:** atomically reserve the TUN ownership semaphore for the complete sweep and skip when it is held or unavailable; pin one valid Windows process handle before ownership inspection through termination; unknown, foreign, exited, invalid-handle, or unreadable candidates are preserved; every enumerated `Process` is disposed; sleep only after an owned process was actually terminated; no owner-record/state schema change; no merge/release/tag/deploy/install.
- **Scope:** `VPNRouter.Service/Program.cs`, the minimal Core friend-assembly/lock-state seam, deterministic source/ownership contracts, ledger and this brief. CLI run generation and Unix signaling remain separate tasks.

## 2. Interface / Data Contract

```text
TunOwnershipLock.TryAcquire + HasOwnership
  false => skip complete sweep (held or unavailable)
  true  => hold reservation through complete sweep
     -> GetProcessesByName("sing-box")
     -> for each candidate in try/finally Dispose
     -> pin candidate.SafeHandle; reject invalid/closed
     -> ProcessOwnership.IsOwnedSingBox(candidate)
        false => log preserved; never Kill
        true  => Kill(entireProcessTree: true)
                 -> bounded WaitForExit
                 -> keep pinned handle alive
  -> release-delay only when at least one owned process exited
```

Rollback: revert this PR; startup returns to the prior broad name-based sweep. Risk is leaving a true orphan alive when ownership evidence is unavailable; that fail-closed outcome is safer than collateral LocalSystem termination and is event-logged.

## 3. Verification Checklist (Definition of Done)

- [x] The service holds an atomic TUN reservation through the sweep; held or unavailable reservation skips enumeration and termination.
- [x] Foreign/unreadable `sing-box` candidates never reach `Kill`.
- [x] A valid native handle is pinned before ownership read and remains alive through `Kill`/wait.
- [x] The only service-startup `Kill` occurs after `IsOwnedSingBox` succeeds.
- [x] Every enumerated process is disposed on all branches.
- [x] Release delay occurs only after confirmed owned termination.
- [x] Source contract cannot silently pass when service source is missing and pins exact ordering.
- [x] Focused/full exact-head CI and three independent reviews pass.
- [x] Outcome records delta, test counts, triage, follow-ups, QA and rollback.
- [x] Owner alone decides merge/release.

Six gates: scope; ownership; handle lifetime/TOCTOU; compatibility/disposal; independent review; exact-head handoff.

## Outcome

**PASS / review-ready in PR #212.** Product head `6966937438a19d1881c4308b64f38d0caa566d7e` closes matrix finding `SU-3-2` without installing or launching the service.

- **Behavior:** startup now holds the global TUN semaphore for the complete sweep and skips when reservation is held or unavailable. Every candidate is pinned by native handle, filtered through the existing VPNRouter ownership predicate, and preserved unless ownership is proven. The sole kill has a bounded wait; handles always dispose; the release delay is enabled only after confirmed exit.
- **Core seam:** `TunOwnershipLock.HasOwnership` exposes actual internal lock state so Service can fail closed despite the legacy `TryAcquire` fail-open return. `VPNRouter.Service` is a friend assembly; no public API or persisted shape changed.
- **Tests:** the coexistence source contract fails loudly when source is missing and pins reservation, handle, ownership rejection, sole Kill, wait, keepalive, timeout rejection, confirmed-exit delay, and disposal ordering. Existing ownership tests cover own-bin, external, sibling-prefix, and custom-path boundaries.
- **Exact-head evidence:** workflow `33574999425` checked out product SHA exactly and passed 2,830 total / 2,773 passed / 57 skipped; Windows characterization 19/19 and Windows Go passed. PR merge-ref run `33574741964` and grep also passed. Exact-head Windows workflow `33575363880` published `VPNRouter.Service.exe`/DLL/config successfully, completed its bounded integration, and cleaned its ephemeral runner paths.
- **Review:** independent correctness, LocalSystem security/TOCTOU, compile/false-pass, atomic-reservation, and mutation lenses are CLEAN. The lead repaired the original probe-to-Kill owner race by holding the semaphore and added the missing timeout rejection pin.
- **QA:** Ouroboros `qa-92ef9170` iteration 1 PASS at `0.92`.
- **Safety/limitations:** local .NET is unavailable; GitHub Actions is authoritative. No service install/start, live VPN process termination, merge, release, tag, deployment, or workstation mutation occurred.
- **Rollback:** revert PR #212; startup returns to the prior broad sweep, with no migration required.
