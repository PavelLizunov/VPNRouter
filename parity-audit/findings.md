# VPNRouter — UI/UX parity audit findings

**Date**: 2026-05-09 after v2.32.0 + AND-PROFILES merge.
**Re-test 1**: 2026-05-09 after F-01 + F-02 — static screenshot match only.
**Re-test 2 (LIVE INTERACTIVE)**: 2026-05-09 after F-10 + F-11 + F-12 — verified press-by-press on real desktop fresh-build + Android phone:
- F-10 ✅ canonical kebab — both kebabs now expose Find a server / Routing profiles / Copy log path / View crash log / Check IP leak / Run Health Check / Export+Import / Restart in Safe Mode / Reset (red). Desktop has residual "Advanced ▶" placeholder (cosmetic — items moved out, container still visible).
- F-11 ✅ active VPN-config input on desktop — typed `https://example.com/sub`, "Detected: subscription URL" hint appeared, Save button enabled, Refresh greyed-until-saved.
- F-12 ✅ silent-flip blocked — Connect button greys when input dirty/unsaved. Unit tests `SmpToggleConnect_WithUnsavedVlessUri/SubscriptionUrl_BlocksAndPreservesConfigMode` (2/2 pass) confirm guard fires + ConfigMode preserved + log line emits at Info level. `LeakProtection.ValidateAppSettings` Core-layer backstop (9 tests) closes defense-in-depth.
- All 42 targeted tests green (incl. pre-existing CustomRulesV2_30 geosite test fixed by alignment chip).
**Convergence**: verified visually via re-rendered composite/simple.png — desktop and Android now share brand row, status wording, default mode, app-list subtext, all-traffic subtext, autostart inline card, advanced settings card, action buttons (Save/Refresh).
**Scope**: 9 desktop pages × Android equivalent overlays.
**Methods used**: M1 (side-by-side), M6 (text content), M7 (locale keys),
M8 (UI element catalog) — partial M2 (pixel diff), M9-M11 deferred.

**Severity legend**: P0 critical · P1 visible UX · P2 polish · P3 platform-justified.

---

## 🔴 P0 — Critical (immediate ship blocker)

None found. Both platforms function for core "connect VPN" flow.

---

## 🟠 P1 — Visible UX (user-noticeable, fix before next stable)

### F-01 — Localization key sets almost completely diverge (9.3% overlap)

Static analysis of `VPNRouter.App/Localization/Strings.cs` vs
`VPNRouter.Android/Localization.cs`:

| Metric | Count |
|---|---|
| Desktop total keys | **540** |
| Android total keys | **303** |
| Keys ONLY on desktop | 490 |
| Keys ONLY on Android | 253 |
| **Keys shared** | **50 (9.3% overlap)** |

Naming conventions differ:
- Desktop: `AboutBrandName`, `ApplyChanges`, `AppsHint`, …
- Android: `BrandTitle`, `ButtonConnect`, `Cc*` (custom config), …

**Impact**:
- Adding a desktop string never automatically reaches Android
- Translation drift inevitable (~3 documented cases over pool 5+6+7)
- Maintenance cost is ~2x for any text change
- Even when both platforms surface the SAME concept, the stored key
  may differ (`AppsHint` desktop vs `BlockOnVpnFailHint` Android)

**Fix**: Phase H (AND-LOCALIZATION-MERGE chip — 2 spawn attempts didn't
complete). Effort 2-3 hours. Make `VPNRouter.Core/Localization/Strings.cs`
the single source-of-truth, both UIs consume it.

---

### F-02 — Simple page layout fundamentally differs (13 sub-divergences)

Side-by-side rendered + visually compared (`composite/simple.png`):

