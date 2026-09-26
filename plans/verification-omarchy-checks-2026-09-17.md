# Omarchy Verification Report - 2026-09-17

**Date**: 2026-09-17 21:20 UTC  
**Target Worker**: `omarchy-test` (`192.168.0.59`, user `tester`)  
**Backend Snapshot Commit**: `b03af5570a5d9425c10910bb4218b84323150cb5` (Tree: `0a51fb17106f8316ef66938759eaae5de6d5c904`)  
**Plugin Snapshot Commit**: `0fa828a5ce2cea158963cb3b611112d318c03696` (Tree: `957a91f7073858e087758f900940929ba364e593`)  
**Remote Check Directories**:
- `/home/tester/vpnrouter-omarchy-checks/b03af5570a5d9425c10910bb4218b84323150cb5`
- `/home/tester/vpnrouter-omarchy-checks/plugin-0fa828a5ce2cea158963cb3b611112d318c03696`

---

## 1. Environment & Preflight

- **Worker OS**: Omarchy 4.0.4 Quattro (Arch Linux x86_64, kernel 6.18.6-arch1-1)
- **CPU / Load**: 8 cores, load average 1.37, 1.20, 1.11
- **Memory**: 7.7 GiB total, 5.8 GiB available, 15 GiB swap free
- **Disk**: `/home` 34 GiB available (31% used)
- **.NET SDK**: `/home/tester/.local/share/vpnrouter-omarchy-sdk/10.0.301` (`dotnet --version` 10.0.301)
- **Node.js**: `v26.8.2`
- **Python**: `3.14.7`
- **Quickshell**: `/usr/bin/quickshell` (present)
- **SSH Strict Identity**: Authenticated via `~/.ssh/id_ed25519` with strict host checking through proxyjump `pve-ninitux`.

---

## 2. Execution Results Summary

| Stage | Command | Exit Code | Result | Details |
|---|---|---|---|---|
| **Backend Build** | `dotnet build VPNRouter.Headless.Tests/... -c Release` | 0 | **SUCCESS** | 0 Errors, 32 Warnings |
| **Backend Tests** | `timeout 180 ./VPNRouter.Headless.Tests` | 1 | **FAIL** | 22 passed, 3 failed |
| **Plugin Packaging** | `python3 tests/test-packaging.py` | 1 | **FAIL** | 10 passed, 13 failures, 13 errors |
| **Plugin Node Tests** | `node tests/test-ui-*.js` (5 suites) | 0 | **SUCCESS** | All 5 test suites passed |
| **Plugin QML Runner** | `bash tests/qml-test-runner.sh` (timeout 60) | 0 | **SUCCESS** | All components loaded offscreen |

---

## 3. Detailed Failure Analysis

### 3.1 Backend Tests (`ProtocolTests` executable, exit code 1)
- **Total**: 22 passed, 3 failed.
- **Passed**: All 22 `LifecycleChecks` passed (initial state, typed readiness, guards, teardown, cancellation, ownership, capability checks). 20 protocol tests passed.
- **Failures**:
  1. `TestSingleOrdinaryCommandBusyAsync`:
     - *Message*: `Assertion failed: resp2 must have error`
     - *Cause*: `ProtocolDispatcher.DispatchAsync` was recently updated to treat `snapshot` as a non-blocking read-only query that bypasses active ordinary operation exclusivity (lines 85-88). The test dispatches a long `connect` operation and sends `snapshot`, expecting a `busy` error.
  2. `TestBurst2000RequestsAsync`:
     - *Message*: `Assertion failed: First response ID. Expected 'req-1', but got 'req-2'.`
     - *Cause*: `req-1` was an in-flight async `connect`. In the burst loop, `req-2` immediately received a `busy` error response and was written to stdout before `req-1` completed, so the first frame received on the output stream was `req-2`.
  3. `FeatureChecks`:
     - *Message*: `Assertion failed: Vless.ActiveServer mismatch` at line 1004 of `FeatureChecks.cs`.
     - *Cause*: In `CheckSubscriptionAddSelectWithFakeServersAndModeTransitionsAsync`, selecting a subscription server via `servers.select` expects `settingsAfterSelect.Vless.ActiveServer == "Sub-Frankfurt-1"`. In `ServerFeature.cs`, `isSubscription` matching requires matching properties across `VlessServerEntry`.

### 3.2 Plugin Packaging Tests (`test-packaging.py`, exit code 1)
- **Total**: 36 tests ran: 10 passed, 13 failures, 13 errors.
- **Root Cause**:
  `setup` contains an `ensure_shell_unlocked` guard:
  ```bash
  if command -v omarchy-shell >/dev/null 2>&1; then
    status="$(timeout 3s omarchy-shell lock status 2>/dev/null)"
  ```
  On `omarchy-test`, `/usr/bin/omarchy-shell` exists. In non-interactive SSH sessions, `OMARCHY_PATH` is not exported, causing `omarchy-shell lock status` to exit with code 1 (`OMARCHY_PATH is not set`).
  `setup` fails closed: `setup: error: failed to query Omarchy session lock status; failing closed.`
  Consequently, all unit tests in `test-packaging.py` invoking `./setup` fail on this check unless `omarchy-shell` is absent or mocked.

### 3.3 Plugin Node & QML Verification
- `test-ui-contract.js`: PASS (GPL-3.0-or-later, manifest v1, 20 QML syntax checks, secrets safety).
- `test-ui-i18n.js`: PASS (141 keys in parity between `en.json` and `ru.json`).
- `test-ui-model.js`: PASS (latency, protocols, filters, MTU, CIDR).
- `test-ui-protocol.js`: PASS (frame size 256 KiB, depth 32, request IDs).
- `test-ui-service.js`: PASS (revisions, queueing, deadlines, input domains).
- `qml-test-runner.sh`: PASS (Offscreen Quickshell execution completed within 60s; note: `KeyboardPanel.qml` substitution used as `PanelWindow` backend requires a display).
