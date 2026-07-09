# VPNRouter Audit Vector Map - 2026-07-09 Batch 1

Status: Active handoff
Date: 2026-07-09
Scope: broad vector map before deeper iterative audits
Repository head observed: a2deeea48e645379476d84eb37792625c3d4c46e / v2.46.0-r36
Target consumers: Codex, Claude Code, future VPNRouter audit chats

> Source: Google Drive "Roadmaps" (doc id 1Th_mhg9U2_b8ce_qoW4yhzDDhle0bAcKKRyakW2VXNA), imported + cleaned 2026-07-09.

## Purpose

This document consolidates the next high-value audit vectors for VPNRouter. It is not a release note. It is a working implementation and verification map.

The user goal is to keep moving through several autonomous audit batches, saving important findings to Drive, then use these findings as implementation context for Codex and Claude Code.

## Method

Batch 1 was repository-first:
- read current README / feature catalog;
- checked recent commits;
- checked OPEN-DEFECTS.md;
- followed the current lifecycle / startup / apply / firewall / Android version / urltest code paths;
- compared current source with older planning notes and corrected stale assumptions where source/commit history proved otherwise.

Evidence categories below:
- Confirmed from source: verified directly in current files.
- Confirmed from commit history: verified through recent commits.
- Inferred: high-confidence consequence of current code shape.
- Hypothesis: needs live/device/repro verification.

---

## Executive priority map

### P0 / P1 candidates for the next batches

1. **ApplyAsync lifecycle gate**
   - Severity: P1, possible P0 under race.
   - Confirmed from source: `VpnEngine.StartAsync`, `Stop`, and failover restart are serialized by `_lifecycleGate`, but `ApplyAsync` is not gated.
   - Risk: Apply can race Stop or failover restart, reload/restart a stale or disposed `_singBox`, or re-engage true-split after Disconnect.
   - Primary files: `VPNRouter.Core/Services/VpnEngine.cs`; tests under `VPNRouter.Tests`.
   - Codex task: add lifecycle gate coverage to ApplyAsync or factor a gated public wrapper; re-check session cancellation inside the gate.

2. **TwoPhaseStartCoordinator false-connected / ambiguous success gate**
   - Severity: P1.
   - Confirmed from source: Phase B races `Connected`, `startTask`, and timeout. If `startTask` completes after `SingBoxStarted` but before typed `Connected`, outcome can be `StartTaskCompleted` rather than `PhaseBTimeout`.
   - Risk: UI can treat a started sing-box process as an acceptable terminal state even if TUN warmup never confirmed.
   - Primary files: `VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs`; connect flow in `MainWindowViewModel`.
   - Codex task: in Phase B, successful `startTask` completion must not short-circuit the wait for typed `Connected`; only fault/cancel should escape early.

3. **URL-test / Auto config selection trust boundary**
   - Severity: P0/P1 UX reliability.
   - Confirmed from source: AutoSelect emits sing-box `urltest` with `http://www.gstatic.com/generate_204`, `interval=3m`, `tolerance=150`, and `interrupt_exist_connections=false`.
   - Risk: Auto proves only one generic HTTP URL probe, not Roblox, Discord Voice, SSH, game UDP, IP reputation, target-specific ports, WebSocket stability, or server-side bans.
   - Primary files: `VPNRouter.Core/Models/VlessConfig.cs`; `VPNRouter.Core/Services/ConfigGenerator.cs`; `VPNRouter.App/Views/Pages/SubscribePage.axaml`; `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs`; `VPNRouter.Android/AndroidConfigBuilder.cs`; `VPNRouter.Android/AndroidApp.SubscribePage.cs`.
   - Codex task: rename/tooltip Auto as quick web/URL selector, not full verification; expose active urltest member and verification age; add app-profile mismatch diagnostics.

