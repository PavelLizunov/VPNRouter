# R-27: faster connect, honest pings and a smarter failover, candidate v2.50.0-r27

## Why

The first block of the owner's night pool (item 3, ping measurement and automatic server choice). Evidence came from the live harness on WINBRAT (r26: connect 10-12 s, stop 5-6 s, every connect probed all servers and resolved 92 process names with where.exe). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r27` (this PR).
2. Contents since r26: N-1a #514 (pings and the connect pre-flight you can trust: probes bound to the physical NIC, the "Implausible" rule only for unbound probes, no probe of every server on each connect), N-1b #516 (process names resolved in process, DNS lockdown rules through the firewall API), N-3a #519 (failover picks the fastest reachable server of the first eight, failover and UDP-check messages in the app language, no crash tail on a normal stop, per-app firewall skip lines at debug level).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, then the live harness `cycles`, `servers`, `ping`, `modes` runs against the installed r27 (timings go in the Outcome).

## Not covered

TUN adapter removal wait on stop, IPv6-only servers in probes, the Android side, the version selector (item 1), everything open in the r26 brief.

## Verification

Exact-head CI on this PR; the live evidence is recorded in the Outcome.

## Outcome

Pending.
