# H-9: code audit of imported and open findings

## Why

The owner ordered the code audit after the repository cleanup (2026-09-29).
First pass: the seven hypotheses recorded on 2026-09-28 (NEW-1 and six imported
from frozen PR #296).

## What

Each hypothesis was traced through current main by reading the code paths, their
guards and mitigations. Results and severities are written into
`plans/OPEN-DEFECTS.md`. No product code changes.

## Result

- NEW-1 confirmed: Android "Block on VPN fail" has no enforcement path.
- WIN-DNS-LOCKDOWN-TOCTOU, WIN-BINDIR-ACL-FAIL-OPEN, FAILOVER-WARMUP-RACE and
  LINUX-NFT-TAILSCALE-LOCKOUT confirmed in source (P2).
- WIN-DNS-RESTORE-ORPHAN largely mitigated by the ProcessExit sweep (P3);
  WIN-DNS-NETSH-TIMEOUT-DEADLINE confirmed on renamed code (P3).
- No entry was raised to P0 or P1; the open P0/P1 gate set is unchanged.

## Second pass: the 21 open P0/P1 entries

Each was compared with current main and annotated in the ledger with evidence;
no checkbox was changed and the release gate set stays at 21 lines. Of 17
entries examined: 12 have a premise that no longer holds or is fixed in source
(NIGHT-01, -03 to -08 and the release-pipeline entries), 2 are confirmed
(NIGHT-FOLLOWUP-01 callback isolation, NIGHT-FOLLOWUP-02 safe-mode routing),
3 are not re-verified (NIGHT-02, the WINBRAT process-name entry, partial hash
checks). Unexamined: VPNCTL-04, the subscription-credential follow-up, the
Tcpip measurement entry and the code-signing entry, which are owner or
measurement gated.

## Limits

Source reading only. Nothing was reproduced, no runtime or device test was run,
and there is no local .NET SDK.

## Outcome

Fixes are separate packages that need owner acceptance.

Merged as #345 on 2026-09-29; exact-head CI green.
