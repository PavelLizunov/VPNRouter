# Russian IP bypass - 2026-10-19

## Why

The owner reports Russian sites still using VPN with the bypass enabled.
At accepted base `7af9aaeb4b6d9f74ec2427ff164807b61db44332`,
`ConfigGenerator.ApplyGeoBypass` registers both Russian rule sets but routes
only `vpnrouter-geosite-ru` directly. `CustomConfigInjector` already uses both.
The generated current.json on WINBRAT also has the domain-only rule.
This confirms a product defect, not yet the cause of the owner's exact site.

## What and how

Add the already downloaded `vpnrouter-geoip-ru` to the generated direct rule.
Pin both tags, insertion precedence, enabled/disabled behavior and preserved
proxy DNS across full, split include and split exclude modes. Compare the custom
injection path. Do not change DNS privacy, custom-rule priority, settings, UI,
geo downloads, release versions or the owner's working checkout/index.

## Risk and rollback

Russian IPs now take the documented direct route even for non-Russian domains.
Shared infrastructure hosted on those IPs is included by design. Revert the
scoped fix commit to restore previous behavior. Existing custom-first rules keep
their precedence. No new dependency or data download is needed for the fix.

## Verification and gates

1. Build: pending exact-SHA Windows worker and GitHub CI.
2. Tests: pending focused regression, negative control and full Windows suite.
3. Documentation: brief and defect ledger; no release facts changed.
4. Review: pending separate-context review of the exact diff.
5. Consumer: inspect serialized config with real geo files and installed core;
   no owner's target device/site is known yet. No release/deployment performed.
6. Integration: generated and custom paths, all routing modes; not a refactor.

## Environment evidence

WINBRAT SSH identity confirmed. Preflight 2026-10-19: 8 logical CPUs, 0% CPU,
13.49 GiB free RAM of 16 GiB, C: 119.90 GiB free, no dotnet/msbuild/testhost or
VPNRouter.App/sing-box processes. Private SDK 10.0.301 available.
The development workstation has no .NET SDK; builds run on WINBRAT/CI.
Original checkout remains on main with 18 pre-existing instruction-file edits.
A separate task worktree preserves those edits and its index.

## Outcome

Investigation and verification in progress. Owner's exact device/site and live
traffic reproduction are not yet available. Source repair is not an installed fix.
