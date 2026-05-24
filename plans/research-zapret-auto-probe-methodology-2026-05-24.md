# Zapret strategy auto-probe methodology

**Date**: 2026-05-24
**Status**: Research only, no code changes
**Companion to**:
- `plans/research-one-button-zapret-deep-2026-05-24.md` — UX driver, state machine
- `plans/research-one-button-tgproxy-zapret-2026-05-24.md` — original Zapret friction baseline
- `plans/research-rkn-block-checker-auto-strategy-2026-05-24.md` — block-type classifier rejected as oracle
- `plans/research-zapret2-bolvan-migration-2026-05-24.md` — long-horizon migration

---

## Context

VPNRouter wraps Flowseal's `zapret-discord-youtube` (winws.exe + WinDivert). On startup the user sees a 10-item strategy ComboBox. Which strategy works depends on ISP + DPI vendor + time of day; there is no API to ask «which one works on my line», it's empirical.

`MainWindowViewModel.cs:4203` `ToggleZapretAsync` picks `ZapretStrategies[ZapretStrategyIndex]` verbatim and hands it to `ZapretManager.Start` / `StartFromBat`. Today the "verification" is implicit — the user opens Discord, sees if it works, returns to VPNRouter and picks another strategy if not. We want to replace that with an automatic probe loop.

This document scopes only the **auto-probe and confidence-tier engine** — the machinery that takes a strategy candidate and answers «works / works partially / doesn't work». UI flow is in the sibling one-button research.

---

## 1. Probe types

The probe set has to discriminate **"DPI blocks this site without zapret → zapret unblocks it"** from **"site reachable from anywhere"**. A passing probe must therefore exercise a domain that we have prior evidence is DPI-blocked on the target ISP, plus a control that is never DPI-blocked.

### Probe inventory

| # | Probe | Signal | Target | Failure mode | Deterministic? | Cost |
|---|---|---|---|---|---|---|
| 1 | TCP+TLS handshake to YouTube | TCP RST mid-ClientHello / TLS hang | `www.youtube.com:443` (SNI=`www.youtube.com`) | RST < 200 ms after ClientHello bytes or 3 s timeout | Yes — `youtube.com` is the canonical DPI target in RU; most reliable signal | ~300-1500 ms |
| 2 | TCP+TLS handshake to Discord | TCP RST or TLS reset on `discord.com` | `discord.com:443` (SNI=`discord.com`) | RST / TLS reset / timeout | Yes on most RU ISPs; on some (Beeline-mobile, Megafon-mobile) Discord HTTP is unblocked while voice (UDP 50000-65535) is throttled | ~300-1500 ms |
| 3 | TCP+TLS handshake to GitHub (control) | Should always succeed | `api.github.com:443` | Genuine network drop / DNS / NIC down | No, control by design | ~100-500 ms |
| 4 | HTTP GET YouTube manifest | HTTP 200 + body present | `https://www.youtube.com/yts/jsbin/check` | 4xx/5xx, HTTP 451, censorship stub, body length < 100 | Yes when site reachable; layered on probe #1 | ~500-2000 ms |
| 5 | UDP probe to Discord voice STUN | UDP datagram sent → no ICMP back | `stun.l.google.com:19302` or discord media | ICMP Port Unreachable / always timeout | Optimistic only | ~2000 ms |
| 6 | TCP-RST signature detector | RST within < 100 ms of ClientHello bytes | YouTube/Discord | Differentiates "ISP DPI" from "site genuinely unreachable" | Yes — RST < 100 ms ≈ on-path injector; > 200 ms RST ≈ legitimate server reset | Same as probe #1 |
| 7 | Latency-spike probe | First TLS-byte latency vs control | YouTube vs 1.1.1.1 | YouTube > 5x control = throttle/inject | Heuristic, noisy on mobile | ~600 ms |

### Recommended probe triplet

Three probes per strategy: **#1 (YouTube TCP+TLS) + #2 (Discord TCP+TLS) + #3 (GitHub control)**.

* **#1** is the load-bearing probe. YouTube SNI is the most consistently DPI-blocked target in the Russian SORM/TSPU footprint; if YouTube TLS succeeds, the strategy demonstrably defeats SNI-based DPI on the user's link.

