# Standard prerelease integration, 2026-11-24

## Intent and scope

Integrate the accepted desktop command confirmation with Android VLESS and per-app selection fixes. Add an explicit read-only crash diagnosis against the installed revision and an independently confirmed Windows recovery path for repeated failures after a client update. Ship the Standard candidate only after verifying the combined binaries and the subscription gateway.

## Work and risks

- Merge the accepted desktop revision and Android review branch. Preserve both defect histories.
- Verify Android upgrade numbering against the published r2 APK. Use an increasing installable version.
- Bind crash evidence and allowlisted source citations to an exact build. Keep diagnosis separate from the command executor.
- Select rollback packages from trusted release metadata on the client. Preserve settings, require consent and check the installed version after relaunch.

## Verification and rollback

1. Hosted CI builds the exact branch and exercises desktop, Android and update contracts.
2. Independently review the public gateway contract, rollback origin and user confirmation.
3. Exercise Android on a device with disabled VLESS TLS and missing saved app exclusions.
4. Verify Windows upgrade, rollback, two cold connection cycles, HTTPS/UDP, assistant consent, application routing and Undo. Investigate the outstanding DNSTT proxy-health failure.
5. Inventory resolved dependencies and inspect the native payload from the final packages, including external downloads. Record advisory applicability.
6. Verify each of the 18 draft assets and sidecars before prerelease publication. Check integrity, stable Latest, APT and WINBRAT after publication.

Use the existing stable installer and settings backup to restore test machines. Restore the previous gateway build if its new endpoint fails acceptance. A published correction needs a fresh tag.

## Observed outcomes

PR 730 passed nine checks at 8f568e59 and merged as 0e29e063. The real Windows panel passed 54 checks on e9d3be76. This branch combines that work with Android 71df6fae. Acceptance of the combined binary is pending.

The published r2 APK has package com.ninitux.vpnrouter, versionName 2.51.0-r2 and versionCode 2051000. Its SHA-256 is 9bf4f6269f1653eb10a7c21b0f064012ec4b521b365785af165fc883387cb5e0. Choose 2.51.1-r1 (2051001) because the Android project excludes the candidate suffix when deriving its install code. Verify that number and the signing identity in the final APK.

At `a12760bf`, the installed Android 14 ARM64 APK matched CI artifact 14918847275
from run 45391064114 (APK SHA-256 `0a1fca599153f0a31ab7484a2007527159e8e773f9532eb48ef3a4b4e293c75f`).
Three connections with VLESS TLS disabled passed. Chrome reached the controlled
HTTP marker and the HTTPS control through the tunnel. Android's VPN UID ranges
placed Chrome, Gmail and YouTube as expected for Include and Exclude. Removing
Gmail while its exclusion was saved blocked Connect. The picker retained the
missing package, allowed removal, and the next Connect succeeded. All three
stops removed the tunnel and foreground service.

The r2 and CI APKs have the same package and ARM64 libbox digest. Their
version codes are 2051000 and 2051001. They use different signing certificates.
At the final tag, compare the production signing certificate and dependency
graph to the actual APK, install it over r2 with saved settings, and repeat
the route checks including a direct browser request while Chrome is excluded.

The source diagnosis request is explicit in the desktop panel. New crash reports record the originating revision. An older or mismatched report stays eligible for ordinary diagnosis. The release Android job retains the resolved restore graph alongside the APK digest as a CI artifact for dependency review.

Deep mode also accepts a classified manual connection failure while the app is still running. The user chooses the incident and sees its observed time and failed stage before sharing. The client invalidates a stale selection on a new attempt or subscription change. A registered build with insufficient relevant source has a separate result. The Android APK at a12760bf passed hosted compilation and Android 14 ARM64 emulator acceptance.

The [desktop and gateway contract](../docs/standard-assistant-gateway.md) documents the three wire operations, trust and consent boundaries, source provenance, recovery ownership, error handling and packaged acceptance.

On 24 November, all ten hosted checks passed for `a12760bf` before the manual-failure, recovery and Telegram runtime changes. The public health endpoint reported `0.9.0+968fde0`; an unauthenticated source-diagnosis POST returned 404. The pre-deployment catalog generator passed eight tests, including refusal to publish a credential-bearing excerpt and preservation of an existing catalog. The reviewed preview catalog maps Windows x64 only.

The prior official Windows archive has 289 entries. The application reports `1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6` in its PE version resource. Verify the installed target against that exact signed commit and archive digest. The optional Telegram helper in 2.50.5 also uses the older wheel pins; include its installed state when evaluating rollback compatibility.

The generated-mode DNSTT refresh gap is fixed in source. A selected generated row is reconciled only when its full old profile, including the client credential, is unique in the previous cache of exactly one enabled HTTPS registration and the old and new outbound ids are unique. The pre-fetch binding is carried through publish; an in-flight selection or editor change rejects the replacement and keeps the old cache and its binding. Ordinary refresh and the first manual disconnected Connect fetch once and persist the rotated row; background refresh remains subscribe-only. Targeted Core and headless ViewModel tests are added. CI and WINBRAT acceptance are pending.

The gateway branch at `a95c7f23` has the source route and the same 551-package Cargo lock as the audited baseline. All 14 hosted jobs passed at this revision, including the root-owned catalog integration case. PR 370 merged at `7dfb59c8` with an identical Git tree. Build-candidate run 45407918439 targets that exact merge. The archive verifier found no package changes. The Windows recovery archive for 2.50.5 matches the release digest, declared size and PE commit. The rollback flow still needs compiled and installed acceptance. The signed Android APK and its restore graph are generated by the final release workflow.

The Windows and Linux Cronet libraries match the core's 246 required exports.
Both upstream core tags v1.14.4-vpnctl.2 and .3 use the same Chromium
154.0.8037.49. The checked official libraries and bindings have no release
covering CVE-2026-76029. Full stable observed on 24 November was 158.0.8127.89.
NaiveProxy is supported on Windows and Linux. Release disposition remains open.

The Telegram proxy source archive, CPython 3.14.10 embeddable and four pinned
wheels passed independent digest verification. The acceptance inventory derives
223 expected installed-file hashes from those archives. The candidate stages
and probes the source with the chosen Python before replacing any live files.
Tests and WINBRAT installation, migration, retry and launch remain outstanding.
