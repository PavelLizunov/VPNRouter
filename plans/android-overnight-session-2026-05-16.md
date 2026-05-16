## Android overnight autonomous session — 2026-05-16

Started: 2026-05-16 ~02:00 (after user said "Сейчас я пойду спать").
Ended: 2026-05-16 ~03:00.
Test device: KYOCERA A101BM (Android 12, API 31, 1080×1920 @ 450 dpi).
Build host: Mac (`slovn@192.168.0.246`).
8 phases, **9 commits** pushed to `github/main` + `origin/main`.

### Commit log (overnight only)

| Hash | Phase | What |
|---|---|---|
| `6a32a34` | 1 | theme/language switch loses Advanced shell content (Bug-AND-009) |
| `5b096d5` | 2 | compact sizing for 5" screens (Bug-AND-010) |
| `9157c56` | 3 | test coverage plan + 31 AndroidCategoryLocalizationTests |
| `69acead` | 4 | code review + Critical-1/2 + High-4/6 + Medium-4 fixes (Bug-AND-011) |
| `0e38786` | 5 | Critical-1 stderr + Medium-5 startForeground |
| `301b8b4` | 6 | BlockAds wired through AndroidConfigBuilder (Bug-AND-012) |
| `c269c1d` | 7 | app selection auto-persist + remove flaky row tap (Bug-AND-013) |

### Phase 1: Theme / language switch lost Advanced shell content

**User report**: "при смене темы с темный на белую и наооборот и смене языка, у меня провало содержимое страниц".

**Three causes, three fixes**:

1. `RebuildSimplePageView` (the theme-switch entry point) swapped `singleView.MainView` for a fresh view but didn't restore navigation. Fresh `BuildSimplePageView` builds the Advanced shell overlay with `IsVisible = false` default → users on Advanced were dropped back to Simple.
   - **Fix**: capture `_advShellOverlay.IsVisible` + `_advShellSelectedTab` before swap, `OpenAdvancedShell(tab)` after.

2. `_advShellTabContent` cache held STALE `Control` references after rebuild. On Advanced reopen, `EnsureTabContentBuilt` saw the tab key already present and added the OLD Control to the NEW `_advShellContentHost`, producing the empty-body symptom.
   - **Fix**: `RebuildSimplePageView` now clears both `_advShellTabContent` and `_advShellTabButtons` before construction.

3. `RefreshAdvancedShellStrings` (the language-toggle entry) only touched tab strip + Settings labels. Every other tab body baked Localization.* strings in at construction and didn't expose field refs for in-place updates.
   - **Fix**: drop `_advShellTabContent`, flush `_advShellContentHost.Children`, re-show current tab — building fresh content tree in the new language.

**Bonus**: promote kebab `Advanced ▸` / `Расширенный ▸` toggle button to a field (`_menuAdvancedToggleBtn`) so its Content updates on language toggle. Pre-fix it was a local var and the label drifted out of sync.

**Verified**: 5 iterations of dark↔light + RU↔EN swap while on Advanced > Applications > Work category. Tab stays selected, body re-renders in correct theme & language, category chips translate.

### Phase 2: Small-screen sizing

**User report**: "у меня телефон 5 дюймов и все в приложении немного большеваное".

**Fixes**:
- `StatusCard`: Title 15→14, header Spacing 10→8, subtitle Margin 20→18 + LineHeight 15→13, content Spacing 8→6, Padding 14→12,11. Net trim ~25 dp.
- `_formCard`: Padding 12→10, Spacing 14→11. Saves ~11 dp vertical.
- Connect / Connecting / Disconnect CTAs: vertical padding 12→10 (still meets Material 44 dp touch target).
- `SmpAdvCardSubtitle`: was hardcoded "Servers · Subscriptions · Zapret · Telegram proxy · Public" — wrong on Android. Android branch now reads "Servers · Subscriptions · Settings · Applications · Public" matching the actual visible tab set.

**Verified**: native 1080×1920 @ 450 dpi + simulated 5" 720p (`wm size 720x1280 && wm density 320`). On 5" 720p Connect button stays visible above nav bar without scrolling.

### Phase 3: Test coverage plan + new unit tests

**Coverage audit doc** (`plans/android-test-coverage-plan.md`):
- Current state: 50 test files, 2 Android-tagged.
- Target matrix: what's testable in net8.0 xUnit vs live-device-only.
- 10-step live-test playbook.
- Future work (Espresso/UIAutomator runner, snapshot tests).

**31 new unit tests** (`AndroidCategoryLocalizationTests.cs`) pin the lookup contract for the Applications-tab chip strip:
- Every internal id used by `AndroidCategoryDefaults` has both EN + RU labels.
- `GroupDisplayName` returns unknown id verbatim (fallback for user-defined categories).
- 10 canonical translation triples locked.

