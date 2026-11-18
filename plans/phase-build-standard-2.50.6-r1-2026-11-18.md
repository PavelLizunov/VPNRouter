# Build VPNRouter Standard 2.50.6-r1

Prepare downloadable build artifacts that include the November security,
routing, and Android fixes. Use the accepted branch at `631221d7ae984a5e4a57c9878365957bafa5685a`.

Build the Windows installer and ZIPs, macOS DMG and ZIP, Linux desktop packages, and the signed Android APK.
Only the version changes in the product source. Build on GitHub-hosted runners.
Verify the exact source checkout, package names, platform test results, and all
artifact SHA-256 sidecars. Retain the packages for evaluation before release.

## Prerelease preparation

Artifact build 44392644537, regression run 44392515696, and Windows update
test 44392515117 passed for the versioned source at 8e9efaba. All 18 Standard
files, including their checksum sidecars, are available in CI. The Standard
installer and the Business preview opened on the owner's Windows VM.

Prepare the version in a PR to main. The published tag must identify the
accepted source and all staging jobs must run at that tag. The release
ledger still records Android issue #689 as open. The device acceptance
covered normal HTTPS IPv4 routing and passive listener observations.

The owner approved a one-time exception for the Android #689 release
gate on 2026-11-19. The exception applies to Standard v2.50.6-r1 as a
prerelease. #689 remains open and the stable release gate still applies.
