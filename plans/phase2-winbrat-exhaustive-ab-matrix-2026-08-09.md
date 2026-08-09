# Phase 2 — exhaustive WINBRAT protocol-row and client A/B matrix

**Owner**: Codex  
**Branch**: `codex/winbrat-official-ab`
**Parent**: `plans/phase2-winbrat-protocol-load-matrix-2026-08-08.md`  
**Risk**: HIGH — six to sixteen hours of repeated remote tunnel changes on the dedicated WINBRAT VM  
**Blast radius**: test tooling and WINBRAT only; no product/runtime code

## Objective

Run the same fixed owned-endpoint GameUdp workload repeatedly against every
privacy-safe selectable row in the user's current subscription, then compare
matched AWG and HY2 profiles with independent official clients. Determine
whether the observed three-second UDP reply gaps follow VPNRouter, a protocol
family, a particular row/cohort, the common WINBRAT underlay, or the remote
server path.

**Current status, 2026-08-09**: Phase A completed all 60 planned VPNRouter
cycles and Phase A2 completed three ten-minute BrowserBurst cycles. The fixed
official-AmneziaWG runner is implemented and the pinned `2.0.2` client is
installed on WINBRAT without launching a tunnel. Both official Control and
Target preflights stop at `FixtureMissing`; Phase B live traffic has not
started and remains explicitly blocked on operator-provisioned opaque fixtures
and their protected Tailscale-safe attestation markers.

## Discovered matrix

The 2026-08-09 safe UI survey found 20 rows by public protocol labels only:

- VLESS Reality: 4;
- VLESS WebSocket: 3;
- VLESS XHTTP: 4;
- Hysteria2: 4;
- AmneziaWG: 4;
- Naive: 1;
- TUIC, DNS tunnel and Shadowsocks: absent, therefore `SKIP`.

These are protocol rows/cohorts, not yet proven unique endpoints. The test does
not read subscription URLs, row names, hosts, ports, keys or generated config.
Same-host pairing can make the effective outbound a cohort; results must not be
called server-specific without a separate privacy-safe runtime proof.

## Phase A — VPNRouter exhaustive run

1. Fix ordinal selection before accepting any ordinal above zero:
   `HOME`, then one `DOWN` per logical row; inspect only the currently selected
   row for one allowlisted public protocol label.
2. Fail closed unless WINBRAT has one GUI, zero owned core, no Up TUN and
   Auto-select already Off. Never toggle Auto-select as part of the matrix.
3. Live-prove ordinals 0, 1 and 2 from one class, an absent ordinal with exact
   selection restoration, and clean connect/disconnect after each.
4. For each of the 20 rows run three cold five-minute repeats. Each repeat is:
   clean state → select → connect → prove Tunnel/Full Tunnel/TUN correlation →
   fixed GameUdp → lifecycle summary → disconnect → clean state.
5. Continue after a structured `ReplyGap` measurement, but stop on identity,
   selector, payload-integrity, route-attribution or cleanup failure.

Expected duration: about six to seven hours including 60 cold connections.

Completed `2026-08-09T06:42:17Z`: all 20 rows and 60 cold repeats finished.
The result was 57 `PASS` and three controlled network measurements: one XHTTP
`ReplyGap`, plus one AWG `ReplyGap` and one AWG `CookieFailure` on AWG ordinal
1. VPNRouter remained connected with one core and an Up TUN after every load;
all lifecycle windows were free of error/FATAL/unknown events and final cleanup
restored zero core, absent TUN and Direct routing. The complete per-family
aggregates and paired-observer interpretation are in the parent Outcome.

## Phase A2 — browser and mixed load

Outcome: three independent ten-minute BrowserBurst cycles completed `PASS`
with 11,488 successful fetches, 7,188 successful WebSocket replies and zero
fetch/WebSocket errors. Each cycle proved Full Tunnel, process/TUN-byte
correlation and clean teardown. The broader per-row and Mixed plan below was
deliberately not executed: no browser symptom reproduced, so more row churn
would add cost without improving UDP attribution. Mixed remains
`MeasurementGated` until a simultaneous browser+UDP failure is observed.

The exhaustive GameUdp matrix proves repeated TUN startup, low-rate UDP
cadence, bounded bursts and multi-second reply-gap behaviour. It does not prove
browser request/stream churn or simultaneous TCP+UDP stability. The existing
`BrowserBurst` and `Mixed` verifier branches still return `MeasurementGated`,
so they are required follow-up work rather than completed coverage.

BrowserBurst must use the installed Microsoft Edge engine on WINBRAT, never a
PowerShell HTTP approximation:

