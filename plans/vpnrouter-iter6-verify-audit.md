# iter#6 — Test / Deep verify audit + unification proposal

**Date**: 2026-05-04
**Trigger**: user request — «Нужно отревьювить, проверку и глубокую проверку
конфигов, нужен также тест этого через computer-use, подойди к этой задаче
максимально дотошно, можешь даже отрефакторить эту опцию так чтоб она
работала по другому, и как эти проверки логически совмещаются с проверками
из страницы public».

**Methodology**: code architecture read across 4 verifier classes + 5
ViewModel commands, then live `mcp__vpnrouter-test__*` audit of all
three surfaces (Servers / Subscribe / Public).

## 1. Surfaces audited

| Surface | Trigger | Test stage | Deep stage |
|---|---|---|---|
| **Servers tab** | "Проверить все" + "Глубокая проверка" (2 separate buttons) | `TcpTlsProbe.ProbeAsync` (3 s TCP, 3 s TLS) | `VlessDeepVerifier.VerifyBatchAsync` (sing-box spawn + HTTP trace + 5 MB BW) |
| **Subscribe tab** | Same 2 buttons (different commands) | `TcpTlsProbe.ProbeAsync` | `VlessDeepVerifier.VerifyBatchAsync` |
| **Public/FreeConfigs** | One CTA: "Найти рабочие конфиги" — auto-pipeline | `FreeConfigTester.TestAllAsync` (1.5 s TCP, 3 s TLS) | `FreeConfigDeepVerifier.VerifyBatchAsync` (sing-box spawn + HTTP trace + BW) |

## 2. Code duplication map (pre-r15)

```
            ┌─ TcpTlsProbe ───────────────┐ ┌─ FreeConfigTester ──┐
            │  ProbeAsync (TCP+TLS+plaus) │ │  TestOneAsync       │  ← ~80 LOC
            │  ProbeTcpAsync              │ │  TcpPingAsync       │  ← duplicate
            │  ProbeTlsAsync              │ │  TlsHandshakeAsync  │  ← duplicate
            │  CertNameMatches            │ │  CertNameMatches    │  ← byte-identical
            └─────────────────────────────┘ └─────────────────────┘

            ┌─ VlessDeepVerifier ─────────┐ ┌─ FreeConfigDeepVerifier ┐
            │  VerifyAsync (spawn + HTTP) │ │  VerifyOneAsync         │  ← ~100 LOC
            │  BuildSingleOutboundConfig  │ │  BuildSingleOutboundCfg │  ← duplicate
            │  ProbeViaSocksAsync         │ │  ProbeViaSocksAsync     │  ← duplicate
            │  MeasureBandwidthViaSocks   │ │  MeasureBandwidthViaSk  │  ← duplicate
            │  WaitForPortBoundAsync      │ │  WaitForPortBoundAsync  │  ← duplicate
            │  IsPrivateOrLoopback        │ │  IsPrivateOrLoopback    │  ← duplicate
            └─────────────────────────────┘ └─────────────────────────┘
```

Total duplicated: **~500 LOC across the 4 verifier classes**.

## 3. Computer-use audit findings

Live click-test on dev binary v2.31.6-r14 with brat's 7-server pool:

### Servers tab
- 7 servers, all show `<5 ms` ping after Test all → all are
  `ServerProbeStatus.Implausible` (sub-5ms threshold).
- Status text: «Готово. Пинг прошёл: 7 / 7 · полная проверка — «Глубокая проверка»».
- Per `v2.30.7-r2` fix, Implausible counted as "passed ping". User
  reads "7/7 passed" but actually all 7 had traffic intercepted by an
  active local TUN (or cached ARP route).

### Subscribe tab
- Same 7 servers, same `<5 ms` Implausible result.
- **BUG: status text leaks across tabs.** "Готово. Пинг прошёл: 7/7"
  showed on Subscribe tab even when Subscribe servers had not been
  tested yet. Both tabs bind to `ServerTestProgressText` /
  `ServerDeepProgressText`. Pre-r15 these are SHARED fields.

### Public/FreeConfigs
- Single big green CTA: «Найти рабочие конфиги».
- Settings expander: target count (10), ping threshold (400 ms),
  skip-RU.
- One auto-pipeline: TCP+TLS first → deep-verify (sing-box spawn +
  HTTP trace + BW) on the passers → stops once N working found.
- Cleaner UX than Servers/Subscribe — user doesn't manage two
  buttons.

## 4. Inconsistencies summary