* **#2** distinguishes "video works, voice may not" cases (Tier-2). Useful for confidence ladder, not as a hard gate.

* **#3** rules out "user is offline" false negatives. If GitHub TLS also fails, surface `NoSignal` state and skip rotation.

Probes #4-7 are **opt-in extensions** (Advanced mode), not part of the default triplet.

### Probe budget

Per strategy: 3 probes × max 1.5-2 s each, executed concurrently within one strategy → **~2 s wall clock per strategy**. Plus 2 s for WinDivert bind + 1 s to stop. **5 s per strategy** is the budget.

---

## 2. Strategy verification flow

### Single-strategy verify

```
verify(strategy):
    1. start winws.exe with strategy args      (~50 ms)
    2. wait 2 s for WinDivert bind + warm-up   (2.0 s)
    3. concurrent {                            (<= 2.0 s)
         tlsYouTube  = TcpTlsProbe(youtube.com, 443, sni=youtube.com)
         tlsDiscord  = TcpTlsProbe(discord.com, 443, sni=discord.com)
         tlsControl  = TcpTlsProbe(api.github.com, 443, sni=api.github.com)
       }
    4. tier = classify(tlsYouTube, tlsDiscord, tlsControl)
    5. stop winws.exe                          (<= 1.0 s with handle.Stop)
    6. return (tier, latency map, error map)
```

### Serial vs parallel across strategies

**Parallel is not safe.** Multiple winws.exe processes attached to the same WinDivert kernel filter driver collide because:

1. WinDivert filter handles are owned per-process, but there is **only one driver instance** (`WinDivert64.sys`). Each winws filter rule uses the same `--wf-tcp=` port list and the same layer; two processes both binding `--wf-tcp=443` race on the same packet, and the kernel delivers each packet to exactly one of them by handle priority.

2. `ZapretUpdater.StopWinDivertService` (lines 534-597) explicitly walks `WinDivert`, `WinDivert14`, `WinDivert15` and stops them — confirms singleton service.

3. `ZapretManager.cs:39` `IsWinwsRunning()` uses `Process.GetProcessesByName("winws")` — existing assumption is "one winws.exe at a time". `Start()` calls `Stop()` first if `IsRunning` (line 152).

Therefore: **serialize**. Cost of serialization:

```
total = N × (warmup + probes + stop)
      = N × (2 s + 2 s + 1 s)
      = 5 s per strategy
```

| N strategies tried | Worst-case wait |
|---|---|
| 1 (cache hit, confirm) | 5 s |
| 3 (default candidates: ALT3, ALT4, multisplit) | 15 s |
| 5 (extended list) | 25 s |
| 10 (full Flowseal sweep) | 50 s |

**Default target = 3 candidates / 15 s.** Anything > 30 s breaks the "magic button" feel.

### Candidate ordering

1. **Last-known-good** — cached strategy for this `(isp, time)`, Tier-1 within past N days.
2. **ALT3** — `ZapretUpdater.ParseStrategies` line 679 already prefers it (score 0). Validated by Flowseal community as highest hit-rate on Rostelecom/MegaFon.
3. **ALT4** — second-most-common, different fragmentation signature.
4. **multisplit** (built-in legacy) — well-tested, simpler args.

Beyond #4 we try ALT1, ALT2, ALT5+, hostfakesplit, "TLS in SNI" in that order. Never try `custom` (user-supplied, may have empty args).

### Caching

Persist `(ispFingerprint, strategyName, tier, lastVerifiedAt, latencyMap)` to `%ProgramData%\VPNRouter\cache\zapret_probe.json` after each successful run. TTL: 7 days. On next launch:

* If cache hit and `lastVerifiedAt < 7 days` → re-verify cached strategy only (5 s confirmatory probe). If still Tier-1 → use it. Otherwise → rotate.
* If cache miss or > 7 days → full sweep up to N candidates.

Pattern reuse: `FreeConfigCache.cs` already does atomic JSON save (.tmp + rename). Same pattern for zapret probe results.

### Cancellation