| # | Element | Desktop | Android | Status |
|---|---|---|---|---|
| 1 | Header / brand title | absent | "Virtual Penguin Network" + mascot | ✅ closed v2.32.0 (SimplePage.axaml mini-header re-instated; MainWindow brand columns hide when IsSimpleMode) |
| 2 | Quick-toggle chips (VPN/Zapret/TG) | absent | present at top | ✅ closed v2.32.0 (same brand row) |
| 3 | Status card wording | "Traffic goes **straight**" | "Traffic goes **direct**" | ✅ closed v2.32.0 (Strings.cs SmpStatusDisconnectedHint) |
| 4 | Config·Mode default | "manual · split" | "subscription · all traffic" | ✅ closed v2.32.0 (AppSettings.ConfigMode default → "subscribe", VM `_isVlessMode/_isSubscribeMode` flipped) |
| 5 | Sub-tabs (Subscription/Server/Custom JSON) | absent | present | 🔵 deferred P3 — adding sub-tabs reframes desktop's auto-detect input flow; needs separate VM rework. Not closed. |
| 6 | Action buttons (Save/QR/Refresh) | absent | present | ✅ closed v2.32.0 (`SmpSaveCommand` + `RefreshAllSubscriptionsCommand` buttons on form; QR omitted on desktop — no camera) |
| 7 | Routing label | "Route through VPN" | "What goes via VPN" | ✅ closed v2.32.0 (Strings.cs SmpTunnelModeLabel) |
| 8 | Selected default | Selected apps | All traffic | ✅ closed v2.32.0 (AppSettings.RoutingMode default → "full", VM `_isSplitTunnel` flipped) |
| 9 | Selected-apps subtext | "Based on your selected apps" | "By selected apps list (advanced settings)" | ✅ closed v2.32.0 (Strings.cs SmpSplitHint) |
| 10 | All-traffic subtext | "Includes games and banking" | "Including games and banks" | ✅ closed v2.32.0 (Strings.cs SmpFullHint) |
| 11 | Autostart inline card | present | absent (in kebab→Settings) | ✅ closed v2.32.0 (Android `BuildAutostartInlineCard` between CTA + Advanced card → opens Settings overlay) |
| 12 | Advanced settings card | present (summary list) | absent (use kebab) | ✅ already present on Android (`advCardButton` opens Subscribe overlay) — re-classified as not actually divergent |
| 13 | Information density | sparse, big margins | dense, scrollable | 🔵 P3 platform-justified — desktop has more horizontal real estate, Android optimises for thumb scroll. No fix planned. |

**Closure summary (2026-05-09)**: 11 of 13 rows closed. Remaining:
- Row 5 (sub-tabs) — deferred. Needs VM rework to add an explicit
  Subscription/Server/Custom 3-way segmented control without breaking
  the existing auto-detect SmpInput flow. Spawn separate chip.
- Row 13 (density) — accepted as platform-justified, no fix planned.

**Why this mattered**: this is the FIRST page user sees. Different
wordings, different default routing modes, different navigation patterns
made the two platforms feel like different products. After this round
desktop and Android first-launch present identical brand + canonical
defaults (subscription · all traffic) + identical wording; remaining
gaps are P3 / platform-justified.

**Fix strategy applied**:
- **Wording parity** (rows 3, 7, 9, 10): canonicalised Android's wording
  in `VPNRouter.App/Localization/Strings.cs`; deferred the shared-Core
  string merge to F-01 (Phase H, separate chip — strings are owned by
  per-platform files until then).
- **Default mode** (rows 4, 8): flipped `AppSettings.ConfigMode`
  "generated" → "subscribe" and `RoutingMode` "split" → "full" so a
  first-launch desktop user matches Android. Existing installs keep
  their stored values (config.yaml has explicit values from prior runs).
- **Layout** (rows 1, 2, 6, 11): SimplePage brand row reinstated;
  MainWindow brand columns hide in Simple mode to avoid duplication;
  Save + Refresh action buttons added to the SimplePage form; Android
  gets an inline Autostart card mirroring desktop.
- **Tests updated**: `SettingsValidatorTests` and
  `SettingsLoaderRobustnessTests` reflect the new defaults.

---

### F-03 — Different navigation models (inline vs kebab)

| Behavior | Desktop | Android |
|---|---|---|
| Settings access | Top-right gear icon → settings page | Kebab top-right → settings overlay |
| About page | Help menu → AboutWindow | Kebab → About item |
| Crash log | n/a (file in logs/ only) | Kebab → View crash log |
| Theme switch | Settings → Appearance | Kebab → Light/Dark buttons |
| Language switch | Settings → Language combo | Kebab → RU/EN buttons |
| Logs | Tools page → Open log button | Kebab → Open log |
| Free Configs | Top tab "Free Configs" page | Kebab → Find a server |
| Profiles | absent | Kebab → Routing profiles |

**Impact**: every user discovery path is different. A desktop user
familiar with the gear icon doesn't find theme switching on Android
without exploring kebab.

**Fix**: standardize on ONE pattern. Recommendation:
- Mobile-first paradigm: kebab (hamburger) for secondary actions
- Desktop has more screen real estate: surface common actions inline
- BUT: copy & icons should match between platforms for shared concepts

---

### F-04 — Default ConfigMode differs

- Desktop: `manual · split` (user must populate vless URL + select apps)
- Android: `subscription · all traffic` (user pastes URL → auto)

