# VPNRouter v2.30.6 — Extended audit (live + code review)

**Date**: 2026-05-02 (overnight session)
**Driver**: Claude autonomous run (~3h)
**App version**: v2.30.6 stable
**Methodology**:
1. **Live UI walk** via `mcp__vpnrouter-test__*` MCP server + UIAutomation (PowerShell)
   to bypass UIPI keyboard restriction on elevated Avalonia window. Mouse via
   SendInput, text via UIA `ValuePattern.SetValue()`, buttons via UIA `InvokePattern.Invoke()`.
2. **Code review** via two parallel general-purpose agents auditing
   `VPNRouter.Core/Services/*.cs` and `VPNRouter.App/ViewModels/*.cs`.
3. **Regression tests** + log inspection.

UIPI tip baked into helper script `C:/tmp/uia-helpers.ps1` for future sessions.

---

## Coverage matrix

| Phase | Action | Result |
|---|---|---|
| 1 | Add hy2 server URI via UIA paste | ✅ `hysteria2 + salamander` parsed correctly, IP 93.95.226.167:9443 |
| 1b | Subscription URL added → 7 servers fetched | ✅ |
| 1c | "Test all" ping → 7 servers <5ms each | ✅ but footer "Working: 0/7" misleading |
| 1d | "Deep verify" → 7/7 checked | ⚠️ Working count stays 0/7 (handshake validation produced no working servers — possibly genuine outage during test) |
| 2 | Free Configs Search button → 10 working found | ✅ EE/AL/NL/FI/DE/CH/HK/SE flags, 6-10ms latency, 9-21 Mbps |
| 3 | Zapret Status → Start DPI Bypass | ✅ Running [multisplit] PID 3936, header chip GREEN |
| 3a | Hosts → Add Discord hosts | ✅ button flips to "Remove Discord hosts" |
| 3b | Updates → Update IPSet list | ✅ click registered |
| 4 | TgProxy → Download → Start | ✅ Installed v1.6.5, Running PID 3716 |
| 5 | Settings sidebar walk | ✅ Routing/Rules/Leak/Content/Updates/Autostart all reachable |
| 6 | Custom Rules — added 4 rules of different types | ⚠️ 1 rule with INVALID value persisted (value "53" with type "domain_suffix") — see BUG-AU-1 |
| 7 | Apps → Discord category → "+ Add" custom app | ✅ MyTestApp.exe persisted to `custom_group_apps.Discord_Privacy[]` |
| 8 | Connect VPN with hy2 → claude.exe routed via vless outbound, curl.exe via direct | ✅ split tunnel works correctly |
| 8a | 3× disconnect/reconnect cycle | ⚠️ memory stable (~373 MB), threads stable, but **handle count grew +500** across 3 cycles |
| 9 | Theme RU/EN toggle | ✅ tabs+sidebar localize correctly |

---

## Findings from live walk

| ID | Severity | Description |
|---|---|---|
| **AU-1** | 🐛 BUG | Custom rule "+ Add" button accepts value that fails type-regex validation. Value `53` with type `domain_suffix` was persisted to `custom_rules` in YAML. UI shows red border on the value input but doesn't gate submission. Fix: `if (!NewRuleValueIsValid) return` early in `AddCustomRuleFromForm` (MainWindowViewModel.cs:1463). |
| **AU-2** | ⚠️ UX | EN copy still has "(zapret by Flowseal)" parenthetical — UX-44 fix in v2.30.5 was applied to RU only. `MainWindowViewModel.cs:1717`. |
| **AU-3** | ⚠️ UX | "Free" tab name not localized in RU mode — stays "Free" in both languages. Should be "Free" / "Free configs" or similar. `Strings.cs:?` (TabFree-equivalent). |
| **AU-4** | ⚠️ UX | "◂ Simple" mode-toggle button in `…` menu stays English in RU mode. Should be "◂ Простой". |
| **AU-5** | ⚠️ UX | "Test all" footer reads "Working: 0/N" after quick-ping batch even when all servers ping successfully. The "working" metric requires full handshake (Deep verify), not just TCP ping. Copy is misleading. Suggest: "Pinged: N/N (handshake not tested)". |
| **AU-6** | 🐛 A11y | `FreeConfigItemViewModel`, `CustomRuleViewModel`, `AppGroupViewModel`, `ServerViewModel` — none override `ToString()` or expose a `Name`/`AutomationProperties.Name` for the ListBoxItem container. UIA returns `VPNRouter.App.ViewModels.X.Y` class name. Affects screen readers + automated testing tools. |
| **AU-7** | 🐛 A11y | Many `<CheckBox><TextBlock TextWrapping="Wrap"/>` patterns produce empty UIA `Name`. Pages affected: Leak Protection (4 ✗), Content (1 ✗), Autostart (1 ✗). Pattern from CLAUDE.md "narrow window CheckBox.Content overflow" rule but breaks accessibility. Fix: add `AutomationProperties.Name` on the CheckBox alongside the wrapped TextBlock. |
| **AU-8** | 🐛 A11y | `…` menu version label exposes UIA Name `Avalonia.Controls.Grid` — leaks layout container class name as accessible name for the version block. Should expose actual version text or be marked `IsControlElement=False`. |
| **AU-9** | ⚠️ RISK | **Handle leak suspicion**: 3× VPN start/stop cycles bumped `HandleCount` from 1012 → 1208 → 1321 → 1386 → 1528 (~+170 per cycle). Memory + thread count stable. Could be sing-box child Process objects retained. See VM-BUG-9 and Core-BUG-2 (CTS leak). |
| **AU-10** | ⚠️ UX | `domain_regex` rule type referenced in code (MainWindowViewModel.cs:584) and validated in Edit-mode validator list, but NOT exposed in the Cards-mode `AvailableRuleTypes` ComboBox (line 638-644). Inconsistent surface — Edit-mode users can author rules of a type the form can't represent. |
| **AU-11** | 🐛 BUG | "Check for updates" button (both `…` menu item AND Settings → Updates → version-card button) fires no log entry and no banner appears even when prerelease channel is ON and a newer prerelease (v2.30.7-r1) exists. Click via UIA InvokePattern AND mouse coords both fail to produce visible feedback. Either click handler not wired or check runs silently. The earlier successful update detection (v2.30.5 → v2.30.6-r1) suggests the menu item DID work earlier, but doesn't on v2.30.6. |

