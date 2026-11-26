# Adopt TurnRelay in VPNRouter Standard

- Branch: `dsh/standard-turnrelay-adoption-20261126`
- Base: `6538682b20ce6229c9b8efd7c6b4e021ed3c95a3`
- Client source: `feat/turnrelay-integration`, development head `96fc0666`
- Risk: High. A new transport and restart-only Apply affect runtime capability detection, subscription issuance, and tunnel availability.
- Rollback: Revert this integration PR and restore the preceding verified runtime pins as a unit.

## Why

Integrate the tested TurnRelay client work and enable it through the released sing-box-vpnctl desktop and Android assets. Development builds have exercised real traffic and Android network recovery. Shipping builds need matching published artifacts and checksums.

## What

- Reconcile the client branch with current Standard `main`, including connected category verification from PR #743.
- Update the current desktop, Arch, CI, and Android runtime pin and its integrity checks from the actual core release inventory.
- Preserve capability gating for subscriptions, direct connections with legacy configurations, validated restart behavior, and the existing edition boundary.
- Use a separate PR for this integration. The memory fixes and the Business redesign have their own PRs.

## How

Obtain the exact implemented branch from its source checkout. Merge or port the task commits onto Standard main and inspect duplicated code and callers. Compare platform assets, release tag, `with_turnrelay`, signatures or checksums, and Windows runtime descriptions before selecting the pin. Exercise the published runtime in client CI and the preflighted platform acceptance paths.

## Verification

1. Build: exact-head Standard builds on Windows, Linux, macOS, and Android with the published runtime pins.
2. Tests: shared suite and the focused subscription, configured detour, restart, category, diagnostics, and Android regressions.
3. Documentation: record the source branch, release assets, and observed CI and runtime evidence.
4. Review: independent review of the integration diff and the privilege, restart, and capability gates.
5. Runtime: retain the previous test evidence, then distinguish any verification of the published runtime on WINBRAT and the Android worker from the development build.
6. Integration: verify outgoing capability compatibility and preserve the accepted active connection and configured resolver across restart.

## Outcome

The source tree at `96fc066625a6593cb242b2ed5a428d72a9e63cd8` was retrieved from
its verified checkout. The merge includes Standard's current category probe
and retains both sides of the defect-ledger update. The independent core PR
was merged and tag `v1.14.4-vpnctl.4` is building its release assets. This
source integration commit still uses the existing runtime until the complete
new inventory can be checked.
