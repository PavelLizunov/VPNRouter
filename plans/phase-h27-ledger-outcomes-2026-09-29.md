# H-27: close the ledger against the merged fixes and record brief outcomes

## Why

Everything from H-10 to H-26 except the Android compile-check workflow (H-25, left open for an owner
decision) is merged. The ledger still showed the corresponding entries open and several briefs said
"Pending".

## What

- Tick 18 ledger entries fixed by #346, #347, #349, #350, #352, #353, #355, #359, #361 or verified fixed
  in the current source (APT, post-ship gate, WINBRAT liveupdate, NIGHT-05, NIGHT-07, the
  Flowseal probe argument list).
- Annotate CrashReporter (partly fixed) and PAGESCREENSHOT-RENDER-INVALIDATION (visual-tree dump).
- Fill in the Outcome of the briefs with the merged PR number.

Open P0/P1 gate lines drop from 21 (start of the audit) to 4, all owner-gated or deferred:
VPNCTL-04 (deferred owner), the Ox Alpha security follow-up, the measurement-gated Tcpip 4266
candidate and the code-signing enrollment.

## Verification

Only `plans/` changes; the gate script parses the same format. Exact-head CI.

## Outcome

Merged as #362 on 2026-09-29; exact-head CI green.