- verify the canonical Edge binary and valid Microsoft Authenticode chain;
- start one dedicated headless process with a new fixed test-only profile and
  only the owned `/browser` page; never inspect an existing browser/profile;
- use a loopback-only DevTools session restricted to that process and fixed
  page, reading only the page's aggregate `fetchOk`, `fetchFail`, `wsOk`,
  `wsFail` and `done` state through one fixed expression; target metadata and
  arbitrary DOM evaluation must never leave WINBRAT;
- prove Full Tunnel, endpoint route through `VPNRouter-TUN`, a dedicated Edge
  process/socket match and TUN-byte growth above a quiet window;
- record aggregate counts and separate maximum fetch/WebSocket no-progress
  intervals so progress on one channel cannot hide a stall in the other; never retain
  DOM dumps, URLs, browser history, PIDs, addresses or screenshots;
- terminate only the spawned process tree. Resolve and validate the temporary
  profile path remains under the fixed per-run browser artifact root before
  recursive cleanup; never touch any existing Edge profile.

Run a simultaneous low-rate non-WINBRAT HTTP/WebSocket observer against only
the owned endpoint, using fixed request/message sizes and aggregate outcomes.
The existing UDP observer is not enough to rule out an HTTP/WebSocket service
incident. A browser-side failure with a contemporaneous observer failure is
endpoint/path evidence and cannot be attributed to VPNRouter.

Run one ten-minute BrowserBurst on each of the 20 selected rows after the UDP
matrix. Then run a ten-minute Mixed profile on one clean VLESS, one clean HY2,
one clean AWG and every row with a repeated `ReplyGap`: BrowserBurst remains
active while two consecutive fixed five-minute GameUdp samples run. A missing
class is `SKIP`; any loss of route/process/TUN attribution is `BLOCKED`.

Functional acceptance is pre-registered: `done=true`, zero fetch/WebSocket
failures, no fetch progress gap over 15 seconds, no WebSocket progress gap over
5 seconds, dedicated Edge socket proof throughout, and clean process/profile
teardown. Counts and negotiated protocol are reported; performance/latency
regression thresholds remain baseline-gated rather than invented from one run.

The fixed browser payload is now source-built locally. It accepts no command
line input, launches only the canonical fixed Edge path against the owned page,
uses a fresh path-validated profile, exposes DevTools on loopback with an
ephemeral port, returns one aggregate JSON object, uses monotonic progress
timing, permits only one machine-wide run and fails closed on process/profile
cleanup. Focused tests are 11/11 and the generated ZIP contains one executable
whose SHA-256 matches its sidecar. This is build evidence only: the payload is
not approved or deployed to WINBRAT until the active UDP matrix ends and the
route/process attribution gate is integrated.

This page models modern browser HTTP/2 streams and WebSocket sessions. GameUdp
is a bounded synthetic game/voice sentinel. Neither is claimed to reproduce
Dota, Roblox or proprietary game wire protocols, and no third-party service is
loaded. Expected combined Phase A + A2 duration is roughly eleven to twelve
hours, before targeted official-client A/B.

## Phase B — independent-client A/B

- AWG: official AmneziaWG for Windows.
- HY2: official Hysteria 2 CLI in Windows TUN mode.
- Optional VLESS: v2rayN only when `xray.exe` native TUN is proven; a sing-box
  wrapper is not an independent core comparison.

Pinned official binaries prepared locally under ignored `artifacts/`. After
Phase A completed, the exact AmneziaWG package was installed once on WINBRAT
with `DO_NOT_LAUNCH`; no official tunnel was launched. Hysteria remains
uninstalled and blocked behind its separate CGNAT-address safety gate:

- AmneziaWG Windows client `2.0.2`, `amneziawg-amd64-2.0.2.msi`, SHA-256
  `1b7308d0c74685193dee5d30fd30f370b5a2748a7f648869cd16f25286efc784`;
  GitHub's release digest matches and Authenticode is valid.
- Hysteria `app/v2.12.0`, `hysteria-windows-amd64.exe`, SHA-256
  `f1f782532aa20fe72574393a0e3775cfe10f7edb07f9af6b7bca5c85e2afdd6c`;
  GitHub's release digest matches, but the executable is not Authenticode
  signed, so only this exact source-reviewed hash may be allowed.