1. **Major**: 4 verifier classes for 2 conceptual operations (~500 LOC
   duplication). TcpTlsProbe ↔ FreeConfigTester. VlessDeepVerifier ↔
   FreeConfigDeepVerifier.
2. **Major**: Status text shared across Manual/Subscribe → leak
   confuses the user.
3. **Medium**: "Pinged 7/7" misleading when active VPN intercepts —
   Implausible status counted as passed.
4. **Minor**: Two parallel enums (`ServerProbeStatus` ↔
   `FreeConfigStatus`) with 1-to-1 semantic mapping.
5. **UX inconsistency**: Servers/Subscribe have separate Test/Deep
   buttons; Public has one auto-pipeline CTA. The Public flow is
   cleaner — single click → "find N working configs". Power users on
   Servers/Subscribe still need Test-only or Deep-only options for
   debugging, but the default flow could be unified.

## 5. r15 — what shipped

### Phase 3 — Status text isolation

Split `ServerTestProgressText` into per-tab fields:

- `ServerTestProgressText` (Manual VLESS, Servers tab)
- `SubscriptionTestProgressText` (Subscribe tab) — NEW
- `ServerDeepProgressText` (Manual VLESS, Servers tab)
- `SubscriptionDeepProgressText` (Subscribe tab) — NEW

`TestServerCollectionAsync` and `DeepVerifyCollectionAsync` now take an
`Action<string> setProgress` parameter. Each call site passes its own
field setter. SubscribePage XAML rebound to the new fields.

Result: tab status text is isolated. Test all on Servers no longer
leaves stale text on Subscribe.

### Phase 4 — Active-VPN warning suffix

When `>= 50%` of tested servers came back Implausible, the "done" line
suffixes with:

«⚠ Активный VPN перехватывает — отключите для реальных результатов или
Глубокая проверка» / «⚠ Active VPN intercepting — disconnect for real
results or use Deep verify».

Surfaces the gotcha that Implausible status was hiding behind the
"Pinged 7/7" framing.

### Phase 1 — TcpTlsProbe per-call timeout overload + FreeConfigTester delegate

Added per-call `tcpTimeout` / `tlsTimeout` parameters to
`TcpTlsProbe.ProbeAsync` (and helper `ProbeTcpAsync` / `ProbeTlsAsync`
overloads). Pre-r15 the only way to override was the static property,
which would interfere with concurrent test flows.

Refactored `FreeConfigTester.TestOneAsync` from ~80 LOC of inline
TCP/TLS/plausibility logic to a single delegation call into
`TcpTlsProbe.ProbeAsync` with FreeConfigTester's bulk-test timeouts
(1.5 s TCP, 3 s TLS), then maps the immutable `ServerProbeResult` back
to the in-place `FreeConfigEntry` mutation pattern. `TcpPingOnlyAsync`
also delegates to `TcpTlsProbe.ProbeTcpAsync`.

Result: ~150 LOC removed from FreeConfigTester. Single source of
truth for TCP+TLS+plausibility. CertNameMatches duplicate removed from
FreeConfigTester (now lives only in TcpTlsProbe).

## 6. r16+ — proposed unified-Verify CTA (Phase 5)

**NOT shipped in r15** — needs design discussion + user buy-in.

### Idea

Replace the two-button (Test all / Deep verify) model on Servers /
Subscribe tabs with a single state-aware "Verify" CTA matching
Public's flow:

1. Click "Verify" → kicks off TCP+TLS pass (fast, all servers in
   parallel, ~3 s wall clock for 10 servers).
2. As entries pass TCP+TLS, queue them for deep-verify on the same
   button click. Deep-verify spawns sing-box + HTTP probe (slow,
   bounded concurrency 5).
3. Per-row status indicator shows the stage (testing → passed-tcp →
   verifying → verified / failed).
4. The button label flips to "Stop" while the pipeline runs.

### Power-user escape hatches

Keep the existing `TestAllServers` and `DeepVerifyAllServers` commands
as keyboard-accessible-only or behind a context menu — for power users
debugging individual stages (e.g. "I want to know if it's a TLS issue
specifically"). The default UI surface becomes the single Verify CTA.

### Benefits

- **One-click flow** matches user mental model: «проверь работают ли
  серверы». No surprise that "Test all" doesn't actually verify the
  VLESS proxy carries traffic.
- **Solves the misleading "Pinged 7/7" problem**: deep-verify
  auto-runs after TCP+TLS, so Implausible-but-counted-as-passed is no
  longer the user's last data point.
- **Aligns Servers/Subscribe with Public's UX** that we already know
  users like.

### Risks

- Behaviour change. Existing power-users may have muscle memory for
  the two-button flow. Mitigation: keep escape hatches; provide
  release notes flagging the change.
- Deep-verify takes 5–10 s per server. On 100-server pools this is a
  real wall-clock cost. Public's flow handles this by bounding concurrency
  + early-stop on N-working — Servers/Subscribe would need similar
  early-stop semantics.
- Need to consolidate VlessDeepVerifier ↔ FreeConfigDeepVerifier
  first (Phase 2) so the single-CTA flow can share the
  pipeline implementation.

### Decision

Implement only after:

a) Phase 2 dedup lands (single deep-verifier class).
b) User explicitly asks for the unified flow OR a usability test
   confirms the two-button model is confusing.

