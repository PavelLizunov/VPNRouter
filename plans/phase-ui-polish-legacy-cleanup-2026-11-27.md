# Phase: UI element polish and legacy string cleanup

Date: 2026-11-27
Branch: `polish/ui-and-legacy-strings-20261127`
Base: `origin/main` (`f89f7615`)

## Intent

Polish visual consistency across desktop views identified during the `v2.51.1-r6` UI MCP audit and clean up outdated legacy strings (`naive://`, `v2.17`, `v2.32.3`, `built-in placeholder`, `vless://`-only hints) across Desktop and Android shared localization.

## Scope

- `VPNRouter.Core/Localization/Strings.SimpleMode.cs` & `Strings.cs`:
  - Replace `naive://` and `vless://`-only watermarks/hints with current protocols (`vless:// / trusttunnel:// / mieru:// / hy2:// / awg://` and subscriptions).
  - Remove stale `v2.17`, `v2.32.3`, and `built-in placeholder` copy.
- `VPNRouter.App/Views/Pages/SimplePage.axaml`:
  - Remove the duplicate power pictogram inside the Connect/Disconnect pill CTA (the hero ring badge already carries it).
- `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml`:
  - Align the Search card and primary Find CTA with the app's neutral surface and `AccentSolidBrush` primary palette instead of `Success` green.
- `VPNRouter.App/Views/Pages/DpiBypassPage.axaml`:
  - Align secondary Zapret strategy-run and bottom maintenance button sizing/padding cleanly.
- `VPNRouter.App/Views/MainWindow.axaml`:
  - Improve unselected tab icon contrast in the Advanced tab strip.
- `VPNRouter.App/Views/Pages/ApplicationsPage.axaml`:
  - Increase vertical breathing room on category list rows.

## Invariants

- Zero public-surface changes to `MainWindowViewModel` (characterization SHA-256 unchanged).
- Zero hardcoded hex colors in XAML (semantic dynamic tokens only).
- All `AutomationProperties.Name` and `AutomationProperties.AutomationId` hooks preserved.

## Verification

- Exact-head GitHub CI checks on PR against `main` (`test`, `grep`, `characterization-windows`, `compile`, `build-ui-mcp.yml`, `macos-privileged-system`, `packaging-arch`).
