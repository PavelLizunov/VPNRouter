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

## Diagnostic Android 14 run

The `38855d4f` APK from Actions run 44883384895 matched the installed APK
SHA-256 `e634447bf4db2af911534f938b503336557cf6839bf9f9d433d84879bc8e9ec7`.
All nine checks passed and independent review accepted the diagnostic delta.
The verified internal-disk media booted the temporary ARM64 guest in
33 seconds. Chrome was the only UID in the Include VPN network.

The diagnostic ended at `command=test-requested outbounds=proxy-pending
positiveCallback=false elapsedMs=25001`. The command socket and request
worked, but the app saw no positive result. The native export recorded an
unrelated direct TUN DNS operation timing out at five seconds. It did not
record the native URL-test outcome. A separate All-traffic attempt had
multiple SSH requests canceled during teardown; it is not an Include test.
The app did not publish Connected. It retired the VPN, and the app UID had
no TCP listener afterward. The temporary guest and media were removed.

Run the same native URL test on the physical Mac worker using the bundled
Darwin runtime and no TUN. This isolates the worker's uplink from Android
and emulator behavior. Do not infer a specific DNS, SSH or TLS failure
from the private command's pending state.

## Darwin host control

The portable Darwin ARM64 runtime from the tested `59d1cf91` macOS ZIP
reported `1.14.4-vpnctl.2` at `c1868557` and passed config validation.
The same DNSTT profile and no-inbound URL-test group reported
`unavailable: context deadline exceeded` exactly 15 seconds after start.
The Debian worker returned `available` for the same test in 262 ms.
No TUN or listener was created and the existing desktop core was left
running. Different OS, architecture and uplink conditions remain; the
Mac timeout establishes that a failure can occur outside Android but
does not identify the failing transport stage.

## Restricted resolver control

A portable Unbound from the signed Debian Bookworm archive forwarded only
the test profile's DNSTT zone on a single-address UDP socket. Nonmatching
names and sources were refused. On VM101 the pinned native readiness test
passed through this resolver in 530 ms on a high loopback port. The
initial wildcard classification included unrelated sockets and was
superseded by the exact local port read below. The Android profile
requires the standard DNS port, so the high-port result does not count as
an Android acceptance run. Check the separate permitted VM107 for a
free standard-port listener before attempting an isolated device repeat.

VM107 was reachable through the worker pool. Its reported wildcard
classification used the same broad listener test. Its DNS service stayed
unchanged and the lease was released without a bind. Compare one documented public
resolver on the standard port in a task-only copy of the profile before
attempting a device run. The copy must preserve the DNSTT and SSH identity.

The Debian A/B control at the `7ed65aba` acceptance snapshot used the
same pinned native runtime, test and profile on both arms. The original
resolver returned `available` at 304 ms. The Cloudflare alternative
timed out after 16 seconds. The comparison changed only the resolver.
Both results are Linux controls. Android acceptance is still pending.

The direct ARM64 repeat at `7ed65aba` matched the staged APK and isolated
Chrome in Include. The private client requested the test and reported
`proxy-pending` at 25,002 ms. The app retired the incomplete tunnel at
25.504 seconds with no Connected state, VPN network or app TCP listener.
The resolver fixture had not run because the VM101 lease was stuck.

The exact VM101 local UDP/53 check found only `127.0.0.53` and
`127.0.0.54`, independently confirmed by the local-address column of
`/proc/net/udp`. The IPv6 port filter returned no listener. The wildcard
from the initial check was in the peer column. VM101's private address
is available for the scoped standard-port fixture. Lease 80 had a
terminal Proxmox shutdown timeout before QGA was ready. After QGA and
SSH recovered, the guest shut down through its OS. Confirmed all three
workers stopped before reconciling the matching reservation under the
pool lock. The failed release detail remains in the request history.

## Standard-port resolver and Android readiness

Lease 81 ran the verified portable Unbound fixture on one private IPv4
UDP/53 address. The scoped zone and source checks passed. The pinned
native URL test on Linux returned available in 435 ms.

The exact `7ed65aba` ARM64 APK used a task copy with only the resolver
changed. Android registered a VPN network for Chrome only and the
private probe returned `proxy-available` with a positive callback at
2,040 ms. Last-good settings were recorded. The system prompts blocked
the UI driver. The retained log does not tie the reported linger event
to removal of that exact VPN network. Ordinary Chrome HTTPS, Exclude
and app Stop remain unverified. The fixture, emulator and lease were
released, and the standing DNS listeners stayed in place.

Repeat with Chrome first-run completed before Connect. Capture the
exact VPN netId, its TUN and UID ownership together with the service
lifecycle. Launch a fresh Chrome HTTPS canary only while that network
is live and native readiness is positive. Verify the same network
before and after the request.

## Android Include HTTPS and Stop

Lease 82 recreated the same scoped resolver fixture. The exact
`7ed65aba` APK returned positive native readiness at 2,090 ms in
Include. Chrome loaded a fresh HTTPS canary while live VPN netId 102
contained its UID and excluded the VPNRouter app UID. The same live
network, `tun0` and foreground VPN service were present before and after
the request. App Stop ended the foreground service, removed `tun0` and
cleared the live network. The retained log has no raw destroy event for
102. The previously reported network loss came from the collector's
partial interface match and stale netId fallback.

Exclude registered live VPN netIds 103 and 104 in separate attempts.
Both excluded Chrome's UID. The collector reused netId 102 and timed
out before launching the direct HTTPS canary. Lease 82 was released
with the fixture listener and emulator stopped. Repeat Exclude only
with a tested collector that reads current network state on every
observation. Capture direct Chrome HTTPS before app Stop and verify
that the exact interface, service and VPN network are gone afterward.

Evidence: `~/Documents/VPNRouter/acceptance/689/standard-7ed65aba/netid-run/`.
The scope covers the task copy with the isolated resolver. The original
resolver still times out on this Android worker.

## Android Exclude HTTPS and Stop

The exact `7ed65aba` ARM64 APK, SHA-256
`1c82ccf6003c03f9d8edebbcffab5d0f7bff53823fd111b902bacc7a3a47d1ed`,
completed Exclude under lease 83 with the scoped resolver. A current
app-owned CONNECTED network, netId 106, reported `tun0` and excluded
Chrome's UID. Native readiness returned a positive callback in
2,219 ms. Chrome loaded a fresh HTTPS canary. The same network and UID
membership remained in place afterward. Chrome traffic was attributed
to the default network and used the host HTTPS proxy outside VPNRouter.
App Stop removed the live network, exact TUN interface and foreground
service. The app TCP listener count after Stop was zero.

The corrected collector accepts the VPN transport token when Android
combines it with Wi-Fi and uses current network state without a
historical fallback. An inert fixture checks stale netIds, UID
membership and exact interface names. The worker released lease 83
after removing the test resolver and emulator.

The managed DNSTT private-readiness scenario passed Include, Exclude
and Stop on this device. This outcome does not establish availability
through the original resolver on the worker network or substantiate
other Android versions. The broader local-control assurance for #689
remains tracked separately in the defect ledger.

Evidence: `~/Documents/VPNRouter/acceptance/689/standard-7ed65aba/exclude-final/`.
