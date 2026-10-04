# N-1b: faster connect and stop (night pool, from the live harness measurements)

## Why

The first live runs on WINBRAT (r26, 3 cycles in the default split mode) measured a connect at 10-12 s and a stop at 5-6 s. The app's own log shows where the time goes. Connect: +2.2 s fetching the subscription and probing all servers (removed by N-1a), then **4.7-5 s between the
process scan and "Created 4 block rules"**: 92 process names are resolved to paths, 88 of them are not running and each costs a `where.exe` process (8 at a time), then 2-3 s for sing-box and the TUN adapter. Stop: 2.6 s waiting for the TUN adapter removal, **1.05 s deleting the DNS lockdown
rules with nine netsh processes**, 0.4 s for the block rules. Enabling the lockdown at connect is the same nine-process cost.

## What

1. `ProcessImagePath.ResolveNameOnPath`: the where.exe search (current directory, PATH, PATHEXT) in process; `FirewallManager` uses it with the real process runner.
2. The DNS leak lockdown rules (seven add, nine delete) go through the in-process firewall API from one list of specs (`BuildDnsLockdownSpecs`), the netsh path builds its command lines from the same specs and stays as the fallback (and is what tests with an injected runner see, in the same order).
   If the COM path fails half way, the rules it added are removed before the netsh fallback runs.

## Not covered

The TUN adapter removal wait (2.6 s) - removing it from the stop path needs a lock handoff design; the sing-box and TUN start (2-3 s).

## Verification

Unit tests for the specs, the netsh arguments and the PATH search; an elevated COM round trip with a harmless rule (action, protocol, port, address range read back from the firewall); the DNS lockdown / firewall suites on windows-worker; the live harness `cycles` run before and after on WINBRAT (the timings in the Outcome).

## Rollback

Revert the PR (netsh remains the fallback).

## Outcome

Merged as #516 and shipped in r27; the elevated COM round trip test passed on windows-worker (272 tests in the filtered run). Measured live: connect to a chosen server 3.6-3.9 s (r26: 7.3-8.8 s), stop 4.3-4.5 s (r26: 5.3-5.9 s); the log shows "DNS leak lockdown disabled" 3.3 s after the stop begins, no `VPNRouter_*` rule remains. Not changed: the TUN adapter removal wait on stop (about 2.6 s) and the sing-box/TUN start (2-3 s).
