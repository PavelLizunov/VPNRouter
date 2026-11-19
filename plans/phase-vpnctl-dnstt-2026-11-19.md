# Phase - Update the Standard runtime for DNSTT

- Branch: dsh/core-1.14.4-standard-20261119
- Base: f22ccc1077a14753b514d8eff7308d7937ddc790
- Risk: MEDIUM, shared runtime and custom routing on all packaged platforms.
- Rollback: Revert the runtime pins and import change together.

## Why

The sing-box-vpnctl 1.14.4-vpnctl.2 release adds DNSTT. VPNRouter's Custom JSON injector can currently select the DNSTT transport before the application-facing SSH outbound, overriding the supplied final route.

## What

Update Windows, macOS, Linux, Arch and Android runtime inputs and hashes. Check the Linux Cronet ABI against the fork's declared dependency. Preserve the imported SSH route and its DNSTT detour through full and split routing. Keep the existing Slipstream path and ordinary subscription behavior. DNSTT is TCP only and uses the native JSON import path. Release publication and a new share-link format are out of scope.

## How

Verify the release inputs and provenance. Match Linux to the fork's Cronet library. Add a narrow import regression and validate the resulting JSON with the pinned runtime. Check both editions with hosted CI.

## Verification

1. Build: Pending exact-head desktop and Android CI.
2. Tests: All 132 Arch packaging tests passed locally. The pin parity test now compares the immutable Cronet download URL and SHA-256 used by both Linux packages. The upstream core passed offline DNSTT config checks on Linux x64, Windows x64 (local Omarchy guest), and macOS arm64. Matching Naive/Cronet config checks passed on Linux and Windows. Generated config tests are wired into CI.
3. Docs: Runtime pins, Custom JSON setup, and TCP limitation documented. Previous release history left intact.
4. Review: Three independent source reviews. Fixed the missing explicit exclude list in the fixture, added a complete TUN/DNS example, and wired the Windows native check into the compiled Business run. Arch's existing catalog verification checks the final staged library.
5. UI: N/A, no interface change. A live DNSTT tunnel and Android dataplane remain unverified.
6. Characterization: macOS shell, Python, release workflow YAML, catalog JSON and whitespace checked locally. Existing behavior suite runs on CI.

## Outcome

- Four upstream runtime archive SHA-256 digests match SHA256SUMS and GitHub attestations for the exact upstream tag.
- The bundled Windows DLL and Linux SO match the Cronet revision recorded in the new runtime.
- The documented SSH final route is preserved for full routing and split excludes. Split includes send selected processes to SSH.
- Scope remains native Custom JSON. Publication and live acceptance are separate.
- Android CI found that libbox removed `useProcFS()`. Removed both Java callbacks. The app still sets per-app VPN selection in Android's VpnService builder.

## Standard prerelease follow-up

Prepare Standard 2.50.6-r2 with a DNSTT profile glyph. Keep the existing icon
weight and palette. Identify the documented SSH-to-DNSTT path from the final
outbound and its detour. Show the icon for the saved profile, independently of
the connection status. Cover alternate tags, unused nodes and invalid JSON.
Run the desktop and Android CI gates and independently review before staging.
The core P1 ledger, 18-asset release gate and post-ship procedure still apply.
The prerelease review found string coercion in the icon classifier. Require actual JSON strings and cover numeric tags separately from numeric-looking strings.
