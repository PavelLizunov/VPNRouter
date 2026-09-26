# Adversarial Review: P05 — UI Consumer Synchronization

> **TL;DR:** Deep adversarial analysis of the Omarchy Plugin (Service.qml, Quickshell views) and Desktop UI Consumer (MainWindowViewModel) synchronization surfaces **2 P0**, **7 P1**, and **14 P2** findings across state-machine gaps, concurrency hazards, fail-open traffic leaks, protocol mismatches, and security/privilege boundary issues.

**Lane:** P05_ui_consumer_sync  
**Reviewer:** Opus adversarial worker  
**Date:** 2026-09-26  
**Sources Audited:**
- `omarchy-vpnrouter/Service.qml` (922 lines)
- `omarchy-vpnrouter/ui/SettingsView.qml` (396 lines)
- `omarchy-vpnrouter/ui/ServersView.qml` (299 lines)
- `omarchy-vpnrouter/ui/RulesView.qml` (219 lines)
- `omarchy-vpnrouter/ui/HeroCard.qml` (253 lines)
- `omarchy-vpnrouter/lib/Protocol.js` (294 lines)
- `omarchy-vpnrouter/lib/Model.js` (207 lines)
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (7429 lines)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs` (559 lines)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Settings.cs` (703 lines)
- Inventory evidence sheets: O02_B069_O02, O03_B070_O03, A02_B002_A02, A06_B010_A06

---

## 1. SUBSYSTEM STATE MACHINES

### 1.1. Service.qml Subprocess Lifecycle (Implemented)

```
                 Component.onCompleted
                        │
                        ▼
               checkHelperAndStart()
                        │
                        ▼
              ┌──────────────┐
              │   starting   │  (starting=true, helperPresent=false)
              └──────────────┘
                        │
              backendProc.onStarted
                        │
                        ▼
              ┌──────────────┐
              │  unavailable  │  (starting=false, helperPresent=true)
              └──────────────┘
                        │
              Qt.callLater(refreshSnapshot)
                        │
                        ▼
              ┌──────────────┐
              │ disconnected  │ ──connect()──▶ ┌──────────────┐
              └──────────────┘                 │  connecting   │
                     ▲                         └──────────────┘
                     │                                │
               disconnect()                    backend snapshot
                     │                                │
              ┌──────────────┐                        ▼
              │disconnecting │ ◀──────────── ┌──────────────┐
              └──────────────┘               │  connected    │
                                             └──────────────┘
```

**Process exit branch:**
```
  backendProc.onExited(exitCode)
         │
         ├── exitCode !== 0 → retryTimer (2s → 3s → 4.5s … max 30s)
         │                          │
         │                          └──▶ checkHelperAndStart()
         │
         └── exitCode === 0 → ⚠ NO RETRY, PERMANENT "unavailable"
```

**Teardown branch:**
```
  Component.onDestruction → tearDown()
         │
         ├── retryTimer.stop()
         └── backendProc.signal(15)  // SIGTERM only, no SIGKILL fallback
```

### 1.2. State Machine Defects — Unreachable States & Missing Transitions

| Gap | Evidence | Impact |
|-----|----------|--------|
| **No recovery from clean exit** | `Service.qml:328` — `if (exitCode !== 0)` gates retry | `exitCode === 0` (systemd restart, EOF on stdin, maintenance exit) leaves plugin permanently dead |
| **No SIGKILL escalation** | `Service.qml:273-278` — only `signal(15)` | Hung backend holds TUN device + routing tables indefinitely |
| **`error` state unreachable from Service.qml** | `state` only set by `applySnapshot()` from backend; `errorCode` set independently | `isError` computed property can be true while `state` is `"disconnected"`, creating contradictory UI displays |
| **Missing `disconnecting` → `error` transition** | No error path during disconnect | If disconnect RPC fails, state stays `"disconnecting"` until 15s deadline timeout |

