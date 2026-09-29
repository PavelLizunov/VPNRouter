# H-14: stop the Android "block on VPN fail" toggle claiming protection

## Why

Audit finding ANDROID-BLOCK-ON-VPN-FAIL (NEW-1). The Android checkbox stored a value that
nothing enforced, and its hint promised `VpnService.Builder.setBlocking(true)`, which is not a
traffic block. The user was told a protection existed that did not.

## What

- Remove the checkbox, its handler and its field from Android settings.
- The leak section now says Android does not block traffic on its own and offers the existing
  "Open VPN settings" button for Always-on VPN with Lockdown.
- The stored key and `Profile.BlockOnVpnFail` stay untouched so config share and profiles keep
  their format.

## Verification

Android project builds (Release, android-arm64); exact-head CI.

## Outcome

- Android compile: `dotnet build VPNRouter.Android -c Release -p:RuntimeIdentifier=android-arm64` (unsigned)
  on `windows-worker` at exact SHA 538c822f: 0 errors, 84 warnings, 4m40s, APK produced. The same
  toolchain built `origin/main` (b1679a87, after the #339 dead-code removal) on a workstation earlier
  with 0 errors; that run broke the workstation build rule and is not repeated.
- Merged as #352; exact-head CI green.
