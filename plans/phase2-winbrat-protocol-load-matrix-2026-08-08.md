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

**Status**: COMPLETE / LIVE MEASUREMENT — the fixed GameUdp matrix and
BrowserBurst path work end to end on WINBRAT. The exhaustive UDP matrix found
three controlled liveness failures while VPNRouter itself stayed connected;
the later browser HTTPS/WebSocket load passed all three ten-minute cold runs.
No VPNRouter product defect is confirmed and AWG is not a blanket recovery:
the exact selected row matters more than the protocol label alone.

### Implemented tooling

- `SelectProtocol` is restricted to `SubList`, a disconnected TUN and fixed
  public protocol labels. It never reads or returns endpoint-bearing row Names.
  Avalonia exposed neither `ScrollPattern` nor a range-valued scrollbar on
  WINBRAT, so the bounded fallback uses fixed Home/PageDown keys on a
  materialized row, still with no coordinates or arbitrary input.
- `loadtest` approves exact source-built archive hashes, copies and rechecks
  only fixed no-argument payloads, and returns only aggregate metrics and fixed
  lifecycle enums. GameUdp and Full-Tunnel BrowserBurst are live verified;
  Mixed remains fail-closed `MeasurementGated`.
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
- [x] Gate 2 — final focused browser/verifier/load suite passed 47/47. The
  non-elevated full suite passed 2,721, skipped 2 and hit the 25 already-ledgered
  `%ProgramData%` ACL failures; production ACLs were not weakened and the normal
  GitHub workflow remains the full-suite environment gate.
- [x] Gate 3 — this Outcome and `plans/OPEN-DEFECTS.md` contain the live
  measurements and attribution boundary.
- [x] Gate 4 — Ponytail/security review kept the fixed-profile design, found
  and fixed exact-selection rollback for unavailable categories, and rejected
  speculative Mixed enablement. Exact DeepSeek-in-Qwen zero-tool reviews were
  attempted but timed out or failed closed without findings; Codex independently
  completed the source/security review and did not relax worker permissions.
- [x] Gate 5 — WINBRAT identity, safe HY2/AWG selection, live GameUdp,
  boundary probes and clean restoration verified.
- [x] Gate 6 — N/A; product/runtime code and characterization surface unchanged.

### Exhaustive 20×3 follow-up — 2026-08-09 (completed)

The resumable fixed manifest now covers every one of the 20 visible subscription
rows with three independent cold five-minute GameUdp runs. Each run starts from
zero owned core processes and an absent TUN, selects only by the fixed non-secret
protocol class/ordinal, proves Full Tunnel plus the fixed route and TUN-byte
correlation, and disconnects back to a clean state. A simultaneous independent
direct observer exercises the same owned UDP endpoint without VPNRouter.

Final completed matrix:

| Protocol class | Rows | Cold runs | Sent | Replies | Loss | Controlled network failures | Worst completed acknowledged gap |
|---|---:|---:|---:|---:|---:|---:|---:|
| VLESS Reality | 4/4 | 12/12 | 72,731 | 72,691 | 40 | 0 | 1,538.2 ms |
| VLESS WebSocket | 3/3 | 9/9 | 54,549 | 54,536 | 13 | 0 | 2,579.7 ms |
| VLESS XHTTP | 4/4 | 12/12 | 68,350 | 68,211 | 139 | 1 ReplyGap | 1,949.9 ms |
| Hysteria2 | 4/4 | 12/12 | 72,733 | 72,652 | 81 | 0 | 2,302.9 ms |
| AmneziaWG | 4/4 | 12/12 | 62,205 | 62,047 | 158 | 1 ReplyGap + 1 CookieFailure | 988.3 ms |
| Naive effective bundle | 1/1 | 3/3 | 18,183 | 18,179 | 4 | 0 | 199.1 ms |