User installing both and trying same workflow gets confused: "why does
the Android version connect and Windows doesn't, with the same URL?"

**Fix**: pick one default. Recommend Android's (subscription·all) as it's
the lower-friction onboarding. Desktop should match in v2.33 onboarding.

---

### F-05 — Brand presentation absent on desktop simple page

Android shows large "Virtual Penguin Network" title + penguin mascot at
top of main scroller. Desktop has none — user just sees status + form.

**Fix**: port the brand heading to desktop SimplePage. The asset
(`penguin_mascot.png`) is already shared via `<AvaloniaResource Link=...>`
so this is pure layout addition.

---

## 🔴 P0 — Behavior divergences (logic, not just look — found 2026-05-09 retest)

### F-10 — Kebab menu structure + items completely diverge

Live interactive comparison desktop v2.32.0 (currently installed) ↔ Android freshest build:

**Desktop kebab** (10 items, 4 sections, **Advanced submenu collapses additional items**):
- View: Light / Dark / RU / EN
- Diagnostics: Open logs · **Check IP leak** · Check for updates
- Troubleshooting: **Run Health Check** · **Restart in Safe Mode** · **Reset config to defaults** (red, danger style)
- About v2.32.0
- **Advanced ▶** — collapsed submenu (Servers/Subscriptions/Zapret/TgProxy/Public configs hidden inside)

**Android kebab** (15 items, 4 sections, **flat — no submenus**):
- Appearance: Light/Dark + RU/EN
- Free configs: **Find a server** (top of kebab, not hidden)
- Profiles: **Routing profiles** (top of kebab, not hidden)
- Diagnostics: Settings (opens overlay, NOT navigates to Network page) · Open log · **Copy log path** · **View crash log** · Check for updates · **Export config** · **Import config**
- Troubleshooting: Reset settings
- About

**Per-item divergence map**:

| Item | Desktop | Android |
|---|---|---|
| Find a server / Free configs | hidden in `Advanced ▶` submenu | top-level |
| Routing profiles | **absent** | top-level |
| Settings entry | navigates to Network page (full app navigation) | opens settings overlay (modal in current view) |
| Copy log path | **absent** | present |
| View crash log | **absent** | present |
| Export config / Import config | **absent in kebab** | present |
| Check IP leak | present | **absent** |
| Run Health Check | present | **absent** |
| Restart in Safe Mode | present | **absent** |
| Reset config to defaults | red danger style | "Reset settings" without color emphasis |
| Submenu | "Advanced ▶" collapsible | **none — all flat** |

**Impact P0**: 8+ items exist on only one side. User who knows desktop → Android can't find Health Check / Safe Mode / IP Leak. User who knows Android → Desktop can't find Routing Profiles / Crash log / Copy log path / Export. **Mental model breaks.**

**Fix strategy**: standardize on canonical kebab sequence + structure. Need design decision: which items belong on **simple** kebab vs Advanced page. Two paths:
- (a) Mirror **all** items on both (no submenu, all flat — Android model)
- (b) Mirror **all** items on both with consistent Advanced submenu (Desktop model)
- Recommend (a) because it scales better on mobile where there's no big window for Advanced.

**Closure 2026-05-09 — strategy (a) applied**:

Both kebabs now expose the same item set in the same canonical order:

```
Appearance:   Light · Dark · RU · EN
Free configs: Find a server
Profiles:     Routing profiles
Diagnostics:  Settings · Open log · Copy log path · View crash log
              · Check IP leak · Run Health Check · Check for updates
              · Export config · Import config
Troubleshoot: Restart in Safe Mode · Reset config to defaults (red on both)
About:        version + (desktop: About dialog · Android: GitHub repo link)
```

Per-item closure:

| Item | Desktop | Android | Status |
|---|---|---|---|
| Find a server | added (kebab → switch Advanced + Free Configs tab) | already present | ✅ |
| Routing profiles | added (new RoutingProfilesDialog catalog) | already present | ✅ |
| Settings | added (kebab → Advanced + Network tab) | already present | ✅ |
| Copy log path | added (clipboard + toast) | already present | ✅ |
| View crash log | added (opens newest crash-*.txt in default editor) | already present | ✅ |
| Export config | added (FilePickerSave + ConfigShareDocument.Serialize) | already present | ✅ |
| Import config | added (FilePickerOpen + ConfigShareDocument.TryParse) | already present | ✅ |
| Check IP leak | already present | added (Intent.ActionView → ipleak.net) | ✅ |
| Run Health Check | already present (moved Diagnostics) | added (HealthCheck.RunAll → in-app log overlay) | ✅ |
| Restart in Safe Mode | already present | added (AndroidStorage.SetSafeModeOnNextLaunch + relaunch) | ✅ |
| Reset config | already red | now red (DangerSolidBrush foreground) | ✅ |
| Advanced ▶ submenu | n/a — was the bottom UI-mode toggle button (kept) | n/a | ✅ |

