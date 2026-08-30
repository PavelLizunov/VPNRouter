# VPNRouter.Core

Core business logic library for VPNRouter (pure C#, UI-free). Shared across `VPNRouter.App`, `VPNRouter.CLI`, and `VPNRouter.Service`.

## Build Targets & Frameworks

- Targets `net10.0` by default. Multi-targeting with `net10.0-android` is opt-in via `/p:EnableAndroidTarget=true`.
- Defines platform compilation constants: `PLATFORM_WINDOWS` and `PLATFORM_ANDROID`.
- Internal helper visibility to unit tests (`VPNRouter.Tests`) and CLI (`VPNRouter.CLI`) is configured via `InternalsVisibleTo` in `VPNRouter.Core.csproj`.

## Quick Verification

Run the canonical Core test oracle:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ConfigGeneratorTests|FullyQualifiedName~VpnEngineStartAsyncSeamTests|FullyQualifiedName~SingBoxManagerProcessRunnerTests"
```

## Directory & Subdirectory Map

- `Models/`: Data transfer objects, settings schema, profile structures, engine settings, and sing-box JSON configuration models (`AppSettings`, `Profile`, `ProcessRule`, `VPNConfig`, `AppConfig`, `TunSettings`, etc.).
- `Services/`: Core service implementations and orchestration logic:
  - `VpnEngine.cs`: Central VPN lifecycle orchestrator (`StartAsync`, `ApplyAsync`, `Stop`). Coordinates profile resolution, process scanning, config generation, firewall management, ETW monitoring, health checks, and true-split driver engagement.
  - `SingBoxManager.cs`: sing-box process lifecycle and Clash API hot-reloading manager.
  - `ConfigGenerator.cs`: JSON generator for sing-box routing, DNS, and outbounds.
  - `CustomConfigInjector.cs`: Injects process routing into custom sing-box JSON configurations; enforces fail-closed DNS and route rules.
  - `LeakProtection.cs`: Safety validation for generated sing-box JSON configs (missing proxy outbounds, DNS strategy, strict routing).
  - `HealthMonitor.cs`: Periodic VPN connectivity health check and automatic restart/backoff logic.
  - `ConnectionHealthClassifier.cs`, `ConnectionHealthState.cs`, `ClashLogStream.cs`: Observe-only connection health telemetry parser, aggregator, and WebSocket subscriber.
  - `VlessServersResolver.cs`: Aggregates subscription endpoints to VLESS server lists (single source of truth).
  - `SubscriptionResolver.cs`, `SubscriptionFetcher.cs`, `VlessUriParser.cs`: Subscription fetching, base64/URI parsing, and configuration mode switching.
  - `ProcessScanner.cs`: Resolves running processes by profile rules and wildcard patterns.
  - `EtwProcessMonitor.cs`: Real-time process event monitor (Windows ETW).
  - `ProcessQuery.cs`: Safe `GetProcessesByName` wrappers (`AnyAlive`, `CountAlive`) ensuring `Process[]` handles are disposed.
  - `RoutingAppListEditor.cs`: App routing list manager for split-tunnel configuration (`TryAddProcessName`, `TryRemoveProcessName`, `IsStillRoutedByAnother` survivor-guard).
  - `FirewallManager.cs`: Windows Firewall manager (`netsh.exe`).
  - `SplitTunnelDriverProtocol.cs`, `SplitTunnelDriverInterop.cs`, `SplitTunnelDriverManager.cs`: True-split kernel driver protocol, P/Invoke interop, and lifecycle manager (`ISplitTunnelDriver`).
  - `ProfileManager.cs`: Merging and source priority resolution for profiles (GitHub > Local > Built-in).
  - `SettingsLoader.cs`, `SettingsMigrator.cs`: YAML settings loading/saving (YamlDotNet) and schema migrations.
  - `ZapretProbeCache.cs`: Zapret probe cache persistence (`%ProgramData%\VPNRouter\cache\zapret_probe.json`).
- `Services/FreeConfigs/`: Free config aggregator pipeline (`FreeConfigAggregator`, `FreeConfigCache`, `FreeConfigTester`, `FreeConfigDeepVerifier`, `FreeConfigGeoIp`, `FreeConfigPoolFetcher`, `FreeConfigSources`).
- `Services/EmergencyChannel/`: Fail-safe backup connectivity channel manager (`EmergencyChannelManager`, `EmergencyChannelEngine`).
- `Services/Diagnostics/`: Log export and diagnostic redaction helpers (`DiagnosticsExporter`, `DiagnosticsRedactor`).
- `Services/UpdateSources/`: Update-source contracts and GitHub/sideload implementations.
- `Interfaces/`: Core contracts (`IFirewallManager`, `IProcessScanner`, `IProcessMonitor`, `IProfileSource`). The Windows split-driver contract is declared with its manager in `Services/SplitTunnelDriverManager.cs`.
- `Platform/`: OS-specific platform adapters:
  - `Platform/Linux/`: `LinuxFirewallManager.cs` (nftables kill-switch) and Linux DNS hardening.
  - `Platform/macOS/`: `MacFirewallManager.cs` (pf kill-switch), macOS DNS hardening, process scanning, and monitoring.
  - `Platform/Android/`: `AndroidSingBoxRuntime.cs` and Android platform helpers.
  - `Platform/Unix/`: Shared parsers for Unix process and DNS command output.
- `Yaml/`: Source-generated YAML context and custom converters.
- `Localization/`: Shared localized string tables (`Strings.cs`, `Strings.FreeConfigs.cs`, etc.).
- `Json/`: `System.Text.Json` source generation context (`AppJsonContext.cs`).
- `AppPaths.cs`: Cross-platform root data directory resolution (`%ProgramData%\VPNRouter` on Windows, `~/Library/Application Support/VPNRouter` on macOS, `~/.config/vpnrouter` on Linux; overrideable via `OverrideDataDir` for Android).
- `AppVersion.cs`: Single source of truth for versioning (`AppVersion.Version`).

## Critical Invariants & Execution Rules

### Process Casing & Matching (Case-Sensitive)
- Windows `QueryFullProcessImageName` returns exact filesystem casing (e.g. `Discord.exe`).
- Do NOT use `ToLowerInvariant()` when processing `process_name` in `ConfigGenerator.cs`, `ProcessScanner.cs`, or `HealthMonitor.cs`.
- Deduplicate process lists using `StringComparer.OrdinalIgnoreCase`, but preserve original casing to maintain compatibility with sing-box Go map lookups.

### SingBoxManager Lifecycle & Graceful Stop
- Before calling `Kill()` or disposing a sing-box process handle in `SingBoxManager.cs`, set `EnableRaisingEvents = false` on the process handle.
- This prevents process exit callbacks (`Exited`) from executing on the thread pool as unexpected crash events during intentional stops or restarts.

### sing-box DNS Direct Route Constraint
- In sing-box routing, `detour: "direct"` causes a fatal launch error when the `direct` outbound is empty.
- Always generate a `dns-direct` outbound with `udp_fragment: true` so non-proxy detour DNS servers point to a valid outbound.

### Subscription to VLESS Aggregation Flow
- In subscription mode, `app.subscriptions[0].servers` stores subscription endpoint sources while `vless.servers` is empty in settings storage.
- Callers of `ConfigGenerator.Generate` MUST call `VlessServersResolver.Resolve` prior to generation to hydrate `Vless.Servers` in memory (handled automatically by `VpnEngine`).

### Fail-Closed Routing & DNS
- `CustomConfigInjector` enforces fail-closed rules: `route.final` is set to proxy in full-tunnel or exclude mode, Cloudflare DoH is synthesized when proxy detour DNS is missing, and `dns-direct` is excluded from remote DNS tags.
- `LeakProtection.ValidateAppSettings` and `ValidateConfig` verify settings/generated JSON for missing proxy outbounds, DNS strategy integrity, and strict routing. Validation runs in both `StartAsync` and `ApplyAsync` flows of `VpnEngine`.

### Safe Process Query Handles
- All process enumeration in Core must use `ProcessQuery` wrappers (`AnyAlive`, `CountAlive`) or handle `Process[]` arrays inside `try...finally` blocks to explicitly dispose process handles and prevent OS handle leaks.

### Split-Tunnel Driver Seam (Windows)
- True-split tunnel integration on Windows relies on `ISplitTunnelDriver` (`SplitTunnelDriverManager`).
- `VpnEngine.StartAsyncInternal` engages the split driver hook when in exclude mode with non-empty routed lists; `ApplyAsync` re-engages upon process restarts.
- Driver disengagement occurs during `TeardownInternal` after sing-box stops. If the driver is absent or fails, the engine safely fails open to post-capture process rules.

### Firewall Kill-Switch Invariants
- Firewall behavior is mode- and platform-specific. Windows supports its intended per-process/full-tunnel protections; Linux/macOS arm their kill switches only for supported full-tunnel flows and remain disarmed in split-tunnel mode.
- Missing privilege prerequisites (for example a Linux sudo grant) must be surfaced explicitly; do not describe an unarmed firewall as fail-closed.

## Test Strategy

- Internal methods and helpers (e.g. `SubscriptionFetcher.ParseBody`, `FreeConfigAggregator.PreservePreviousValidation`) are directly testable thanks to `InternalsVisibleTo` declarations in `VPNRouter.Core.csproj`.
- Core test targets in `VPNRouter.Tests` include:
  - `ConfigGeneratorTests`: Validates DNS rules, routing rules, and outbound generation.
  - `VlessServersResolverTests`: Validates subscription aggregation logic.
  - `ConfigGeneratorEmptyServersGuardTests`: Pin for empty server guard throwing behavior.
  - `LeakProtectionTests`: Protocol-aware dispatch and DNS strategy leak protection.
  - `CustomConfigInjectorTests`: Custom JSON validation, injection, and fail-closed DNS configuration.
  - `RoutingAppListEditorTests`: App list additions/removals and survivor-guard process routing.
  - `ProcessQueryTests`: Handle disposal verification for process queries.
  - `SubscriptionFetcherParserTests`: Multi-format subscription payload parsing.
  - `FreeConfigAggregatorPreserveTests`: Cache validation merging.
