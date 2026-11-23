# Android TUN policy review

## Scope

Review Android package selection, TUN ownership, and effective outbound
configuration from Standard main d50cfb2f. Compare with sing-box for
Android, WireGuard, and Mullvad. On the exact 2.50.2 APK the lab confirmed
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
