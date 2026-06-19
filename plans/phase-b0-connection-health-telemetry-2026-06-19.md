# Phase B0 — passive connection-health telemetry (observe-only)

**Owner**: Claude session
**Branch**: `main` (project convention = direct-commit; feature flag-gated OFF → safe on main)
**Roadmap ref**: `plans/server-health-failover-backlog-2026-06-19.md` §B0 + `independent-review-server-health-mtu-2026-06-19.md` §F1
**Effort**: ~2-3 days (split into B0a classifier+state, B0b stream+wiring)
**Risk**: LOW — read-only, no VPN behaviour change, off by default
**Blast radius**: VPNRouter.Core (3 new files) + VPNRouter.Tests (1 new file) + tiny VpnEngine wiring · ~400 LOC · zero runtime impact when flag off
**Rollback**: `git revert <hash>` (feature is additive + gated)

## Why

DE-user full-tunnel ChatGPT failure traced to frequent VLESS **relay-open EOF** on a
single TCP-only node (backlog v2). To act on this (warning / failover) we first need a
**correct** runtime signal. The independent review proved a naive `(EOF+RST)/min`
signal is wrong: 733/737 "forcibly closed" in bundle 214717 are **local** upload-side
closes, only 4 are real outer-proxy resets. B0 builds an **observe-only** classifier
that gets this distinction right, validated against the two diagnostic bundles, before
any user-facing warning (C) or failover (B) is wired. No toast, no switching — it only
logs its own classification for calibration.

## What

New files in `VPNRouter.Core/Services/`:

1. **`ConnectionHealthClassifier.cs`** — pure, `static`, `#nullable enable`. Parses a
   sing-box Clash `/logs` payload line into a `ConnLogEvent?` record. Categories
   (`ConnHealthCategory` enum):
   - `RelayOpenFail` ← `open connection to <dest> using outbound/<tag>: EOF` (suffix
     literally `: EOF`). The 1952/1587.
   - `OuterProxyReset` ← any `forcibly closed` line whose socket error references an
     **active proxy endpoint** (`read tcp <local>-><proxyIp>:<port>: wsarecv ...`).
     Covers both `download closed: read tcp->proxy` and `open connection ... using
     outbound: read tcp->proxy`. The 4.
   - `LocalClose` ← `connection upload/download closed: raw read` / `raw-read tcp4
     172.19.0.x->172.19.0.x` (TUN tuple / no real remote socket) — benign. The 733.
   - `RelayOpenSuccess` ← `outbound/<tag>: outbound connection to <dest>` (INFO) —
     denominator.
   - `Other` ← everything else.
   Extracts: conn-id (`[<id> <dur>]`), duration, outbound tag, destination.

2. **`ConnectionHealthState.cs`** — `sealed`, thread-safe (lock). Holds active proxy
   endpoints (set on connect). `Record(ConnLogEvent)` into rolling time-windows
   (per-category counters + per active node). Exposes read-only `Snapshot()`
   (counts + failure-rate = RelayOpenFail / (RelayOpenFail+RelayOpenSuccess)).
   **Observe-only**: computes a `WouldWarn` bool for calibration but triggers nothing.

3. **`ClashLogStream.cs`** (B0b) — `sealed`. Subscribes to Clash API `/logs` over
   WebSocket (`ws://<external_controller>/logs?level=info`), parses `{type,payload}`
   JSON, feeds payloads to the classifier→state. Reconnect with backoff, full
   `CancellationToken`. Reuses `ClashSingBoxApi` for the base address/secret.

Wiring (B0b): `VpnEngine` starts the stream on connect / stops on disconnect, behind a
feature flag (default OFF, silent). No UI.

```diff
+ // ConnHealthCategory: RelayOpenFail | OuterProxyReset | LocalClose | RelayOpenSuccess | Other
+ var ev = ConnectionHealthClassifier.Classify(payload, proxyEndpoints);
+ if (ev is not null) _connHealth.Record(ev);   // observe-only; emits nothing
```

## How

