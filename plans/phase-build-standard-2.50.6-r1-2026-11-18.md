# Build VPNRouter Standard 2.50.6-r1

Prepare downloadable build artifacts that include the November security,
routing, and Android fixes. Use the accepted branch at `631221d7ae984a5e4a57c9878365957bafa5685a`.

Build the Windows installer and ZIPs, macOS DMG and ZIP, Linux desktop packages, and the signed Android APK.
Only the version changes in the product source. Build on GitHub-hosted runners.
Verify the exact source checkout, package names, platform test results, and all
artifact SHA-256 sidecars. Retain the packages for evaluation before release.
