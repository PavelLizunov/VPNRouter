# VPNRouter.CLI

CLI wrapper built with Spectre.Console. A thin wrapper around `VPNRouter.Core`.

## Quick Verification

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~CliVersionSourceTests|FullyQualifiedName~P07CliStopSourceGuardTests|FullyQualifiedName~ServiceAppCoexistenceTests"
```

## Commands & Usage

```
VPNRouter.CLI start --profile <name> [--dry-run]
VPNRouter.CLI start --profile "Name1,Name2,Name3"   (merge multiple profiles)
VPNRouter.CLI stop
VPNRouter.CLI status
VPNRouter.CLI doctor                                 (health check: exit 0=OK, 1=warn, 2=err)
VPNRouter.CLI profiles list
VPNRouter.CLI profiles show <name>
VPNRouter.CLI profiles update
VPNRouter.CLI service install / uninstall / start / stop / status
VPNRouter.CLI --version                              (version from VPNRouter.Core.AppVersion.Version)
```

## Structure & Layout

- `Program.cs`: Spectre.Console root entry point and DI configuration.
- `SettingsAwareTypeRegistrar.cs`: Spectre.Console registrar that resolves DI-aware command constructors.
- `Commands/`:
  - `StartCommand.cs`: SubscriptionResolver + PlatformServices.CreateVpnEngine.
  - `StopCommand.cs`: Stops active VPN session.
  - `StatusCommand.cs`: Reports engine state and PID status.
  - `DoctorCommand.cs`: Config, catalogue, binary, and state health check.
  - `ProfilesCommand.cs`: Profiles list, show, and update actions.
  - `ServiceCommand.cs`: Service install, uninstall, start, stop, status (wraps `ServiceInstaller`).
  - `TestUpdateCommand.cs`: CI auto-update driver (`VPNROUTER_CI=1`).
- `Helpers/`:
  - `AdminHelper.cs`: `IsAdmin()` check.
  - `ProfileSourceFactory.cs`: Factory for profile sources.
  - `StateFile.cs`: `state.json` reader/writer for PID and status sync.
  - `CliJsonContext.cs`: Source-generated JSON context.

## Critical Invariants & Execution Rules

### Administrator Privilege Checks
- `start` requires elevation except in `--dry-run` mode. `service install`, `uninstall`, `start`, and `stop` also enforce elevation through `AdminHelper.IsAdmin()`.
- `stop`, `doctor`, profile commands, status queries, and `service status` do not have a blanket elevation gate; preserve their current least-privilege behavior unless the command gains a privileged operation.

### Dry-Run Mode
- `start --dry-run` generates sing-box JSON, validates it via `LeakProtection`, and outputs config preview without launching sing-box.

### Subscription Resolution
- Service and CLI must invoke `SubscriptionResolver.ResolveAsync(refreshFromNetwork: true)` before `VpnEngine.StartAsync` so fresh subscription endpoints are hydrated into `Vless.Servers`.

## Build & Publish

```powershell
dotnet publish VPNRouter.CLI -c Release -r win-x64 --self-contained -o publish/cli
```
