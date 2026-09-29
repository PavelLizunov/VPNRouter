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

## Limits

Source reading only. Nothing was reproduced, no runtime or device test was run,
and there is no local .NET SDK.

## Outcome

Fixes are separate packages that need owner acceptance.
