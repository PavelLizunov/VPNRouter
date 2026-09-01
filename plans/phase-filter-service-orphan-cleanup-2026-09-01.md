# Phase: Filter Windows service orphan cleanup

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/filter-service-orphan-cleanup`
Audit ID: `SU-3-2`

## 1. Intent & Invariants

- **What:** stop the LocalSystem service startup sweep from killing every process named `sing-box` when the TUN lock is free. Only a process proven VPNRouter-owned may be terminated.
- **Invariants:** retain the existing TUN-owner skip; pin one valid Windows process handle before ownership inspection through termination; unknown, foreign, exited, invalid-handle, or unreadable candidates are preserved; every enumerated `Process` is disposed; sleep only after an owned process was actually terminated; no owner-record/state schema change; no merge/release/tag/deploy/install.
- **Scope:** `VPNRouter.Service/Program.cs`, deterministic source/ownership contracts, ledger and this brief. CLI run generation and Unix signaling remain separate tasks.

## 2. Interface / Data Contract

```text
GetProcessesByName("sing-box")
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

- [ ] TUN held by any owner still skips enumeration and termination.
- [ ] Foreign/unreadable `sing-box` candidates never reach `Kill`.
- [ ] A valid native handle is pinned before ownership read and remains alive through `Kill`/wait.
- [ ] The only service-startup `Kill` occurs after `IsOwnedSingBox` succeeds.
- [ ] Every enumerated process is disposed on all branches.
- [ ] Release delay occurs only after confirmed owned termination.
- [ ] Source contract cannot silently pass when service source is missing and pins exact ordering.
- [ ] Focused/full exact-head CI and three independent reviews pass.
- [ ] Outcome records delta, test counts, triage, follow-ups, QA and rollback.
- [ ] Owner alone decides merge/release.

Six gates: scope; ownership; handle lifetime/TOCTOU; compatibility/disposal; independent review; exact-head handoff.

## Outcome

Pending implementation and verification.
