# Windows Business TurnRelay candidate

- Base: Business `a4951195792bf7b4a425bc0759f893d27ac93c53`.
- Reference: Standard TurnRelay in PR #746, verified tree `57aab662`.
- Risk: managed subscription ingestion, route identity, real traffic verification and update packaging.
- Rollback: revert the port and restore the preceding Business core pin together.
  Retain existing private provisioning and the current production update manifest.

## Why

Deliver a manual Windows Business candidate `2.51.2-r1` with sing-box-vpnctl .4
to verify the kernel replacement independently of changes to managed routing.

## What

- Retain the current Business subscription capabilities and server choices.
  Standard can import TurnRelay chains. Business portal routing accepts
  direct VLESS choices and the vpnctl Corp eligibility contract also excludes
  detoured routes. Business chain support requires a matching client and
  backend change with verified egress mappings.
- Use the exact signed/attested core archive hash in the shared Business pin.
- Produce a credential-free candidate ZIP using the existing Business
  packaging workflow. Existing public Standard releases and employee manifests
  have separate delivery paths.

## How and verification

1. Build the exact Windows Business revision in hosted CI.
2. Verify the update ZIP identity, absence of embedded provisioning, native
   version and build tags, source SHA and SHA-256.
3. Keep actual traffic evidence separate from CI assertions.

## Outcome

Source and package verification pending.
