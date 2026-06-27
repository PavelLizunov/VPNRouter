# Roblox / UDP-game reliability — RB1–RB4

## Trigger
User: "проверь почему у пользователя постоянно дропает из roblox" + diags
`VPNRouter-diagnostics-20260626-222927.zip` / `…-224714.zip` / `…-20260627-135341.zip`.

## Symptom
Roblox (and other UDP games) repeatedly disconnect. Diags show:
- 06-26: 179× UDP "no recent network activity" timeouts on `proxy-udp`.
- 06-27: 36× forcibly-closed TCP + repeated Roblox DNS parse errors.

## Root cause
The active server was **NAIVE** (HTTP CONNECT, TCP-only — cannot carry UDP). VPNRouter
pairs a NAIVE server's UDP onto a UDP-capable **sibling** (Hysteria2/TUIC). The sibling
was picked by pairing tag / base-name **without any liveness check**, so the game's UDP
rode a **dead Latvia-HY2** node → the "no recent network activity" drops. Secondary: a
known upstream sing-box DNS-parser bug on Roblox domains.

---

## RB1 — UDP path never pairs onto a dead sibling  [HIGH] — DONE (commit daa5e6a1)
`NaivePairing.FindUdpSibling` takes an optional liveness predicate. Dead candidates are
skipped; if the paired sibling is dead it falls back to ANY alive UDP-capable server
(prefer Hy2/TUIC). `ConfigGenerator`/`ConfigPipeline` thread the predicate; the standard
flow/no-flow split also drops dead no-flow (UDP) servers. `StartupPipeline` runs a
**scoped, best-effort** pre-connect probe (only when the active set has a NAIVE server;
3 s deadline; failure → null → unchanged tag-based pairing; HotReload skips it), reusing
`ServerHealthProbe` (G1).
- **Acceptance: a dead UDP sibling is NEVER selected.** Proven by
  `NaivePairingUdpLivenessTests` (no-probe unchanged; paired-alive kept; paired-dead →
  any-alive-UDP; all-dead → null).
- Default-on. This is the core fix for what the diags actually showed (dead UDP at connect).

## RB2 — prefer UDP-native (Hy2/TUIC) for the UDP path  [MED] — SATISFIED by RB1
The UDP-sibling selector orders **Hysteria2 > TUIC** (`NaivePairing.PreferUdp`), and RB1's
liveness fallback picks a UDP-native server over nothing. There is no separate "prefer a
UDP-native server as the *connect* server" change: that would override the user's pick / the
fastest-probe choice with a slower Hy2 purely on protocol — a UX regression for non-gamers,
and not what the diags call for. RB2 is therefore the UDP-native ordering already in RB1,
covered by an explicit ordering test.

## RB3 — Roblox DNS "buffer size too small"  [MED] — UPSTREAM, NOT FIXABLE HERE
`router: process DNS packet: unpack request: bad question name: dns: buffer size too small`
is an **upstream sing-box** bug (https://github.com/SagerNet/sing-box/issues/3478), thrown
by sing-box's own DNS parser (miekg/dns) and written to **sing-box's own log file** — we
bundle sing-box (1.13.10), we don't fork it, so we cannot change either the parser or that
log. Crucially the DNS resolution still **succeeds** (retries), so it is NOT the primary
drop cause (RB1 is). Tracked for the next sing-box bump (chip task_8afcdad2): check if the
newer release fixes it; else probe whether a config change (the `type: udp` RU DNS server /
1280 MTU / hijack-dns) avoids triggering it. Acceptance ("no such line") is **not
achievable by VPNRouter code**.

## RB4 — fail over the UDP path on sustained degradation  [MED] — LOGIC LANDED, WIRING DEFERRED
**Decision logic landed + tested**: `UdpDegradationDetector` fires only when the UDP path is
essentially DEAD for a sustained window (≥N proxy-udp timeouts AND zero proxy-udp successes)
AND a cooldown has elapsed since the last fire — so it can't cause reconnect storms even
without live tuning. Pure + deterministic (time injected). Covered by `UdpDegradationDetectorTests`.

**Runtime wiring DEFERRED — and here is the honest why.** To act on this at runtime we need a
real-time proxy-udp "no recent network activity" signal, and **it does not exist in the app
today**:
- B0's `ConnHealthSnapshot` tracks `RelayOpenFails`/`ProxyStreamErrors`/`LocalCloses`, **not**
  proxy-udp UDP timeouts.
- B0 (`ClashLogStream` + classifier) is **observe-only and default-off** (`VPNROUTER_CONN_HEALTH`).
- It is unconfirmed whether the "no recent network activity" UDP-NAT timeout even appears in the
  Clash-API `/logs` stream B0 consumes, or only in sing-box's own log file.

Wiring RB4 therefore means: (a) extend the B0 classifier to recognise + count proxy-udp UDP
timeouts, (b) confirm that signal is actually in the Clash-API stream (needs a live Roblox
repro), (c) enable that telemetry, (d) wire the detector into `HealthMonitor` to raise the
existing `FailoverRequested` (→ G4 reconnect → RB1 re-probe). Steps (b) and the threshold
tuning are **un-verifiable on the test VM** (its network can't run Roblox). Per this project's
own lesson — green tests ≠ ship; never blind-ship an un-verified fix — the runtime trigger is
NOT shipped blind. It is staged behind the conn-health flag for a future **live-tuning session**
with a real Roblox client.

**Wiring plan (for the live session):**
1. `ConnectionHealthClassifier`: add `UdpTimeout` category for proxy-udp "no recent network
   activity"; confirm the line is in the Clash-API `/logs` stream via a live capture.
2. `ConnHealthSnapshot`: add `UdpTimeouts` (windowed) + `UdpSuccesses`.
3. `HealthMonitor` (only when conn-health is active): each tick feed the snapshot to
   `UdpDegradationDetector`; on fire raise `FailoverRequested("UDP path dead — failing over")`.
4. Tune N / window / cooldown against the live repro; only then consider default-on.

---

## Status
- RB1 — DONE, default-on, tested (daa5e6a1). The core fix.
- RB2 — satisfied by RB1's UDP-native ordering (+ test).
- RB3 — upstream sing-box #3478; not fixable here; tracked (task_8afcdad2).
- RB4 — decision logic landed + tested; runtime trigger deferred to a live-tuning session
  (no verifiable real-time data source + un-tunable without a Roblox repro).