4. **OPEN-DEFECTS ledger drift / cut gate integrity**
   - Severity: P1 process/release safety.
   - Confirmed from source: `tools/check-open-p0.ps1` blocks on every unresolved `- [ ]` line in `## Open`, but current `OPEN-DEFECTS.md` still contains old P0/P1 entries that commit history says were fixed in v2.44.3.
   - Risk: release gate can be noisy/stale, encouraging waivers and reducing trust in the gate.
   - Primary files: `plans/OPEN-DEFECTS.md`; `tools/check-open-p0.ps1`.
   - Codex task: reconcile ledger against current commits, mark resolved entries with exact version/commit, keep genuinely open P0/P1 only.

5. **Android versionCode for rolling -rN releases**
   - Severity: P1 release/update blocker.
   - Confirmed from source: Android csproj strips prerelease suffix into `_VpnVerCore` before computing `ApplicationVersion`, so `2.46.0-r35` and `2.46.0-r36` share the same Android versionCode.
   - Risk: updater can correctly discover a newer rolling SemVer, but Android PackageInstaller rejects same-or-lower versionCode; app-side install dispatch cannot observe final failure.
   - Primary files: `VPNRouter.Android/VPNRouter.Android.csproj`; `build-android.ps1`; `.github/workflows/sign-android.yml`; `.github/workflows/verify-release-integrity.yml`.
   - Codex task: introduce monotonic Android version code source or encode `rN` safely; add metadata guard with `aapt2 dump badging`.

6. **Custom config AWG/XHTTP gate bypass**
   - Severity: P1 for power users / official upstream builds.
   - Confirmed from OPEN-DEFECTS and source shape: parser/config-gen gates AWG/XHTTP through `SingBoxFeatures`, but custom raw JSON is injected/migrated by `CustomConfigInjector` and may carry top-level `endpoints` wireguard blocks or xhttp transport into an official sing-box build.
   - Risk: official build FATALs on fork-only config constructs; user sees opaque custom-config startup failure.
   - Primary files: `VPNRouter.Core/Services/CustomConfigInjector.cs`; `VPNRouter.Core/Services/SingBoxFeatures.cs`; `VPNRouter.Core/Services/LeakProtection.cs`.
   - Codex task: add custom-config capability gate for fork-only endpoint/transport shapes, with explicit unsupported error instead of FATAL at sing-box start.

7. **macOS/Linux kill switch full-tunnel intent ambiguity**
   - Severity: P1 if split scan failure plus block-on-fail intersects.
   - Confirmed from source: Linux/macOS full-tunnel kill switch uses empty process list as the full-tunnel signal; StartupPipeline process scan timeout continues with empty list.
   - Risk: split tunnel with process scan timeout can look like full-tunnel to the Unix firewall manager and arm a global kill switch.
   - Primary files: `VPNRouter.Core/Services/StartupPipeline.cs`; `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`; `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`.
   - Codex task: pass explicit routing intent to firewall managers; empty process list must not mean global kill switch unless routing mode is confirmed full.

8. **Android per-app package filtering e2e gate**
   - Severity: P1 coverage / Android production readiness.
   - Confirmed from source: Android deliberately uses `VpnService.Builder` package filters, not sing-box `process_name` rules. The shared config itself cannot prove include/exclude package routing.
   - Risk: package filter mistakes are invisible to Core route tests.
   - Primary files: `VPNRouter.Android/VpnRouterService.java`; `VPNRouter.Android/AndroidApp.PerAppFilter.cs`; `tools/android-e2e-test.sh`.
   - Codex task: add logs and e2e test modes for include, exclude, Always-on restore, and self-package sanitation.

9. **Android LAN/local route invariant after libbox TunOptions translation**
   - Severity: P0/P1 verification gate.
   - Confirmed from prior source audit: Core config emits local/private excludes; Android Java layer applies route prefixes from libbox `TunOptions`. The final Android route table must be live-proven.
   - Risk: full tunnel can capture router/NAS/local resources if libbox route translation does not preserve excludes as Android expects.
   - Primary files: `VPNRouter.Android/VpnRouterService.java`; `VPNRouter.Android/AndroidConfigBuilder.cs`; `tools/android-e2e-test.sh`.
   - Codex task: log TunOptions routes in openTun and add LAN probe checks: `ip route get $LAN_IP`, ping/curl LAN host, fail if route uses `tun0`.