### 1.3. Desktop MainWindowViewModel State Machine (Contrast)

The Desktop app uses a fundamentally different state ownership model:

- **Connected**: Set by `OnEngineConnected(pid)` through a readiness guard + anti-race filtering (`Service.qml:119-136`)
- **Stopped**: Set by `OnEngineStatus("Stopped")` (`Connection.cs:96-107`)
- **Guarded by**: `IsConnecting`, `IsApplying`, `_isReconnecting` triple-lock in `ToggleConnectionAsync` (`Connection.cs:170-178`)

**Key desynchronization risk**: The Desktop uses `TwoPhaseStartCoordinator` with typed outcomes (`SingBoxStarted` → `Connected`) and explicit `CaptureReadinessGuard(pid)` stale-PID filtering. The Omarchy plugin has none of this — it trusts `applySnapshot(data)` from the headless backend blindly, with no PID validation or readiness guard. A stale snapshot from a previous backend session could set `state = "connected"` on a new backend process that hasn't actually established a tunnel.

---

## 2. CONCURRENCY HAZARDS & RACE CONDITIONS

### 2.1. Cascading Revision Conflict Chain (Service.qml:404-420)

**Mechanism:** When a mutation fails with `code: "conflict"`:
1. `handleResponse` calls `root.refreshSnapshot()`, which enqueues a `snapshot` request at the _tail_ of `_requestQueue`
2. `handleResponse` then calls `pumpQueue()` on line 419
3. `pumpQueue()` dequeues the _next queued mutation_ (not the snapshot), injecting `root.revision` — which is STILL THE STALE REVISION
4. That mutation also fails with `conflict`
5. The cascade repeats for every queued mutation

**Severity**: Every queued mutation after a conflict fails serially, each generating an `errorNotice` and a new `refreshSnapshot` enqueue, filling the queue with snapshot requests.

### 2.2. Queue-Residence Timeout Gap (Service.qml:493-502, 547)

**Mechanism:** The deadline clock starts at `pumpQueue()` dispatch time, not at `sendRequest()` enqueue time. If 7 slow requests are queued ahead of request 8, request 8 sits in `_requestQueue` indefinitely with no timeout. The user's UI callback never fires — no error, no completion, no timeout.

**Impact:** User clicks "Save Settings" → sees no response → clicks again → queue_full error. The original save is silently orphaned in the queue.

### 2.3. Follow-up Query Silent Drop on Queue Saturation (Service.qml:608-614, 663-669, 756-762)

**Mechanism:** Mutation success callbacks fire follow-up queries (e.g., `listServers()`, `getApps()`) without `onError` handlers:
```javascript
function selectServer(id, onDone, onError) {
    return sendRequest("servers.select", { ... }, function(res) {
        applySnapshot(res)
        listServers()    // ← No onError! Silent drop on queue_full
        if (onDone) onDone(res)
    }, onError)
}
```

If `_requestQueue` is saturated (rapid user actions: select server, import servers, remove subscription in quick succession), follow-up queries silently fail. `root.servers`, `root.subscriptions`, etc. remain stale until the user manually refreshes.

### 2.4. FreeConfigsPageViewModel._savedConfigs Cross-Thread Race (A02 evidence)

**Mechanism:** In `RecheckAllStaleAsync`, `SaveSavedConfigsToCache()` is called on a worker thread after `Task.WhenAll`. Inside, `_savedConfigs.ToList()` enumerates the list. But `_savedConfigs` is normally mutated on the UI thread. No lock protects the enumeration → `InvalidOperationException` on concurrent modification.

**Source:** `FreeConfigsPageViewModel.cs` (lines ~1176-1184 per A02 evidence)

---

## 3. FAIL-CLOSED & LEAK INVARIANTS

### 3.1. Backend Clean Exit → Permanent Unavailability → Traffic Fail-Open

**FINDING-P05_ui_consumer_sync-01 (P0)**