Across all 20 rows and 60 cold runs, 57 completed and three returned controlled
network failures: one XHTTP `ReplyGap`, plus one AWG `ReplyGap` and one AWG
`CookieFailure` on the same ordinal-1 row. The payload sent 348,751 authenticated
datagrams and received 348,316 replies (435 lost, 0.1247%), with zero duplicate,
corrupt or unknown replies. All 60 runs proved Full Tunnel, TUN-byte correlation
and Tunnel routing. VPNRouter remained connected with one owned core and an Up
TUN after load in 60/60 runs; all 60 lifecycle windows contained zero
error/FATAL/unknown events. Every one of 100 clean-state checkpoints observed
one GUI, zero owned core, an absent TUN and Direct routing.

The planned direct observer completed 84/84 cycles independently of VPNRouter:
509,129 sends, 508,736 replies, 393 losses (0.0772%), one reordered reply, zero
corruption and a 2,907.3 ms worst acknowledged gap. It produced no controlled
three-second failure. These data demonstrate substantial external-path noise but
do not erase the clean-observer tunnel divergences listed below.

The XHTTP terminal event occurred on ordinal 0 repeat 1 after 1,680 sends and
1,619 replies. VPNRouter retained one core, an Up TUN and Tunnel routing; the
paired direct observer was clean. The same row then passed twice, and ordinal 1
passed all three runs. Ordinal 2 reproduced two non-terminal degraded runs
(38/36 lost; 1,949.9/1,843.6 ms gaps) followed by a clean 6,061/6,061 run. This
is evidence of intermittent selected-path quality, not an application disconnect
and not yet an attributable VPNRouter defect.

The direct observer independently produced both clean intervals and external
degradation, including 49/6,061 loss with a 2,507.4 ms gap while an overlapping
VPN run was clean. Conversely, both observer intervals overlapping XHTTP ordinal
2 repeat 2 were clean while that tunnel lost 36/6,061. XHTTP ordinal 3 then
passed all three cold runs with 18,181/18,183 replies and a 112.8 ms worst gap;
its four overlapping observer intervals were also loss-free. The paired evidence proves
that both the external observer path and individual tunneled/provider paths can
degrade independently. It does not identify the client core, access network,
provider node, proxy server or UDP egress as owner.

Hysteria2 ordinal 1 passed all three cold runs and retained one core, an Up TUN
and Tunnel routing throughout every load interval. Its third run lost 45/6,061
replies and reached a 2,302.9 ms acknowledged gap; the simultaneously overlapping
direct-observer interval independently lost 44/6,061 and reached 2,246.8 ms. The
near-identical timing and magnitude is strong evidence of a shared external
underlay/endpoint-path event rather than a VPNRouter disconnect. The first two
runs were loss-free with 86.7/93.6 ms gaps, and final cleanup was clean.

Hysteria2 ordinal 2 also passed all three cold runs. Repeat 2 lost 34/6,061
with a 1,759.0 ms gap while its overlapping direct observer was loss-free at
6,061/6,061 with a 106.9 ms gap; repeats 1 and 3 were loss-free with 79.5/77.6
ms gaps. This is one intermittent selected tunneled/provider-path divergence,
not a deterministic HY2 failure. VPNRouter remained connected and every
lifecycle and final-cleanup check passed.

Hysteria2 ordinal 3 was loss-free in all three runs (18,183/18,183) with a
275.0 ms worst gap. During its last run the independent direct observer instead
produced two degraded intervals (58/55 lost; 2,907.3/2,792.7 ms gaps) while the
tunnel completed 6,061/6,061 with a 275.0 ms gap. The complete HY2 family is
therefore 12/12 PASS with no terminal ReplyGap or VPNRouter lifecycle failure;
its one selected-path degraded run and the separate common/direct-path events
remain attribution measurements, not protocol-family or product defects.

AmneziaWG ordinal 0 passed all three cold runs with 18,173/18,183 replies,
10 losses (all in repeat 1) and a 362.7 ms worst gap. VPNRouter remained
connected, lifecycle was clean and every run returned to zero core/absent TUN.
This first row is stable but does not by itself prove AWG is a universal
recovery: the earlier long AWG sequence still contains one isolated ReplyGap,
and the remaining three rows must complete under the same profile.