Behavior parity caveats (platform-justified):
- **Settings**: desktop navigates to Network tab (Advanced); Android opens
  modal overlay. Same outcome (user reaches settings UI), different
  presentation. Documented in F-03.
- **About**: desktop opens AboutWindow dialog; Android shows Version row
  + GitHub repo link inline in kebab. Same section, different shape.
- **Restart in Safe Mode**: desktop relaunches with `--safe` flag;
  Android sets `safe_mode_on_next_launch` SharedPreferences flag and
  re-launches the activity (Java VM lifecycle).
- **Reset config**: desktop has 5 s auto-disarm; Android has 2-tap
  confirm. Different gestures, both protect against accidental clicks.

Code changes:
- New canonical Core keys: `MenuItemCheckLeaks` / `MenuItemHealthCheck` /
  `MenuItemSafeMode` (+ Tip variants) — alias the existing `SmpMenu*`
  values so both platforms can name the same string identically.
- Desktop: `MainWindow.axaml` kebab restructured (removed item-by-item
  ordering, added 4 new sections in canonical order); `MainWindowViewModel.cs`
  gained `OpenSettingsMenu/OpenFreeConfigsMenu/OpenRoutingProfiles/
  CopyLogPath/ViewCrashLog/ExportConfig/ImportConfig` commands; new
  `RoutingProfilesDialog.axaml{,.cs}` view; `ApplyProfileFromDialog`
  public API on the VM so the dialog applies a profile through the
  existing AppGroups path without reflecting into private fields.
- Android: 3 new menu items wired in `AndroidApp.axaml.cs` (Diagnostics
  block extended with Check IP leak + Run Health Check; Troubleshooting
  gets Restart in Safe Mode); `AndroidStorage.SetSafeModeOnNextLaunch` /
  `ConsumeSafeModeOnNextLaunch` one-shot flag mirrors desktop's `--safe`;
  Reset menu item now uses `DangerSolidBrush` (matches desktop's
  v2.30.3-r1 foreground tint).

Tests + build: `dotnet build VPNRouter.sln -c Release` 0 errors;
regression suite (VlessServersResolver / ConfigGeneratorEmptyServersGuard
/ FreeConfigAggregatorPreserve) 20/20 green.

---

### F-11 — VPN config input is passive on desktop, active on Android ✅ closed (2026-05-09)

Same scenario: paste `https://example.com/sub` into the Simple page VPN config field.

**Desktop pre-fix**:
- Text appears in input field
- **Config·Mode stays as "manual · full"** — does not auto-flip
- **No Save / Refresh buttons appear** — input is just a passive text field
- User must navigate Advanced settings → Subscriptions → manually paste again → Save
- Effectively the Simple page input is decorative

**Android**:
- Subscription tab already active by default (sub-tabs pattern)
- Save / QR / Refresh buttons present inline
- Tap Save → URL persisted, server list begins fetch
- Tap Refresh → re-pulls servers
- One-page workflow

**Impact P0**: First-launch desktop user types URL on Simple page, hits Connect, **nothing visibly happens**. Android user does same flow, sees feedback. Desktop user is left wondering "where do I save this?".

**Fix shipped (2026-05-09)**: SimplePage input now actively detects + persists.
Changes in `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` +
`VPNRouter.App/Views/Pages/SimplePage.axaml`:
- New computed properties: `SmpInputDetectedKind`, `SmpInputDetectedHint`,
  `SmpInputDetectedHintVisible`, `SmpSaveEnabled`, `SmpRefreshEnabled`,
  `SmpInputDirty`, `SmpConnectEnabled`. All wired to PropertyChanged on
  `SmpInput` via `[NotifyPropertyChangedFor]`.
- Auto-detect hint line under the input ("Detected: server link" /
  "Detected: subscription URL"), localized RU+EN in
  `VPNRouter.Core/Localization/Strings.cs`.
- `SmpSaveAsync` (was sync `SmpSave`) — on success: writes snapshot,
  flips dirty=false, surfaces toast ("Saved as subscription" /
  "Saved as server"), and for subscription URLs kicks off
  `RefreshAllSubscriptionsAsync` automatically + shows
  "Subscription refreshed" toast on completion.
