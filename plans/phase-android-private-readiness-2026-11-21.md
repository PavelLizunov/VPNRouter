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

## Android 14 ARM64 repeat

The exact `f8f6e094` build (Actions run 44878341675) passed CI. The
installed APK matched SHA-256
`cebc5f9fa48c1f8b2109f1e236ae9b3b48a51645c5e2586c8f01f69b095863b1`.
With Chrome as the only included app, Android verified its UID selection,
opened the TUN and started libbox. The app returned `proxy-unreachable`
after 25.146 seconds and retired the VPN without publishing Connected.
The app UID had no TCP listener after teardown and the crash buffer was
empty. Browser HTTPS and Exclude were not exercised in this attempt.

## Independent Linux control

An isolated Debian 12 x86-64 worker ran the official portable
`1.14.4-vpnctl.2` binary at `c1868557` with the same Standard profile.
Ordinary HTTPS returned HTTP 200 in 1.435 seconds through a loopback SOCKS
inbound and the SSH outbound with its DNSTT detour. The host route hash
matched before and after, and the lease and process were released. This
proves the profile carried HTTPS on the Linux path. The test used an
ordinary request to a different HTTPS origin and no TUN interface.

A second Linux control used the pinned core's URL-test group with the
same SSH outbound, default HTTPS destination and HEAD method. The core
reported the outbound available in 262 ms. The Linux control did not
exercise Android's TUN, its command socket or the outbound result callback.

An Android diagnostics attempt using the external-disk image ended before
the guest registered with ADB. The image copy was interrupted, and the
subsequent launch had no ADB device or emulator output after 70 seconds.
The app was never installed and no DNSTT result or native log was produced.
The guest was stopped and the host slot was released.

Repeat with the verified official archives from the working Android 14
ARM64 run. Extract them on the worker's internal disk into a task-owned
SDK and AVD, and use the working boot procedure. Collect the production
APK's native log through its local diagnostic export and retain only
sanitized phases and timing. Preserve TUN ownership, per-app selection
and cleanup checks. An empty native log cannot rule out a readiness
request because the pinned single-outbound API does not log URL-test errors.

Log one readiness summary per attempt with the private command phase,
outbound snapshot state, whether the latch accepted a positive result,
and monotonic elapsed time. Use fixed labels and omit native messages and
config values. The timeout, positive-result requirement and tunnel
ownership checks are unchanged.
