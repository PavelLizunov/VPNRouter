# Phase 2 — WINBRAT protocol load matrix

**Owner**: Codex  
**Branch**: `codex/winbrat-loadtest`  
**Parent**: `plans/phase2-winbrat-load-test-mvp-2026-08-08.md`  
**Risk**: MEDIUM — remote UI selection and a fixed executable run on the dedicated test VM  
**Blast radius**: WINBRAT verifier/coordinator and focused tooling tests only; no product/runtime code

## Why

The completed soak proved one unknown selected configuration stayed connected,
while the 30-minute GameUdp baseline ran outside WINBRAT. Neither result compares
the protocol families relevant to the reported game disconnects. The useful next
step is a repeatable matrix that selects a server by the UI's non-secret use-case
chip, proves Full Tunnel plus TUN traffic, and runs the same bounded workload for
each family without reading or returning subscription URLs, hosts, ports or keys.

## What

- Extend the fixed WINBRAT UIA helper with one narrow operation: select a
  subscription row by an allowlisted visible use-case category and ordinal.
- Build the existing fixed GameUdp payload, approve its exact source-built hash,
  deploy and run only that executable through the verified WINBRAT session.
- Require VPNRouter connected, route scope `Tunnel`, Full Tunnel UI state and a
  correlated `VPNRouter-TUN` byte increase. Otherwise return `BLOCKED`.
- Snapshot and restore the original routing mode and selected row through the UI;
  cleanup must leave zero core processes and no TUN.
- Run the first comparison on the subscription families already proven present:
  Games/voice (HY2 family) and Low ping (AWG family), then Daily (VLESS family).

## How

1. Add an allowlisted `ProtocolUseCase` plus bounded ordinal to
   `tools/brat-verify.ps1`. Search only the `SubList` UIA list, scroll through
   virtualized rows, select the ancestor `ListBoxItem`, and return no row name.
2. Add a fixed payload deploy/run path. It accepts no target, executable, rate,
   packet size or duration from the caller; the existing source constants remain
   the authority.
3. Record only category, ordinal, route/full-tunnel booleans, TUN byte-correlation
   boolean and the existing aggregate GameUdp metrics.
4. Add focused source-contract tests for the allowlists, no-secret evidence schema,
   cleanup and absence of generic remote execution.
5. Build/test, run the exact DeepSeek-in-Qwen read-only review, then execute one
   bounded live shakedown before starting 30-minute family runs.

## Verification gate

- [ ] Gate 1 — `dotnet build VPNRouter.sln -c Release`: 0 errors.
- [ ] Gate 2 — focused load/stability tooling tests and full test suite green.
- [ ] Gate 3 — this brief, Outcome and `plans/OPEN-DEFECTS.md` current.
- [ ] Gate 4 — Ponytail minimality and security review of fixed remote execution.
- [ ] Gate 5 — WINBRAT identity, safe row selection, one shakedown, cleanup PASS.
- [ ] Gate 6 — N/A; no product characterization surface changes.

## Acceptance

- No subscription URL, host, port, key, row name, PID, route or raw log is copied
  from WINBRAT or written to evidence.
- A run cannot start unless the fixed payload hash is approved, VPNRouter is
  connected, the fixed endpoint route is `Tunnel`, Full Tunnel is visible, and
  cleanup restoration is armed.
- Each selected category runs the identical 20 pps, 256-byte GameUdp profile with
  its fixed 50 pps burst. A 30-minute result is six independent five-minute runs.
- Any crash, restart, disconnect, lost attribution, corruption, unknown reply,
  unclassified error or failed cleanup makes the family result `FAIL`.
- Loss/RTT comparisons remain measurements until repeated baselines exist; they do
  not trigger MTU, failover or release changes by themselves.

## Outcome

**Status**: PARTIAL / LIVE FINDING — the fixed GameUdp path and safe
protocol-category selection work end to end on WINBRAT. The gaming-priority
HY2 and AWG families were exercised, but the result is not a product defect and
not a blanket AWG PASS: both families produced an isolated multi-second UDP
gap while VPNRouter itself stayed connected. VLESS and browser/mixed profiles
were not run because they do not improve attribution of the current incident.

### Implemented tooling