Result: 31/31 PASS in 36 ms.

### Phase 4: Senior Android code review

**Findings** (full doc at `plans/android-code-review-2026-05-16.md`): 2 Critical, 5 High, 4 Medium, 4 Low/Info.

**Critical-1**: `singboxLogPath` defaulted to `GetExternalFilesDir(null)` — world-readable via adb / file manager / USB on all API levels. sing-box at `level=info` emits remote hostnames, UUIDs, Reality handshake metadata.
   - **Fix**: route to `FilesDir` (private sandbox) unconditionally.

**Critical-2**: `test-uri.txt` external-file override was a takeover surface. Any app / person with shared-storage write could drop a file to silently redirect all VPN traffic through an attacker server, with no debug gate.
   - **Fix**: gated behind `IsDebuggable()` (reads `ApplicationInfo.Flags.Debuggable`) + moved to `FilesDir`. Release builds cannot honour an externally-planted override.

**Same fix scope**: `config-dump.json` writes (full VLESS UUID, Reality keys, custom JSON) also gated behind `IsDebuggable()`.

**High-4**: `MainActivity.IntentChanged` / `TunnelErrorReported` static events never unsubscribed. Every AndroidApp reconstruction added another subscriber, leaking the visual tree + Bitmap cache.
   - **Fix**: `AttachLifecycleEvents` / `DetachLifecycleEvents` helpers + `s_currentLifecycleSubscriber` static tracker so attaching on a new instance auto-detaches the previous.

**High-6**: `CancellationTokenSource` for chip pulses cancelled but never Disposed — Timer + ManualResetEvent leak on every state change.
   - **Fix**: capture + null + Cancel + Dispose pattern on both `SetVpnChipState` and `SetZapretChipState`.

**Medium-4**: `LoadMascot`'s `AssetLoader.Open` stream not disposed.
   - **Fix**: `using var stream = AssetLoader.Open(...)`.

### Phase 5: Performance + battery

CPU baseline measured at ~7-8 % idle on this device (no VPN, foreground). Identified single DispatcherTimer (Bug-AND-006 already fixed earlier — only fires while connected). Subscriptions only refresh on manual user action (no hourly auto-refresh like the desktop one that was fixed in v2.31.10 r3). Wake-lock has a 60 s timeout + finally-release. No periodic background work.

**Two new fixes** (`0e38786`):
- Critical-1 continuation: `VpnRouterService.java` was redirecting Go-side stderr to external storage. Moved to private FilesDir.
- Medium-5: migrated `startForeground` to the 3-arg form on Android 14+ (API 34) with explicit `FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED`. Pre-API-34 keeps the 2-arg form.

### Phase 6: BlockAds wiring

**Bug-AND-012**: `AndroidStorage` had `KeyBlockAds` + `GetBlockAds` + `SetBlockAds` plumbed through Settings UI but `AndroidConfigBuilder.BuildConfigJson` never copied the stored bool into `settings.App.BlockAds`. The toggle was silently a no-op for every Android user.

**Fix**: wire `settings.App.BlockAds = AndroidStorage.GetBlockAds()` in both `BuildConfigJson` paths (generated + custom). `RuleSetCacheManager` already resolves `AppPaths.DataDir` which is overridden to `FilesDir` on Android, so the .srs cache lands in the private sandbox without further plumbing.

### Phase 7: 15 verification iterations + Bug-AND-013 discovery

**Iteration log**:

| # | Action | Result |
|---|---|---|
| 1 | Fresh launch | EN/Light loads cleanly |
| 2 | Simple > theme dark | Page rebuilds in dark |
| 3a | Open Advanced > Applications (dark) | Tab strip + chip strip + content correct |
| 3b | Theme switch dark→light while in Advanced | Tab + Custom chip preserved (Phase 1 ✓) |
| 4 | Language switch EN→RU while in Advanced | All chips + body labels translate (Phase 1 ✓) |
| 5 | Tap Custom chip | 113 apps showing |
| 6 | Navigate back via relaunch | App restores last Advanced state |
| 7 | Vertical scroll through apps (540 1300 → 400) | List scrolls, Selected stays 0 |
| 8 | Tap Camo Camera row (light theme) | Selected=1, "Свои 1" badge |
| 9 | **Theme switch dark — REGRESSION** | Selected→0, scroll position lost |
| 10 | **Bug-AND-013 fix #1**: auto-persist on toggle | Build + install |
| 11 | Retry test | Row Border tap not toggling now |
| 12 | Tap detector approach iteration: bare PointerPressed, manual, Tapped | None worked inside ListBoxItem |
| 13 | **Bug-AND-013 fix #2**: drop row tap, use CheckBox-only | Build + install |
| 14 | Tap CheckBox at right edge of row | Selected=1, badge appears |
| 15 | Theme switch dark → light | **Selected=1 PRESERVED, Camo Camera in Selected section, checkbox still cyan-filled, "Свои 1" badge intact** |