- `SmpRefreshAsync` — Simple-page wrapper around
  `RefreshAllSubscriptionsAsync` that surfaces "Syncing…" → "Refreshed"
  toast feedback. Bound to the inline Refresh button.
- `SmpToastText` + `HasSmpToast` + `ShowSmpToast` — 2.5 s auto-dismiss
  toast pattern (mirrors existing `RulesToastText` / `TgProxyToast`).
- Save button `IsEnabled` gated on detected-kind != Invalid AND dirty.
- Refresh button `IsEnabled` gated on subscribe mode + saved
  subscription.

Sub-tab pattern (Subscription / Server / Custom JSON segmented selector
above the input — F-02 row 5) intentionally **deferred to a separate
P3 chip**. The auto-detect hint covers the discoverability gap for now.

**Done when** (verified):
- Paste `https://example.com/sub` → "Detected: subscription URL" hint
  appears → Save lights up → click Save → toast → ConfigMode flips to
  subscribe → Refresh button enabled → click Refresh → progress toast.
- Paste `vless://abc@host:443?#name` → "Detected: server link" → Save →
  ConfigMode flips to generated.
- F-12 follow-up: Connect with empty input + existing config still works
  (upgrader path) but Connect with unsaved-pasted-URL is disabled
  (`SmpConnectEnabled = !SmpInputDirty`). This closes F-12 below.

---

### F-12 — Connect button with URL flips ConfigMode silently ✅ closed 2026-05-09

Same flow continued: with `https://example.com/sub` typed in, click Connect.

**Desktop observed behavior** (pre-fix):
1. Config·Mode silently flips: "manual · full" → "subscribe · full"
2. Status stays "Not connected"
3. Connect button greys out (disabled)
4. **No toast, no error, no spinner, no log entry surfaced** — user sees only that mode changed
5. Underneath: VpnEngine attempted to fetch subscription → empty result → silently bailed

**Android observed behavior** (parallel test on phone):
- Same paste → tap Connect
- Either: Connect button is disabled until Save tapped, OR explicit "Subscription not refreshed" toast
- (Verified earlier: Android forces Save → Refresh → server pick before Connect can fire)

**Impact P0**: dangerous UX. User who pasted URL + clicked Connect silently lands in subscribe mode with empty server list — next start of VPN may LEAK (config is "subscribe" but no servers, falls through to direct). **Same class of bug as v2.28.2 silent leak** but different trigger (UI-driven, not Apply-driven).

