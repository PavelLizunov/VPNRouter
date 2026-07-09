# Plan — URL-test / verification trust-boundary (Fable, 2026-07-09)

Goal input: `plans/fable-urltest-research-and-implement-2026-07-09.md` + the audit
corpus (`plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md` + Drive).
Working personally, no subagents.

## Phase 1 — verification against current source (confirmed / corrected)

Verified directly by reading the files at HEAD (v2.46.0):

- **CONFIRMED — `urltest` shape is generic-HTTP-only.**
  `ConfigGenerator.AddOutboundGroup` (`VPNRouter.Core/Services/ConfigGenerator.cs:1448-1457`)
  emits `Type="urltest"`, `Url="http://www.gstatic.com/generate_204"`, `Interval="3m"`,
  `Tolerance=150`, `InterruptExistConnections=false`. This is a latency selector, not
  app/protocol verification — exactly the audit's vector #3.
- **CONFIRMED — failure classification collapses; no phase preserved.**
  Quick probe `ServerProbeStatus` (`TcpTlsProbe.cs:18-51`) = `Unknown/Ok/Slow/Unreachable/
  Timeout/TlsFailed/Implausible/SkippedNotApplicable`. Deep verify `DeepVerifyResult`
  (`VlessDeepVerifier.cs:13-20`) = `(bool Ok, int HttpLatencyMs, double? BandwidthMbps,
  string? Error)`. Both collapse *why* a proxy failed into one status/free-text string.
  No `ProtocolHandshakeBlockedLikely`, no ASN/provider concept.
- **CONFIRMED — DeepVerify proves one control URL only.**
  `DeepVerifyConstants.ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace"`; a single
  `GET` through the spawned sing-box SOCKS (`VlessDeepVerifier.ProbeViaSocksAsync`, line 396).
  No blocked-target canary, no UDP/app-profile probe.
- **CONFIRMED (useful prior art in-repo) — protocol-aware quick probe already exists.**
  `TcpTlsProbe.ProbeServerAsync` dispatches vless-reality→TCP-only, hy2/tuic→UDP,
  ss→TCP, so "Reality rejects a naive TLS handshake" is already handled at the quick
  layer. The gap is that a TCP-reachable-but-proxy-fails result is not *classified* as a
  likely protocol/subnet block; it just becomes `Ok` (quick) then `Failed(string)` (deep).
- **CORRECTED (from the audit's own note) — several old lifecycle P0/P1 items are fixed.**
  Not re-touching failover self-cancel etc.; not in this slice's scope.
- **HYPOTHESIS (needs live) — RU-path-only failure + provider/ASN grouping** need real
  RU-ISP verification; the *classifier* is pure and testable now, the *live probes*
  that feed it are later slices.

## What (scope, in corpus priority order)

1. **P0 — pure phased server-health classifier (Core, no network).** New enums +
   `ServerHealthClassifier` that maps observed phase outcomes (DNS / TCP / TLS-camouflage /
   proxy-handshake / proxied-control-HTTP / blocked-target-canary / UDP-app) to a verdict:
   `Healthy`, `HostUnreachable`, `TcpOpenProtocolUntested`, `ProtocolHandshakeBlockedLikely`,
   `ProxyStartedButHttpFailed`, `OnlyControlWorks`, `UdpOrAppProfileFailed`; plus a grouped
   `ProviderRiskAnalysis` that flags `ProviderSubnetHighRisk` when same-ASN servers fail at
   the protocol phase (not TCP) while other ASNs work. **This slice.**
2. P1 — feed the classifier from the real pipeline: preserve phases in `DeepVerifyResult`
   (add phase fields, keep `Ok` back-compat), map quick+deep outcomes into
   `ServerHealthPhases`, attach ASN/provider metadata (local cache, redacted).
3. P1 — blocked-target canary layer: control canary + multi-canary matrix, safe-default
   via-VPN only, updateable/user-supplied list, URL redaction.
4. P1 — Auto/urltest trust-boundary UX: reword "Auto" as quick web selector, expose the
   selected member + last-test age, penalize `ProtocolHandshakeBlockedLikely` /
   `ProviderSubnetHighRisk` in ranking, RU-specific copy.

## How

TDD. Start with slice 1: pure classifier + xUnit tests pinning the audit's regression
cases (RU-block heuristics + canary distinctions + provider grouping). No network, no
platform code, CI-safe. Each later slice builds clean + adds tests before the next.

## Verification gate (this slice)

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
- `dotnet test --filter ServerHealthClassifierTests` → green; cases pin the audit's
  regression list 1-7 + provider grouping.
- No behavior change to existing probes yet (classifier is not wired into the pipeline
  in this slice — it's the pure decision core other slices call).

## Risk

LOW for slice 1 (additive pure code + tests, nothing wired in). MEDIUM later (touching
DeepVerify result shape, Auto ranking, UI copy, ASN network calls with redaction).