10. **AWG / real-time games / DNS leak cluster**
    - Severity: P1/P2 depending on active transport and user scenario.
    - Confirmed from OPEN-DEFECTS: SDR/Dota root cause moved toward AWG single-socket ENOBUFS + MTU; separate AWG DNS leak remains open; DeepVerify AWG/XHTTP parity remains open.
    - Risk: generic VPN connectivity passes while games/voice fail; DNS leak can persist in AWG full tunnel unless lock-down/hijack path is proven.
    - Primary files: `VPNRouter.Core/Services/ConfigGenerator.cs`; `VPNRouter.Core/Services/VlessDeepVerifier.cs`; `VPNRouter.Core/Services/LeakProtection.cs`; `tools/build-singbox-lx.ps1`.
    - Codex task: treat AWG/games/DNS as separate transport-profile diagnostics, not generic connectivity; add unsupported/degraded DeepVerify status for AWG/XHTTP until parity implemented.

11. **AOT-safe JsonNode serialization and Android config post-processing**
    - Severity: P1 hardening.
    - Confirmed from previous audit notes, not revalidated in this batch with source search beyond the absence of an `AndroidConfigPostProcessor` commit hit.
    - Risk: Android config JSON mutation can fail under AOT/source-gen settings and silently return unpatched JSON.
    - Primary files: `VPNRouter.Android/AndroidConfigBuilder.cs`; `VPNRouter.Core/Services/AndroidDpiBypassInjector.cs`; `VPNRouter.Core/Services/DiagnosticsRedactor.cs`.
    - Codex task: extract shared Core helper `AndroidConfigPostProcessor`, use source-generation-aware JsonNode output options, add Core tests.

---

## Corrected assumptions from Batch 1

### Corrected: Android dns-tunnel runtime gate appears implemented

Earlier audit notes suspected Android might not flip `ServerUriParser.SlipstreamRuntimeAvailable`. Commit history and current source show this was implemented: `AndroidApp.axaml.cs::OnFrameworkInitializationCompleted` calls `JavaSystem.LoadLibrary("slipstream_jni")` and sets `ServerUriParser.SlipstreamRuntimeAvailable` true/false before `ReloadServerList()`.

Action: do not keep this as a source-level blocker. Keep only device verification (paste real dns-tunnel link; confirm accepted on supported ABI; connect and verify service logs show Slipstream starts before libbox).

### Corrected: failover P0/P1 items in old plan were fixed in code/commits

Recent commits indicate the self-cancelling failover restart, ResetCycle, and subscription-leak persist were fixed around v2.44.3. Current `VpnEngine.cs` has lifecycle gate/session token comments and implementation.

Action: the remaining issue is ledger drift and live regression coverage, not the original self-cancel code path as if unfixed.

---

## Suggested next autonomous batches

- **Batch 2: Lifecycle and false-connected states** — `ApplyAsync` gate; `TwoPhaseStartCoordinator` Phase B semantics; Runtime polling vs typed Connected; Free Configs apply/connect path; Stop vs Apply vs failover races. Deliverable: `P1 Lifecycle Gate + TwoPhase Connected Contract`.
- **Batch 3: Firewall / kill-switch / DNS restore cross-platform** — explicit full-tunnel intent for Unix firewall; macOS PF anchor migration follow-up; Windows DNS lockdown and DNS restore gates; empty process scan timeout behavior. Deliverable: `P1 Unix Kill Switch Intent Gate + DNS restore regression tests`.
- **Batch 4: Android production-readiness gates** — versionCode + release metadata; per-app e2e; LAN route invariant; Auto active-member diagnostics. Deliverable: `Android Release + Routing E2E Gates`.
- **Batch 5: Protocol/app diagnostics** — URL-test / Auto trust boundary; DeepVerify profile gaps for AWG/XHTTP/UDP-native; Roblox/Discord/Dota/SSH scenario diagnostics; health states and UI wording. Deliverable: `Protocol-Aware Diagnostics and Auto Trust Boundary`.
- **Batch 6: Custom config / subscription intake security** — custom config fork-feature gates; subscription parser bounds; placeholder/secrets/logging surfaces; clash_api secret and loopback control. Deliverable: `Custom Config and Local API Hardening`.