- **Source Anchor:** `Service.qml:328-335`
- **Mechanism:** When the headless backend exits with `exitCode === 0` (e.g., killed by systemd, EOF on stdin, internal maintenance shutdown, SIGTERM from another process), the condition `exitCode !== 0` is false. `retryTimer` is never scheduled. The plugin enters permanent `state: "unavailable"`.

  The VPN tunnel (sing-box) may still be running as a separate process, but the plugin has no way to monitor, control, or tear down the tunnel. If sing-box also crashes or is restarted independently, the user has no UI indication, no kill-switch enforcement from the plugin layer, and traffic routes unencrypted.

- **Threat:** Silent traffic leak. User believes VPN is running because the desktop panel shows no error banner (it shows "unavailable" which may be interpreted as "backend loading"). Actual traffic flows direct/unencrypted.

- **Fix:**
  ```qml
  // In backendProc.onExited:
  if (exitCode !== 0 || !root._tearingDown) {
      root.lastError = exitCode === 0
          ? "Backend stopped unexpectedly. Reconnecting…"
          : "Backend exited (code " + exitCode + "). Retrying…"
      // ... existing backoff logic
      retryTimer.restart()
  }
  ```
  Add a `_tearingDown` flag set in `tearDown()` to distinguish intentional destruction from unexpected clean exit.

### 3.2. No Kill-Switch Awareness at the Plugin Level

The Omarchy plugin exposes `capabilities.killSwitch` and `capabilities.dnsLockdown` as reactive properties but never reads or enforces them locally. If the backend process crashes while kill-switch rules are active in the system firewall, the plugin has no mechanism to:
1. Detect the orphaned kill-switch state
2. Warn the user that all traffic is blocked
3. Remove stale firewall rules

This is a backend-side responsibility, but the UI consumer should at minimum surface a "kill-switch may be active but backend is down" warning when `state === "unavailable"` and last-known `capabilities.killSwitch === true`.

### 3.3. SIGTERM-Only Teardown (Service.qml:273-278)

If the backend is deadlocked (e.g., blocking I/O in sing-box, uncooperative driver), `SIGTERM` has no effect. No `SIGKILL` escalation timer exists. The orphaned process holds:
- TUN network interface
- Routing table entries
- DNS resolver configuration
- Port bindings

New backend instances will fail to start, and the retry loop will spin indefinitely.

---

## 4. SECURITY & PRIVILEGE BOUNDARIES

### 4.1. helperPath URL-Encoding Vulnerability

**FINDING-P05_ui_consumer_sync-02 (P1)**

- **Source Anchor:** `Service.qml:15-18`
- **Mechanism:**
  ```qml
  readonly property string helperPath: {
      var raw = Qt.resolvedUrl("bin/vpnrouter-headless").toString()
      return raw.replace("file://", "")
  }
  ```
  `Qt.resolvedUrl().toString()` returns a percent-encoded URI. The `replace("file://", "")` is a simple string prefix strip — it does NOT decode `%20`, `%D0%...`, or other percent-encoded sequences.

  If the plugin is installed in a path containing spaces (e.g., `/home/user name/.local/share/omarchy/plugins/...`), `helperPath` becomes `/home/user%20name/...`. The `Process` component receives this as a literal filesystem path and fails with `ENOENT`.

  The same bug affects locale file paths (lines 120, 133) — locale loading silently fails, UI shows raw translation keys.

- **Threat:** Backend never starts on systems with space-containing home directories. User sees permanent "unavailable" with no actionable error message.

- **Fix:** Use `decodeURIComponent()`:
  ```qml
  return decodeURIComponent(raw.replace(/^file:\/\//, ""))
  ```

### 4.2. Same Pattern in Locale FileView Paths

- **Source Anchor:** `Service.qml:120, 133`
- Both locale `FileView` path properties use the same `Qt.resolvedUrl(...).toString().replace("file://", "")` pattern without URL decoding. Locale files silently fail to load on affected systems.

