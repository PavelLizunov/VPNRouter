# H-51: split LeakProtection.ValidateConfig into named checks

## Why

`LeakProtection.ValidateConfig` (114 lines), the pre-start safety net for generated sing-box configs, mixed six kinds of
checks (DNS strategy and inbounds, per-process DNS paths, Reality flow, full-tunnel and routing-mode consistency,
required outbounds, proxy outbound details) in one body that appends to two lists in an interleaved order.

## What

`ValidateConfig` keeps the no-outbounds early exit and the scope-aware server check and then calls, in the original
order, `ValidateDnsStrategyAndInbounds`, `WarnAboutProxyProcessDnsPaths`, `WarnAboutRealityWithoutFlow`,
`WarnAboutFullTunnelAndRoutingMode`, `ValidateRequiredOutbounds` and `ValidateProxyOutbounds`. The statements moved
unchanged, so the relative order of entries inside `Errors` and inside `Warnings` is unchanged. A multiset
comparison of the trimmed lines shows nothing removed and only method headers, braces and the six calls added.

## Verification

Worker-only corpus (not committed): 6,000 seeded `ConfigGenerator.Generate` results are validated with
`ValidateConfig` as generated and after one random mutation (wrong DNS strategy, missing `direct` or `proxy`
outbound, strict route on, flipped `route.final`, no hijack-dns rule, wrong DNS final, cleared flow, null outbounds);
the hash of the ordered error and warning lists is compared between the parent SHA and this SHA. Exact-head CI runs
the full suite.

## Outcome

Merged after green exact-head CI. Worker equivalence check passed on 2026-09-30: 4,912 generated configs validated as generated and 4,912 after a random mutation, identical ordered error and warning lists at the parent SHA and at this branch, corpus deterministic on a rerun (6,000 Generate results identical as well).
