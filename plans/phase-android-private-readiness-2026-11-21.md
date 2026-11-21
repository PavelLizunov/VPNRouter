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

Outcome pending.