---

## Decision status

Batch 1 produced a broad prioritized vector map, not final fixes. The strongest next implementation candidates are:
1. ApplyAsync lifecycle gate.
2. TwoPhaseStartCoordinator Connected-only success contract.
3. Unix kill-switch explicit full-tunnel intent.
4. Android rolling versionCode monotonicity.
5. Auto URL-test trust-boundary UX/tests.

---

## External OSS comparison - 2026-07-09

Scope: sing-box, mihomo/Clash, Clash Verge Rev, Hiddify. Purpose: verify planned vectors align with real patterns + open issues in comparable clients.

The common industry pattern: URL/latency tests are cheap health checks and selectors; they are NOT proof that every target app/protocol works. GUI clients layer extra config merging, DNS overrides, TUN setup, service orchestration, update logic on top of the core, and many real issues occur exactly at those boundaries (URL-test selection, TUN vs system proxy, DNS override drift, route/local-network capture, reconnect/autostart, stale/misleading UI state).

### sing-box
- Official URLTest fields: `outbounds`, `url`, `interval`, `tolerance`, `idle_timeout`, `interrupt_exist_connections`. Empty url defaults to `https://www.gstatic.com/generate_204`; empty interval `3m`; empty tolerance `50` ms.
- Docs: https://sing-box.sagernet.org/configuration/outbound/urltest/
- Open issues:
  - `SagerNet/sing-box#4255` (open): urltest batch wait can stall when one relay accepts TCP but never responds to HTTP. https://github.com/SagerNet/sing-box/issues/4255
  - `SagerNet/sing-box#4135` (open): request URLTest support HTTP Client to test UDP/QUIC/HTTP3 or authorized addresses. https://github.com/SagerNet/sing-box/issues/4135
  - `SagerNet/sing-box#4253` (open): request custom latency test URL for Selector API. https://github.com/SagerNet/sing-box/issues/4253
- Decision: keep `urltest` as cheap runtime selector; add UX wording "quick web URL test" not "full verification"; add app/protocol diagnostic layer separately; use bounded HTTP transaction timeout + surface stale/unknown state.

### mihomo / Clash ecosystem
- `url-test` group uses `url`, `interval`, optional `tolerance`, `lazy`; docs use `generate_204` + interval `300`s. `fallback` selects first available node by order on timeout.
- Docs: https://wiki.metacubex.one/en/config/proxy-groups/url-test/ , https://wiki.metacubex.one/en/config/proxy-groups/fallback/
- Ecosystem separates `url-test` latency selection from `fallback` availability selection. VPNRouter uses sing-box `urltest` only; explain whether it does latency auto-select, fallback, or app-level failover.
- Open issues:
  - `MetaCubeX/mihomo#2945` (open): url-test node-selection loophole; tolerance can be ignored when current selected node is first element. https://github.com/MetaCubeX/mihomo/issues/2945
  - `MetaCubeX/mihomo#1862` (open): url test does not apply to provider nodes via `use`; want group-level test URL. https://github.com/MetaCubeX/mihomo/issues/1862
  - `MetaCubeX/mihomo#1819` (open): TUN cannot access WireGuard internal network service despite DNS/routing match. https://github.com/MetaCubeX/mihomo/issues/1819
- Decision: test selected-member stability not just generated JSON; add pool-scope UI (current server, source/subscription/group, test URL, last test age); keep local/LAN route invariants as explicit live gates.

### Clash Verge Rev
- GUI over mihomo/Clash core; runtime config gen/merge, service mode, TUN toggles, DNS overrides, config validation. Same risk as VPNRouter: app can show/validate one config while runtime config differs due to app-layer merge/override.
- Open issues:
  - `clash-verge-rev#7420` (open): custom config forcibly overwritten + possible DNS leak / DNS mismatch between profile, current config, logs. https://github.com/clash-verge-rev/clash-verge-rev/issues/7420
  - `clash-verge-rev#6380` (open): system proxy works, TUN mode loses network after some time without obvious logs. https://github.com/clash-verge-rev/clash-verge-rev/issues/6380
  - `clash-verge-rev#7210` (open): macOS TUN WireGuard endpoint hostname fails to resolve, IP endpoint works. https://github.com/clash-verge-rev/clash-verge-rev/issues/7210
