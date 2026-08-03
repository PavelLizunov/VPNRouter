# VPNRouter v2.48.0-r6

This candidate fixes the repeatable Windows cold-start TUN crash found during
the r5 WINBRAT verification.

## Changes

- The Windows sing-box-lx build no longer supplies Wintun's deterministic
  `RequestedGUID` for the transient TUN adapter. Windows allocates the adapter
  GUID, avoiding the 15-second create/remove failure that ended with
  `already exists` plus `open existing adapter: Element not found`.
- The source patch is fail-closed: the build stops unless the pinned sing-tun
  call is found and replaced exactly once.
- AmneziaWG/XHTTP tags and the existing AWG Windows runtime smoke remain
  mandatory and passed after the dependency tree was materialized.
- Includes the MTU contract fixes from r5.

## Recommended test flow

1. Start from zero sing-box processes and zero VPNRouter TUN adapters.
2. Connect, wait at least 60 seconds, and confirm one sing-box process and one
   Up `VPNRouter-TUN` adapter with no restart around 15 seconds.
3. Pass a DF UDP payload of 1392 bytes (inner IPv4 size 1420).
4. Disconnect and confirm zero sing-box processes and zero VPNRouter TUN
   adapters. Repeat the cold-start cycle three times.
5. Scan only the new verification log window for `ERR`, `Exception`, and
   `FATAL`.

## Verification status and known limitation

Before commit, the exact r6 Windows artifact passed three clean WINBRAT
cold-start cycles. Two cycles passed DF UDP at inner IPv4 1420, every Stop left
zero process/TUN state, and the 15-minute r6 log window was clean.

The existing desktop HTTP warmup is still not proof that TUN initialization
completed: r5 showed it could report Connected while Wintun was still starting.
That separate readiness defect remains tracked for a focused lifecycle change;
this hotfix does not add a sleep, retry, or cleanup workaround.

Full evidence and the next exact task prompt are in
[`plans/mtu-end-to-end-audit-2026-08-03.md`](mtu-end-to-end-audit-2026-08-03.md).