- `SelectProtocol` is restricted to `SubList`, a disconnected TUN and fixed
  public protocol labels. It never reads or returns endpoint-bearing row Names.
  Avalonia exposed neither `ScrollPattern` nor a range-valued scrollbar on
  WINBRAT, so the bounded fallback uses fixed Home/PageDown keys on a
  materialized row, still with no coordinates or arbitrary input.
- `loadtest` approves one exact source-built archive hash, copies and rechecks
  only that payload, runs the fixed five-minute GameUdp profile with no caller
  target/rate/size/duration, and returns only aggregate metrics and fixed
  lifecycle enums. BrowserBurst and Mixed remain fail-closed
  `MeasurementGated`.
- The payload now emits a fixed status and aggregate snapshot on every
  controlled exit. The verifier distinguishes timeout, missing/empty/invalid
  output and non-zero exit without returning stderr or exception text.

### Live matrix evidence

All VPN runs used the existing Full Tunnel setting with Auto-select Off. Every
load interval proved `RouteScope=Tunnel`, TUN-byte correlation and one stable
owned core. Boundary probes passed HTTPS plus UDP 64/512/1200/1392 before and
after the incidents.

**Hysteria2 category:**

- Shakedown: 6,057/6,061 replies, 4 lost, p99 78.6 ms, maximum gap 151.0 ms.
- Matrix interval 1: 6,061/6,061, zero loss, p99 63.2 ms, gap 95.4 ms.
- Matrix interval 2 stopped near the planned burst: 3,079 sent, 2,825
  received, 254 outstanding/lost (8.25%), p99 1,283.1 ms and `ReplyGap`.
  The last completed acknowledged gap was 1,052.5 ms; the fail condition itself
  proves the terminal no-reply window reached at least three seconds.

**AmneziaWG category:**

- Shakedown: 6,061/6,061, zero loss, p99 61.5 ms, gap 118.4 ms.
- Thirty-minute series: five full PASS intervals followed by one failed final
  interval. Aggregate was 35,744 sent, 35,628 received, 116 outstanding/lost
  (0.325%); 114 of those belonged to the final interval, which stopped at
  5,439/5,325 with `ReplyGap`. The last completed acknowledged gap was 2,648.0
  ms; the terminal no-reply window reached the fixed three-second failure
  threshold. The first five intervals lost only two packets across 30,305 sends.

**Independent endpoint controls:** two five-minute runs from the dev host,
outside WINBRAT/VPNRouter, completed 6,035/6,061 and 6,037/6,061 with 26 and
24 losses (0.43% and 0.40%); maximum gaps were 163.1 and 135.2 ms. They did not
reproduce either VPN-side multi-second gap, but no server receive/reply/drop
counter was available because the Proxmox/Mac route and localhost UI tunnel
were offline.

During both VPN incidents the app kept one core, an Up TUN and Tunnel routing;
the lifecycle window contained zero errors, FATAL, restart or failover. The
next control and all packet-size probes recovered immediately. This refutes an
MTU threshold, a full disconnect and a Hysteria2-only explanation. It does not
distinguish VPNRouter/sing-box, the common WINBRAT underlay, both provider
paths, or the owned endpoint. Do not implement automatic AWG switching from
this measurement: AWG reduced the observed frequency but also reproduced the
symptom.

### Cleanup

The live run ended in Simple mode, disconnected, zero owned core processes,
no VPNRouter TUN and Direct route scope. The original Hysteria2 category,
Auto-select Off and the pre-existing Full Tunnel mode were restored.

### Gate results

- [x] Gate 1 — final `VPNRouter.sln` Release build completed with 0 errors
  (pre-existing warnings remain).
- [x] Gate 2 — final focused verifier/load suite passed 26/26 and the root
  regression filter passed 22/22.
- [x] Gate 3 — this Outcome and `plans/OPEN-DEFECTS.md` contain the live
  measurements and attribution boundary.
- [x] Gate 4 — Ponytail/security review kept the fixed-profile design, found
  and fixed exact-selection rollback for unavailable categories, and rejected
  speculative browser enablement. Final configured DeepSeek-in-Qwen zero-tool
  reviews returned PASS for both selector and payload/runner excerpts.
- [x] Gate 5 — WINBRAT identity, safe HY2/AWG selection, live GameUdp,
  boundary probes and clean restoration verified.
- [x] Gate 6 — N/A; product/runtime code and characterization surface unchanged.
