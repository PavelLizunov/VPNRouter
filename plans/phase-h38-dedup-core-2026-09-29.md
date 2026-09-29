# H-38: remove four duplicated blocks in Core

## Why

The cross-file duplicate scan (2026-09-29, blocks of 8+ non-trivial lines) listed identical or near-identical
code that lives in two places, so a fix in one place silently misses the other.

## What

- `CustomConfigInjector.Geo.cs` had a byte-identical copy of `FindRouteInsertIndex` (`FindGeoInsertIndex`);
  the geo rule insertion now calls the single function in `CustomConfigInjector.Routing.cs`.
- `SlipstreamManager` and `TgProxyManager` each had the same `netstat` port-owner lookup; both call the new
  `PortOwnerResolver.TryResolve`.
- `FreeConfigDeepVerifier.BuildSingleOutboundConfig` and `VlessDeepVerifier.BuildSingleOutboundConfig` were two
  copies of the temporary sing-box config builder that differed only in an optional clash port and in the
  `awg3`/`amneziawg3` aliases (the URI parser always stores `amneziawg`, so the aliases never matter). The
  Vless verifier now holds the single builder (`int? clashPort`); the Free verifier method delegates to it.
  The only observable difference is the key order inside the generated JSON of the Vless verifier
  (`endpoints` now precedes `experimental`); sing-box does not care about key order.
- `GitHubReleaseSource` and `SideloadSource` repeated the "parse tags, drop drafts, keep newer, newest first"
  query; `ReleaseCandidates.NewerThan` / `Parse` is used by both and by `ListStableAsync`.

## Verification

Read of the diff; exact-head CI (all platform builds, Windows job runs the full suite, including
`DeepVerifierDnsPrivacyTests`, `FreeConfigDeepVerifierBuilderTests` and the update source tests); equivalence
run of `Inject`/`Generate` hashes against the pre-change SHA on `windows-worker`.

## Outcome

Merged in #373 after green exact-head CI. Worker equivalence check passed (comment on the PR).
