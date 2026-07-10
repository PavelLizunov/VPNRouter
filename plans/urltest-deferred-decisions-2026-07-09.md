# URL-test verification — deferred / needs-approval items (Fable, 2026-07-09)

Per user instruction: Fable continues only the SAFE slices (pure code + tests,
additive, no live/system/privacy risk). Anything below needs an explicit user
decision or a riskier change and is parked here instead of being implemented.

## D1. ASN/provider metadata lookup (external network call)

Slice 2+ wants ASN/org per server IP for `AnalyzeProviderRisk`. Any lookup
(RIPEstat, ip-api, bgp.tools, MaxMind ASN DB) either sends server IPs to a
third party or requires bundling a DB. Privacy + supply-chain decision:
- Option A: bundle MaxMind GeoLite2-ASN (offline, license note, ~8 MB).
  FreeConfigGeoIp already uses MaxMind — likely reuse path.
- Option B: RIPEstat REST (no key, rate-limited, sends IPs out).
- Option C: user-supplied/disabled by default.
Blocked on: user choice. Default recommendation: A (reuse existing MaxMind
plumbing, no new egress).

## D2. Blocked-target canary probes (privacy: reveals user intent)

The corpus itself mandates: direct blocked-target probes MUST NOT run by
default (they can reveal intent to the ISP); via-VPN probes only after tunnel
up; the canary list should be remotely updateable/user-supplied. This is a
product/privacy surface (opt-in UX, list hosting, TTL policy) — not just code.
Blocked on: user approval of the canary list + opt-in UX wording.
(The CLASSIFICATION for canary outcomes already shipped in
`