Probe loop honours `CancellationToken` end-to-end. User clicks "Cancel" mid-rotation → linked CTS cancels active probe, kills active winws.exe, returns "user cancelled". Template: `FreeConfigDeepVerifier.cs:100`.

---

## 3. Confidence calibration

A passing probe set doesn't guarantee user's full traffic works. Failure modes not caught:

* **UDP / voice** — Discord voice runs over UDP 50000-65535 with QUIC; TCP+TLS probes don't exercise it.
* **Session age** — winws.exe filters degrade after long uptime in some Flowseal strategies. Probe at t=4s won't see this.
* **Mobile vs Wi-Fi** — same machine, different ISP DPI.
* **CGNAT / nested VPN** — user behind another VPN masks DPI signature; probe passes "naturally" though strategy isn't needed.

### Tier ladder

| Tier | Triggered when | UI hint | Action |
|---|---|---|---|
| **Tier-1 — Confirmed** | YouTube TLS OK + Discord TLS OK + GitHub TLS OK + all latencies < 3 s | «Стратегия Flowseal (Confirmed)» green badge | Cache, use. |
| **Tier-2 — Partial / tentative** | YouTube TLS OK + GitHub TLS OK + Discord TLS Failed | «Стратегия Flowseal (Partial — Discord может не работать)» amber badge | Cache, use, surface optional «tryHostsInstall» suggestion. |
| **Tier-3 — Inconclusive** | YouTube TLS Failed but GitHub TLS OK | «Не работает» red badge | Move to next candidate. |
| **NoSignal** | GitHub TLS Failed too | «Нет интернета или DPI блокирует control» grey badge | Don't rotate. Surface diagnostic. Skip remaining strategies. |

Persisted with cache entry; survives restart without re-probing. Re-validate when:

* `lastVerifiedAt + 7 days` elapsed, or
* `IspFingerprint` changes (user moved Wi-Fi, switched ISP), or
* user clicks «Re-probe now».

### Failure-mode escalation

If all candidates land Tier-2 / Tier-3:

1. Toast: «Auto-probe failed — try installing Discord hosts / updating IPSet list».
2. Offer fallback to manual selection (preserve existing combo path).
3. Log full per-strategy / per-probe matrix to `zapret-probe.log` for support.

Degraded but honest — better than auto-picking Tier-3 silently.

---

## 4. Implementation outline (no code)

### New classes

**`VPNRouter.Core/Services/ZapretStrategyProber.cs`** — per-strategy probe orchestrator.

* Constructor: `(ILogger, ZapretManager, ITcpTlsProbe? probe = null)`.
* `Task<ProbeResult> ProbeAsync(ZapretStrategy strategy, CancellationToken ct)`
* Inside:
  1. `_zapret.Start(strategy.Arguments)` (or `StartFromBat`).
  2. Listen for `ImmediateExitDetected` — if fires, abort, return `ProbeResult.Tier3Failed("immediate exit / AV-quarantined")`.
  3. `await Task.Delay(2000, ct)`.
  4. Three parallel `TcpTlsProbe.ProbeAsync` (youtube.com, discord.com, api.github.com).
  5. Classify into tier per §3.
  6. `_zapret.Stop()` + await stop.
  7. Return `ProbeResult(tier, perProbeStatuses, latencies, error?)`.
* Internal const: `WarmupDelay = 2 s`, `ControlHost = "api.github.com"`, `BlockedHostA = "www.youtube.com"`, `BlockedHostB = "discord.com"`.
* Test seam: inject probe calls via delegate so unit tests can pre-can responses without socket I/O.

**`VPNRouter.Core/Services/ZapretStrategyAutoSelector.cs`** — multi-strategy rotation engine.

* Constructor: `(ILogger, ZapretStrategyProber, IZapretIspFingerprint, IZapretProbeCache)`.
* `Task<AutoSelectResult> SelectAsync(IReadOnlyList<ZapretStrategy> candidates, CancellationToken ct, IProgress<AutoSelectProgress>? progress)`
* Algorithm:
  1. Compute `ispFingerprint`.
  2. Cache lookup: if last-known-good for `ispFingerprint` is in `candidates`, move to position 0.
  3. Iterate serially:
     * Call `_prober.ProbeAsync(strategy, ct)`.
     * Report progress (`stratIndex / total, currentTier`).
     * Tier1 → cache, return success.
     * Tier2 → save as fallback; continue (at most 1 extra try, then accept Tier-2).
     * Tier3 → continue.
     * NoSignal → abort whole sweep, return `AutoSelectResult.NoSignal`.
  4. After loop: if Tier-2 fallback exists, return it; otherwise `AutoSelectResult.AllFailed`.

