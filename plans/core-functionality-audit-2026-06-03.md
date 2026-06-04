# Core-functionality audit plan (2026-06-03)

## Why this exists

The two adversarial sweeps we ran (v2.40 diff, then the r8 fix diff) were
**diff-scoped** — they catch regressions in *recent changes*. They do NOT probe
**latent bugs in stable core code** that hasn't changed (it's in no recent diff).

The recent finds clustered in **DNS classification + fail-closed logic +
lifecycle**, and we only deep-audited the **custom-config** path. The default,
most-used **generated / subscribe** path has never had the same adversarial
eyes — high chance the same bug classes (leak mis-classification, circular DNS
bootstrap, missing-outbound silent leak) lurk there too.

This plan re-verifies the **core value chain** independent of any update:
*route selected app traffic through the proxy, everything else direct, never
leak.* If a core invariant can be broken, a user leaks — regardless of which
release they're on.

## Method — invariant-driven (not freeform)

For each core subsystem: define the **invariants that must ALWAYS hold**, then
adversarially try to BREAK each across an input matrix. Three verification
layers, strongest first:

1. **Property / matrix tests** — synthesize an input matrix
   (servers × dns_mode × routing_mode × apps_mode × protocol × edge cases),
   generate the artifact, assert every invariant + run real `sing-box check`.
   Turns "review" into "proven across N inputs". Highest leverage; becomes
   permanent regression coverage.
2. **Adversarial workflow** — fan out reviewers, one per invariant, each tasked
   to CONSTRUCT an input that violates it; skeptical 2-lens verify; live-repro
   the survivors (compile against Core.dll + sing-box check), as we did for the
   r8 #1/#2 finds.
3. **Ground-truth device / MCP** — the real user flow end-to-end. Per the
   double-VPN test env (MEMORY: egress-IP unreliable) verify via **per-layer
   `current.json` + `singbox.log`**, plus `mcp__vpnrouter-test__*` for desktop
   UI flows and the A101BM for Android.

## Subsystems + invariants (prioritized by leak/safety risk)

### Phase A — Generated config pipeline  [HIGHEST: default path, never deep-audited]
`ConfigGenerator` + `VlessServersResolver` + DNS/route emission.
- A1: any valid (servers, profile, dns_mode, routing_mode, protocol) input →
  generated config passes `sing-box check`.
- A2: `route.final` = direct in split, proxy in full — never the leaky inverse.
- A3: every routed process_name has a DNS rule → a proxy-detour resolver
  (no per-app DNS leak).
- A4: every route rule's `outbound` tag EXISTS in `outbounds[]`
  (the v2.28.1 silent-leak class).
