# Unix launch provenance — design only

## Owner decision and scope

During repository cleanup, independent review of PR #240 found source-confirmed conditional readiness regression NIGHT-INTEGRATION-UNIX: an exited elevation wrapper with a live child is accepted by existing Unix health/warmup logic but rejected by new typed-readiness guards. No native frequency or incident reproduction is claimed. Owner approved separate corrective investigation, then explicitly selected designing a separate trusted launch contract before implementation.

Current branch dsh/fix-night-audit-gemini-2026-09-04 is in an uncommitted merge of ae352fdb with accepted main673cedac. Characterization and ledger working content reconciled; index remains unresolved until parent staging. No corrected product implementation or current CI acceptance. Preserve all original repairs and unrelated untracked files. Do not publish or merge as accepted while blocker remains.

## Established constraints

Existing Unix path/start-time discovery and owner-record pairs do not authenticate a particular manager launch. API liveness, matching executable path, a PID file or a discoverable nonce are insufficient. OwnedProcessIdentity and exact identity rechecks are useful only after provenance is established.

Design one launch-bound channel or demonstrably stable foreground launch identity, not generic process adoption. Preserve manager/launch generation, engine session/generation, successful warmup and delayed-callback identity checks. Readiness must not imply new termination authority. Handle restart with confirmed old stop and fresh new-launch identity. Missing/unreadable/foreign/stale proof fails closed. Existing NIGHT-FOLLOWUP-01/02 remain separate.

## Design gate

Read existing process runner/handle, redirected IPC, elevation commands, dispatch and sudoers rules. Present exact helper executable, arguments, channel authentication, config binding, timeout/EOF/error behavior and privilege write scope before implementation. Evaluate native assumptions explicitly; no claim that sudo/pkexec preserves a proposed descriptor/channel without evidence. No daemon, sudoers grant, infrastructure change or live deployment is authorized by design approval.

## Verification plan

After approved implementation scope: isolated fake-backed red/green tests for wrapper-exit/live-owned-child, typed Connected and restart success; negatives for foreign listener, same-path sibling, stale generation/record, PID reuse, unreadable/dead identity, canceled session, failed warmup and unconfirmed stop. Preserve strict direct-handle tests. Native acceptance requires a separately bounded non-VPN harness; no execution authorized/performed in this design note.

## Search-first correction

Official pkexec manual (https://www.freedesktop.org/software/polkit/docs/latest/pkexec.1.html) ties exit status to program completion; sudo manual (https://man7.org/linux/man-pages/man8/sudo.8.html) describes foreground exec or monitor/wait models. Current launch arguments contain no sudo -b. The old Health comment is not empirical proof that supported foreground launch detaches. Conditional guard failure is established, but prerequisite reachability is unverified. Do not introduce a privileged helper before checking this premise.

Verdict: Adopt — existing foreground sudo/pkexec process model as the first characterization candidate, pending native evidence; no helper implementation selected. Native non-VPN characterization needs explicit bounded execution approval and worker preflight, with no policy or deployment changes.

## Authorized native preflight and first fixture — 2026-09-10

Owner approved bounded isolated non-VPN characterization after preflight, no privilege-policy changes. SSH identities matched documented Linux/macOS workers. Read-only resource checks showed adequate room for the tiny fixture; Python and sudo available, Linux pkexec122 available. No SDK installation, files, deployment or VPN operations.

Exact sudo command permission query (`sudo -n -l <absolute sleep> 1`) returned0 on both workers, but this does not prove passwordless execution. Parent executed a self-contained Python observer over SSH: sudo -n <absolute sleep> 1, stdin closed, captured streams, inspected PID/PPID/command only after200ms, waited boundedly for natural exit. No termination signals sent.

Linux: tracked sudo alive at200ms, sleep direct child observed, exit0 after1.008s, both streams empty. This is one native foreground lifecycle witness, not sing-box/config/pkexec proof. macOS: launcher already exited at200ms, exit1 after0.270s,29 stderr bytes; child not observed. Failed launch, NOT proof of detached healthy child. Exact diagnostic was not retained by the initial observer, so cause is unclassified; do not infer password failure solely from byte count. Stop elevated macOS scenario; no retry via shell or policy change. No task files or lingering fixture were created (both processes reached terminal state).

Remaining: classify macOS launch limitation without elevated retry, inspect upstream sing-box foreground behavior, and plan noninteractive pkexec authorization safely. No claim that the full native matrix passed. New helper remains deferred.

## Primary-source reachability recheck

Inspected polkit122 `src/programs/pkexec.c` (https://raw.githubusercontent.com/polkit-org/polkit/122/src/programs/pkexec.c): authorized command execution ends in execv(path, exec_argv); success does not return or detach a separate command child. This matches the worker's reported pkexec version, but is upstream source rather than binary attestation.

Current build-mac.sh calls tools/build-singbox-lx.sh pinned to c7a2592e750406ade9ebaae1d0fdb7482fc0773e. Its exact command implementation (https://raw.githubusercontent.com/Leadaxe/sing-box-lx/c7a2592e750406ade9ebaae1d0fdb7482fc0773e/cmd/sing-box/cmd_run.go) creates the instance and blocks on osSignals in the same run() process until shutdown/reload. No command daemonization in this path. Together with documented foreground sudo wait/exec, these contradict automatic wrapper detachment during normal inspected launch. Abnormal launcher termination and orphan/API-only state are separate conditions, not proof of normal startup regression.

The original review proved guard rejection GIVEN an exited wrapper, not that normal foreground reaches that premise. Independent reviewer is reassessing the classification. No new helper, ownership exception or privilege-policy change warranted by current evidence; no full native matrix pass claimed.

Status: INVESTIGATION CLOSED WITHOUT PRODUCT CHANGE. Independent reviewer withdrew confirmed-reachable-P1 classification after primary-source recheck and accepted bounded source scope for exact-head CI progression. No helper or ownership relaxation selected. Native limits above remain unverified, not waived. Repository cleanup and PR #240 acceptance remain in progress.
