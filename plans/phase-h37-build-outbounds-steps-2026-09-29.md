# H-37: split ConfigGenerator.BuildOutbounds into helpers

## Why

`ConfigGenerator.BuildOutbounds` was a 211-line method that filtered servers by build features, narrowed them
by health records, built the chained-proxy outbounds and picked the active server for AmneziaWG and the
UDP-native protocols, with the same protocol and name checks written out several times.

## What

- `FilterUnavailableServers` (naive, AmneziaWG and xhttp availability), `NarrowByServerHealth` (the
  auto-select health block) and `BuildChainedOutbounds` (the chained-target validation and outbounds) are
  their own methods; the main method keeps the ordering and the out-parameters.
- `IsAwg`, `IsUdpNativeProtocol` and `PickActiveServer` replace the repeated protocol comparisons and name
  lookups. `PickActiveServer` takes `fallbackToFirstOnMiss` because the two callers really differ: the
  AmneziaWG check falls back to the first server when the named one is missing, the hysteria2/tuic check does
  not. Both behaviours are preserved.

## Verification

Same equivalence method as [H-36](phase-h36-strip-features-steps-2026-09-29.md): recorded hashes of every
`Generate` result across the full test run at the pre-change SHA (twice) and at this SHA must be identical;
full suite and exact-head CI.

## Outcome

Merged in #372 after green exact-head CI. Worker equivalence check passed (comment on the PR).
