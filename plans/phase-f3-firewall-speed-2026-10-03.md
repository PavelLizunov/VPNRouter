# F-3: firewall rules in milliseconds, not minutes (tester report on r20, items 3 and 9)

## Why

The tester's log: connecting in split mode took 22-29 s (24.5 s creating ~109 block rules, one `netsh.exe` per rule plus one `where.exe` per unresolved process name), and stopping after it took 41 s (disable 20 s, delete 15 s).
On this machine a `netsh` call costs about 180 ms; on the Windows worker 54 ms (109 rules: 5.9 s add, 7.5 s delete, measured with `fwtime.ps1`). Slow enable also lengthens the leak window when the VPN fails.

## What

1. `ComFirewallRuleStore`: add, enable/disable, delete and find the managed rules through `HNetCfg.FwPolicy2` in-process. `FirewallManager` uses it only with the real process runner and falls back to the old netsh code for the rest of the
   session if any COM call throws.
2. Process names are resolved to paths side by side (8 at a time) instead of one `where.exe` after another.

## Not covered

DNS lockdown rules (a handful, still netsh), Linux/macOS firewall managers, whether the VpnEngine stop path still disables before it deletes (left as is: a failed delete must not leave a blocking rule).

## Verification

Fake-store unit tests (no netsh when a store exists, fallback on COM failure, orphan cleanup), an elevated real-COM round trip test (skipped when not elevated), the FirewallManager and VpnEngine suites on windows-worker at the exact SHA, CI,
then timings from the next live run on WINBRAT (log lines `Firewall block rules created` and `All VPNRouter firewall rules deleted`): target connect in split mode under 8 s and stop under 8 s on the same machine.

## Rollback

Revert the PR (netsh path is unchanged and stays as the fallback).

## Outcome

Pending.
