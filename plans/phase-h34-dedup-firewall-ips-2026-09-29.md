# H-34: one implementation of the kill-switch server-IP parsing

## Why

The duplicate-code scan of 2026-09-29 (windows of 10 non-trivial lines) found `LinuxFirewallManager` and
`MacFirewallManager` carrying two copies of the same `ParseServerIps` (about 95 lines each) and the same
`DefaultResolveHost` (about 25 lines). A diff of the two showed identical behaviour; only the log tag differed.
Two copies mean a fix has to be made twice.

## What

New `Platform/UnixFirewallServerIps` with `Parse` and `ResolveHost`; both managers keep their instance
`ParseServerIps` / `DefaultResolveHost` (tests call them) and delegate with their log tag. Log text is unchanged.
Net about 105 lines fewer.

## Verification

`LinuxFirewallManagerTests` and `MacFirewallManagerTests` (which cover WireGuard peers, hostnames and IPv6)
on `windows-worker`; exact-head CI (Ubuntu builds these platform classes too).

## Outcome

Merged in #369 after green exact-head CI.