- A5: process_name preserved-case everywhere (never ToLowerInvariant).
- A6: empty-servers hard guard throws (never a proxy-less config).
- A7: every VLESS outbound has flow=xtls-rprx-vision + reality pubkey/shortid/sni.
- A8: `default_domain_resolver` always resolves LOCALLY — no circular bootstrap
  (the exact class as custom #2; check the generated path has no twin).
- A9: smart / vpn_only / direct dns_mode each emit the documented server set.

### Phase B — Routing / split-tunnel
`ProcessScanner` + include/exclude + wildcard/child resolution.
- B1: ProcessScanner-resolved names == what ConfigGenerator emits (case-exact).
- B2: include routes ONLY listed apps; exclude routes everything EXCEPT listed.
- B3: wildcard / child-process resolution neither over- nor under-matches.
- B4: an app routed by one group survives scrub when another routes it (Core side).

### Phase C — VPN lifecycle
`VpnEngine` + `SingBoxManager` + `HealthMonitor`.
- C1: Start → Connected → Stop leaves NO orphaned sing-box / TUN adapter /
  firewall rule / TUN lock.
- C2: intentional stop never fires false-crash; genuine crash always recovers.
- C3: HealthMonitor recovers a real crash (backoff) + stops after intentional stop.
- C4: hot-reload vs restart-fallback both end in a correct running state.
- C5: concurrent Stop()/Restart() never double-teardown or leak the TUN lock.

### Phase D — Leak protection / kill-switch  [safety-critical]
`LeakProtection` + `FirewallManager` + DNS hardening.
- D1: `ValidateConfig` REJECTS every leaky config (DNS, missing proxy,
  route.final inversion) — across the same matrix as Phase A.
- D2: kill-switch blocks all traffic when VPN is down (block_on_vpn_fail) +
  cleans up on shutdown; localized-netsh parser safe.
- D3: no IPv6 leak (ipv4_only / route_exclude).
- D4: the fail-closed paths hold in BOTH generated + custom across the matrix.

### Phase E — Subscription / server resolution
`SubscriptionFetcher` + `VlessUriParser` + resolve-before-generate.
- E1: all 3 body formats parse + dedup correctly; unsupported schemes filtered.
- E2: VlessUriParser round-trips every URI shape (pbk/sid/sni/flow) uncorrupted.
- E3: subscribe mode ALWAYS resolves servers before generate (v2.28.2 invariant).
- E4: a subscription returning 0 servers keeps cached servers (no wipe).

### Phase F — Free configs pipeline
`FreeConfigAggregator` + tester + deep verifier + cache.
- F1: Verified-only Connect/Apply gates hold (no connecting to unverified).
- F2: cache merge preserves verified + recent-Ok entries.
- F3: aggregator survives malformed / truncated / huge / duplicate-heavy pools
  without crash or unbounded queue/memory growth.

### Phase G — Cross-platform
- G1: Android (libbox path) config semantics match desktop — no Android-only leak.
- G2: Linux/macOS kill-switch — currently a KNOWN GAP (task #131); confirm + plan.

## Execution order + cost

Run **one phase per workflow**, highest-risk first, read results before the next
(stay in the loop). Each phase = 1 adversarial workflow (~5-9 agents) + a
property-test file + (where applicable) a device/MCP ground-truth pass.

Recommended order: **A → D → B → C → E → F → G**
(generated leak surface + validator first, since that's the default path and the
highest-leverage place a latent leak hides; lifecycle + subscription next; free
configs + cross-platform last).

Rough cost: ~1 workflow + 1 test file per phase. A is the big one (the matrix);
the rest are smaller. Fully incremental — stop after any phase.

## Output of each phase
- A property-test file (permanent regression coverage for that subsystem's
  invariants) committed to `VPNRouter.Tests/`.
- A findings list (confirmed real+impactful) → fixed before the next phase or
  triaged.
- A short PASS/FINDINGS note appended here.

## Not in scope
- UI polish / layout (covered by visual-diff + MCP page walks).
- Performance (separate measurement-first track,
  `bug-responsiveness-memory-audit-targets-2026-06-02.md`).

## Recommended start
**Phase A** — it's the default path, never deep-audited, and the most likely
home of a latent leak of the exact class we just kept finding in custom.

---

## Results log

### Phase A — DONE (r9, 2026-06-03)
8 latent generated-config invariant fixes (Reality pubkey/short_id validation,
SS2022 multi-key validation, Privacy_Shell DNS leak, smart-mode info warning).
Shipped in v2.40.0-r9. +CoreAuditPhaseATests.

### Phase D — DONE (r10, 2026-06-03)
All 7 leak/kill-switch findings fixed (#1 kill-switch fail-open re-resolve HIGH,
#5 custom IPv6 closure HIGH, #7 ConfigPipeline Advisory-fatal, #3 deferred
kill-switch lift [Clash-API-gated after re-sweep], #6 IPv6 2000::/3 DNS blocks,
#4 CLI/Service orphan sweep, #2 ProcessExit sweep). Shipped r10. **#3 + #6
LIVE-verified** on a real subscription tunnel (crash-restart deferred-lift fired
+7s with clashApi=true; IPv6 blocks coexist with live tunnel DNS/HTTP).

### Phase B — DONE (2026-06-03)
Adversarial sweep (3 agents) + property tests. **B1 (case-exactness), B2
(include/exclude correctness), B4 (survivor-guard) HOLD** — verified by tracing
the code; pinned by existing suites (ConfigGenerator/RoutingAppListEditor/
LeakAuditFix). **B3 (wildcard/child): 5 findings.**
- **B3-1 HIGH — FIXED (commit 20fdfd5)**: BuildPatternRegex had no matchTimeout
  → catastrophic-backtracking scan_pattern (~8.5s) wedges the ETW/rescan hot
  paths → intended apps leak by starvation. Added 250ms ceiling + fail-safe
  catch (Windows + macOS scanners). +4 CoreAuditPhaseBTests.
- **B3-2 / B3-3 (default profile over-match) — TRIAGE, not fixed**: `firefox.exe`
  in the Tor rule + `browser.exe` in the Yandex rule (`profiles/default.json` /
  ProfileManager built-ins) over-match the user's unrelated Firefox / any
  `browser.exe`. This over-ROUTES through the proxy (not a direct leak) and is a
  fundamental limit of name-based matching for Tor/Yandex. Decision needed: trim
  the patterns (Tor/Yandex routing via process_name becomes incomplete) vs
  accept + document.
- **B3-4 MED — latent**: `include_children` PID-reuse over-include + post-snapshot
  under-include + global name-routing (intrinsic to sing-box process_name). Also:
  `include_children` is a no-op on Linux (`CollectDescendantNames` is Windows-only)
  → children of routed apps leak direct on Linux.
- **B3-5 MED — latent**: a `*` / all-wildcard pattern routes every running
  process; no pattern validation. (matchTimeout from B3-1 does NOT cover this —
  `*` matches fast, just over-broadly. Recommend a guard rejecting all-wildcard.)

Phase B B4 MED items (RoutingAppListEditor guard naming/abstraction-drift in
exclude mode + shell-verb dead-in-exclude UX) — hardening, no live leak; triage.

### Pending phases
C (lifecycle) → E (subscription) → F (free-configs) → G (cross-platform; G2 =
known Linux/macOS kill-switch gap, task #131; G also: B3-4 Linux include_children
no-op).