### 4.3. Direct Service Property Mutation from Views (Unidirectional Flow Violation)

- **Source Anchor:** `HeroCard.qml:248`, `DiagnosticsView.qml:81` (per O03 evidence)
- Views directly mutate `root.service.lastError = ""` and `root.service.diagnosticsExportPath = ""` instead of calling a service method.
- **Risk:** If these properties are refactored to `readonly` or computed, the views crash. Additionally, direct mutation bypasses any future logging, analytics, or state-change hooks.

---

## 5. INCONSISTENCIES & PROTOCOL VIOLATIONS

### 5.1. SettingsView Draft Binding Breakage on External Snapshot Refresh

**FINDING-P05_ui_consumer_sync-03 (P1)**

- **Source Anchor:** `SettingsView.qml:19-35, 173, 181, 187, 193, 199, 205, 216`
- **Mechanism:** Draft properties (`mtuVal`, `ipv6Val`, `strictRouteVal`, etc.) are initialized with QML binding expressions referencing `service.settingsData.*`. When a user toggles any option, the imperative assignment (e.g., `root.ipv6Val = val`) permanently severs the QML declarative binding.

  After binding severance, if `service.settingsData` updates externally (profile switch, `refreshSnapshot()`, concurrent backend-pushed state event), SettingsView continues displaying stale local draft values. The user sees one state; the backend has another.

- **Threat:** User changes settings on one view, switches profiles on another, returns to SettingsView and sees stale values. Saving these stale values overwrites the profile-switched configuration.

- **Fix:** Either:
  1. Convert to fully controlled mode: explicitly reset drafts on `service.settingsData` change via `onSettingsDataChanged` handler
  2. Or use a `Connections { target: service; ... }` block to re-sync drafts when settingsData changes

### 5.2. RulesView Binding Breakage — Identical Pattern

**FINDING-P05_ui_consumer_sync-04 (P1)**

- **Source Anchor:** `RulesView.qml:10, 96-97, 137`
- `selectedPriority` binding to `service.rules.priority` severed on toggle. `rulesTextEdit.text` binding to `service.rules.text` severed on first keystroke. External rule updates are never reflected.

### 5.3. settingsData Default Missing Properties

**FINDING-P05_ui_consumer_sync-05 (P1)**

- **Source Anchor:** `Service.qml:72-82` vs `Service.qml:879-892`
- **Mechanism:** Default `settingsData` (line 72) lacks `dnsModeOverride` and `dnsModeSemantics`. These are only populated by `getSettings()` (line 885-886). Before the first successful `getSettings()`:
  - `settingsData.dnsModeOverride` is `undefined`
  - `settingsData.dnsModeSemantics` is `undefined`
  - `SettingsView.savedDnsMode()` reads `data.dnsModeOverride !== undefined` → false → falls through to `data.dnsMode` which exists → no visible bug
  
  However, any view that directly reads `service.settingsData.dnsModeOverride` before handshake (e.g., for conditional UI rendering) gets `undefined`, not `null` or `"vpn_only"`.

- **Fix:** Add to defaults:
  ```qml
  property var settingsData: ({
      mtu: 1420,
      // ... existing ...
      dnsModeOverride: "vpn_only",
      dnsModeSemantics: null
  })
  ```

### 5.4. Desktop vs Omarchy DNS Override Semantics Mismatch

The Desktop `MainWindowViewModel.Settings.cs` uses `SettingsLoader` which reads `dnsMode` from `config.yaml`. The Omarchy plugin's `getSettings()` constructs `dnsModeOverride` as:
```javascript
dnsModeOverride: res.dnsModeOverride !== undefined ? res.dnsModeOverride : (res.dnsMode || "vpn_only")
```

This means if the headless backend returns `dnsModeOverride: null` (explicit profile default), the Omarchy plugin stores `null`. But if `dnsModeOverride` is missing from the response entirely, it falls back to `dnsMode`. The Desktop app has no such fallback chain — it reads `DnsMode` directly from the settings model.

