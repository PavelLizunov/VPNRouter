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
and retains both sides of the defect-ledger update. The source integration
commit used the .3 runtime. The [core release](https://github.com/PavelLizunov/sing-box-vpnctl/releases/tag/v1.14.4-vpnctl.4)
is now published at source revision `de4d55ca046482cc3d90c3a97693651768020866`.

### Runtime provenance

Local SHA-256, the release sidecars, `SHA256SUMS`, and the GitHub asset digests
agree. GitHub attestations verified against the core repository. Go build
metadata confirms `with_turnrelay` on Windows amd64, Linux amd64, both macOS
slices, and all four native ABIs of both Android AARs. The Windows and Linux
Cronet library module remains at `4ecba4761b20`. The app packages the API 21
legacy AAR, and the Android build checks that the packaged ABI retains the tag.

| Standard runtime input | SHA-256 |
| --- | --- |
| Windows amd64 ZIP | `1efb965de78720f7a4c86a4a4c0403bc8b388397bb640e4547acc14817ef3287` |
| Linux amd64 tarball, desktop and Arch | `a6014cfad5494fe8fa3f178facad6a7c6dab59f5143f7b5af645e8e4e5d84094` |
| macOS Universal ZIP | `2020768bfefb2e1ce52a1535ab7b39b4904ca7bf95de119bc8986a1549a75117` |
| Android legacy AAR | `07a903e0a8b8f8dda3bba409e663e5c2d235c8df15348c17d22bc246c8bbae26` |

The Arch license catalog records the extracted Linux binary digest
`839c0ddc4383f5bc603a2d00f23d2901102b5f8a8dbadec359d65607c6201269`.
The bundled license digest matches the existing notice. The core source at
the pinned revision includes tagged outbounds and endpoints in `GET /proxies`.
Installed product traffic on the published core remains a separate check
from the development-run evidence.

### Follow-up after independent review

The ten checks on `8afb7b55` passed with core .3. Neon reviewed that revision
and flagged unsafe restart transitions in HealthMonitor and desktop Apply,
missing TURN relay firewall allowance, cleartext subscription imports, and
entry-only rows being picked as exits. Keep the PR draft through the fixes,
new CI and re-review of the material lifecycle changes.

- Restart safety: retain the macOS controller secret through automatic recovery,
  protect configured traffic before replacing a TUN, and explicitly reconcile
  an unconfirmed or rejected replacement with the last accepted runtime config.
- Transport boundaries: admit remote TurnRelay links only over HTTPS, allow the
  real TURN destination in Unix firewall rules, and keep hop rows out of exit
  selection and standalone validation. Exercise both link orders at import.
- Compatibility: check the rollback boundary, feature-version parsing, placeholder
  rejection, trim-safe JSON mapping, and diagnostic credential redaction.