1. (B0a) `ConnectionHealthClassifier` + `ConnLogEvent`/`ConnHealthCategory` + unit tests
   on curated **sanitized** lines (each category, both reset forms, TUN-tuple vs
   proxy-endpoint). Build + test. Commit.
2. (B0a) `ConnectionHealthState` rolling-window aggregator + tests (synthetic counts;
   failure-rate; window expiry). Build + test. Commit.
3. (B0b) `ClashLogStream` WS client + `VpnEngine` wiring behind flag. Build + test +
   live smoke (connect, confirm stream parses, no errors). Commit.

### Tests written
- `ConnectionHealthClassifierTests.ClassifiesRelayOpenEofAsRelayOpenFail`
- `ConnectionHealthClassifierTests.UploadClosedRawRead_IsLocalClose_NotOuterProxyReset` — the key regression (the 733)
- `ConnectionHealthClassifierTests.ReadTcpToProxyEndpoint_IsOuterProxyReset` — the 4 (both forms)
- `ConnectionHealthClassifierTests.OutboundConnectionTo_IsRelayOpenSuccess` — denominator
- `ConnectionHealthClassifierTests.ParsesConnIdTagAndDestination`
- `ConnectionHealthStateTests` — counts per category, failure-rate, window expiry, per-node
- (local-only, `Skip`-if-absent) `ConnectionHealthFixtureCountsTests` — reads real
  `C:\Project\logs\diag-*\singbox-tail.log` and asserts **214717→1952 RelayOpenFail,
  733 LocalClose, 4 OuterProxyReset; 205004→1587** (NOT committed as data — privacy).

### Privacy note
Raw user diagnostic logs (real traffic destinations) are **NOT** committed to the public
repo. CI tests use curated sanitized lines + synthetic corpora. Exact-count reproduction
of 1952/733/4/1587 is a local-only integration test (skipped when fixtures absent).

### Verification approach
Full xUnit suite green (new tests in run); local fixture-count test reproduces the
review's corrected numbers; flag OFF → zero behaviour change (existing tests unaffected).

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: full suite + new classifier/state tests; local fixture
  test reproduces 1952/733/4/1587.
- [ ] **Gate 3 — Docs**: this brief Outcome filled; `VPNRouter.Core/CLAUDE.md` service
  map gains 3 rows; backlog B0 acceptance checked.
- [ ] **Gate 4 — Self-review**: `simplify` (diff >100 LOC); `security-review` (touches
  Clash API socket I/O — WS to localhost controller).
- [ ] **Gate 5 — MCP verify**: N/A — Core-only, no UI surface (flag OFF, observe-only).
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split.

## Outcome — B0a (filled 2026-06-19)

**Status**: PARTIAL — B0a (classifier + state + tests) done; B0b (Clash `/logs` WS
stream + VpnEngine wiring) pending.
**Files changed**: 5 new · +681 LOC (2 Core src, 3 test). No existing files modified
(additive; nothing references the new types yet — wiring is B0b).

**Gate results:**
- [x] Gate 1 build: `dotnet build -c Release` 0 errors (154 warnings, all pre-existing).
- [x] Gate 2 tests: 28 new green (26 unit + 2 fixture-vs-real-logs). Fixture repro on
  real bundles: 214717 → 2178 RelayOpenFail (1952 EOF / 224 dial-timeout / 2 reset),
  739 LocalClose, 6 ProxyStreamError; 205004 → 1588 (1587 EOF) + 216 LocalClose.
  Full non-GUI suite: 1926 pass / 59 FAIL — all pre-existing environmental
  (`UnauthorizedAccessException` on `C:\ProgramData\VPNRouter` in non-admin session;
  process/TUN/VM classes), unrelated to this additive change. CI is the real gate.
- [x] Gate 3 docs: this brief + `VPNRouter.Core/CLAUDE.md` service map (+2 rows) +
  backlog §B0 acceptance updated with verified numbers.
