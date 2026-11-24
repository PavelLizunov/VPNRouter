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
