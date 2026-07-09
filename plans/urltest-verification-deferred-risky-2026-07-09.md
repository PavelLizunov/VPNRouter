# Deferred / risky sub-tasks — urltest verification (do NOT auto-run)

These are the parts of `plans/urltest-verification-plan-2026-07-09.md` that Fable is
NOT executing autonomously because they are behaviour-changing, touch a wide public
surface, do network I/O, or can leak user intent. They need explicit approval / a
review pass / a more-cautious model / live windows-brat verification. Fable keeps doing
the safe, additive, pure+tested work; each item here stays a written spec until someone
signs off.

## R1 — Add an explicit failure-phase enum to `DeepVerifyResult` — **DONE 2026-07-09**
- Landed on main (post-v2.46.0): `DeepVerifyFailurePhase { None, Precondition, LocalSpawn,
  SocksBind, ProxiedHttp, Timeout, Cancelled, UnsupportedByVerifier }` as an optional
  record field (default None — full back-compat); every `VlessDeepVerifier` failure site
  typed; mapper reads the typed phase first, string heuristic only for legacy None.
- The feared blast radius did not materialize: consumers were 3 product files (not ~15),
  `FreeConfigDeepVerifier` has its own enum (untouched), characterization hash unmoved.
- BONUS (same unit): AWG/XHTTP deep-verify PARITY — real verification when the core
  carries with_awg/with_xhttp (AWG endpoint config via the shipped ConfigGenerator
  builder; xhttp transport mirrored + Vision-flow drop), typed UnsupportedByVerifier
  when it doesn't. Closed the OPEN-DEFECTS P2.

## R2 — Wire the classifier into the live pipeline + UI — **DONE + LIVE-VERIFIED 2026-07-09**
- Landed (commit 2d87d244): ServerViewModel folds quick+deep phases through
  mapper->classifier into one verdict line on Servers/Subscribe rows + rich tooltip
  (RU-block DPI/TSPU copy for ProtocolHandshakeBlockedLikely, canary copy for
  OnlyControlWorks, raw errors preserved); deep '✗' now only for mapper-confirmed
  server failures, local/unsupported render '!' (IsDeepInconclusive); deep phases
  re-merge on later quick probes. 8 VM tests; snapshots+visual-diff green;
  characterization unmoved.
- LIVE GATE PASS on windows-brat (invisible WinRM+UIA harness, shots r2c-01..05):
  after quick probe every reachable row read "ТСР открыт, VPN-протокол не проверен"
  (never "works" from ping alone); after deep verify 21/21 the working VLESS/HY2 rows
  flipped to "Работает через VPN", an inconclusive one honestly KEPT the untested
  verdict, naive (probe not applicable) showed no false line.
- Verdict-chips beyond the line (Host/Protocol/HTTP/ASN split) + ASN chip wait on R3.
- INCIDENT captured during the gate (fixed): overlaying ONLY App+Core binaries on the
  brat install tripped InstallHealthCheck (mixed VPNRouter.* SHAs) -> no app.bak ->
  SelfRepair (web install.ps1) gutted app/ when interrupted. Deploy rule: always push
  a SHA-consistent App+Core+Service+CLI set. See OPEN-DEFECTS P2 (SelfRepair gutting).

## R3 — ASN / provider metadata — **DONE 2026-07-09 (offline PREFIX grouping, zero network)**
- Implemented WITHOUT the risky parts: `AnalyzeProviderRisk` needs a GROUPING key, not a
  registry ASN — `ProviderKey` groups by IP prefix (/24 v4 — hoster allocation
  granularity, /48 v6), fully offline: no mmdb bundle/licensing, no reader dependency,
  no API calls. NOTE: `FreeConfigGeoIp` turned out to be ip-api.com over plain HTTP
  (the "MaxMind" note in Core CLAUDE.md is stale) — acceptable for PUBLIC free configs,
  forbidden for private subscription IPs, which cemented the offline choice. The only
  lookup is one OS-resolver DNS query for hostname entries (same lookup the probes
  already make); literal IPs resolve inline with zero I/O.