**`VPNRouter.Core/Services/ZapretIspFingerprint.cs`** — cheap ISP fingerprint for caching key.

* `static Task<string> ComputeAsync(CancellationToken ct)` returns stable string like `"AS25513-rostelecom-msk-v4"`.
* Cheapest reliable path:
  1. Resolve `myip.opendns.com` against `resolver1.opendns.com` — get egress IPv4.
  2. Reverse-DNS the egress IPv4 → PTR record (e.g. `client.megafon.ru`).
  3. Combine `(reverseDnsLastTwoLabels, asn-guess-from-cidr)` — first 16 bits of egress IP enough to bucket.
  4. Fallback: use just first /16 of egress IP.
* Cost: one DNS + one reverse-DNS, ≤ 500 ms. Cached for session.

**`VPNRouter.Core/Services/ZapretProbeCache.cs`** — typed JSON cache wrapper for `%ProgramData%\VPNRouter\cache\zapret_probe.json`.

* Schema:
  ```json
  {
    "version": 1,
    "entries": [
      {
        "isp_fingerprint": "AS25513-rostelecom-msk-v4",
        "strategy_name": "general (ALT3)",
        "tier": 1,
        "last_verified_at": "2026-05-24T12:34:56Z",
        "latencies_ms": { "youtube": 230, "discord": 215, "control": 180 }
      }
    ]
  }
  ```
* Atomic save (`.tmp` + rename), mirroring `FreeConfigCache.SaveAtomic`.
* `Get(isp_fingerprint) → entry?`, `Save(entry)`, `Clear()`, `Cleanup(ttl: 30 days)`.

### VM wiring

In `MainWindowViewModel.cs` (next to `ToggleZapretAsync` at line 4203):

* New `[RelayCommand] private async Task AutoProbeZapretStrategyAsync()`:
  1. Candidate list = `_parsedStrategies` filtered + built-in `multisplit/fake+multisplit`.
  2. Spin up `ZapretStrategyAutoSelector`.
  3. Subscribe to `IProgress<AutoSelectProgress>`, push to `ZapretAutoProbeStatus` ObservableProperty.
  4. `await selector.SelectAsync(candidates, _cts.Token, progress)`.
  5. On success: set `ZapretStrategyIndex` to winning strategy, call existing `ToggleZapretAsync` to actually start it (or have prober NOT stop the winner — wire as `selector.SelectAsync(candidates, ct, keepWinnerRunning: true)`).
  6. On failure: tier-aware toast.
* New `ObservableProperty` `ZapretAutoProbeStatus` shown in `DpiBypassPage.axaml` next to strategy ComboBox.

### Tests

Following `VPNRouter.Tests` partial-class + InternalsVisibleTo pattern:

* `ZapretStrategyProberTests` (~6 facts) — fake `ITcpTlsProbe` returns canned `ServerProbeResult` triples → tier classifier returns expected tier.
* `ZapretStrategyAutoSelectorTests` (~6 facts) — fake prober returns pre-canned per-strategy `ProbeResult`s, assert candidate ordering, Tier-1 short-circuit, Tier-2 fallback, NoSignal abort.
* `ZapretProbeCacheTests` (~4 facts) — atomic save, get-by-isp, TTL cleanup.
* `ZapretIspFingerprintTests` (~3 facts) — fake DNS resolver via interface seam.
* Integration test (Windows-only, `[SkipOnNonWindowsFact]`) — actually spawn winws.exe with `multisplit`, probe, stop. Gate behind `WINWS_AVAILABLE` env.

---

## 5. Comparison with prior art

### vs Flowseal manual dropdown

