# VPNRouter.App.ViewModels Sub-Zone Instructions

This document governs `VPNRouter.App/ViewModels/` and its nested subdirectories (`FreeConfigs/`, `Internals/`).

## Architecture & Partial Class Decomposition

`MainWindowViewModel` is the primary orchestrator of the desktop Avalonia UI. To avoid monolithic god-class complexity, it is decomposed into partial classes organized by functional concern.

### Characterization Contract
The public API surface of `MainWindowViewModel` is pinned by a SHA-256 hash in:
`VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs`
Any modification, addition, or removal of public properties, methods, or commands will change this hash and break the test gate. Do not change public visibility or signatures without re-pinning intentionally.

---

## MainWindowViewModel Partials Map

| Partial File | Core Responsibility | Key Properties & Commands |
|---|---|---|
| `MainWindowViewModel.cs` | Root constructor, DI resolution, tab switching, and overall coordinator. | `CurrentPage`, `SelectTabCommand`, `StatusText`, `IsConnected`, `IsConnecting`. |
| `MainWindowViewModel.Connection.cs` | VPN connection lifecycle orchestrator. Handles start, stop, reconnect, error popups, and engine event handlers. | `ToggleConnectionCommand`, `StartCommand`, `StopCommand`, `ReconnectAsync()`, `OnAutoFailoverMessage`. |
| `MainWindowViewModel.ConnStats.cs` | Real-time traffic telemetry and bandwidth statistics. | `UploadSpeedText`, `DownloadSpeedText`, `TotalUploadedText`, `TotalDownloadedText`, `LatencyText`. |
| `MainWindowViewModel.Profiles.cs` | Profile management, merging, and source priority resolution (GitHub > Local > Built-in). | `Profiles`, `SelectedProfile`, `ReloadProfilesCommand`, `ImportProfileCommand`. |
| `MainWindowViewModel.Subscriptions.cs` | Remote VLESS subscription management and auto-updating. | `Subscriptions`, `AddSubscriptionCommand`, `RefreshSubscriptionCommand`, `UpdateAllSubscriptionsCommand`. |
| `MainWindowViewModel.ServerTesting.cs` | Latency and ping testing across server candidates. | `TestServersCommand`, `FastestServerCommand`, `SortServersCommand`, `PingAllCommand`. |
| `MainWindowViewModel.FreeConfigs.cs` | Free configuration aggregator integration and status. | `FreeConfigs`, `RefreshFreeConfigsCommand`, `SelectFreeConfigCommand`. |
| `MainWindowViewModel.Settings.cs` | Application preferences and advanced routing options. | `DnsMode`, `IsKillSwitchEnabled`, `IsAutoStartEnabled`, `TunMtu`, `SelectedLanguage`, `SaveSettingsCommand`. |
| `MainWindowViewModel.SimpleMode.cs` | Simplified "one-click" UI presentation for casual users. | `IsSimpleMode`, `ToggleUIModeCommand`, `SimpleStatusTitle`, `SimpleStatusDescription`. |
| `MainWindowViewModel.RuntimeStatus.cs` | Observes engine health, external sing-box processes, and conflicting VPN adapters. | `RuntimeStatus`, `ConflictingVpnWarning`, `IsEngineRunning`. |
| `MainWindowViewModel.AutostartBootstrap.cs` | CLI/OS autostart bootstrap arguments (`--autostart`, `--minimized`, `--start-vpn`). | `HandleCommandLineArgs()`, `InitializeTrayIcon()`. |
| `MainWindowViewModel.ThemeAndLogo.cs` | Light/Dark theme switching and mascot penguin logo state animations. | `IsDarkTheme`, `ToggleThemeCommand`, `LogoState` (Idle, Connecting, Connected, Error). |
| `MainWindowViewModel.Localization.cs` | Dynamic string getters (`L_*`) for UI data bindings. | `L_StartVPN`, `L_StopVPN`, `L_Settings`, `L_Connected`, `L_NotConnected`. |
| `MainWindowViewModel.LocalizedLabels.cs` | Localized label adapters for enums, drop-downs, and mode tooltips. | Mode labels, DNS strategy labels, protocol labels. |
| `MainWindowViewModel.Wgturn.cs` | WireGuard and Wgturn utility integration and updates. | `WgturnStatus`, `UpdateWgturnCommand`. |

