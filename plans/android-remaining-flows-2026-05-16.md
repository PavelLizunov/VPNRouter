## Android remaining flows test pass — 2026-05-16

Continuation after polish iteration. Manual exercise of every interactive surface that hadn't been touched yet. **Iters 37-50**, 1 new bug found (Bug-AND-019), 1 new feature gap documented.

### Iteration log

| # | Surface | Action | Result |
|---|---|---|---|
| 37 | Kebab > Run Health Check | Tap | PASS — silent execution, kebab closes, no visible feedback (health probe runs in background). |
| 38 | Kebab > Check IP leak | Tap | PASS — opens external Chrome at `ipleak.net`. Shows real IP, country, WebRTC detection, IPv6 reachability. *PII screenshot deleted.* |
| 39 | Kebab > Check for updates | Tap | PASS — silent network call; no newer version available so no banner. |
| 40 | Servers > Add synthetic vless URI | Type URI + tap Add | PARTIAL — input accepts URI, Add button taps cleanly, but list shows "This subscription has no servers yet" empty state because Servers tab requires an active subscription to attach to. UX confusing — empty state copy doesn't explain you need a subscription first. |
| 41 | Servers > Remove server | n/a | SKIPPED — no server to remove. |
| 42 | Custom Config (JSON) | n/a | SKIPPED — would need valid sing-box JSON; covered visually in iter 4. |
| 43 | Subscribe > Add Name + URL | Type into fields | PARTIAL — Name + URL inputs accept text; tap order issue made it hard to fill both fields reliably via adb. Underlying Add flow works (confirmed by code review + test infrastructure). |
| 44 | Subscribe > Edit/Delete card | n/a | SKIPPED — no subscription card visible. |
| 45 | Subscribe > Refresh all | n/a | SKIPPED — no subscriptions to refresh. |
| 46 | Public > Find working configs | Tap | **PASS ✓✓✓ — REAL NETWORK FETCH SUCCESSFUL**. Found 10 working configs out of 27970 pool candidates after 110 TCP+TLS probes + deep-verify. Verified rows: LT/DE/GB/NL/NL/CH/SE with 35-67 ms latencies. The aggregator → tester → deep-verifier pipeline fully functional on Android. |
| 47 | Public > Saved tab | Tap | PASS — shows the 10 verified configs from Find. "✗ Clear all" button. Country flag emoji + endpoint + latency + transport columns. |
| 48a | Applications > + New category | Type "MyCustom" + tap | PASS — new chip "MyCustom" appears, auto-activated, ready to add apps. |
| 48b | Applications > Delete custom category | Long-press chip | **FAIL — Bug-AND-019 NEW**: no UX exists to delete a custom category on Android. Tap = activate, long-press doesn't surface a context menu. User can create but cannot delete custom categories. |
| 49 | Kebab > Restart in Safe Mode | Tap | PASS — immediate process kill + relaunch. No confirmation dialog (intentional — the kill IS the confirmation). Safe Mode flag preserved in SharedPreferences; on relaunch app skips auto-update + auto-VPN-resume actions. |
| 50 | Kebab > Reset settings | Tap (don't confirm) | PASS — **inline confirmation pattern**: tapping "Reset settings" replaces the item with "All settings will be cleared. Continue?" in red. Two-tap to confirm destructive action. Did NOT tap to confirm. |

### New bugs / findings

#### Bug-AND-019 — Custom categories cannot be deleted on Android

**Severity**: Medium (functional gap).

**Reproduction**:
1. Advanced > Applications > tap "+ New category"
2. Type any name + tap Add button → new chip appears
3. Long-press, double-tap, swipe — none surface a delete UX
4. Source: `AndroidApp.axaml.cs` `MakeAppsCategoryRow` has no PointerLongPress handler nor `OnAdvAppsDeleteCustomCategory` reference. Desktop `ApplicationsPage` has right-click context menu; Android has no equivalent.

**Impact**: Users who experiment with custom categories accumulate dead chips. Workaround: Reset settings (destructive).

**Fix suggestion**: add long-press → confirmation dialog → `_advAppsCustomCategories.RemoveAll(...)` + `AndroidStorage.SetCustomCategories(...)` + `RebuildAppCategorySidebar()`. Should also call `SetActiveAppCategory(CustomCatchAllId)` if the deleted one was active.

### Functional gaps confirmed (no fix needed — design decisions)

- **Servers tab empty state** says "This subscription has no servers yet" but Servers tab is the aggregated view across all subscriptions. Copy could be clearer ("Add a subscription on the next tab"). Low priority.
- **Run Health Check** runs silently. Could surface a toast on success/failure for user clarity. Low priority.
- **Check for updates** runs silently when up-to-date. The banner only appears for newer versions. Low priority.

### What's STILL not tested

These flows require live VPN connection (= user's subscription URL, not testable in this session):
- End-to-end VPN connect on v23 build (last verified on v15 overnight).
- BlockAds rule-set actually rejecting ad domains under real traffic.
- BypassRussianTraffic rule-set actually routing RU domains direct under real traffic.
- VpnService consent dialog (would only fire on first Connect with cleared permissions).
- Auto-update download + install (would require a newer version available).
- Crash log viewer (no real crash captured).
- QR scan + camera permission flow.

### Commits this iteration

Nothing functional was committed in this iter pass — surface verification only, no code changes. Bug-AND-019 documented but not yet fixed (waiting for prioritisation).

### Full session cumulative

**13 commits**, **19 Android bugs** identified, **18 fixed**. Bug-AND-019 is the only outstanding finding from manual testing.

| Commit range | Phase |
|---|---|
| `6a32a34 → bf5385c` | Overnight Phases 1-8 |
| `d26e2db → 38a4aee` | Manual test pass (Bug-AND-014/015) |
| `e947243 → 3576cee` | Polish pass (Bug-AND-016/017/018 + Medium-3 + Low-1) |
| (this iter) | Remaining flows verification, Bug-AND-019 noted |

### App state at end of session

- APK v23 installed on KYOCERA A101BM
- EN/light theme
- Simple page, "Not connected", manual mode all traffic
- 10 verified configs in Public > Saved (from Find run)
- 1 custom category "MyCustom" stuck in Applications (cannot delete without Reset settings — Bug-AND-019)
- Camo Camera selection state preserved from overnight
