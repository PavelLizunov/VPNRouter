# Android TUN policy review

## Scope

Review Android package selection, TUN ownership, and effective outbound
configuration from Standard main d50cfb2f. Compare with sing-box for
Android, WireGuard, Mullvad, and v2rayNG. On the exact 2.50.2 APK the lab confirmed
Gmail exclusion and retained Chrome and YouTube UID coverage with VLESS
and TLS. Native playback and the Android 17 report remain unverified.

The lab separately observed a Go TLS panic and VPN loss with a test
profile that explicitly disabled TLS. The native 1.14.2-vpnctl.2 stack
matches the source path where a VLESS TLS dialer receives a nil config.
The currently pinned 1.14.4-vpnctl.2 has the same construction. Omit the
disabled TLS object from generated VLESS and Android canonical input.
Preserve enabled TLS and REALITY, VMess settings, package rules, and TUN
lifecycle. Share initialization between generated and custom Android
config paths. Revert the implementation commit to roll back.

## Verification

- Build the exact PR head using hosted Android compile and the Release suite.
- Run the configuration regressions and the Java TUN policy contract.
- Record peer source revisions and the runtime evidence boundary.
- Verify independent source findings against the same revision.
- No UI change. A new Android runtime run is separate from configuration
  and build verification.
- Preserve config behavior apart from the explicit VLESS wire normalization.

## Peer implementations

- [sing-box for Android](https://github.com/SagerNet/sing-box-for-android/blob/07376b13886242facaf4c5e1f642b432dda8be4a/app/src/main/java/io/nekohasekai/sfa/bg/VPNService.kt#L140-L186)
  applies package filters immediately before establishing the interface and
  uses the core's DNS mode and route options. It catches missing packages
  individually and continues.
- [WireGuard](https://github.com/WireGuard/wireguard-android/blob/e7b3a3c118836e112620b1302a8ba1873ad4daac/tunnel/src/main/java/com/wireguard/android/backend/GoBackend.java)
  applies package filters from its prepared config, hands a detached
  descriptor to the core, and restores the prior tunnel after a failed change.
- [Mullvad](https://github.com/mullvad/mullvadvpn-app/blob/b94efb5f52937f0f2b7dfce11911b897112a70c0/android/lib/talpid/src/main/kotlin/net/mullvad/talpid/TalpidVpnService.kt)
  applies exclusions through Android's builder and returns typed results for
  TUN, DNS, and IPv6 setup.
- [v2rayNG](https://github.com/2dust/v2rayNG/blob/d4c3c796968e69463cffc947a1255d147b565a12/V2rayNG/app/src/main/java/com/v2ray/ang/service/CoreVpnService.kt#L262-L299)
  uses the same builder methods and excludes its own package from bypass
  selections. It falls back to broad coverage when there is no selection.

VPNRouter already applies the Android package policy at this boundary.
Keep the checks before replacing the active TUN and recheck at establish
time. This preserves requested exclusions and prevents an empty include
selection from expanding tunnel coverage.

The three Android DNS choices map to a boolean. Both prefer values set
the same flag, and the generated TUN's default IPv6 setting makes its
global DNS strategy IPv4-only. This review preserves that behavior. A
separate DNS change needs generated and imported profile coverage,
effective IPv4/IPv6 routes, and device evidence. Native TikTok and YouTube
playback still need a device and a path with working UDP.

## Outcome

- Baseline d520bdef passed all six jobs of [hosted CI](https://github.com/PavelLizunov/VPNRouter/actions/runs/45234148626).
- Baseline Java policy and lifecycle contracts passed on mm4, Temurin
  17.0.15. The worker reported 10 CPUs, load 2.03/1.77/1.76, 16 GiB RAM,
  approximately 6 GiB free pages, 1.87 GiB swap in use, 43 GiB disk
  available, and no active Java or .NET build. The worker's direct GitHub
  fetch failed at TLS, so the source and pinned JSON dependency were
  transferred with SHA-256 and the embedded Git archive commit verified.
- The GitHub PR integration returned 403 for PR creation from the task
  branch. The full Android compile workflow requires a PR.
- Implementation f620ecb0 passed all six jobs of [hosted CI](https://github.com/PavelLizunov/VPNRouter/actions/runs/45235837471).
  Ubuntu passed 3,835 tests and Windows passed 4,048. Both ran all four
  VLESS serialization cases. The Java policy and lifecycle contracts
  also passed on mm4 with the same commit. The new Java regression
  failed at the intended assertion when paired with baseline policy.
- Android APK build remains blocked by the toolchain. VM100 had an
  Android 36 workload on .NET 10.0.301 and shared SDKs 10.0.302 and
  10.0.400. Both global.json files require 10.0.404 with latestPatch.
  VM100 had 8 CPUs at 3 percent load, 12,866,576 KiB free RAM,
  108,516,859,904 bytes free on C:, and no active build processes.
  VM101 had 4 CPUs at load 0.14/0.03/0.01, 11,254 MiB available RAM,
  zero swap, 4.3 GiB free disk, and no .NET SDK in the standard or
  Android worker tool directories. mm4 has Java 17 and no compatible
  .NET SDK. The VM100 and VM101 preflight leases were released.
- The Android 17 field logs capture TikTok-related DNS exchanges
  inside the TUN in the 18:08 and 18:12 bundles. These logs do not
  associate the requests with an app UID or show video delivery.
  The 18:06 bundle records server URL-test failures including
  timeouts. The November tails do not show the lab TLS panic.
- No UI changed in f620ecb0. Exact APK and live Android 17 verification
  are open. Revert f620ecb0 to roll back the TLS and config changes.

## Independent review follow-up

The independent review examined the original d50cfb2f blobs. It confirmed
the disabled VLESS TLS object and the duplicate DNS preference behavior.
Its main additional finding was an exclusion that survives uninstall:
the tunnel requires the saved package, but the picker shows only apps
from its current scan.

Show saved packages absent from the scan in the existing picker. Keep
them checked until the user removes them, including when a scan fails or
the system-app filter hides a package. Keep native validation strict.
Make the exclusion error explain the recovery action. Verify the picker
population, search and uncheck path, then check removal and reconnection
on a built Android APK when available.

The source also confirms that the active picker takes priority over
per-app rules in custom profiles, and that a Connect rejected while the
native tunnel closes must be retried. Those changes require separate
runtime coverage. The review did not test the possible custom-profile
listener exposure on a device.

At 10cfdb16 the Java policy and lifecycle contracts passed on mm4 with
Temurin 17. The picker source contract failed as expected with the
pre-fix picker. The worker had 10 CPUs at load 1.47/1.44/1.56, about
4.6 GiB of free pages, 1.87 GiB of swap in use, 43 GiB of free disk,
and no active Java or .NET jobs. APK compilation and UI acceptance
remain open under the build constraints above.

## Combined candidate acceptance

PR 731 at `a12760bf` passed three connections on the Android 14 ARM64
emulator with VLESS TLS disabled, including the saved-exclusion removal
and reconnect path. The installed APK matched the Actions artifact. The
[Standard prerelease record](phase-standard-prerelease-r3-2026-11-24.md)
tracks the production-signed upgrade and final routing checks.
