# VPNRouter.App Zone Instructions

This zone file governs `VPNRouter.App` and all descendant paths (`ViewModels/`, `Views/`, `Services/`, `Styles/`, `Localization/`, `Assets/`, etc.).

## Overview & Target Framework

Avalonia 12.x cross-platform desktop GUI (Windows, macOS, Linux) targeting `net10.0` (declared in `VPNRouter.App.csproj` without platform-specific suffixes). Platform-specific logic is conditionalized via `#if PLATFORM_WINDOWS`.

## Quick Verification

Canonical test oracle: `docs/agent-contract.md`.

Run ViewModel & App-layer unit tests:
```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelCharacterizationTests|FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~MainWindowViewModelTests"
```

Headless Avalonia harness in `VPNRouter.Tests` runs offscreen rendering, page screenshot, and visual diff tests.

## Layout & Mapped Directories

- `VPNRouter.App/`: Project root, `App.axaml` (global resources, Light/Dark theme dictionaries), `Program.cs` (entry point).
- `VPNRouter.App/Styles/`: Design tokens in `Tokens.axaml` (semantic color brushes, radii, spacing).
- `VPNRouter.App/Localization/`: `Strings.cs` pass-throughs to the bilingual Core string source; ViewModel-facing `L_*` getters live in the relevant ViewModel partials.
- `VPNRouter.App/ViewModels/`: ViewModels including `MainWindowViewModel` partial files split by concern (AutostartBootstrap, Connection, ConnStats, FreeConfigs, Localization, LocalizedLabels, Profiles, RuntimeStatus, ServerTesting, Settings, SimpleMode, Subscriptions, ThemeAndLogo, Wgturn) and sub-directories such as `FreeConfigs/`.
- `VPNRouter.App/Views/`: Windows (`MainWindow`, `AboutWindow`, `SetupWizardWindow`) and `Views/Pages/` tab views (`SimplePage`, `ServersPage`, `SubscribePage`, `NetworkPage`, `ApplicationsPage`, `ToolsPage`, `DpiBypassPage`, `TelegramPage`, `FreeConfigsPage`, `EmergencyChannelPage`).
- `VPNRouter.App/Services/`: Desktop application services (e.g., `SelfRepair`, `FileManagerHelper`, `ShellMenuRegistrar`, `ShortcutSelfHeal`, `InstallHealthCheck`, `ShortcutResolver`, `SingleInstance`, `WindowsServiceHelper`, `SteamLibraryScanner`, `WindowForegroundHelper`).
- `VPNRouter.App/Assets/`: App icons, mascot assets, embedded fonts, and install guide resources.

## Design System Rules

`Styles/Tokens.axaml` defines semantic design tokens.
- **Never hardcode hex colors** in XAML; always use dynamic resources (e.g., `{DynamicResource SurfaceAppBrush}`, `{DynamicResource TextPrimaryBrush}`, `{DynamicResource BorderSubtleBrush}`).
- Use semantic token categories for Surfaces, Text, Borders, Accent, States, and Radii (`RadiusXs`, `RadiusSm`, `RadiusMd`, `RadiusLg`, `RadiusPill`).

## Code Patterns & Architecture Rules

### Partial Class Extraction & Characterization Hash
- `MainWindowViewModel` is structured across partial files organized by concern. `MainWindowViewModel.cs` retains constructor and cross-concern orchestration.
- **Characterization snapshot**: `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs` pins the public-surface SHA-256 hash. Any extraction or refactoring must preserve this public surface hash unless an intentional API change is re-pinned.

### Bilingual UI
- Add new bilingual values to the single source of truth, `VPNRouter.Core/Localization/Strings.cs`.
- Add only the required pass-through getter in `VPNRouter.App/Localization/Strings.cs`, then expose it to bindings from the appropriate ViewModel partial such as `MainWindowViewModel.Localization.cs`.
- Never hardcode raw English or Russian strings directly in XAML.

## Critical Gotchas

### Avalonia 12 TabControl + ScrollViewer Overflow Bug
- **Do not use `<TabControl>` if tab content can overflow.** TabControl in Avalonia 12 renders content in a Carousel presenter that fails to propagate bounded parent height to an inner `ScrollViewer`.
- Use manual primitives: a `ToggleButton` selection strip with tab commands, paired with a `Panel` containing per-tab `ScrollViewers` in a bounded Grid `*` row. Current examples are `DpiBypassPage.axaml` and `TelegramPage.axaml`.

### CheckBox.Content Overflow on Narrow Layouts
- **Do not use bare-string `Content="{Binding XLabel}"` on CheckBox.** Bare strings generate non-wrapping TextBlocks that push parent container widths.
- Wrap content explicitly:
```xml
<CheckBox IsChecked="{Binding X}" MinHeight="0" Padding="4,0">
  <TextBlock Text="{Binding XLabel}" TextWrapping="Wrap"/>
</CheckBox>
```

### NumericUpDown Binding
- `NumericUpDown.Value` is `decimal?`. Binding directly to an `int` property causes `InvalidCastException`. Bind to `int?` and apply fallback in logic.

### ConfigMode & Settings Persistence
- `SaveSettings()` persists `ConfigMode`. `IsSubscribeMode` takes precedence in selection; ensure `IsSubscribeMode = false` before saving when switching to VLESS or custom modes to avoid persisting stale subscribe state.
- Guard custom sub-tab selection so missing custom configurations fallback safely to generated or subscribe modes.

### DataContextChanged Event Unsubscription
- Avalonia `DataContextChanged` can fire multiple times (window recreation, host context swaps). Event handlers bound on old ViewModel instances must be unsubscribed before subscribing to a new ViewModel instance to prevent memory leaks and double-firing.

## UI Design & Responsive Layout Rules

1. **Adapt to Narrow Windows**: Default window (520x640) can be resized down to ~360px. Horizontal Grids with 3+ items must specify `MinWidth` on star/Auto columns, use wrapping grids, or switch to a vertical stack gated by an `IsXxxNarrow` flag driven by `SizeChanged` and `AttachedToVisualTree`.
2. **Strict Design Parity**: Copy CSS/design specifications exactly (`padding`, `border-radius`, `border`, `background`). Remember Avalonia `Padding="H,V"` ordering vs. CSS `padding: V H`.
3. **Component Parity**: Custom flyouts and controls must preserve stock auto-close, outside-click, and Escape key behaviors.
4. **State Synchronization**: Rebuilding ViewModels or observable collections must refresh secondary derived views and filter counts atomically.
