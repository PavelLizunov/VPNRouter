# Android private DNSTT readiness

## Why

The package policy strips public control listeners during preparation. Managed
DNSTT readiness currently adds one back after validation. Keep readiness
inside the private libbox command socket for issue #689.

## What and how

- Request the native HTTPS URL test for the SSH outbound on the freshly started
  session. Require that outbound's positive result before publishing Connected.
- Preserve the current TUN descriptor, current-command and retired-session
  checks. Bound the wait and close the temporary command client on all paths.
- Preserve generated and restored package selections. Cover the lifecycle order
  and private transport in the existing Android fixtures.

Risk is limited to Android managed DNSTT startup and its timeout. A failed
probe must retire the incomplete tunnel. Keep the candidate unpublished if a
rollback would restore the control listener.

## Verification

1. Android compile and ARM64 APK from the exact candidate on GitHub Actions.
2. Host policy and lifecycle fixtures, plus the standard CI suite.
3. Record scope, tested source and device limits here.
4. Independent review of the native command contract and session boundaries.
5. On an isolated ARM64 guest verify DNSTT readiness, ordinary included and
   excluded HTTPS, passive listener inventory and Stop/reconnect cleanup.
6. Keep desktop and public protocol interfaces unchanged. Mechanical
   characterization is not applicable.

The pinned `libbox-legacy-1.14.4-vpnctl.2.aar` SHA-256 and Java symbols
were checked before integration. Its URL test runs HTTPS through the named
outbound and reports the result over its app-private command socket. The
native instance creates a separate result history for every start.

CI passed all nine checks at `bbeed793`, including the ARM64 APK build
(run 44849525633, artifact 14684443813). The installed APK hash matched
the build artifact on an isolated Android 14 guest.

Independent review of `bbeed793` confirmed that the service no longer
adds a TCP listener. It found that a late client connection could outlive
cancellation and that the TUN descriptor was checked only before the probe.
The probe worker now owns client cleanup, and the Connected commit
rechecks the descriptor of the same session. The updated exact-commit
CI, independent review and device verification remain pending.

## Managed readiness deadline

Exact `d8675207` passed CI and independent review. Android 14 ARM64
confirmed normal included/excluded HTTPS routing and no app TCP listener.
The managed DNSTT path started libbox but returned `proxy-unreachable`
at the application cutoff in both attempts (12.09 seconds in the repeat);
positive DNSTT HTTPS remains unverified. The repeated ordinary control HTTPS succeeded.

The pinned libbox `v1.14.4-vpnctl.2` uses a 15-second HTTP client timeout
on the private URL test after DNSTT/SSH pre-dial. The old 12-second app
window could retire a successful candidate before the result arrived.
Give the complete private readiness check a 25-second bounded window with
room for command connection and pre-dial. Preserve the 100 ms cancellation
check, one-shot callback and same-session commit. Verify a positive result
at 13 seconds, expiry and supersession on a fake monotonic clock. A new
device run must still distinguish an unavailable endpoint from a late
result; the prior timeout alone establishes neither cause.