**Fix shipped (2026-05-09)** — combined both chip approaches:
- (Connect-side gate) `SmpToggleConnectAsync` guards on
  `IsSimpleInputAlreadySaved`: typed input that doesn't match
  `_settings.App.Subscriptions[].Url` / `_settings.Vless.Servers[]` →
  Connect refuses to silently mutate state, sets inline
  `SmpErrorText = SmpSaveFirstSubscription` / `SmpSaveFirstServer`
  pointing at the Save button, logs block at Info level. Empty input
  keeps existing behaviour (connect with what's saved); matching input
  falls through.
- (CTA disable) Connect button binding additionally gated on
  `SmpConnectEnabled` (false whenever `SmpInputDirty == true`) so the
  user sees the Connect button disable visually before clicking.
- (Audit trail) Every `_settings.App.ConfigMode` mutation in
  `MainWindowViewModel*` now logs at Info level — future silent flips
  become grep-able from the application log.
- (Defense in depth) `LeakProtection.ValidateAppSettings(AppSettings)`
  added at Core layer. Catches "ConfigMode=subscribe + no enabled
  subscription with servers + no manual fallback" pre-config-generation.
  Wired into both `VpnEngine.StartAsync` and `VpnEngine.ApplyAsync`.
- Tests: `MainWindowViewModelTests.SmpToggleConnect_WithUnsaved*`
  (2 cases) + `LeakProtectionAppSettingsTests` (9 cases).

Same failure class as v2.28.2 silent leak — see
`plans/session-night-shift-2026-04-25.md`.

---

### F-13 — Default ConfigMode different on installed desktop v2.32.0

Live shipping v2.32.0 on this machine: ConfigMode **"manual · full"**. Our F-02 fix flipped default to **"subscribe · full"** on main HEAD but that change has NOT shipped yet.

**Impact P1**: any user upgrading from v2.32.0 → next stable will see ConfigMode change at next config.yaml load (because we updated the validator's allowed-default). May confuse some users. Need release note for it.

**Fix**: documentation in next stable release notes. NOT a code change — already addressed by F-02.

---

### F-14 — Wording on installed v2.32.0 still uses pre-F-02 strings

Live shipping v2.32.0:
- "Traffic goes **straight**" (should be "direct")
- "**Route through VPN**" label (should be "What goes via VPN")
- "**Based on your selected apps**" subtext (should be "By selected apps list (advanced settings)")
- "**Includes games and banking**" (should be "Including games and banks")

**Impact P1**: my earlier "convergence verified" claim was wrong. PageScreenshotTests use fresh build. **Installed binary is older.** The match only holds for a future stable, not what users see today.

**Fix**: ship next -rN candidate with F-01 + F-02 + F-03..F-09 fixes bundled. Then verify on fresh install (uninstall old → install new). NOT an additional code change.

---

## 🟡 P2 — Polish (batch fix in v2.33+)

### F-06 — Status card icon differs

Desktop: small dot (●). Android: same dot. Visually consistent, but the
animated states (connecting/connected) may render differently. **Need
captures in those states to confirm**.

### F-07 — Card border-radius / shadow

Desktop cards: subtle border + minimal shadow. Android cards: same border
+ slight drop shadow. Small visual inconsistency. Likely Avalonia FluentTheme
handling differently between desktop and mobile renderer.

### F-08 — Localization wording drift on shared strings (sample)

For the 50 keys that DO appear on both platforms, audit if values match.
Sampling shows divergence on:

- "All traffic" subtext: desktop vs Android (F-02 row 10)
- Status sub-text: "straight" vs "direct" (F-02 row 3)
- "Route through" vs "What goes via" (F-02 row 7)

Need full RU+EN value-by-value diff (deferred — file-level read of
both Strings.cs files for the 50 shared keys).

### F-09 — Quick-toggle chips on Android lack desktop equivalent

Android has VPN/Zapret/TG chips at top providing one-tap toggle. Desktop
has same toggles but in NetworkPage's right rail. Discoverability
differs significantly.

---

## 🟠 P1 (M8 — UI element catalog) — added 2026-05-09

Source: `parity-audit/catalog/element-diff.md`. Static parse of 9 desktop
XAML pages (760 elements) vs 6 Android C# code-behind files (372 elements
+ 72 helper-bucket). Counts per page:

| Page | Desktop | Android | Delta |
|---|---:|---:|---:|
| Simple | 52 | 76 | +24 |
| Subscribe | 49 | 32 | -17 |
| Servers | 68 | 24 | -44 |
| FreeConfigs | 78 | 57 | -21 |
| Applications | 37 | 23 | -14 |
| Network | 342 | 65 | -277 |
| DpiBypass | 87 | 6 | -81 |
| Telegram | 41 | 0 | -41 (P3) |
| Tools | 6 | 10 | +4 |
| AutoUpdate | 0 | 7 | +7 (P3) |

### F-10 — DpiBypass page: Android is a stub (87 → 6 elements)

Desktop `DpiBypassPage.axaml` exposes the full Zapret integration:
12 Buttons (Discord/YouTube/Hosts/Strategy/Open Folder/Apply/Run Tests/
Open Service Menu/Remove Service/...), 3 ComboBoxes (strategy + 7
ComboBoxItem options), 23 TextBlocks (status, version, hints), 1 TextBox
(IpSet filter), 1 ProgressBar, 5 ListBoxItems (advanced strategy choices).

Android's "DpiBypass" surface is 1 ComboBox + 3 TextBlocks inside the
settings overlay — effectively a strategy-mode dropdown only. Missing:
- Discord/YouTube hosts editors
- Strategy advanced options
- Run-tests / clear-cache / open-folder actions
- DpiToggle (start/stop) explicit button
- Version/status display
- IpSet filter + secondary advanced expander

**Why P1**: DPI bypass is a marquee feature on Win (multiple user-visible
controls). On Android the user can only pick a strategy mode without any
of the diagnostics or hosts management. Cross-platform feature parity claim
is misleading.

**Fix**: track as separate roadmap item (`AND-DPI-BYPASS-FULL`). Likely
needs its own overlay (similar shape to settings overlay) and reuse of
desktop ZapretManager logic — but Android binary doesn't ship Zapret
binaries (Cygwin), so the surface should reflect Android-side equivalents
(per-app split etc.) where available, or label the missing controls
"Windows only".

### F-11 — Network settings overlay: 19% element parity (342 → 65)

Desktop `NetworkPage.axaml` is the kitchen-sink page (40 Buttons,
15 CheckBoxes, 4 ComboBoxes, 133 TextBlocks, 7 TextBoxes, 2 RadioButtons,
6 ListBoxItems, 2 MenuItems). Android settings overlay has 5 Buttons,
7 CheckBoxes, 1 ComboBox, 26 TextBlocks, 2 RadioButtons.

Top missing element groups on Android (sample of the 141 only-on-desktop
labelled elements):
- **Routing rules editor**: NewRuleValue/NewRuleType/NewRuleComment
  TextBoxes + Apply/Cancel buttons + RulesEditorStatusText
- **Force-IPv4** (`ForceIpv4Label` CheckBox + matching TextBlock)
- **Apply-now reload**: `L_ApplyNowReloadVpn` Button
- **Restart service**: `L_RestartService` Button
- **Autostart UI/TgProxy** sub-controls (`LblAutostartUi`, `LblAutostartTgProxy`)
- **RulesViewCards** toggle + `RulesEditorApplyText` action

**Why P1**: lots of these controls are runtime essentials (apply-without-
restart, force-IPv4, restart-service). Android user has no path to them.

**Fix**: this is the broadest gap in the catalog. Defer fix to
`AND-NETWORK-SETTINGS-EXPANSION` roadmap item. Suggest pruning to a
"settings essentials" subset on Android and exposing the rest behind
"Advanced..." disclosure (so we don't have to build 277 controls at
once).

### F-12 — Servers page: manual server edit UI absent on Android

Desktop `ServersPage.axaml` has 7 TextBoxes wiring `Name/Server/Port/
Uuid/ShortId/VlessUri` editors plus add/remove buttons (`LblAddServers`,
`LblRemove`) and `LblClickToActivateConfig`/`LblTcpUdpHint`. Android
`AndroidApp.ServerList.cs` has 0 TextBoxes — read-only list of subscribed
servers, no manual entry.

**Why P1**: a Win user can paste a single VLESS URI directly into the
Servers tab. Android user must add it via the Subscribe overlay (subscription
URL), which is a different mental model and doesn't accept raw `vless://`.

**Fix**: track as `AND-SERVERS-MANUAL-ADD`. Add a "+ Add server" button
inside the Servers overlay opening a small sheet with single
`vless://` URI field. Reuse Core `VlessUriParser`.

---

## 🟡 P2 (M8 catalog) — added 2026-05-09

### F-13 — Subscribe page: ping-test column headers / actions missing on Android

Desktop has 4 column-header TextBlocks (`L_ColServer/L_ColIp/L_ColPort/
L_ColPing`), `L_RefreshAll` Button, `SubscriptionDeepProgressText` /
`SubscriptionTestProgressText` / `SubscriptionTestImplausibleWarning`
texts and `ServerDeepButtonText` Button. Android Subscribe overlay has
none of these — it shows the subscription card list but no per-server
test grid.

**Why P2**: parity feature, but Android's saved-server list (in Servers
overlay) covers the basic test latency display. The "deep verify" button
is the actual gap.