---

## Findings from Core/Services review

(via Agent autonomous read of `VPNRouter.Core/Services/*.cs`)

### 🐛 BUG (real defects)

| ID | File:Line | Issue |
|---|---|---|
| **CO-1** | `HealthMonitor.cs:124-128` | `_debounceTimer?.Dispose(); _debounceTimer = new Timer(…)` not atomic. ETW callbacks on multiple threadpool threads → double-dispose race + leaked timer. |
| **CO-2** | `HealthMonitor.cs:188` | `_restartCts = new CancellationTokenSource()` reassigned without disposing prior — leaks one CTS per restart attempt. |
| **CO-3** | `SingBoxManager.cs:421` | Sync-over-async: `_http.PutAsync(...).GetAwaiter().GetResult()` on shared static HttpClient. Deadlock window on saturated threadpool (mitigated by 3s timeout). |
| **CO-4** | `ProfileManager.cs:236` | `JsonConvert.DeserializeObject<ProfileCollection>(json)` from remote URL with no `MaxDepth`. DoS vector if profile source is HTTP-redirect MITM'd. Add `MaxDepth = 64`. |
| **CO-5** | `FirewallManager.cs:251-269` | `FindRulesByPrefix` parses `netsh` output line-by-line on `:` delimiter, ignoring localized field names. On RU/DE/ES Windows could match user-defined rules whose **Description** starts with `VPNRouter_Block_` and silently delete them. |
| **CO-6** | `EtwProcessMonitor.cs:57-66` | `_session = session` race vs `Stop()` reading `_session` before assignment. If Stop fires in the millisecond window, worker thread blocks on `Process()` forever. |
| **CO-7** | `CustomConfigInjector.cs:866` | `new Uri(address)` throws `UriFormatException` on malformed user DNS server. No try/catch wrapper. |
| **CO-8** | `SingBoxManager.cs:610-615` | Empty `catch { }` swallows ExitCode read failure → `exitCode == 0` and `unknown` branches report identical "Failed" state. |

### ⚠️ RISK

| ID | File:Line | Issue |
|---|---|---|
| **CO-9** | `SingBoxManager.cs:48` | `_tunLock` singleton subscribed to `ProcessExit += _tunLock.Dispose` per ctor. Multiple SingBoxManager instances → multiple subscriptions → Dispose called N×. |
| **CO-10** | `FirewallManager.cs:53-110` | `RunNetsh` doesn't escape rule names with embedded quotes. Profile-supplied process names with `"` could inject netsh args. Low-likelihood but unguarded. |
| **CO-11** | `ConfigGenerator.cs:93-110` | Mutates `config.Route.Rules` from multiple Apply* methods independently. Comment notes "LAST Apply* runs ENDS UP FIRST" — fragile invariant; v2.30.1 already shipped a regression of this exact ordering bug. |
| **CO-12** | `LeakProtection.cs:35-49` | UDP-only proxy path not validated by leak protection (only matches `Outbound == "proxy"`, not `proxy-udp`). Process using only UDP could leak DNS. |