- Decision: add "effective runtime config" diagnostics + redaction; show which sections VPNRouter inserted/overrode; DNS leak diagnostics compare intended vs generated vs runtime logs vs external leak result.

### Hiddify
- Multi-platform GUI over proxy/VPN cores; system proxy vs VPN/TUN, connection test URL, url-test interval, Clash API, per-platform TUN/service.
- Open issues:
  - `hiddify/hiddify-app#2232` (open): Linux TUN root requirement, instability, DNS failures, UI/core race, NetworkManager/tun2proxy proposal. https://github.com/hiddify/hiddify-app/issues/2232
  - `hiddify/hiddify-app#2281` (open): Android random background disconnections, missing auto-reconnect + start-on-boot. https://github.com/hiddify/hiddify-app/issues/2281
  - `hiddify/hiddify-app#1964` (open): Windows works as system proxy but not VPN service; TUN + DNS timeout/cancel errors. https://github.com/hiddify/hiddify-app/issues/1964
- Decision: treat proxy-mode, TUN warmup, DNS, target-app success as separate states; keep Android reconnect/battery/Always-on gates separate; keep Linux privilege model explicit + diagnosable.

### Android versionCode external check
- Android `versionCode` is the internal integer deciding recency; each release must use a greater value; Play does not allow reuse. Docs: https://developer.android.com/studio/publish/versioning
- Decision: `versionName` may keep `2.46.0-r36`; `versionCode` must monotonically increase for every installable APK; add release metadata verification (`aapt2 dump badging`).

### External comparison conclusions (confirmed design constraints)
1. Do not call URL-test Auto a full server verifier.
2. Add app/protocol diagnostics for target workloads.
3. Distinguish system proxy, TUN up, DNS up, selected outbound, and app traffic states.
4. Expose effective runtime config and app-added overrides.
5. Do not rely only on generated JSON tests; add live selected-member and route/DNS/device checks.
6. For Android, every installable release must have monotonic versionCode.
7. For Linux/macOS, do not infer host-wide firewall intent from empty process lists.

---

## RU ASN / hosting subnet protocol-block vector - 2026-07-09 (highest-priority protocol diagnostics)

### Decision
Promote this vector above generic URL-test trust-boundary work. VPNRouter must distinguish:

```
HostReachableByICMP/TCP/SSH != ProxyProtocolWorks
```

A server can be alive as an IP host and still be unusable as a VPN/proxy endpoint from Russia because the censor path blocks a protocol fingerprint, transport handshake, or a hosting subnet/ASN policy path.

### Why it matters
User-reported symptom: host responds to ping; accepts SSH; TCP port may open; but VLESS/Reality/XHTTP/AWG/HY2 cannot complete handshake or carry traffic. This invalidates naive ranking (ping latency, TCP connect, SSH reachability are all insufficient; generic URL-test may be insufficient if it does not propagate handshake failure; a server/provider/subnet can look healthy from outside Russia but fail from a Russian ISP/TSPU path).

### External evidence
1. Russia/RKN targets VPNs via DPI, protocol fingerprinting, IP/range blocking (Amnezia reporting on protocol fingerprinting, rapid IP blocking, targeted server-fingerprint blocking). Treat as external evidence, not source-level proof for one hoster.
2. `XTLS/Xray-core#5908`: TCP established; server log `failed to read client hello`; client TLS handshake error; HTTP GET/HEAD ping/observatory still reports node alive; balancer keeps routing through blocked outbound.
3. `XTLS/Xray-core#5897`: REALITY handshake blocked / ClientHello dropped; node still appears alive to observability.
4. `XTLS/Xray-core#5332`: TCP+Reality blocked on a specific ISP path; server IP/country changes did not trivially solve it.
5. General VPN fingerprinting research: DPI identifies VPN traffic from protocol features, packet sizes, active probing — supports why TCP/SSH liveness coexists with protocol-level VPN failure.

