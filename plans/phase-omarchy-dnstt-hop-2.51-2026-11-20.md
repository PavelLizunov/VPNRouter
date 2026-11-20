# Omarchy DNSTT and chained subscriptions for 2.51

- Base: 6066dd593f47f82a45eae4d55be0d5e4ca429a28
- Risk: medium. Changes affect the installed package, subscription selection, and connection readiness.
- Rollback: restore the previous package and manifest. Preserve the user's subscription and settings.

## Why

The installed Arch backend predates DNSTT capability negotiation even though its packaged binary reports version 1.14.4-vpnctl.2. The same HTTPS subscription returns a DNSTT row only when the VPNRouter user agent advertises DNSTT. The user also reports a chained VLESS server that fails to connect.

## What and how

- Prepare app version 2.51.0-r1 and stage the exact Arch package with the matching Omarchy plugin.
- Test DNSTT import and chained selection via the installed backend. Trace the live ingress, hop and exit separately before changing chain behavior.
- Preserve the selected route and reject a missing or ambiguous required hop. Use installed-package checks and a separate Omarchy worker.
- Carry the confirmed audit findings in scoped changes. Track device-only checks independently from source findings.

## Verification and outcome

1. Build: pending exact-head CI and Arch packaging.
2. Tests: pending subscription import, selected hop, and connected/failed readiness checks.
3. Documentation: this brief records the observed installation and feed results.
4. Independent review: DeepSeek diagnosis started. Review the implementation diff separately.
5. Actual client: pending Omarchy package, UI and HTTPS data-plane checks.
6. Characterization: no mechanical split planned.

The live feed returns five VLESS rows including one chained target. With DNSTT capability it also returns one DNSTT row. No profile contents are stored in Git.

The exact packaged core returned HTTP 204 through both the first hop and the
complete chain on the independent Omarchy worker. The owner's Omarchy timed
out through the same profile, including with a physical-interface binding.
The provider's chain and the client-side route must remain intact while the
owner-machine failure is diagnosed.

The existing Simple start-timeout fix was already verified on WINBRAT.
This candidate also surfaces an unconfirmed Stop from audit AUD-01, including
when the engine still reports a live tunnel. The installed Arch smoke test
now imports and lists a synthetic DNSTT profile without starting a connection.