### F-14 — Applications page: AddCategory + AppsFullTunnelBanner missing

Desktop has `L_AddCategory` Button, `NewCategoryName` TextBox, plus an
`L_AppsFullTunnelBanner` notice + `L_AppsFullTunnelBannerAction` button
that show when full-tunnel mode is active. Android has neither.

**Why P2**: category creation absent → user can only pick from default
categories or add individually. Banner is an informational state cue;
without it, an Android user in full-tunnel mode wouldn't know that the
selected-app list is currently bypassed.

### F-15 — FreeConfigs: empty-states + freshness label + advanced settings expander missing

Desktop `FreeConfigsPage.axaml` has 1 Expander (`L_FcAdvancedSettings`),
empty-state TextBlocks (`L_FcSavedEmpty/L_FcFilteredEmpty/L_FcSearchListEmptyHint`),
plus per-row `BandwidthDisplay`, `FreshnessLabel`, `CountryDisplay`. Android
FreeConfigs has the list + tabs, but lacks the advanced-settings disclosure
+ empty-state copy + freshness/bandwidth metadata in the per-row view.

**Why P2**: cosmetic parity. Information density is lower on Android, but
the core feature works.

### F-16 — Tools page: different UX paradigm

Desktop `ToolsPage.axaml` is a sidebar `ListBox` with 2 items (`LblToolTgProxy`,
`LblToolZapret`) — a navigation surface. Android has no Tools page; instead
`BuildLogOverlay()` (3 elements: TextBlock "singbox.log", close + reload
buttons) is reachable from the kebab. Fundamentally different shape.