AmneziaWG ordinal 1 is the first reproducibly unhealthy row in this matrix.
Repeat 1 passed 6,060/6,061 with a 153.3 ms gap. Repeat 2 then stopped at the
fixed terminal ReplyGap after 1,597 sends/1,535 replies, while the overlapping
direct observer was clean at 6,062/6,062 with a 92.0 ms gap. Repeat 3 failed the
initial authenticated UDP-cookie exchange before sending workload traffic; its
broader overlapping direct-observer window also degraded by 49/6,061 with a
2,497.9 ms gap, so this second failure coincided with real underlay/endpoint-path
noise rather than isolating cleanly to the tunnel.
Both failures retained one core, an Up TUN and Tunnel routing, and both lifecycle
and cleanup checks were clean. This is strong selected tunneled/provider-path
evidence and makes this exact opaque row the first official AmneziaWG A/B
priority; it is still not attribution to VPNRouter without that matched bracket.

AmneziaWG ordinal 2 passed all three runs with 18,138/18,181 replies and a
519.2 ms worst gap. Repeat 2 lost 42/6,059 while the overlapping direct observer
was clean, but repeats 1 and 3 were essentially clean and no controlled failure
occurred. This neighbor is materially healthier than ordinal 1 and supports a
row/provider-path distinction rather than an AWG-family failure.

AmneziaWG ordinal 3 passed all three runs with 18,141/18,183 replies and a
988.3 ms worst gap. Its first run lost 39/6,061 while the observer was clean;
the next two runs passed with 0/3 losses and 153.0/235.7 ms gaps. Across all
four AWG rows, ordinals 0, 2 and 3 completed 9/9 runs without a controlled
failure; both controlled failures occurred on ordinal 1. This concentration is
why ordinal 1, not generic AWG, is the matched A/B target.

The single Naive row passed all three runs with 18,179/18,183 replies and a
199.1 ms worst gap. This is explicitly an effective-row result, not a claim of
protocol-pure Naive UDP: the current production resolver may pair Naive TCP with
same-IP Hysteria2 UDP. The original direct observer completed 84 fixed five-minute
cycles and covered the first Naive run. Before repeats 2 and 3, a replacement
observer failed closed because the dev-host route to the owned endpoint was no
longer independent of VPNRouter; no local route or VPN state was changed. Those
last two runs therefore retain WINBRAT Full-Tunnel/TUN/lifecycle evidence but no
simultaneous direct control.

The matrix refutes an application/core/TUN disconnect in this eight-hour window
but confirms intermittent UDP liveness failures while the tunnel remains Up.
No product/config/failover change is authorized from these measurements. The
next high-value A/B target is the exact opaque AmneziaWG ordinal-1 configuration;
that remains gated on an operator-provisioned equivalent fixture and a
management-safe official-client runner. The isolated XHTTP event is second
priority and additionally requires a vetted exact Xray-core build, not an
arbitrary latest prerelease. The completed BrowserBurst follow-up below covers
browser HTTPS/WebSocket stability; Mixed remains measurement-gated.

### BrowserBurst 3×10-minute follow-up — 2026-08-09 (completed)

Three independent cold BrowserBurst cycles ran on WINBRAT in Full Tunnel. Each
cycle started from zero owned core processes and an absent TUN, connected one
owned core, proved the fixed endpoint route through `VPNRouter-TUN`, observed a
positive TUN-byte delta from the exact spawned browser tree, ran the fixed page
for 600 seconds, then disconnected back to zero core, absent TUN and Direct
routing. A pinned official Chrome for Testing archive was expanded only beneath
the verifier-owned transient directory and removed during cleanup; it was not
installed system-wide.