### Current VPNRouter gap
- `TcpTlsProbe` is primarily transport reachability / protocol-aware quick probe.
- VLESS Reality quick probe is intentionally TCP-only, defers correctness to DeepVerify.
- DeepVerify starts sing-box + HTTP through local SOCKS (closer to truth) but is still generic HTTP, not a target-app/protocol matrix.
- Auto/urltest is a cheap runtime selector, not an anti-censorship compatibility proof.

Missing concepts: `ProtocolHandshakeBlocked`, `ProtocolCarriesHttpButNotUdp`, `ProtocolBlockedOnlyFromRussia`, `LikelyRuAsnSubnetProtocolBlock`, `ProviderSubnetHighRisk`.

### Proposed product model
Add a new diagnostics layer: `RuBlockProbe` / `CensorshipProbe`. It should classify observed symptoms, not prove legal/regulatory truth.

Suggested states: `Unknown`, `HostUnreachable`, `TcpOpenOnly`, `TlsOkButProxyFailed`, `ProxyHttpOk`, `ProtocolHandshakeBlockedLikely`, `RuPathOnlyFailureLikely`, `UdpOrAppProfileFailed`, `ProviderSubnetHighRisk`.

Heuristic for `ProtocolHandshakeBlockedLikely`: TCP connect OK + optional TLS/SNI to camouflage domain OK/ambiguous + SSH/host reachable OK + sing-box deep verify through actual outbound FAILS (TLS/clienthello/handshake/no response) + another server/provider on same client/network works + same server from non-RU vantage works.

Heuristic for `ProviderSubnetHighRisk`: several servers in same ASN/provider/prefix fail protocol DeepVerify from RU path + other ASNs/providers work from same client + failure is protocol-specific not host-wide.

### Implementation candidates
1. Extend server test result model — do not collapse all failures into `TlsFailed`/`Timeout`; preserve phase: DNS resolution, TCP connect, TLS/camouflage handshake, proxy handshake, proxied HTTP GET, proxied UDP/QUIC/app profile.
2. Add ASN/provider metadata — resolve server IP; attach ASN/org/country/prefix; cache locally; do not upload subscription URLs/secrets.
3. Add grouped failure analysis — many servers from one ASN/prefix failing at protocol phase (not TCP phase) flags provider/subnet as likely blocked/degraded.
4. Add RU-specific warning copy:
   ```
   Сервер доступен по сети, но VPN-протокол не проходит.
   В России такое бывает при блокировке протокола, IP или подсети хостера через DPI/ТСПУ.
   Ping/SSH в этом случае могут работать, но VLESS/Reality/AWG/HY2 — нет.
   Попробуйте другой хостинг/ASN, страну или транспорт: XHTTP/gRPC/Naive/HY2/AWG 2.0.
   ```
5. Add profile-specific probes — generic HTTP through proxy; HTTPS through proxy; UDP/QUIC test if protocol claims UDP; app profile probes later (Discord voice relay, Roblox WebSocket/UDP, SSH-over-proxy).
6. Update Auto selection ranking — exclude/penalize `ProtocolHandshakeBlockedLikely`; prefer ASN/provider diversity over many servers from one high-risk subnet; show pool composition by provider/ASN.

### UX
Server row chips: `Host: OK`, `Protocol: blocked/failed`, `HTTP via VPN: failed/ok`, `UDP/app: unknown/failed/ok`, `ASN: high-risk? unknown/flagged`. Avoid "Сервер работает" when only ping/TCP/SSH works; prefer "Хост доступен, но VPN-протокол не прошёл проверку".

