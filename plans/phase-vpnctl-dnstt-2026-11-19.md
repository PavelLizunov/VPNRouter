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

1. Build: Pending exact-head CI for desktop and Android.
2. Tests: Pending custom JSON regression, native config validation, and packaging contracts.
3. Docs: Pending current runtime references and DNSTT use instructions.
4. Review: Pending independent review of routing selection and package inputs.
5. UI: N/A, no interface change. Platform runtime acceptance remains separate.
6. Characterization: Pending existing import and subscription tests. No mechanical extraction.

## Outcome

- Status: Brief prepared; implementation pending baseline checks.