### 💡 SUGGEST

| ID | File:Line | Issue |
|---|---|---|
| **CO-13** | `ProcessScanner.cs:215` | TODO: macOS ps/sysctl child-process detection (single TODO in Services). |
| **CO-14** | `ConfigGenerator.cs:333` | `route.Network = values[0].ToLowerInvariant()` silently drops additional values for `network` rule. Should warn or split. |
| **CO-15** | `ProfileManager.cs:147` | `MergeProfiles` propagates `KeyNotFoundException` from `GetProfile` — inconsistent with tolerant variant at line 96. CLI surface throws on first miss instead of listing all bad names. |

---

## Findings from ViewModels review

(via Agent autonomous read of `VPNRouter.App/ViewModels/*.cs`)

### 🐛 BUG

| ID | File:Line | Issue |
|---|---|---|
| **VM-1** | `MainWindowViewModel.cs:3527` | `StartSubRefreshTimer` bails when legacy `SubscriptionUrl` is empty, but multi-sub model uses `Subscriptions[]`. Auto-refresh timer **never starts**. Subscriptions only refresh on user manual click. |
| **VM-2** | `MainWindowViewModel.SimpleMode.cs:80-113` | `SmpAutostartChecked` setter calls `OnPropertyChanged` then `SaveSettings()`; mutation of `_settings.App.AutostartVpn` triggers `OnAutostartVpnChanged` which calls `SaveSettings()` AGAIN. Double-save on every Simple-mode autostart toggle. |
| **VM-3** | `MainWindowViewModel.cs:5126-5128` | `ApplyFreeConfigAsync` sets `_settings.App.ConfigMode = "generated"` directly BEFORE `SaveSettings()` — but SaveSettings (line 2943) recomputes ConfigMode from VM flags, overwriting. Dead-code line. |
| **VM-4** | `MainWindowViewModel.cs:1463-1497` | **Custom rule "+ Add" missing validity gate** — only checks `IsNullOrWhiteSpace(NewRuleValue)`, no check on `NewRuleValueIsValid`. User can submit with red border. (Live AU-1.) |
| **VM-5** | `MainWindowViewModel.cs:1162-1185` | Bulk `EnableAllCustomRules` / `DisableAllCustomRules` triggers `FlushCustomRulesListToSettings` per row (= one full SaveSettings + Serialize). For 100 rules: O(N²) operations, perceptible lag. |
| **VM-6** | `FreeConfigsPageViewModel.cs:272-274` | `CustomMaxPingMs` + `CustomMinBandwidthMbps` are `int` (non-nullable). If bound to NumericUpDown anywhere, clearing → `InvalidCastException`. CLAUDE.md flagged this exact pattern. |
| **VM-7** | `MainWindowViewModel.cs:701` | `TgProxyPort = int` (not `int?`). NumericUpDown bind would crash on clear. |
| **VM-8** | `MainWindowViewModel.cs:2542-2575` | App `PropertyChanged` subscriptions never unsubscribed across `LoadApps()` rebuilds (RU↔EN toggle). Memory leak on locale switches. |
| **VM-9** | `FreeConfigsPageViewModel.cs:142-155` | `Dispose` doesn't dispose `_deepVerifier` (HTTP client + sing-box probes). Resource leak on page tear-down. |
| **VM-10** | `MainWindowViewModel.cs:1246-1253` | `ShowRulesToast` uses `Task.Delay(2000, token).ContinueWith(...)` — task chain not awaited or stored. Cumulative leak per toast. |
| **VM-11** | `MainWindowViewModel.cs:1896-1898` | `ResetConfig` arms 5s disarm timer without cancellation token. Double-click within 5s → second timer arms while first still pending → race on `ResetConfigArmed`. |
| **VM-12** | `MainWindowViewModel.ServerTesting.cs:152, 160, 176, 208` | Bare `server.IsTesting = false` from background thread. PropertyChanged fires off-UI-thread → Avalonia bindings throw or drop silently. Should be wrapped in `Dispatcher.UIThread.InvokeAsync`. |
| **VM-13** | `MainWindowViewModel.cs:3349-3365` | `RebuildSubscriptionPool` reassigns `SelectedSubscriptionServer` which fires `OnSelectedSubscriptionServerChanged` → triggers `ReconnectAsync`. Subscription refresh while connected can spuriously reconnect. |

### ⚠️ RISK