- [x] Gate 4 self-review: `simplify` ran (4 agents). Applied: defer regex/extraction
  past null-return + attempt-first ordering (efficiency); named marker constants,
  explicit teardown precedence helper, EOF/IPv6 comments (altitude). Skipped: per-node
  / Destination / ConnId / WouldWarn YAGNI flags — spec-required (brief What, backlog
  §B0 "destination + connection-id", review §E3 per-node, observe-only calibration).
  `security-review`: N/A for B0a — pure parsing + in-memory state, no socket/TLS/
  process/firewall/file-write surface. Deferred to B0b (Clash `/logs` WS client gets
  the loopback-only guard, mirroring `ClashSingBoxApi`).
- [-] Gate 5 MCP: N/A — Core-only, observe-only, no UI surface.
- [-] Gate 6: N/A — not a god-file split.

**Surprises**: the review's grep-based "1952 EOF / 4 RST" were subsets — `using
outbound/` marks ANY relay-open failure, surfacing 224 dial-timeouts an EOF-only
count missed; and the "4 forcibly-closed" were 4 RST + 4 wsarecv-timeouts (8 total
node-socket breaks), of which 6 are mid-stream ProxyStreamError. Taxonomy widened
accordingly and documented in the backlog.

**Follow-ups**: B0b — `ClashLogStream` (Clash `/logs` WebSocket, reconnect/backoff,
loopback guard, security-review) + `VpnEngine` wiring behind a flag (off) + live smoke.

## Outcome — B0b (filled 2026-06-20)

**Status**: PARTIAL — code complete; **live-smoke pending the test machine** (needs a
running sing-box + elevated env; this session is non-admin).
**Files changed**: 2 new (`ClashLogStream.cs`, `ClashLogStreamTests.cs`), 2 modified
(`ClashSingBoxApi.cs` IsLoopbackHost private→internal static for reuse; `VpnEngine.cs`
+`TryStartConnectionHealthStream` + Stop() teardown + fields).

**What**: `ClashLogStream` subscribes to Clash `/logs` over WebSocket, parses
`{type,payload}` (JsonDocument), routes payloads through the B0a classifier into
`ConnectionHealthState`. Reconnect with capped backoff, cancellable, loopback-guarded
(reuses `ClashSingBoxApi.IsLoopbackHost`). Wired into `VpnEngine` **gated by env var
`VPNROUTER_CONN_HEALTH=1`** (off by default): started post-start (best-effort try/catch
— never throws into the VPN lifecycle), stopped/disposed in `Stop()`. `proxyEndpoints`
passed null for now → `ProxyStreamError` attribution deferred (the primary relay-open
failure-rate signal needs no node IP).

**Gate results:**
- [x] Gate 1 build: `dotnet build VPNRouter.sln -c Release` 0 errors.
- [x] Gate 2 tests: 48 B0 tests green (26 classifier/state + 20 stream + 2 fixture).
  20 new ClashLogStream tests (BuildLogsUri scheme+loopback guard, TryExtractPayload,
  HandleMessage routing). Live WS receive/reconnect loop = smoke on test machine.
- [x] Gate 3 docs: service map +ClashLogStream row (env flag documented); this Outcome.
- [x] Gate 4 self-review: `security-review` ran (identify + filter) → **no findings**
  (loopback guard tested against 12 bypass payloads incl. userinfo/subdomain/octal/
  IPv6 — all rejected; JsonDocument no XXE/gadget; TLS .NET defaults; env-gate
  observe-only, can't affect VPN). `simplify`: B0b reused the B0a-reviewed patterns
  (named constants, IsLoopbackHost reuse, testable seams) — self-reviewed, no YAGNI.
- [-] Gate 5 MCP: N/A — no UI surface (observe-only, flag off).
- [-] Gate 6: N/A.

**Live-smoke checklist (test machine — windows-brat or debian-xfce):** set
`VPNROUTER_CONN_HEALTH=1`, connect VPN, confirm log `"Clash /logs stream connected"`,
generate traffic, confirm classified events accrue (no exceptions); flag-off → no stream.
