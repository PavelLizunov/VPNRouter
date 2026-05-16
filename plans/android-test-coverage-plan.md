## Android test coverage plan

Generated 2026-05-16 during overnight autonomous Phase 3 work.

### Current state

VPNRouter.Tests has 50+ test files but only 2 are Android-tagged:

| File | Layer | What it covers |
|---|---|---|
| `AndroidStorageSaneTests.cs` | Core | Repair pass for corrupted SharedPreferences (5 scenarios) |
| `AndroidDpiBypassInjectorTests.cs` | Core | DPI bypass JSON injection (off/standard/aggressive + idempotency) |

Everything else is Windows/desktop-leaning. Android-specific surface that **cannot** run in xUnit net8.0:

- `MainActivity`, `VpnRouterService.java`, libbox JNI — needs Android runtime.
- `AndroidApp.axaml.cs` UI builders — touch `Application.Context`, `Android.Util.Log`.
- `AppListLoader` — depends on `PackageManager`.
- `AndroidUpdater` — depends on `Settings`, `PackageManager`.

Android-specific surface that **can** run in xUnit net8.0:

- `AndroidCategoryDefaults` — pure data + lookups (`Find`, `IsCustomCatchAll`, `AllBuiltInPackages`).
- `AndroidConfigBuilder.PatchLogPathForAndroid` — pure JSON manipulation.
- `AndroidConfigShare` — pure JSON manipulation (export/import). Already tested via `ConfigShareDocumentTests`.
- Pure helpers in `AppIconCache` (key normalisation, cache eviction).

### Target coverage matrix

| Surface | Layer | Test approach | Status |
|---|---|---|---|
| `AndroidCategoryDefaults.All` | Core | xUnit + InMemory | **NEW Phase 3** |
| `AndroidCategoryDefaults.Find` | Core | xUnit | **NEW Phase 3** |
| `AndroidCategoryDefaults.AllBuiltInPackages` | Core | xUnit | **NEW Phase 3** |
| `AndroidCategoryDefaults.IsCustomCatchAll` | Core | xUnit | **NEW Phase 3** |
| `AndroidConfigBuilder` log level patch | Core | xUnit + JSON-fixture | **NEW Phase 3** |
| `AppListLoader.Load` (system + curated allowlist branch) | Android | Live device (`adb`) | **Live test only** |
| `RebuildSimplePageView` (theme switch preserves state) | UI | Live device (MCP / `uiautomator`) | **Live test only** |
| `RefreshAdvancedShellStrings` (language preserves state) | UI | Live device | **Live test only** |
| App-row tap detection (drag vs tap) | UI | Live device | **Live test only** |
| Category-chip tap | UI | Live device | **Live test only** |
| `VpnRouterService.openTun` allow/disallow | Service | Live device | **Live test only** |
| `AndroidUpdater` | UI | Live device | **Live test only** |

### Live-test playbook (post-build verification)

Each release-candidate build must pass these manual checks via MCP/`adb`:

1. **Theme cycle**: Light→Dark→Light, on each: Simple page renders, Advanced > Servers/Subscribe/Settings/Applications/Public all render in the new theme.
2. **Language cycle**: RU→EN→RU, on each: Simple page + Advanced shell + every kebab item have updated labels.
3. **Theme inside Advanced**: open Advanced > Applications > Browsers, switch theme → tab preserves, content re-renders in new theme.
4. **Lang inside Advanced**: open Advanced > Applications > Browsers, switch language → tab preserves, chip labels + body translate.
5. **App-list scroll**: open Advanced > Applications > Custom, swipe ≥ 6 rows vertical → list scrolls, Selected count unchanged.
6. **Category-chip strip**: tap each visible chip in turn (Custom, Discord, ..., Privacy) → each activates, app list rebuilds, Selected unchanged.
7. **VPN subscribe**: paste working subscription URL, Connect → tun0 up, `curl ifconfig.io` returns proxy IP, sing-box log shows hijack-dns + vless[proxy] outbounds.
8. **Theme/lang under VPN**: with VPN connected, switch theme → tunnel stays up, no log delta interruption.
9. **Background CPU**: with VPN connected + Simple page foreground, `top` reports < 15 % per second sustained.
10. **Cold-start time**: `time` between launcher tap and Simple page rendered should be < 2 s on KYOCERA A101BM.

### Methodology compliance

Per `plans/android-development-methodology.md`:

- **§3.1 Layer A (Core)**: Pure C# tests in xUnit, run on every CI build.
- **§3.1 Layer B (Android runtime)**: Live-device tests via `mcp__computer-use` or `uiautomator` dumps + screenshots. Currently manual; ideal future state is an Espresso/UIAutomator integration test suite running in CI emulator.
- **§3.1 Layer C (Service / Native)**: `libbox` start, TUN open, packet-route smoke test. Manual via `adb logcat` filters.

Tests added in this Phase 3 batch lock down Layer A behaviour for recent fixes:

- `AndroidCategoryDefaultsTests` covers ordering, hint stability, and `AllBuiltInPackages` symmetry.
- `AndroidConfigBuilderLogLevelTests` covers the Bug-AND-006 fix (no `debug` override on `info`-configured levels).

### Future work

1. Add Espresso/UIAutomator project under `VPNRouter.Android.Tests` (separate csproj, `net8.0-android`, runs in emulator).
2. Wire that project into `.github/workflows/android.yml` once we have a hosted emulator runner. The current `r10` workflow only builds the APK.
3. Add property-based tests for `AndroidConfigBuilder.PatchLogPathForAndroid` with random JSON fragments to catch edge cases.
4. Snapshot-test Simple page and Advanced tab visual trees in light + dark + RU + EN combinations via `Avalonia.Headless` (port the desktop `PageScreenshotTests` infrastructure).