If the headless backend evolves its response schema, the Omarchy plugin's fallback logic could diverge from the Desktop's behavior, leading to different DNS routing on the same configuration.

### 5.5. Conflict-Triggered refreshSnapshot Enqueues at Queue Tail

**FINDING-P05_ui_consumer_sync-06 (P0)**

- **Source Anchor:** `Service.qml:404-406, 417-420`
- **Mechanism:** On a `"conflict"` error response:
  1. `refreshSnapshot()` is called (line 405), which calls `sendRequest("snapshot", ...)` — this enqueues at the TAIL of `_requestQueue`
  2. `pumpQueue()` is called (line 419), which dequeues the NEXT queued item — a mutation, not the snapshot
  3. That mutation dispatches with the STALE `root.revision` (the snapshot hasn't returned yet)
  4. Backend rejects with `"conflict"` again
  5. Repeat for every queued mutation

  **Cascading failure**: With N queued mutations after a conflict, ALL N fail with conflict before the snapshot even dispatches. Each failure enqueues another snapshot. The queue fills with snapshots. Total wasted round-trips: 2N.

- **Threat:** Rapid user actions (import servers + select server + set routing) after any concurrent backend state change causes all operations to fail serially. User sees N error banners in rapid succession.

- **Fix:** On conflict, flush all queued mutations (they're all stale) and prioritize the snapshot:
  ```javascript
  if (errCode === "conflict") {
      root.flushQueuedMutations("Stale revision after conflict")
      root.refreshSnapshot()
  }
  ```

---

## 6. PRIORITIZED ACTIONABLE FINDINGS

### P0 — Critical (Leak / Crash / Security)

#### FINDING-P05_ui_consumer_sync-01: Backend Clean Exit → Permanent Unavailability → Traffic Fail-Open
- **Source Anchor:** `Service.qml:328-335`
- **Mechanism:** `exitCode === 0` skips retry scheduling. Plugin enters permanent `state: "unavailable"`. VPN tunnel state unknown; traffic may route unencrypted.
- **Threat:** Silent traffic leak. User has no UI indication that VPN protection is lost.
- **Fix:** Retry on `exitCode === 0` unless intentional teardown via a `_tearingDown` flag set in `tearDown()`. Surface a distinct user-visible warning for unexpected clean exits.

#### FINDING-P05_ui_consumer_sync-02: Cascading Revision Conflict → Serial Mutation Failure Storm
- **Source Anchor:** `Service.qml:404-406, 417-420`
- **Mechanism:** Conflict triggers `refreshSnapshot()` at queue tail, then immediately pumps next queued mutation with stale revision. All queued mutations fail serially.
- **Threat:** N queued operations → N serial failures → N error banners → degraded UX and wasted backend round-trips. During this cascade, no user mutations succeed.
- **Fix:** On conflict, call `flushQueuedMutations("Stale revision")` before `refreshSnapshot()` to prevent stale mutations from dispatching.

---

### P1 — Major Functional Defect

#### FINDING-P05_ui_consumer_sync-03: helperPath URL-Encoding → Backend ENOENT
- **Source Anchor:** `Service.qml:15-18`
- **Mechanism:** `Qt.resolvedUrl().toString()` returns percent-encoded URI. `replace("file://", "")` doesn't decode. Paths with spaces → `ENOENT`.
- **Threat:** Backend never starts on systems with space-containing paths. Same bug affects locale FileView paths (lines 120, 133).
- **Fix:** `return decodeURIComponent(raw.replace(/^file:\/\//, ""))`

#### FINDING-P05_ui_consumer_sync-04: SettingsView Draft Binding Severed by User Input
- **Source Anchor:** `SettingsView.qml:19-35, 173-216`
- **Mechanism:** QML binding expressions on draft properties permanently severed by imperative assignment on user toggle. External `settingsData` updates no longer propagate.
- **Threat:** Profile switch or external config change displays stale settings. User saves stale values, overwriting intended configuration.
- **Fix:** Add `Connections { target: service; onSettingsDataChanged: { /* re-sync drafts */ } }` or reset drafts explicitly on data change.

#### FINDING-P05_ui_consumer_sync-05: RulesView Binding Severed — Same Pattern
- **Source Anchor:** `RulesView.qml:10, 96-97, 137`
- **Mechanism:** `selectedPriority` and `rulesTextEdit.text` bindings severed on user interaction. External rule updates not reflected.
- **Fix:** Same pattern as SettingsView — explicit re-sync on `service.rules` change.

#### FINDING-P05_ui_consumer_sync-06: Follow-up Queries Silent Drop on Queue Saturation
- **Source Anchor:** `Service.qml:608-614, 663-669, 679-689, 756-762, 897-906`
- **Mechanism:** Mutation success callbacks call `listServers()`, `listSubscriptions()`, etc. without `onError`. Queue-full rejection silently dropped. UI model (`root.servers`, etc.) stale.
- **Threat:** Under rapid mutation burst, UI shows outdated data. User must manually navigate away and back to trigger refresh.
- **Fix:** Pass `onError` to follow-up queries, or at minimum log a warning:
  ```javascript
  listServers(0, 100, null, function(err) {
      console.warn("Follow-up listServers failed:", err.code)
  })
  ```

#### FINDING-P05_ui_consumer_sync-07: settingsData Default Missing dnsModeOverride/dnsModeSemantics
- **Source Anchor:** `Service.qml:72-82` vs `Service.qml:879-892`
- **Mechanism:** Pre-handshake, `settingsData.dnsModeOverride` is `undefined`. Views reading this field directly get unexpected `undefined` instead of default value.
- **Fix:** Include `dnsModeOverride: "vpn_only"` and `dnsModeSemantics: null` in the default property initialization.

#### FINDING-P05_ui_consumer_sync-08: Queued Request Has No Queue-Residence Timeout
- **Source Anchor:** `Service.qml:493-502, 547`
- **Mechanism:** Deadline computed at `pumpQueue()` dispatch, not at `sendRequest()` enqueue. Queued items can wait minutes without any timeout or user feedback.
- **Threat:** User action appears to hang indefinitely. No error, no timeout, no progress indicator.
- **Fix:** Record `enqueueTime` on queued items. In `checkDeadlines`, also evict items where `now - enqueueTime > maxQueueResidenceMs`.

#### FINDING-P05_ui_consumer_sync-09: FreeConfigsPageViewModel SavedConfigs Cross-Thread Race
- **Source Anchor:** `FreeConfigsPageViewModel.cs` (~line 1176 per A02 evidence)
- **Mechanism:** `SaveSavedConfigsToCache()` called on worker thread enumerates `_savedConfigs` without lock. UI thread may concurrently modify via `UpsertSavedConfig`/`RemoveFromSaved`.
- **Threat:** `InvalidOperationException` during enumeration.
- **Fix:** Marshal `SaveSavedConfigsToCache()` to UI thread, or take a snapshot under lock before serializing.

---

### P2 — Edge-Case / Performance

#### FINDING-P05_ui_consumer_sync-10: SIGTERM-Only Teardown, No SIGKILL Escalation
- **Source Anchor:** `Service.qml:273-278`
- **Mechanism:** `tearDown()` sends only `signal(15)`. No fallback timer for `signal(9)`.
- **Threat:** Deadlocked backend holds network interfaces indefinitely.
- **Fix:** Add a 2-second timer that fires `backendProc.signal(9)` if `backendProc.running` is still true.

#### FINDING-P05_ui_consumer_sync-11: Inert tooltipText in IconButton.qml
- **Source Anchor:** `IconButton.qml:8` (per O03 evidence)
- **Mechanism:** Property declared and populated by all consumers but no `ToolTip` component instantiated.
- **Threat:** All button tooltips across the entire UI are silent. Accessibility degradation.
- **Fix:** Add a `ToolTip` child component or integrate with Quickshell's tooltip system.

#### FINDING-P05_ui_consumer_sync-12: Swallowed Locale JSON Parse Errors
- **Source Anchor:** `Service.qml:123-128, 136-141`
- **Mechanism:** `catch (e) {}` swallows `SyntaxError` without logging. Malformed locale files → empty catalogs → raw translation keys shown.
- **Fix:** `catch (e) { console.warn("Failed to parse locale JSON:", e) }`

#### FINDING-P05_ui_consumer_sync-13: Async Callbacks Fire After Component Destruction
- **Source Anchor:** `RulesView.qml:174-177`, `SettingsView.qml:387-389`
- **Mechanism:** Inline callbacks reference `rulesTextEdit.text` / `root.saveNotice`. If component destroyed before backend responds, `TypeError: Cannot set property 'text' of null`.
- **Fix:** Guard callbacks with component-alive check or use `Component.onDestruction` to cancel pending requests.

#### FINDING-P05_ui_consumer_sync-14: Direct Service Property Mutation from Views
- **Source Anchor:** `HeroCard.qml:248`, DiagnosticsView (per O03 evidence)
- **Mechanism:** Views write `service.lastError = ""` and `service.diagnosticsExportPath = ""` directly.
- **Threat:** Violates unidirectional data flow. Future refactoring to readonly/computed properties breaks these views.
- **Fix:** Add `clearLastError()` and `clearDiagnosticsPath()` methods to Service.qml.

#### FINDING-P05_ui_consumer_sync-15: Cross-Sibling Property Reference During Layout
- **Source Anchor:** `ServersView.qml:184`
- **Mechanism:** `rowMouse.anchors.rightMargin: rowActions.width + Style.space(16)` — `rowActions` is a sibling child not yet measured during initial layout pass.
- **Threat:** Momentary layout twitching on first render; `rowActions.width` may be 0.
- **Fix:** Use binding to ensure re-evaluation, or restructure layout so `rowMouse` doesn't depend on sibling geometry.

#### FINDING-P05_ui_consumer_sync-16: Unscrollable CustomConfigView TextEdit
- **Source Anchor:** `CustomConfigView.qml:65-92` (per O03 evidence)
- **Mechanism:** `customTextArea` in 90px `Rectangle` without `Flickable` wrapper. Large JSON configs overflow without scrollbar.
- **Fix:** Wrap in `Flickable { clip: true; ... }` like RulesView does.

#### FINDING-P05_ui_consumer_sync-17: Dead Signal openServerPickerRequested
- **Source Anchor:** `HeroCard.qml:20`
- **Mechanism:** Declared `signal openServerPickerRequested()` — never emitted, never connected.
- **Fix:** Remove dead signal declaration.

#### FINDING-P05_ui_consumer_sync-18: Tab Repeater Re-instantiation on Locale Toggle
- **Source Anchor:** `Navigation.qml:22-35` (per O03 evidence)
- **Mechanism:** `readonly property var tabs` array reconstructed on every `i18nRevision` change. `Repeater` destroys and recreates all 9 tab delegates.
- **Threat:** Active focus reset, scroll position loss on language switch.
- **Fix:** Use a `ListModel` and update labels in-place rather than recreating the array.

#### FINDING-P05_ui_consumer_sync-19: async void Crash Risk in ApplicationsPage
- **Source Anchor:** `ApplicationsPage.axaml.cs:83, 124` (per A06 evidence)
- **Mechanism:** `OnBrowseExeClicked` and `OnSelectRunningProcessClicked` are `async void`. Unhandled exceptions crash the application.
- **Fix:** Wrap method bodies in top-level `try { ... } catch (Exception ex) { _logger.Error(ex, ...); }`.

#### FINDING-P05_ui_consumer_sync-20: Synchronous File I/O in CustomConfigViewModel Constructor
- **Source Anchor:** `CustomConfigViewModel.cs:19-38` (per A02 evidence)
- **Mechanism:** `File.ReadAllText(resolvedPath)` blocks UI thread during ViewModel construction. Silent exception swallowing with `catch { }`.
- **Fix:** Defer file reading to an async initialization method, or cache parsed results.

#### FINDING-P05_ui_consumer_sync-21: FlagFor ArgumentOutOfRangeException on Non-ASCII Country Codes
- **Source Anchor:** `FreeConfigItemViewModel.cs:177-187` (per A02 evidence)
- **Mechanism:** Assumes `upper[0]` is ASCII `'A'..'Z'`. Cyrillic `'Р'` → codepoint offset 991 → `char.ConvertFromUtf32` throws.
- **Fix:** Add `char.IsAsciiLetterUpper()` guard before codepoint arithmetic.

#### FINDING-P05_ui_consumer_sync-22: Redundant Double-Loop in AppGroupViewModel.SelectAll/ClearAll
- **Source Anchor:** `AppGroupViewModel.cs:51-72` (per A02 evidence)
- **Mechanism:** `SelectAll()` sets `IsChecked = true` (triggering `OnIsCheckedChanged` loop), then loops all apps again.
- **Fix:** Remove the redundant loop from `SelectAll()`/`ClearAll()`, relying on `OnIsCheckedChanged` alone.

#### FINDING-P05_ui_consumer_sync-23: UI Thread Blocking in AboutWindow.GetSingBoxVersion
- **Source Anchor:** `MainWindowViewModel.Settings.cs:68-103`, `AboutWindow.axaml.cs:38` (per A06 evidence)
- **Mechanism:** Synchronous child process spawn + `WaitForExit(3000)` on UI thread in constructor. Blocks About dialog rendering up to 3 seconds.
- **Fix:** Populate asynchronously on `Loaded` event, or cache the version string from the ViewModel.

---

## 7. CROSS-CONSUMER SYNCHRONIZATION SUMMARY

| Dimension | Omarchy Plugin (Service.qml) | Desktop (MainWindowViewModel) | Sync Risk |
|-----------|------------------------------|-------------------------------|-----------|
| **State ownership** | Backend snapshot events via NDJSON | VpnEngine events + TwoPhaseStartCoordinator | Different state machines; no shared contract |
| **Revision CAS** | Deferred injection at pumpQueue | Not applicable (in-process engine) | Omarchy conflict cascade vs Desktop direct access |
| **Connection guard** | `capabilities.connect` gate only | Triple-lock (IsConnecting/IsApplying/_isReconnecting) + readiness guard + stale PID filter | Omarchy has weaker guard |
| **DNS override** | Fallback chain: dnsModeOverride → dnsMode → "vpn_only" | Direct SettingsLoader read | Potential semantic divergence |
| **Kill-switch awareness** | Exposed as capability, not enforced locally | Engine-level enforcement | Plugin cannot detect orphaned kill-switch state |
| **Process lifecycle** | Child process with SIGTERM teardown, exponential backoff | In-process + Windows Service integration | No SIGKILL escalation in plugin |
| **Binding reactivity** | QML bindings severed by user input | CommunityToolkit MVVM with explicit INPC | Omarchy views go stale after user interaction |

---

## 8. FINDING COUNT SUMMARY

| Severity | Count | IDs |
|----------|-------|-----|
| **P0** | 2 | FINDING-P05_ui_consumer_sync-01 (traffic fail-open), -02 (conflict cascade) |
| **P1** | 7 | FINDING-P05_ui_consumer_sync-03 through -09 |
| **P2** | 14 | FINDING-P05_ui_consumer_sync-10 through -23 |
| **Total** | **23** | |
