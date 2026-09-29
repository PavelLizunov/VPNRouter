# H-36: split StripUnsupportedFeatures into named steps

## Why

`CustomConfigInjector.StripUnsupportedFeatures` (in `CustomConfigInjector.Compat.cs`) was one 303-line method that
migrated the legacy sing-box config shape, rewrote DNS, removed block/dns outbounds, rewrote route rules and
normalised inbounds. It is the longest real method in Core and every config a user imports goes through it.

## What

- The body is now ten steps called in the original order: `MigrateFakeIp`, `MigrateLegacyDnsServers`,
  `ReplaceLocalDnsServers`, `RouteLocalDnsDetoursThroughDirect`, `NormalizeDnsStrategyAndFinal`,
  `RemoveLegacyDnsRules`, `RemoveBlockAndDnsOutbounds`, `RewriteRouteRules`, `NormalizeInbounds`,
  `AddSniffRuleIfNeeded`, `EnsureLogOutput`. State that later steps read (the DNS server array, the removed
  outbound tags, whether an inbound had sniffing) is passed as arguments and return values.
- `MigrateFakeIp` shares two small helpers, `ReadOptionalCidr` and `MergeGlobalRange`, for the IPv4 and IPv6
  branches that were written out twice.
- No log message, key name or order of operations changed.

## Verification

Equivalence run on `windows-worker`: the instrumented full test run records a hash of every
`CustomConfigInjector.Inject` and `ConfigGenerator.Generate` result (or the exception type) at the pre-change
SHA (twice, to prove the run is deterministic) and at this SHA; the sorted record lists must be identical. Full
suite and exact-head CI.

## Outcome

Pending equivalence run and CI.