| Cycle | Fetch success | Fetch errors | WebSocket replies | WebSocket errors | Worst fetch no-progress | Worst WS no-progress | Result |
|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 3,840 | 0 | 2,396 | 0 | 5,090 ms | 2,007 ms | PASS |
| 2 | 3,840 | 0 | 2,396 | 0 | 5,077 ms | 2,245 ms | PASS |
| 3 | 3,808 | 0 | 2,396 | 0 | 11,073 ms | 2,042 ms | PASS |
| **Total** | **11,488** | **0** | **7,188** | **0** | — | — | **3/3 PASS** |

All three cycles completed with `StayedConnected=true`, one owned core, an Up
TUN and Tunnel routing after load. Their lifecycle windows contained zero
error, FATAL or unknown events. Per-cycle and final cleanup returned one GUI,
zero owned core, absent TUN and Direct routing. The third cycle's 11.1-second
fetch no-progress interval stayed within the fixed 15-second limit and did not
affect WebSocket progress; it is a measurement, not a failure.

Combined with the UDP matrix, this supports a narrow conclusion: during the
observed period ordinary browser HTTPS/WebSocket traffic was stable, while rare
UDP liveness failures occurred on particular tunneled/provider paths without an
application, core or TUN disconnect. This does not prove every destination or
future session is healthy. It does show that a generic app restart or automatic
protocol switch is not justified by this evidence. Mixed browser+GameUdp load
is deferred until a real simultaneous-failure symptom makes the additional
complexity attribution-useful.

### Official AmneziaWG A/B readiness — 2026-08-09

The fixed official-client harness is implemented on branch
`codex/winbrat-official-ab`. It accepts no arbitrary client, executable,
profile, endpoint, rate or duration. The official AmneziaWG `2.0.2` package was
hash/signature verified and installed on WINBRAT with `DO_NOT_LAUNCH`; no
official tunnel has been started.

Pre-live adversarial review found and fixed watchdog task teardown races,
reparse-unsafe recursive cleanup, incomplete ACL rights checks, acceptance of a
stale Down `VPNRouter-TUN`, Boolean JSON type confusion, partial aggregate
schemas and data-integrity misclassification. Local parsers and 12/12 focused
contracts pass. Exact DeepSeek zero-tool review timed out without a verdict and
its permissions were not relaxed.

Read-only Control and Target preflights now stop only at `FixtureMissing`.
Phase B therefore remains `BLOCKED`, not failed: it still needs two separately
provisioned opaque final-name DPAPI fixtures, a healthy AWG ordinal-0 Control
and the ordinal-1 Target, plus their protected Tailscale-safe attestation
markers. Neither fixture contents nor keys may be read, copied, hashed or
derived by Codex. After they exist, the runner will execute three Control
cycles and permits three Target cycles only if all Control cycles pass.

### Exact next-task prompt — continue matched official-client A/B

```text
Continue the WINBRAT stability study from the completed 20×3 VPNRouter matrix
and 3×10-minute BrowserBurst evidence. Work only in a new test-tooling branch;
do not change product code, subscription state, keys, endpoints or user config.

Prerequisite: the operator must provision both already-planned final-name opaque
DPAPI fixtures on WINBRAT: Control matched to healthy AWG ordinal 0 and Target
matched to AWG ordinal 1, plus their protected `.tailscale-safe` markers. The
markers attest split `/1 + /1` defaults instead of a single `/0` WFP kill
switch. Never read, print, copy, hash or derive either fixture. If either is
absent or its ACL shape is unsafe, stop as BLOCKED.

Use the existing fixed official-AmneziaWG runner. Re-run read-only Control and
Target preflights, then run `brat-official-ab.ps1 -Mode Run3 -Profile Target`.
Do not add arbitrary executable, target, rate, duration or config arguments.

For attribution, bracket the failing row closely as VPNRouter → official client
→ VPNRouter under the same direct-observer windows. A failed control, watchdog
expiry, dirty cleanup or missing attribution is BLOCKED/ABORTED, never a network
failure. Compare recurrence only; do not claim server, client or product root
cause unless the matched bracket separates them. Record every finding in
plans/OPEN-DEFECTS.md before implementation and update this Outcome with exact
sanitized evidence. Do not release or merge.
```
