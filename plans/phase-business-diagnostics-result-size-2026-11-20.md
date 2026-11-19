# Business diagnostics receipt size

- Branch: dsh/business-diagnostics-result-size-20261120
- Base: 0746df68f2ce0b090871c60b1fdea9aecf925d22
- Risk: Low. Receipt serialization is internal UTF-8 JSON.
- Rollback: Revert the receiver change. Previous readers accept UTF-8 JSON.

## Why

Business reports can be accepted while their summary remains unavailable. A
synthetic, schema-valid Russian result at 23,953 UTF-8 bytes was rejected when
writing a receipt because the escaped result alone grew to 65,540 bytes.

## Scope and verification

Write the exact receipt bytes counted by the existing limit and disk quota.
Keep both configured limits, result validation, receipt retention and atomic
replacement. Exercise the production worker path with a synthetic boundary
fixture and run the stdlib diagnostics tests and Business CI. Use DeepSeek for
independent review. The change is server-only and needs no app package or UI
snapshot.

## Outcome

Pending.