- Wired: `ServerHealthRecordDto.ProviderKey` (additive, keyless-overwrite preserves it);
  ServerViewModel records the key (background DNS for hostnames); ConfigGenerator's
  Auto-pool now ALSO drops untested siblings of a HighRisk subnet (>=2 blocked-likely +
  another subnet Healthy — `AnalyzeProviderRisk`), same fail-open >=1 rail;
  `RefreshProviderRiskFlags` (mirrors RefreshUdpSiblingFlags, 5 call sites + post-deep
  batch) sets `IsProviderHighRisk` -> tooltip explains "Подсеть хостера под риском
  блокировки... Авто-выбор исключает её". 13 new tests.
- Future upgrade path (still deferred): real GeoLite2-ASN mmdb (needs MaxMind.Db reader
  + a bundled/downloaded DB) would only replace the ProviderKey producer — every
  consumer takes opaque string keys.

## R4 — Blocked-target canary probes — **DONE 2026-07-09 (via-VPN only BY CONSTRUCTION)**
- The safety model is structural, not a toggle: canaries are probed ONLY through the
  spawned sing-box SOCKS of a deep verify — the ISP sees nothing but the tunnel; no
  direct-from-client probing exists anywhere. `CanaryTargets`: lightweight built-ins
  (YouTube generate_204, Discord gateway JSON) + user-override
  `cache/canary_targets.json` (updateable without a release; corrupt file falls back);
  45d review-TTL degrades stale targets to ambiguous via `CanaryPolicy.IsStale`.
- `VlessDeepVerifier` runs the stage after the control probe passes (4s per-target cap,
  parallel, skipped=Unknown when the 12s budget is drained — our own timeout never
  condemns); pass = ANY http response (bytes flowed through the blocked host), fail =
  timeout/reset. Redacted logs (scheme+host). Result rides `DeepVerifyResult.BlockedCanary`
  (additive) -> mapper -> classifier: control Pass + canary Fail = OnlyControlWorks ->
  the R2 verdict line + canary warning copy light up with ZERO new UI work.
- 7 new tests (targets/override/corrupt, mapper OnlyControlWorks + back-compat, VM e2e).
- Live RU-ISP verification rides the next -rN ship (brat sits behind a real RU ISP).

## R5 — Auto/urltest ranking + selection behaviour change — **DONE 2026-07-09**
- Landed: new `ServerHealthStore` (cache/server_health.json, identity =
  server:port:protocol so verdicts survive subscription refreshes, 12h freshness TTL,
  atomic save, corrupt-file graceful) written best-effort by ServerViewModel on every
  probe; `ConfigGenerator` drops Auto-pool members with a FRESH
  ProtocolHandshakeBlockedLikely verdict — ONLY in AutoSelect mode (a manual choice is
  never overridden), fail-open ≥1 member (all-blocked keeps the full pool), stale
  verdicts never exclude. Wording: "Авто-выбор по быстрому веб-тесту" + tip states
  generate_204 is a quick web test, not protocol verification, and blocked servers are
  excluded (shared Core string — desktop + Android). Selected member was already shown
  (v2.44.1-r6); added verdict AGE to the health tooltip; folded the perf-hunt F3 P2
  (GetGroupNow every 3rd tick). 13 new tests incl. the audit's wording pin.
- ASN diversity / ProviderSubnetHighRisk penalty intentionally NOT wired — needs R3
  (ASN metadata) first; `ServerRankingScorer` is ready for it.

## R6 — Release — **SHIPPED 2026-07-09 (v2.46.1-r2 in-flight; stable cut = user command)**
- v2.46.1-r1 (c53badbc) shipped on the user's "ship"; its post-ship live gate on
  windows-brat (real release ZIP, real RU ISP) proved the chain end-to-end: Iceland AWG
  deep-verified through the lx core (R1 parity), canary Pass/Fail differentiated live
  (Germany AWG = a real OnlyControlWorks in the wild), SOCKS-bind failures honestly read
  "untested". The same gate CAUGHT a real bug: the optional bandwidth probe draining the
  12s budget discarded an already-passed HTTP+canary result (working-but-slow server
  false-failed) -> fixed in r2 (f386958c): post-control-HTTP steps are best-effort
  enrichment, their timeout never downgrades the pass.
- v2.46.1-r2: 14 desktop assets, Mac/Linux CI green, commit CI 0 failures, r1 deleted
  per rolling policy, Latest = v2.46.0. r2 live re-verify on brat = final gate.
- Stable cut of v2.46.1 stays USER-GATED ("cut" / "ok" / "promote"), incl. the
  mandatory live-update gate (cut-stable skill 6.5) before asking.