| | Manual dropdown | Auto-probe (this design) |
|---|---|---|
| User input | 1 choice from 10 names | 1 click "Auto" |
| Time to working state | Trial + error, can be minutes | 5-25 s (cached: instant) |
| Knowledge needed | Knows ALT1 vs ALT3 vs hostfakesplit | None |
| ISP-aware | No | Yes, via cache key |
| Fallback if wrong | Manual rotation | Auto-rotate within sweep |

### vs `rkn-block-checker`

That tool is a **block-type classifier** (DNS/TCP/TLS/HTTP verdict matrix), not a **strategy-effectiveness tester**. Rejected separately for Python dependency. Our design tests "works/doesn't work" given a specific strategy applied. Useful idea borrowed: pattern of "10 workers, 5 s timeout" maps to our "parallel triplet within one strategy, 2 s each".

### vs `zapret-auto.lua` in bol-van/zapret2

| | zapret-auto.lua | This design |
|---|---|---|
| **When it acts** | Reactive — waits for real traffic, rotates on N failures | Proactive — probes before user-visible traffic |
| **Vantage** | Inside the C packet engine | Outside winws.exe — synthesizes probes |
| **Reaction time** | After N failed flows | Before any user traffic |
| **Strategy library** | Lua functions | Flowseal `general*.bat` parsed |
| **Failure signal** | TCP RST observed mid-flow | TLS handshake outcome on synthetic probe |
| **Availability** | Only if/when we migrate to zapret2 | Available now on Flowseal v1 |

**Complementary, not competing.** If zapret2 lands, auto-probe layers on top: probe-pick-cache for initial strategy, then zapret-auto.lua rotates inside the engine for transient DPI shifts during the session.

### vs the v2.36.0-r7 TgProxyOneTap design

