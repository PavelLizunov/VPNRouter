# H-48: split BuildRoute, BuildDns and Inject into named steps

## Why

After H-36/H-37 the next long, pure functions of the config pipeline were `ConfigGenerator.BuildRoute` (122 lines),
`ConfigGenerator.BuildDns` (97 lines, with an empty `if (isFullTunnel) { }` branch and two identical per-app blocks)
and `CustomConfigInjector.Inject` (119 lines).

## What

- `BuildRoute`: `AddDnsTunnelSelfExclusionRules`, `AddQuicRejectRules` and `AddPerAppRoutingRules` hold the three
  rule groups; the method keeps the order of the rule list and the `Final` decision.
- `BuildDns`: `AddLanSystemDns` holds the LAN suffix handling; the three-way `if (isFullTunnel) {} else if
  (isExcludeMode) ... else ...` became one `if (!isFullTunnel && processes.Count > 0)` with the server chosen by
  `isExcludeMode ? (strictDns ? vpn : local) : (strictDns || DnsMode != "smart" ? vpn : local)`. This is the only
  place where statements were rewritten instead of moved; it is the same truth table.
- `Inject`: `ThrowIfPlaceholderProxy`, `SetRouteFinal` and `SetDnsFinal` hold the placeholder gate, the final route
  and the final DNS decision. The statements moved unchanged.

## Verification

Worker-only corpora (not committed), each result or exception (type and message) hashed at the parent SHA and at this
SHA: 6,000 seeded full `ConfigGenerator.Generate` calls (servers of every protocol, routing modes, apps modes,
strict DNS, ad block, LAN suffixes, custom rules, custom direct rules, geo files present or not, feature switches)
and 8,000 seeded `CustomConfigInjector.Inject` calls (random sing-box configs with real and placeholder proxy
outbounds, both rule formats, all routing modes). Exact-head CI runs the full suite.

## Outcome

Pending equivalence run and CI.
