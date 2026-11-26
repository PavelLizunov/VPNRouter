# Windows Business TurnRelay candidate

- Base: Business `a4951195792bf7b4a425bc0759f893d27ac93c53`.
- Reference: Standard TurnRelay in PR #746, verified tree `57aab662`.
- Risk: managed subscription ingestion, route identity, real traffic verification and update packaging.
- Rollback: revert the port and restore the preceding Business core pin together.
  Retain existing private provisioning and the current production update manifest.

## Why

Windows Business currently bundles sing-box-vpnctl .3. Deliver a Business
candidate with .4 that can use a managed feed containing a valid VLESS/VMess
exit and its TurnRelay hop.

## What

- Confirm the Business feed and policy contracts. Reuse Standard TurnRelay
  parsing, validation and config generation where compatible.
- Preserve the feed and policy trust boundaries. Only verified complete
  chains can become choices. The hop is never a selectable exit.
- Use the exact signed/attested core archive hash in the shared Business pin.
- Produce a credential-free candidate ZIP using the existing Business
  packaging workflow. Existing public Standard releases and employee manifests
  have separate delivery paths.

## How and verification

1. Build the exact Windows Business revision in hosted CI.
2. Exercise capability negotiation, managed-feed validation, selection and
   generated chain, old core, ambiguous/missing upstream and private packaging.
3. Document the default and tested limits in this brief and package contract.
4. Obtain independent review of the managed route and packaging changes.
5. Use the fixed WINBRAT procedure for a live test if an eligible test feed is
   available. Keep actual traffic evidence separate from CI assertions.
6. Verify the update ZIP metadata, native version, provenance and SHA-256.

## Outcome

Source and package verification pending.
