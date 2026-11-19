# Business DNSTT readiness probe

- Branch: dsh/business-dnstt-readiness-20261120
- Base: 0746df68f2ce0b090871c60b1fdea9aecf925d22
- Risk: Low. This changes the DNSTT startup probe destinations.
- Rollback: Revert the probe change.

## Why

The reported Windows startup reached the kernel but exceeded the app's
TUN-readiness deadline. On the same machine and exact installed core,
the primary Google delay probe returned 504. An HTTPS Cloudflare request
through the same DNSTT and SSH outbounds returned 200.

## Scope and verification

Use the existing Cloudflare HTTPS canary first for generated DNSTT startup.
On failure check Google through the same selected outbound. Keep the local
controller, authorization, cancellation, deadlines and positive-delay guard.

- Build and tests: exact-head Business CI and the local-controller fixture.
- Docs and review: record outcomes and obtain read-only DeepSeek review.
- UI: shared Core change. The exact published Standard baseline was tested on
  WINBRAT. Verify the fixed release with the original scenario when shipped.
- Characterization: existing listener fixtures retain their Google behavior
  for other protocols. No mechanical extraction is planned.

## Outcome

Pending implementation verification.