Primary sources: [AmneziaWG 2.0.2 release](https://github.com/amnezia-vpn/amneziawg-windows-client/releases/tag/2.0.2),
[AmneziaWG enterprise commands](https://github.com/amnezia-vpn/amneziawg-windows-client/blob/master/docs/enterprise.md),
[Hysteria 2.12.0 release](https://github.com/apernet/hysteria/releases/tag/app/v2.12.0),
[Hysteria TUN configuration](https://v2.hysteria.network/docs/advanced/Full-Client-Config/).

Profiles must be provisioned as opaque test fixtures without Codex reading or
copying keys. Before any full-tunnel alternative starts, preserve the
Tailscale/WinRM control route, exclude `100.64.0.0/10`, arm a client-specific
local watchdog, and verify cleanup without broad adapter-disable commands.
The fixed allowlisted AmneziaWG runner now exists. Phase B remains `BLOCKED`
only because the two opaque matched fixtures and their attestation markers are
absent; it is never approximated with another sing-box GUI.

The fixed Phase B contract is symmetric and time-bracketed:

1. Recheck the exact executable hash before every start; revalidate the full
   Authenticode chain for AmneziaWG. Hysteria is authorized only by its pinned
   official GitHub hash.
2. Require VPNRouter cleanly disconnected, a healthy Tailscale adapter and
   `100.64.0.0/10` management route, and no address collision between the
   started test TUN and that prefix. These are state-only checks; fixture
   content remains unread.
3. Measure a quiet adapter-byte window, arm a local ten-minute client-specific
   watchdog immediately before start, then prove the endpoint route belongs to
   the intended test TUN and workload produces a positive byte delta above the
   quiet window. Do not impose one byte ratio on AWG and HY2.
4. Run exactly three cold five-minute repeats for the official client, matching
   the app sample count. For a repeatedly failing app row, use a close-time
   VPNRouter → official client → VPNRouter bracket. If the final app run does
   not reproduce, attribution is `INCONCLUSIVE`.
5. Stop only the fixed client/service, disarm the watchdog and prove no test
   process/TUN remains. Any dirty cleanup halts the batch. A fired watchdog is
   `ABORTED`, never a network `FAIL`, and its local aggregate event is recovered
   after management connectivity returns.
6. Run one known-healthy matched control first. A control failure blocks that
   Phase B batch as fixture/harness suspect. Missing or expired protocol-matched
   fixtures are `BLOCKED`, not substituted.

Hysteria-specific safety gate: official `app/v2.12.0` defaults the TUN IPv4
address to `100.100.100.101/30`, inside Tailscale's management prefix. The
opaque fixture must therefore declare a non-`100.64.0.0/10` address and route
exclusion, and the runner must verify the resulting adapter address after
start. The official source is
[`app/cmd/client.go`](https://github.com/apernet/hysteria/blob/app/v2.12.0/app/cmd/client.go#L1152-L1232).

### Opaque fixture contract

The operator provisions fixtures locally on WINBRAT. Codex and verifier code
may check only existence, ACL shape, fixed filename and resulting runtime
state; they never read, hash, copy, print or retain fixture contents.

| Client | Fixed fixture | Fixed runtime identity |
|---|---|---|
| AmneziaWG Control (healthy AWG ordinal 0) | `C:\ProgramData\VPNRouterTestFixtures\AWG\VPNRouter-AB-AWG-Control.conf.dpapi` | service `AmneziaWGTunnel$VPNRouter-AB-AWG-Control`, adapter `VPNRouter-AB-AWG-Control` |
| AmneziaWG Target (AWG ordinal 1) | `C:\ProgramData\VPNRouterTestFixtures\AWG\VPNRouter-AB-AWG.conf.dpapi` | service `AmneziaWGTunnel$VPNRouter-AB-AWG`, adapter `VPNRouter-AB-AWG` |
| Hysteria | `C:\ProgramData\VPNRouterTestFixtures\HY2\VPNRouter-AB-HY2.yaml` | executable hash for `2.12.0`, adapter `VPNRouter-AB-HY2` |

Both fixture directories must deny inherited broad user access and permit only
`SYSTEM` and the fixed WINBRAT test administrator. The Hysteria fixture must
explicitly define the fixed TUN name, a non-CGNAT IPv4 address, full IPv4
routing and `100.64.0.0/10` exclusion. AmneziaWG uses the official DPAPI form;
the test runner must never create or decrypt it.

Each AWG fixture also requires a protected empty attestation marker beside it:
`VPNRouter-AB-AWG-Control.tailscale-safe` and
`VPNRouter-AB-AWG.tailscale-safe`. Creating a marker is an operator assertion
that the corresponding opaque profile uses split defaults
`0.0.0.0/1` + `128.0.0.0/1` (and the two IPv6 halves when applicable), not a
single `/0` that enables the official client's WFP kill switch. The runner
never infers this from or reads the DPAPI payload; missing marker is
`FixtureAttestationMissing`.

After start, runtime-only proof must show:

- exactly one expected client process/service and one expected Up adapter;
- the adapter has no address inside `100.64.0.0/10`;
- the management prefix still resolves through the existing Tailscale adapter;
- the fixed load endpoint resolves through the expected test adapter;
- workload adapter bytes increase above a preceding quiet-window delta;
- no VPNRouter-owned core or `VPNRouter-TUN` is present.

The AmneziaWG MSI is installed once with the official `DO_NOT_LAUNCH` property
only after local and remote hash/signature verification. Hysteria remains a
portable exact-hash executable with update checks disabled and no retained raw
stdout/stderr. A fixed ten-minute local scheduled watchdog is armed immediately
before client start: it may stop only the exact service/process and writes only
`Armed`, `Fired` or `Disarmed`. WinRM loss is awaited until watchdog recovery;
the corresponding sample is `ABORTED` and excluded from network attribution.

The direct observer must record bounded start/end times so its windows can be
matched to app and official-client runs. Its fixed 256-byte profile covers UDP
liveness, loss, reorder and multi-second reply gaps; it does not prove MTU or
fragmentation behaviour. No official-client raw logs are retained.

### Phase B runner and live readiness outcome — 2026-08-09

The tooling surface is fixed and non-generic:

- `brat-verify -Action altclient` accepts only `AmneziaWG`, fixed
  `Preflight|Install|Cycle|Cleanup` operations and `Control|Target` profiles;
- `brat-official-ab -Mode Run3 -Profile Target` runs three Control cycles first
  and allows three Target cycles only if every Control cycle passes;
- every cycle pins the package/client/payload hashes, arms a ten-minute local
  watchdog before tunnel start, proves the live WinRM peer remains routed over
  Tailscale, proves the endpoint route and positive adapter-byte correlation,
  then requires exact teardown;
- watchdog expiry, transport loss, dirty state, malformed booleans, missing
  aggregate fields, corrupt/unknown replies and cleanup uncertainty are
  `ABORTED` or `BLOCKED`, never counted as a network failure;
- fixture bytes, hashes, keys, raw client output, addresses, routes, process IDs
  and endpoint metadata never leave WINBRAT.

The exact official AmneziaWG `2.0.2` MSI was installed successfully through
this flow. Subsequent privacy-safe preflights for both profiles returned only
`BLOCKED / FixtureMissing`; management connectivity and clean state remained
intact, and no official tunnel/workload ran. After the operator provisions both
final-name DPAPI fixtures and markers with protected ACLs, the only approved
continuation is:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-official-ab.ps1 -Mode Preflight -Profile Control
powershell -ExecutionPolicy Bypass -File tools/brat-official-ab.ps1 -Mode Preflight -Profile Target
powershell -ExecutionPolicy Bypass -File tools/brat-official-ab.ps1 -Mode Run3 -Profile Target
```

`Install` is a required separate bootstrap on a fresh WINBRAT; it has already
completed on the current VM. Exact DeepSeek-in-Qwen zero-tool review was
attempted and timed out without a verdict; permissions were not relaxed.

## Interpretation

- Same row fails in VPNRouter and official client: server/provider path or a
  shared Windows/underlay layer is primary.
- VPNRouter fails repeatedly while matched official client stays clean:
  VPNRouter/sing-box-lx becomes primary.
- Many rows and both independent clients fail, direct WINBRAT stays clean:
  common tunneled underlay/provider path becomes primary.
- Direct WINBRAT also shows the gap: host, underlay or load endpoint is primary.

Complete outcome handling:

| VPNRouter | Official client | Observer | Disposition |
|---|---|---|---|
| pass | pass | pass | no incident observed |
| fail twice around official run | pass | pass | VPNRouter/sing-box path candidate |
| pass | fail | pass | official client or opaque fixture candidate; not a product defect |
| fail | fail | pass | protocol/server/provider or shared Windows TUN path; not app-specific |
| pass | pass | fail | observer-path incident; app/client comparison remains clean |
| fail | pass | fail | path/time-dependent and inconclusive |
| pass | fail | fail | path/time-dependent and inconclusive |
| fail | fail | fail | owned endpoint or common external incident; discard for client attribution |

No automatic MTU change, protocol switch or release is justified from isolated
measurements. A product change requires repeated attribution to a product-owned
layer.

## Gates

- [x] Selector contract tests and live ordinal proof pass.
- [x] Phase A completes with exact cleanup and sanitized aggregate evidence.
- [x] AmneziaWG is official, pinned, independently verified and installed
  without launching a tunnel.
- [ ] Matched opaque profiles and Tailscale-safe watchdog are provisioned.
- [x] Phase B is explicitly reported blocked before live traffic because both
  opaque fixtures are absent.
- [x] Findings and current outcome are recorded before commit/push/PR update.