---

## Auxiliary ViewModels

- `ServerViewModel.cs`: Represents an individual proxy server item (name, ping, protocol badge, country flag, active state).
- `SubscriptionViewModel.cs`: Represents a subscription source (URL, expiration date, data limit, active server count).
- `CustomConfigViewModel.cs`: Editor and validator for raw sing-box JSON configurations.
- `CustomRuleViewModel.cs`: Manager for user-defined process/domain/IP routing rules.
- `AppItemViewModel.cs` & `AppGroupViewModel.cs`: Split-tunnel applications list (running process detection, routing toggles).
- `ServiceViewModel.cs`: Manages Windows Service / system daemon state (install, uninstall, start, stop).
- `SetupWizardViewModel.cs`: Initial configuration wizard for first-time application launches.
- `UpdateNotificationViewModel.cs`: Application update banner and modal dialog.
- `ZapretStrategyDisplayItem.cs`: Selectable strategy options for Zapret DPI bypass.
- `AutoSelectStatus.cs`: Telemetry state for automatic server selection.
- `SubscriptionRefreshDiff.cs`: Delta report when updating remote subscriptions.
- `FreeConfigs/`:
  - `FreeConfigsPageViewModel.cs`: View model for the dedicated free configs tab.
  - `FreeConfigItemViewModel.cs`: Individual free configuration card view model.
- `Internals/`:
  - `TwoPhaseStartCoordinator.cs`: Coordinates the two-phase start process (pre-resolve -> launch).
  - `ToolTabAvailability.cs`: Gating logic for tools and diagnostic tabs based on OS capabilities.

---

## Connection State Machine Flow

```
[ User Clicks Start / ToggleConnection ]
                 │
                 ▼
     IsConnecting = true, StatusText = "Starting..."
                 │
                 ├─► Validate profile & active servers
                 │   (Resolve subscriptions if needed)
                 │
                 ├─► Check for conflicting VPNs / active locks
                 │
                 ├─► Call VpnEngine.StartAsync(...)
                 │         │
                 │         ├─► Clean old orphans (TunOwnershipLock)
                 │         ├─► Generate sing-box config (ConfigGenerator)
                 │         ├─► Validate config (LeakProtection)
                 │         ├─► Launch sing-box process (SingBoxManager)
                 │         ├─► Arm Firewall Kill-Switch (FirewallManager)
                 │         └─► Apply DNS Hardening (Windows/Mac/Linux)
                 │
                 ▼
          [ Success? ]
          ├── YES ──► IsConnected = true, IsConnecting = false
          │           LogoState = Connected, StatusText = "Connected"
          │           Start periodic health telemetry (ConnStats)
          │
          └── NO ───► IsConnected = false, IsConnecting = false
                      LogoState = Error, StatusText = "Connection Error"
                      Trigger SafeMode or AutoFailover if applicable
```

### Disconnect Transition:
- `StopCommand` sets `IsConnecting = true`, `StatusText = "Stopping..."`.
- Symmetrically calls `_engine.Stop()`, runs `OrphanCleanup.KillOrphans(respectTunLock: false)`, stops service if needed.
- In `finally`: `IsConnected = false`, `IsConnecting = false`, `StatusText = "Not Connected"`.

---

## Critical Rules for ViewModel Development

1. **Thread Dispatching**: Engine events (`StatusChanged`, `HealthChanged`, `AutoFailoverMessage`) arrive on background thread-pool threads. All ViewModel property setters bound to UI elements MUST be wrapped in `Dispatcher.UIThread.Post(...)`.
2. **Re-entrancy Protection**: `ToggleConnectionAsync` checks `if (IsConnecting || IsApplying || _isReconnecting) return;` at entry to prevent race conditions from double-clicks.
3. **Never Hardcode UI Text**: All user-facing strings must come from `Strings.*` (via `VPNRouter.Core/Localization/Strings.cs`).
4. **Dispose Subscriptions**: Event subscriptions to `_engine` or `HealthMonitor` must be cleaned up on disposal to avoid leaking ViewModel instances.
