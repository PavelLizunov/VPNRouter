# rkn-block-checker as zapret auto-strategy companion

**Date**: 2026-05-24
**Status**: Research only, no code changes
**Coordinates with**: `plans/research-zapret2-bolvan-migration-2026-05-24.md` (#59),
  `plans/research-one-button-tgproxy-zapret-2026-05-24.md` (UX driver)

---

## Context

User hint: "MayersScott/rkn-block-checker — это возможно отличная связка для zapret
для авто формирования методов" — could enable VPNRouter's missing "Auto" zapret
strategy and replace the cryptic ALT1/ALT3/hostfakesplit dropdown.

Pain point recap (`research-one-button-tgproxy-zapret-2026-05-24.md`): user must
pick strategy upfront from 10+ Flowseal `general*.bat` names. No guidance, no
fallback, no validation.

---

## 1. MayersScott/rkn-block-checker — what it is

Fetched: repo, `README.md`, releases page.

- **What**: Python 3.10+ CLI **diagnostic** tool. Walks the network stack
  **DNS -> TCP -> TLS -> HTTP** against a built-in list (~21 controls, ~15 RKN
  targets) or user-supplied JSON, stops at first failure, classifies the
  blocking layer.
- **Verdict types** (README): `OK`, `DNS_BLOCK`, `TCP_RESET`, `TLS_BLOCK`,
  `HTTP_STUB`, `TIMEOUT`, `DOWN`, `UNKNOWN`, with `HIGH/MEDIUM/LOW` confidence.
- **Probe mechanics** (README "Probes & Detection Strategies"):
  - DNS: system resolver vs Cloudflare DoH set-comparison
  - TCP: plain `:443` handshake
  - TLS: SNI handshake (DPI signature = TCP ok + TLS reset/timeout)
  - HTTP: GET, checks status code + Russian-language stub markers / HTTP 451
- **License**: MIT. Active May 4-9, 2026 (9 versions in 5 days, v0.5.0 latest).
  Pre-1.0, no Windows binary, **Python source + Docker only**.
- **External calls**: `cloudflare-dns.com` (always), `ipinfo.io` (optional, off
  via `--no-self-info`). No telemetry.
- **Output**: colored terminal or `--json` machine-readable with per-target
  `dns_mismatch`, `tcp_time_ms`, `tls_error`, `status_code`, `plt_ms`, etc.

---

## 2. Capabilities & limits

- **Cron/CI-friendly**: README explicitly recommends
  `rkn-check --json --no-self-info > snapshots/$(date -I).json` for time-series.
- **Parallel**: 10 workers default, 5s timeout — full run ~10-30s.
- **Wrapper integration**: JSON output makes it scriptable, but the tool itself
  has **no before/after comparison**, **no strategy-effectiveness scoring**,
  and **zero awareness of DPI bypass tools** (no mention of zapret / GoodbyeDPI
  / Xray / sing-box in README, confirmed by direct query).
- **Platform**: Cross-platform Python. Windows users need Python 3.10+ installed
  or Docker — neither is currently in VPNRouter's runtime dependency footprint
  (Flowseal/winws is a self-contained Cygwin binary).

**Critical limit**: the tool diagnoses **what kind of block** is in front of the
user, **not whether a given bypass strategy defeats it**. To use it as an
auto-strategy oracle we'd need to run it twice — once direct, once with
strategy X applied — and compare. That comparison logic is **our work to
write**, not provided.

---

## 3. Integration patterns

### Pattern A: on-demand probe button
User clicks "Auto-detect best strategy". VPNRouter starts winws with strategy 1,
runs `rkn-check --json` against a target list, scores success rate, repeats with
strategy 2..N, picks the winner, persists. 30-60s per strategy * N strategies
= 5-10 min total. One-time per user setup.

### Pattern B: background daemon
Periodic (hourly) probe; if current strategy's success-rate drops, auto-switch.
High infrastructure cost (Python runtime on user's box, recurring network
probes leaving a footprint per `--no-self-info` README warning).

### Pattern C: first-run wizard
Pattern A but only during install. Simplest, but if ISP DPI changes later, user
re-runs manually.

### Pattern D: CI-curated bundled metadata
Run rkn-check against a known-blocked set in GitHub Actions cron (like
`build-free-pool.yml`); publish "Strategy X is best as of {date}" as bundled
JSON. Zero per-user network footprint, zero Python dependency on user box, but
**curation is from GitHub runners' egress IP** — not the user's ISP. Russian
ISP TSPU varies per-region; this is fundamentally the wrong vantage point.

---

## 4. Comparison matrix

| Aspect | A (on-demand) | B (daemon) | C (first-run) | D (CI-curated) |
|---|---|---|---|---|
| UX | Manual button, 5-10 min | Set-and-forget | Wizard at install | Instant |
| Privacy footprint | Per-user calls to Cloudflare DoH + targets | High recurring | Low one-time | **Zero per-user** |
| Maintenance | Low | High (daemon, retry, logging) | Low | Med (CI cron) |
| Per-ISP correctness | High | High | Medium (one snapshot) | **Low — GH runner not the user's ISP** |
| Runtime deps | **Python 3.10+ or Docker on user box** | Same | Same | None |
| Latency to working state | 5-10 min once | n/a | 5-10 min once | Instant |

The "Python 3.10+ on user box" row is the cliff — VPNRouter currently ships
zero Python. Embedding CPython is ~25 MB, breaks signed-installer story, adds
AV false-positive surface. Docker-on-user-Windows is a non-starter.

---

## 5. Recommendation: **(B) Defer until zapret2 migration decision settles**

**Primary rationale**:

1. **zapret2 already has built-in auto-rotation** — `lua/zapret-auto.lua` in
   `bol-van/zapret2` (21 KB). Per WebFetch: *"orchestrators can decide which
   instances to call... circularily change strategy numbers when failure count
   reaches threshold"*. Per-host failure counter, success detection on TCP RST
   / HTTP redirect / retransmit signals, automatic rotation. This is the
   auto-strategy feature, **inside zapret2 itself, at the packet engine level**
   — vastly better vantage than spawning a Python probe.
2. Per `research-zapret2-bolvan-migration-2026-05-24.md`, recommendation is
   "wait 3-6 months for zapret2 to mature". When we migrate, we **inherit
   auto-strategy for free** via `--lua-desync` + `zapret-auto.lua`.
3. Pattern A (the only viable rkn-block-checker pattern) bolts a Python runtime
   on top of every Windows install. The cost (CPython embed or PATH dependency,
   AV surface, installer bloat) is not worth a feature that lands native in
   zapret2.

**Secondary rationale (why not A even today)**:

- rkn-block-checker is **not** an auto-strategy tool. It's a **block-type
  classifier**. Turning classifier output into "use ALT3" requires us to write
  a strategy-to-block-type mapping table, validate it against Flowseal's
  current presets, keep it fresh as Russian DPI evolves. Same maintenance
  burden as just shipping smart defaults in our YAML.
- Pre-1.0 (v0.5.0, 5-day-old project, single maintainer) — pinning a CI
  dependency on it is fine; bundling on every user box is premature.

**Tactical alternative (no rkn-block-checker, no zapret2)**:

For the "one-button UX" goal in `research-one-button-tgproxy-zapret-2026-05-24.md`,
ship a **curated default** (`ZapretUpdater.cs:679` already sort-prefers ALT3).
Add a `Strategy: Auto` UI entry that **just runs through Flowseal's top 3
presets sequentially**, validates with a single domain HEAD request between
each (no Python — use `HttpClient` we already have), picks first that returns
200. ~5 lines of `ZapretManager` change, zero new runtime deps. Covers
~80% of the value of Pattern A without any of the cost.

---

## 6. Open questions

1. **Does Flowseal's strategy list publish a "block-type -> recommended-preset"
   mapping anywhere?** If yes, even the tactical alternative simplifies. If no,
   we curate ourselves (~2h one-time).
2. **Does zapret2's `zapret-auto.lua` work on the `winws2.exe` Windows build, or
   is it Linux-nfqws-only?** Critical for the "inherit auto-strategy via
   migration" rationale. Not verified — needs spike inside #59's follow-up.
3. **Could rkn-block-checker run server-side in our build-free-pool.yml cron**
   to flag when Russian ISP DPI signatures shift, sending us a maintenance
   signal (not a per-user feature)? Cheap experiment, no user-side cost. This
   is a Pattern D-lite — for **us**, not for users.

---

## Sources

- `https://github.com/MayersScott/rkn-block-checker` (repo, README, releases)
- `https://raw.githubusercontent.com/MayersScott/rkn-block-checker/main/README.md`
- `https://github.com/bol-van/zapret2/blob/master/lua/zapret-auto.lua`
- `plans/research-zapret2-bolvan-migration-2026-05-24.md` (sibling research #59)
- `plans/research-one-button-tgproxy-zapret-2026-05-24.md` (UX driver)
- `VPNRouter.Core/Services/ZapretUpdater.cs:652-697,679` (current preset
  parsing + ALT3 sort heuristic)