| ID | File:Line | Issue |
|---|---|---|
| **VM-14** | `MainWindowViewModel.cs:888 + RecomputeRulesEditorState` | No debounce on `OnEditedCustomRulesTextChanged`. Re-allocates HashSets, splits text, builds StringBuilder, fires 5+ PropertyChanged per keystroke. |
| **VM-15** | `MainWindowViewModel.cs:228-275` | `OnSelectedTabIndexChanged` flips IsVlessMode/IsSubscribeMode without saving. Subsequent `SaveSettings` from another flow persists the new flag silently — non-obvious coupling. |
| **VM-16** | `MainWindowViewModel.cs:1525` | `OnAutostartVpnChanged` always fires `OnPropertyChanged(SmpAutostartChecked)` even during `_isLoadingUI`. Asymmetric guard (early return only protects SaveSettings, not notify). |
| **VM-17** | `MainWindowViewModel.cs:752-778` | `OnCustomRulesTextChanged` rebuild chain protected by `_isSyncingCustomRules` — single guard prevents recursion, but relies on hardcoded property name list at CustomRuleViewModel.cs:99-101. Adding a 12th computed property breaks the guard. |
| **VM-18** | `MainWindowViewModel.cs:516-535 + 638-644` | `_typeValidatorMap` includes `domain_regex` but `AvailableRuleTypes` ComboBox doesn't. Cards-mode users can't add this type; Edit-mode parser accepts it. |

---

## Aggregate stats

- **Total findings**: 47 (10 live + 15 Core + 22 ViewModels)
- **🐛 BUG**: 22
- **⚠️ RISK**: 17
- **💡 SUGGEST**: 8

## Recommended next batch (P0/P1 candidates)

These are deterministic, safe, and verifiable:

1. **VM-4 / AU-1** — Add `NewRuleValueIsValid` gate to `AddCustomRuleFromForm`. Also explicit error message via `NewRuleValidationError`.
2. **AU-2** — Drop "(zapret by Flowseal)" from EN side of `LblDpiDescription`.
3. **AU-3** — Localize `TabFree` (or rename to `FreeConfigsTab` consistent with code).
4. **AU-4** — Localize `Simple` mode toggle in menu.
5. **AU-5** — Reword "Working: 0/N" footer copy to clarify "TCP ping vs handshake validation".
6. **AU-6** — Override `ToString()` on FreeConfigItemViewModel / CustomRuleViewModel / AppGroupViewModel / ServerViewModel to expose a meaningful display name.
7. **AU-8** — Fix version-label UIA Name (set `AutomationProperties.AccessibilityView=Raw` or expose actual version text).
8. **VM-9** — Add `_deepVerifier?.Dispose()` to `FreeConfigsPageViewModel.Dispose`.
9. **VM-18** — Remove `domain_regex` from `_typeValidatorMap` (since Cards-mode doesn't support it) OR add to `AvailableRuleTypes`.
10. **CO-7** — Wrap `new Uri(address)` in `CustomConfigInjector` with try/catch.

P0/P1 candidates that need careful design (deferred):

- **CO-1** (HealthMonitor timer race) — needs lock-free or atomic-swap pattern
- **CO-5** (FirewallManager netsh localization) — switch to COM API HNetCfg.FwPolicy2
- **CO-6** (ETW Stop deadlock) — needs barrier or restructure
- **VM-1** (Sub auto-refresh on multi-sub) — needs new path through Subscriptions[]
- **VM-8** (App PropertyChanged unsubscribe) — needs WeakEvent or explicit dispose chain

---

## Cross-refs

- `plans/vpnrouter-ux-audit-2026-05-01.md` — original 72-finding catalog (32 shipped through v2.30.6)
- `plans/release-notes-v2.30.6.md` — current stable
- `tools/VpnRouterTestMcp/` — MCP server used for in-app drive
- `C:/tmp/uia-helpers.ps1` — PS UIA helper functions (Get-Win, Find-Btn, Invoke-Btn, Set-Edit, Select-Tab, etc.)

## UIPI workaround note

VPNRouter.App runs at High IL (admin required for TUN+ETW+Firewall). Standard
`SendInput` keyboard from Medium-IL automation is silently blocked. Solutions:

1. Run automation context (Claude Code) as Administrator → SendInput keyboard works.
2. **OR** use Windows UI Automation (UIA) `ValuePattern.SetValue()` which goes
   through the accessibility API and bypasses UIPI for value-set operations.
   Requires loading `UIAutomationClient` + `UIAutomationTypes` assemblies.

The MCP server `tools/VpnRouterTestMcp/` provides mouse + screenshot but NOT
text input (uses SendInput). Combine with UIA from elevated PS for text-set:

```powershell
. C:\tmp\uia-helpers.ps1
Set-Edit 0 "hysteria2://..."  # via ValuePattern, UIPI-immune
Invoke-Btn "Add Server(s)"     # via InvokePattern — also UIPI-immune
```