## 7. r16+ — proposed Phase 2 (deep-verifier dedup)

Approach: keep `VlessDeepVerifier` as the canonical impl. Convert
`FreeConfigDeepVerifier.VerifyOneAsync` to an internal shim that:

1. Maps `FreeConfigEntry → VlessServerEntry` (small projection — fields
   are mostly the same, plus a few FreeConfig-only fields like
   `Verified` / `LastVerifyFailedAt` that don't matter for the spawn).
2. Calls `VlessDeepVerifier.VerifyAsync(entry, measureBandwidth: this.MeasureBandwidth, ct)`.
3. Maps `DeepVerifyResult → FreeConfigStatus` mutation (Verified on
   success; existing Status preserved on failure per the in-place
   mutation pattern).

This removes ~300 LOC of duplicated sing-box spawn + SOCKS HTTP probe
+ bandwidth measurement code. `BuildSingleOutboundConfig`,
`ProbeViaSocksAsync`, `MeasureBandwidthViaSocksAsync`,
`WaitForPortBoundAsync`, `FindFreePort`, `IsPrivateOrLoopback` would
all live only in VlessDeepVerifier.

Risk: `FreeConfigDeepVerifier` mutates entries in place (sets
`Status = FreeConfigStatus.Verified` etc.) while VlessDeepVerifier
returns immutable `DeepVerifyResult`. The mapping shim has to faithfully
preserve FreeConfig's per-status mutation semantics — testing this
end-to-end requires the FreeConfigsAggregator integration tests, which
already cover most paths.

## 8. r17+ — enum unification proposal

`ServerProbeStatus` and `FreeConfigStatus` have semantic 1-to-1 mapping
for the 7 buckets that TcpTlsProbe produces. Plus FreeConfig adds
`Verified` (post deep-verify) which is orthogonal.

Approach:

a) Promote `ServerProbeStatus` to be the canonical TCP+TLS-stage enum.
b) Convert `FreeConfigStatus` to wrap it: `FreeConfigStatus.FromProbe(ServerProbeStatus)` +
   FreeConfig-specific extra states (`Verified`).
c) Or: collapse `FreeConfigStatus` into `ServerProbeStatus` + a
   separate `bool Verified` flag.

Lower priority — current dual-enum works and is tested. Cosmetic.

## 9. Test coverage delta

iter#6 r15 doesn't add new dedicated tests for the refactor (the
existing 22 regression tests in `FreeConfigAggregatorPreserveTests` /
`TcpPingOnlyPlausibilityGateTests` / `FreeConfigCacheMigrationTests` /
`VlessServersResolverTests` / `ConfigGeneratorEmptyServersGuardTests`
exercise FreeConfigTester end-to-end).

Future test gap to close:

- Direct test that `FreeConfigTester.TestOneAsync` produces correct
  `FreeConfigStatus` from each `ServerProbeStatus` input (mock
  TcpTlsProbe? Or use a known-loopback target?).

## 10. Carried-forward backlog

- Phase 2: `FreeConfigDeepVerifier` → `VlessDeepVerifier` dedup
  (~300 LOC).
- Phase 5: unified Verify CTA on Servers/Subscribe.
- Enum unification (cosmetic).
- Direct unit tests for FreeConfigTester→TcpTlsProbe mapping.

## 11. Cross-references

- iter#4 audit: `plans/vpnrouter-iter4-code-review.md`
- iter#5 wrap (r9-r14): `plans/release-notes-v2.31.6-r{9..14}.md`
- iter#6 r15 ship: `plans/release-notes-v2.31.6-r15.md`
- Verifier source files: `VPNRouter.Core/Services/TcpTlsProbe.cs`,
  `VlessDeepVerifier.cs`, `FreeConfigs/FreeConfigTester.cs`,
  `FreeConfigs/FreeConfigDeepVerifier.cs`.
