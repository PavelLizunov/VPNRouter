# ADR — Phased server-health verification model

- Status: Accepted (2026-07-09, Fable)
- Context: `plans/urltest-verification-plan-2026-07-09.md` + audit vector map.

## Decision

Introduce a **pure, network-free classification core in `VPNRouter.Core`** that turns
per-phase probe outcomes into a single server-health verdict, and a grouped
provider/ASN risk analysis. All *decision* logic lives here (zero I/O), mirroring the
existing pure-policy classes (`SplitTunnelPolicy`, `ConnectionHealthClassifier`) so it
is golden-tested on CI with no network, no sing-box, no platform code. The probes that
*produce* the phase outcomes (quick TCP/TLS/UDP, deep sing-box HTTP, canary, ASN lookup)
stay in their existing services and later slices feed this core.

### Shape

- `enum PhaseOutcome { Unknown, Pass, Fail, Skipped }`
- `record ServerHealthPhases(Dns, TcpConnect, TlsCamouflage, ProxyHandshake,
  ProxiedHttpControl, BlockedTargetCanary, UdpAppProfile)` — each a `PhaseOutcome`,
  all defaulting `Unknown` (a phase that never ran stays `Unknown`, never fabricated).
- `enum ServerHealthVerdict { Unknown, Healthy, HostUnreachable, TcpOpenProtocolUntested,
  ProtocolHandshakeBlockedLikely, ProxyStartedButHttpFailed, OnlyControlWorks,
  UdpOrAppProfileFailed }`
- `ServerHealthClassifier.Classify(ServerHealthPhases) -> ServerHealthResult` (verdict +
  the phases + a short reason string, no secrets).
- `ServerHealthClassifier.AnalyzeProviderRisk(IEnumerable<(string asn, ServerHealthVerdict)>)
  -> IReadOnlyList<ProviderRisk>` — flags an ASN `HighRisk` when >=2 of its servers are
  `ProtocolHandshakeBlockedLikely` (TCP-reachable, protocol blocked) while at least one
  *other* ASN has a `Healthy` server for the same client — the "grouped by subnet, not
  host-wide" heuristic.

### Verdict rules (first match; TCP alive is the pivot)

1. `Dns==Fail` or `TcpConnect==Fail` → `HostUnreachable`.
2. `TcpConnect==Pass` and `ProxiedHttpControl==Pass`:
   - `BlockedTargetCanary==Fail` → `OnlyControlWorks` (tunnel up, censorship-bypass unproven).
   - `UdpAppProfile==Fail` → `UdpOrAppProfileFailed`.
   - else → `Healthy`.
3. `TcpConnect==Pass` and (`TlsCamouflage==Fail` or `ProxyHandshake==Fail` or
   `ProxiedHttpControl==Fail`) → `ProtocolHandshakeBlockedLikely` (host reachable at TCP,
   VPN protocol does not carry traffic — the RU-ASN/TSPU signal).
4. `TcpConnect==Pass`, nothing deeper ran → `TcpOpenProtocolUntested` (NOT `Healthy`).
5. else → `Unknown`.

## Why this shape

- **Pure + testable now, unblocks everything.** No network in the decision → CI golden
  tests pin the audit's regression cases deterministically; the risky I/O is added later
  behind this stable contract.
- **`Unknown` is first-class.** A phase that did not run is `Unknown`, never silently
  `Pass`. That is the whole point: we stop turning "ping/TCP works" into "server works".
- **TCP-alive is the pivot** for the RU-block signal, matching the corpus + external Xray
  issues (`#5908/#5897/#5332`): TCP established, handshake dropped, node still "looks alive".
- **Provider grouping is separate** from the per-server verdict, so one bad host never
  condemns a whole ASN and vice-versa.

## Consequences / non-goals

- This slice does NOT rewire `DeepVerifyResult`, `TcpTlsProbe`, Auto ranking, or UI — those
  are later slices that map their outcomes into `ServerHealthPhases`.
- No legal/regulatory claims — only observed-symptom classification.
- No secrets: the classifier takes primitives only (outcomes + an ASN string); redaction
  stays the callers' job via `DiagnosticsRedactor`.