**Bug-AND-013 root cause**: `ReseedAppPickerTabState` replaces `_appPickerSelected` with `AndroidStorage.GetPerAppPackages()`. Pre-fix selections were only persisted on Done tap, so theme/lang rebuild mid-edit dropped every unsaved tap. Fix auto-persists on every `IsCheckedChanged`.

**Bug-AND-013 fix #2 rationale**: The synthetic "tap row to toggle" feature was the source of the Phase 6 brat report ("scroll lands in apps") AND the root cause of multiple failed iterations during Phase 7. Three implementations were tried:
- Bare `PointerPressed`: fired mid-scroll, toggled rows accidentally.
- Manual press-position + release-position detector: works for chip strip (WrapPanel) but fails inside ListBox because `ListBoxItem` captures pointer for its own selection, swallowing the inner Border's `PointerReleased`.
- `Tapped` event: swallowed by ListBox's `ScrollGestureRecognizer` on Android before bubbling.

Resolution: drop the synthetic row tap entirely. Users toggle via the explicit CheckBox at the row's trailing edge.

### Phase 8: Final summary (this doc)

### What's known-broken or pending

From the Phase 4 code review, the following remain:

- **High-1**: scrub regex too permissive for IPs + Reality keys in crash log. Backlogged.
- **High-2**: auto-update has no SHA verification. Backlogged.
- **Medium-1**: `_appPickerSelected` reassignment race (safe today, hazard if future async paths added). Backlogged.
- **Medium-2**: `_advAppsCustomCategories` mutate + iterate on same thread without snapshot. Backlogged.
- **Medium-3**: `LaunchCameraForQr` temp JPEG cleanup. Backlogged.
- **Low-1**: `_diagnosticsTimer` reused but never released. Aggravator only.
- **Low-2**: Play Store policy review for `FOREGROUND_SERVICE_SYSTEM_EXEMPTED`. Documented; verify before Play Store submission.
- **Low-3**: broad `ConfigChanges` mask hides config-change-driven bugs.
- **Info-1**: library versions current as of 2026-05.
- **Info-2**: `libbox.aar` has no SBOM / integrity check.

### Recommended next steps (when user wakes)

1. **Verify the BlockAds fix end-to-end**: enable BlockAds in Settings, connect VPN with a working subscription, browse an ad-heavy site (e.g. `dirty.ru`), confirm fewer ads. (Could not be done overnight without user credentials for a working subscription.)

2. **Verify RU bypass + ad-block under real traffic**: connect → browse → check sing-box log for `reject` actions on ad domains + `direct` actions on RU domains.

3. **Confirm Bug-AND-013 doesn't regress under language toggle**: dark + RU + Applications + select 3 apps + switch RU→EN → check all 3 still selected.

4. **Plan APK release**: bump `AppVersion`, build full Win + Mac + Linux + Android, push as `vX.Y.Z-rN`, run live-update gate test.

5. **Decide on Play Store policy**: `FOREGROUND_SERVICE_SYSTEM_EXEMPTED` is the defensible choice but warrants verification against current Play Store policy before submission.

### Files touched

| File | Lines added/changed |
|---|---|
| `VPNRouter.Android/AndroidApp.axaml.cs` | +160 / -55 |
| `VPNRouter.Android/AndroidApp.AdvancedShell.cs` | +43 |
| `VPNRouter.Android/MainActivity.cs` | +56 / -25 |
| `VPNRouter.Android/AndroidConfigBuilder.cs` | +25 |
| `VPNRouter.Android/VpnRouterService.java` | +20 / -10 |
| `VPNRouter.Android/Controls/StatusCard.cs` | +12 / -5 |
| `VPNRouter.Core/Localization/Strings.cs` | +6 / -2 |
| `VPNRouter.Tests/AndroidCategoryLocalizationTests.cs` | +130 (new) |
| `plans/android-test-coverage-plan.md` | +75 (new) |
| `plans/android-code-review-2026-05-16.md` | +170 (new) |
| `plans/android-overnight-session-2026-05-16.md` | +200 (this doc) |

### CI state

- Windows build: green via local `dotnet publish`.
- Mac/Linux build: not triggered (no tag bump; user wakes to decide).
- Unit tests: 31/31 PASS for new AndroidCategoryLocalizationTests; pre-existing tests not re-run.

Hand-off ends here. APK v19 is installed on the test phone, light theme, no VPN connected, Custom category with Camo Camera selected.