**Why P2**: the "Tools" concept is defined differently on each side
(desktop = sub-feature switcher, Android = log viewer). Not a bug per se,
but shared mental model breaks.

---

## 🔵 P3 (M8 catalog) — confirmed expected divergences

- **Telegram page is desktop-only** (TgProxy is Win Service / Cygwin only).
  41 elements on desktop, 0 on Android. Already documented in §15 of
  `vpnrouter-platform-current-diff.md`.
- **AutoUpdate banner is Android-only** (Win uses Squirrel installer
  notify path). 7 Android elements vs 0 desktop.

---

## 🔵 P3 — Platform-justified (already documented, not goal of audit)

Per `vpnrouter-platform-current-diff.md` §15:

- TgProxy and Zapret service-on-Win-only — Android uses tls_fragment outbound
- Process-name vs UID routing — fundamental Android security model
- File system paths
- Update mechanism (helper.cmd vs PackageInstaller)
- Single-instance / foreground promotion lifecycles

These are already accepted divergences, not issues to fix.

---

## Summary metrics

| Category | Count |
|---|---|
| P0 critical | 0 |
| P1 visible UX | 6 (F-01..F-05, F-10) — F-11 + F-12 closed 2026-05-09 |
| P2 polish | 8 (F-06..F-09, F-13..F-16) |
| P3 platform-justified | 12 (Telegram + AutoUpdate added) |

**Total fix-required findings**: 16.

**Estimated effort to close P1**: ~20-30 hours (locale merge + desktop
Simple-page layout updates + Android DPI/Network/Servers expansion).

---

## Methods used in this audit

- ✅ M1 — Side-by-side composite (8 pages, ImageMagick `+append`)
- ✅ M6 — Text content review (eyeballed visible labels)
- ✅ M7 — Locale key static diff (50/540/303 stats)
- ✅ M8 — UI element comparison: manual + scripted (`catalog/parse-desktop.ps1`,
  `catalog/parse-android.ps1`, `catalog/diff-elements.ps1` → 760+372 elements
  catalogued + per-page diff → F-10..F-16)
- ⏸️ M2 — Pixel diff (deferred: layouts diverge structurally so pixel-diff
  is meaningless without first aligning structure)
- ⏸️ M3 — Perceptual diff (same reason)
- ⏸️ M4 — Color palette extraction (deferred, partial via eyeball)
- ⏸️ M5 — Layout grid heatmap (deferred)
- ⏸️ M6 OCR — tesseract not installed locally
- ⏸️ M9-M11 — click trace (partially done in earlier UI verify session)
- ⏸️ M12 — network capture
- ⏸️ M13 — theme/lang matrix (only Light EN captured; Dark + RU pending)
- ⏸️ M14 — animation timing

**First-pass productivity**: methods 1+7+8 alone surfaced 9 findings.
Pixel-diff (M2-M5) only useful AFTER structural alignment — it's 100%
divergent now because layouts differ.

---

## Captures inventory

```
parity-audit/
├── desktop/  (9 PNGs from PageScreenshotTests headless)
│   ├── page-simple.png
│   ├── page-subscribe.png
│   ├── page-servers.png
│   ├── page-network.png
│   ├── page-applications.png
│   ├── page-tools.png
│   ├── page-dpi-bypass.png
│   ├── page-telegram.png
│   └── page-free-configs.png
├── android/  (12 PNGs from live phone via Mac SSH + adb)
│   ├── page-simple.png
│   ├── page-kebab-menu.png
│   ├── page-subscribe.png
│   ├── page-servers.png
│   ├── page-custom-json.png
│   ├── page-applications-mode.png
│   ├── page-applications-picker.png
│   ├── page-network-settings.png
│   ├── page-dpi-bypass-settings.png
│   ├── page-free-configs.png
│   ├── page-profiles.png
│   ├── page-tools-log.png
│   ├── page-crash-log.png
│   └── page-settings-full.png
├── composite/  (8 side-by-side from ImageMagick)
└── catalog/
    ├── desktop-locale-keys.txt (540 entries)
    └── android-locale-keys.txt (303 entries)
```

---

## Next steps

1. **Spawn fix chips** for F-01 (locale merge) and F-02 (Simple layout
   parity) — these are the highest-leverage.
2. **Capture remaining state matrix** — Dark theme, RU language, error
   states, connected state.
3. **Re-run composite** after F-01 fix to verify locale parity.
4. **Re-run composite** after F-02 fix to verify Simple page parity.
5. **Repeat** until P1 list empty.
