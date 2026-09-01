# VPNRouter.Service

Windows Service wrapper running under `LocalSystem` at boot before user logon. Shares `VpnEngine` with the desktop `VPNRouter.App` and CLI.

## Quick Verification

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ServiceAppCoexistenceTests|FullyQualifiedName~AutostartContractTests|FullyQualifiedName~RuntimeStatusAdoptionTests"
```

## Structure & Layout

- `Program.cs`: Console vs `--service` mode detection.
- `VPNRouterService.cs`: `BackgroundService` implementation using `SubscriptionResolver` and `VpnEngine`.
- `ServiceInstaller.cs`: Service installation/uninstallation via `sc.exe` and failure recovery configuration.

## Lifecycle & Operational Flow

1. Service is registered with `sc.exe create VPNRouter binPath= "...\VPNRouter.Service.exe --service" start= auto`.
2. Starts under `LocalSystem` account after networking boot dependencies (`Tcpip`, `Dnscache`, `Dhcp`).
3. `ExecuteAsync` loads `config.yaml`. When `App.AutostartVpn` is enabled, `AutostartVpnAsync` resolves subscriptions and starts `_engine` through `ResilientStarter`; Zapret and TgProxy have separate startup paths.
4. **Watcher Mode**: If another process (e.g. `VPNRouter.App`) owns `TunOwnershipLock`, the service parks without contention while continuing `config.yaml` file-watching for hot-reload.

## Critical Invariants & Execution Rules

### TunOwnershipLock & Concurrency
- Global single semaphore ensures only one process controls sing-box at a time. Service gracefully falls back to watcher mode when TUN lock is claimed.

### Hot-Reload Configuration Watcher
- `config.yaml` file watcher triggers `_engine.ApplyAsync(newSettings)` when settings change, executing `VlessServersResolver.Resolve` internally.

### Windows Event Logging
- Errors and lifecycle events log to Windows Event Log (`Source: "VPNRouter"`).

### Installation & Execution Scope
- `VPNRouter.Service.exe` is managed via `VPNRouter.CLI service install / uninstall`. Service process execution requires elevation.