### Regression tests
1. TCP OK + DeepVerify handshake error => `ProtocolHandshakeBlockedLikely` not `Ok`.
2. TCP OK + SSH OK + proxy HTTP fail => warning: host reachable but VPN protocol failed.
3. Multiple failures in same ASN/prefix => grouped provider/subnet warning.
4. One server fails but other ASN works => do not mark client/network fully broken.
5. Auto selector excludes `ProtocolHandshakeBlockedLikely` from preferred pool.
6. UI string test pins wording: ping/SSH do not prove VPN protocol works.
7. Free-config refresh must not promote TCP-only success to Verified under RU-block mode.

Live gates: test from >=1 Russian ISP/mobile carrier; >=2 hosters/ASNs; VLESS Reality TCP, VLESS XHTTP/gRPC, HY2/TUIC/AWG; compare same server from non-RU vantage; capture server-side logs (`failed to read client hello`, TLS timeout, no inbound bytes).

### Priority adjustment
```
P0: RU protocol/subnet block diagnostics
P1: URL-test trust boundary wording and selected-member diagnostics
P1: Lifecycle/false-connected races
```

---

## Real blocked-target canary probes - 2026-07-09 (required live verification layer)

### Decision
Add a post-connect / DeepVerify extension that tests real target reachability through the actual VPN config. Complements (does not replace) the RU ASN/TSPU vector:

```
Protocol works enough to start sing-box != blocked target is reachable
Blocked target reachable != every app/protocol works
```

### Why YouTube is useful but not enough
YouTube is a strong user-visible canary in Russia but availability differs by ISP/region/app; providers may offer app-level access; browser/app behavior differs; cache/CDN can hide failures; policy changes fast; it may get special treatment vs smaller resources. Use a multi-canary matrix, not a single YouTube result.

### Proposed probe tiers
1. Control canary (proves normal proxied internet): `https://www.gstatic.com/generate_204`, `https://www.cloudflare.com/cdn-cgi/trace`.
2. High-signal popular blocked/degraded canaries (if enabled + legally appropriate): YouTube `generate_204`/lightweight page; Discord bootstrap endpoint; Telegram web bootstrap.
3. Less popular blocked-target canaries: remotely updateable or user-supplied list (not hardcoded). Each canary: domain/url, category, expected direct-from-RU status, expected via-VPN status, last reviewed date, source, risk notes.
4. App-profile canaries (later): Discord voice/UDP, Roblox HTTPS/WebSocket/UDP, game launcher, SSH-over-proxy, QUIC/HTTP3.

### Classification model
States: `ProxyHttpOk`, `ControlCanaryOk`, `BlockedTargetCanaryOk`, `BlockedTargetCanaryFailed`, `OnlyControlWorks`, `LikelyCensorshipBypassFailed`, `TargetSpecificFailure`, `CanaryListStaleOrAmbiguous`. Key distinction: `ControlCanaryOk + BlockedTargetCanaryFailed` = tunnel up but censorship bypass not proven.

### Safe default behavior
Do not run direct blocked-target probes by default if they may leak user intent to the ISP. Prefer via-VPN probes after tunnel up; direct probes only as explicit advanced diagnostics; no subscription secrets/personal URLs in telemetry/logs; redact query strings/path fragments unless a canary needs them.

### UX
When control internet works but blocked canary fails:
```
VPN подключился, но проверка заблокированного сервиса не прошла.
Обычный интернет через VPN работает, но этот сервер/транспорт может не обходить блокировку в вашей сети.
Попробуйте другой сервер, ASN/хостинг или транспорт.
```
For YouTube: "YouTube — полезная проверка, но не абсолютная. В России его доступность может отличаться по провайдерам, регионам, приложениям и временным правилам блокировки."

### Regression tests
1. Control OK + YouTube fail => `OnlyControlWorks`/`LikelyCensorshipBypassFailed`, not `ConnectedOk`.
2. Control OK + two blocked-target OK => stronger `BlockedTargetCanaryOk`.
3. YouTube OK + less popular blocked target fail => partial/ambiguous, not global OK.
4. Blocked-target list item older than TTL => `CanaryListStaleOrAmbiguous`.
5. Direct probe disabled by default; enabling requires explicit advanced action.
6. Logs redact full URLs and never include subscription URL/secrets.
7. Auto ranking penalizes servers that only pass control canary but fail blocked-target canaries.
