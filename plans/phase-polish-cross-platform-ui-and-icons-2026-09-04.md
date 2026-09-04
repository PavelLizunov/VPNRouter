# Phase — Cross-Platform UI Polish, Iconography & Android Mobile UX

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/polish-cross-platform-ui-and-icons`
**Accepted base**: `origin/main` head `de77441d`
**Roadmap ref**: Cross-Platform UI Polish & Iconography
**Effort**: 0.5 days
**Risk**: LOW
**Blast radius**: `VPNRouter.Core/Localization/Strings.cs`, `VPNRouter.Android/AndroidApp.AdvancedShell.cs`, `VPNRouter.Android/Resources/mipmap-*/ic_launcher_round.png`, `VPNRouter.App/App.axaml.cs`, and `VPNRouter.Tests`.
**Rollback**: revert branch commit; restore prior implementations

## Why

Following the comprehensive OpenDesign multi-platform visual audit:
1. **Android Copy Leak**: `Strings.OsDisplayName` lacked an `OperatingSystem.IsAndroid()` check, defaulting to `"Linux"`. In the Simple mode autostart card on Android, strings mentioned Windows or Linux boot instead of device boot.
2. **Android Tab Truncation**: In `AndroidApp.AdvancedShell.cs`, the 5-tab strip in `UniformGrid` on narrow screens (320–360 dp) squeezed buttons to ~60 dp, truncating long tab labels like `Applications` (`Applica...`) and `Subscribe` (`Subscri...`).
3. **Android Circular Launcher Icon**: `ic_launcher_round.png` was previously an exact duplicate of the square `ic_launcher.png`. When Android round launchers (e.g. Pixel Launcher, One UI) masked it, corners were clipped and the mascot was placed right against the circular boundary.
4. **macOS Dark Mode Menu Bar Tray Icon**: In `App.axaml.cs`, macOS unconditionally received `penguin_mascot.ico` (black lineart). On dark menu bars (common in macOS Dark Appearance), black lineart is nearly invisible against dark gray panels.

## What

1. In `VPNRouter.Core/Localization/Strings.cs`:
   - Add `OperatingSystem.IsAndroid() ? "Android" :` in `OsDisplayName`.
   - Update `SmpAutostartCardOff`, `SmpAutostartCardSubtitle`, `AutostartBootSectionSub`, and `AutostartBootCheckText` to use device-appropriate terminology on Android ("Настроить автозапуск VPN при загрузке устройства" / "Configure VPN autostart on device boot").
   - Use `"Apps"` for `TabAdvApplications` on Android in English to save horizontal space while keeping Russian `"Приложения"`.
2. In `VPNRouter.Android/AndroidApp.AdvancedShell.cs`:
   - Wrap the tab strip in a horizontal `ScrollViewer` (with hidden scrollbars) and set `MinWidth` on tab buttons, ensuring tab text never gets crushed into ellipsis on narrow screens while maintaining fluid stretch on wider viewports.
3. In `VPNRouter.Android/Resources/mipmap-*/ic_launcher_round.png`:
   - Generate true circular-masked icons with antialiased borders and 72% centered mascot scaling to maintain safe padding across all Android densities (mdpi, hdpi, xhdpi, xxhdpi, xxxhdpi).
4. In `VPNRouter.App/App.axaml.cs`:
   - Update tray icon selection so macOS uses `penguin_mascot_white.ico` when dark appearance is active (`ActualThemeVariant == ThemeVariant.Dark`), and subscribe to `ActualThemeVariantChanged` to dynamically adapt if the user or OS switches appearance.
5. Tests:
   - Add unit tests in `VPNRouter.Tests` validating platform strings on Android, tray icon theme selection, and characterization hash stability.

## How

1. Commit phase brief.
2. Implement Core strings, Android tabs, Android round icons, and macOS tray selection.
3. Add unit tests in `VPNRouter.Tests`.
4. Multi-iteration verification (build/tests, Opus adversarial review, GitHub Actions CI).
5. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [x] Gate 1 — Build clean: Release solution build completes with zero errors in CI workflow `33853122863`.
- [x] Gate 2 — Tests green: all unit and characterization tests pass (2,947 passed, 0 failed, 0 errors, 0 warnings; Windows characterization 33/33 passed).
- [x] Gate 3 — Docs: outcome recorded and plans updated.
- [x] Gate 4 — Adversarial review: Opus swarm review confirmed Android OS checks, tab horizontal scrolling without ellipsis, RGBA round icon integrity, and zero surface drift on both AndroidApp and MainWindowViewModel.
- [x] Gate 5 — Public API surface: MainWindowViewModel and AndroidApp surface hashes unchanged.

## Outcome

**Status**: READY FOR OWNER REVIEW / MERGE — PR #231
**Commits**: `bcf34e1a` (brief); `c395348e` (implementation); `621bac69` (review fixes & RGBA checks); pending docs commit
**Pushed**: `origin/dsh/polish-cross-platform-ui-and-icons`; PR #231 — https://github.com/PavelLizunov/VPNRouter/pull/231
**Files changed**:
- `VPNRouter.Core/Localization/Strings.cs`: added explicit `OperatingSystem.IsAndroid()` branch in `OsDisplayName` and updated autostart strings so Android devices cleanly display "Запуск при загрузке устройства" / "Configure VPN autostart on device boot". English Android tabs use "Apps" to save horizontal space while keeping Russian "Приложения".
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs`: wrapped tab strip in horizontal `ScrollViewer` (with hidden scrollbars) and added `MinWidth = 62` per tab button, preventing text truncation into ellipsis on narrow screens while maintaining fluid uniform distribution on wider screens.
- `VPNRouter.Android/Resources/mipmap-*/ic_launcher_round.png`: generated circular masked icons with antialiased borders and 72% centered mascot scaling to maintain safe padding across all Android densities (mdpi, hdpi, xhdpi, xxhdpi, xxxhdpi).
- `VPNRouter.App/App.axaml.cs`: updated tray icon selection via `GetTrayIconUri(ActualThemeVariant)` to render `penguin_mascot_white.ico` on macOS in Dark Appearance and subscribed to `ActualThemeVariantChanged` for live theme adaptation.
- `VPNRouter.Tests/CrossPlatformUiAndIconPolishTests.cs`: added unit tests validating tray icon theme selection, Android platform string integrity, tab ScrollViewer wrapping, and 8-bit RGBA dimensions/format across all 5 mipmap densities.

**Gate results**: All 5 verification gates passed cleanly in workflow `33853122863`. Total executed tests: 2,947 passed with 0 failures.