`TgProxyOneTap` (task #64) achieves "magic button" by sequencing `download → start → ensure-scheme → open-Telegram` with per-step progress. We mirror that lifecycle:

1. Ensure Flowseal installed.
2. Auto-probe rotation with per-strategy progress.
3. Start the winning strategy.
4. Surface "Done — strategy: Flowseal ALT3 (Confirmed)" toast.

UI affordance: same `MainActionButton` pattern, single visible button on the DpiBypass page that progresses through "Probing strategies… (2/3)" → "Confirmed: Flowseal" labels.

---

## 6. Risks & mitigations

### R1 — WinDivert filter driver state between strategy switches

Stopping winws.exe doesn't fully release WinDivert's per-process filter handle until kernel collects it. Starting next winws.exe before kernel cleanup leaves stale filter rules.

**Mitigation**: `_zapret.Stop()` already does `Kill(entireProcessTree)` + `WaitForExitAsync(3000 ms)` (ZapretManager.cs:308-358). Add post-stop `Task.Delay(500ms)` before next start to give kernel time. If issues persist, full `StopWinDivertService` between strategies — but that's destructive.

### R2 — AV scanning during probe (Bug-r9-G)

Windows Defender / Kaspersky quarantines winws.exe mid-probe.

**Mitigation**: subscribe `ImmediateExitDetected` in `ZapretStrategyProber`. If fires during a probe:
1. Cancel probes for this strategy.
2. Return `Tier3Failed("av_quarantine")` immediately.
3. Surface AV whitelist toast (reuse existing).
4. **Abort the full sweep** — every subsequent strategy will hit same AV behavior.

### R3 — DNS cache pollution

`youtube.com` resolves to cached IP via DNS the ISP DPI no longer routes through. TLS probe hits "wrong" CDN edge that happens to be unblocked.

**Mitigation**: bypass system DNS for probe target resolution. Resolve via DoH to Cloudflare (`https://cloudflare-dns.com/dns-query?name=youtube.com&type=A`) and pin resolved IPs for probe duration. Existing app uses DoH via `dns-direct-out` outbound; hit DoH directly via HttpClient for probe-target resolution.

Acceptable simpler v1 mitigation: hard-code `Host:` header to `www.youtube.com` and connect by name (let OS resolve through DNS, like real Chrome).

### R4 — User behind CGNAT / nested VPN

User already on another VPN that hides ISP DPI from us. Probe passes for every strategy. We auto-pick something arbitrary; user later disables other VPN and gets bad-strategy frustration.

**Mitigation**: add **direct (no zapret) baseline probe** as first sweep step. If YouTube TLS succeeds with no winws.exe running, return `AutoSelectResult.NoDpiDetected` and skip strategy rotation — surface «Auto-probe не нашёл DPI на этой сети — Zapret можно не включать.»

Cost: 5 extra seconds. Worth it.

### R5 — Probes leak to public hosts unconditionally

Probe traffic to `youtube.com / discord.com / api.github.com` from every install. Privacy minimal (bare TLS handshakes to public CDN; no user identifier).

**Mitigation**: log nothing identifying. Document in CLAUDE.local.md / README that auto-probe runs three TLS handshakes to publicly listed CDN hosts. Make auto-probe **opt-in for first run**.

### R6 — winws.exe restart side-effects within session

Rapid stop/start during sweep may briefly drop user's existing TCP connections (browser tabs, Discord client, ongoing downloads).

**Mitigation**: warn at start: «Auto-probe займёт ~15 секунд, активные подключения могут на секунду прерваться.» Single banner, dismiss-and-remember. Cheaper alt: only auto-probe at cold-start; re-probes mid-session require explicit "OK to disconnect briefly?" confirmation.

### R7 — Beeline-mobile / MTS-mobile probe asymmetry

Some carriers throttle/inject only when traffic crosses specific gateway. Probe runs through Wi-Fi → hits home DPI; user tethers mobile → different DPI; cached strategy wrong.

**Mitigation**: `ZapretIspFingerprint.ComputeAsync` includes egress IP /16 — switching Wi-Fi to cellular changes egress IP, invalidates cache, triggers re-probe.

---

## 7. Open questions

1. **Should the prober also try strategy = "off" (no zapret) as candidate 0?** Yes — tied to R4. Cheapest probe, most user-friendly outcome.

2. **Do we expose tier in YAML settings?** No — cache is the right home.

3. **What about HTTP/3 (QUIC) probes?** YouTube/Discord serve over QUIC alongside TLS. Strategy that defeats TLS DPI but not QUIC DPI passes triplet but fails user traffic. For v1, accept this. Future v3.0+ could add QUIC INITIAL probe.

4. **Re-probe cadence — 7 or 30 days?** ISP DPI rules change monthly+; 7 days conservative. Start with 7 days, dial back if too noisy.

5. **Integration with HealthMonitor?** `VPNRouter.Core/Services/HealthMonitor.cs` does auto-restart of sing-box. Analogous for zapret would re-probe after N consecutive immediate-exits within an hour. Defer.

---

## 8. Roll-out plan

### Phase 1 — Engine only (no UI)
* `ZapretStrategyProber`, `ZapretStrategyAutoSelector`, `ZapretProbeCache`, `ZapretIspFingerprint` + unit tests with fake probe seam.
* Behind feature flag `app.zapret.enable_auto_probe = false` in YAML.
* No UI surface — only callable via dev CLI (`vpnrouter-cli zapret probe`).
* Ship in rolling candidate, observe logs from brat/stas testers.

### Phase 2 — Opt-in UI
* `[Auto]` entry at top of ComboBox.
* "Re-probe now" button in Advanced section.
* Tier badge next to strategy name.
* `app.zapret.enable_auto_probe` defaults to `false` still, but UI is the switch.

### Phase 3 — Default for new installs
* New installs default `enable_auto_probe = true`.
* On first launch with auto enabled, run probe sweep, write tier badge.
* Existing installs keep their explicit strategy choice.

### Phase 4 — Deprecate manual combo (long horizon)
* Manual combo moves behind `[Advanced]` disclosure.
* Default UI = single "Start" button → auto-probes if no cache.
* Aligns with one-button vision.

---

## Critical files for implementation

* `VPNRouter.Core/Services/ZapretManager.cs` (process lifecycle, immediate-exit signal)
* `VPNRouter.Core/Services/ZapretUpdater.cs` (strategy parsing, WinDivert lifecycle)
* `VPNRouter.Core/Services/TcpTlsProbe.cs` (probe primitive we extend)
* `VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs` (atomic-save pattern)
* `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (VM wiring; `ToggleZapretAsync` at line 4203)
