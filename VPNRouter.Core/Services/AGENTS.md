# VPNRouter.Core.Services Sub-Zone Instructions

This document governs the `VPNRouter.Core/Services/` directory and its nested subdirectories (`FreeConfigs/`, `EmergencyChannel/`, `Diagnostics/`, `UpdateSources/`).

## Architecture & Subsystem Taxonomy

The `Services` directory contains 123 service files forming the core runtime business logic of VPNRouter. Services are categorized into 12 functional subsystems:

### 1. Tunnel Lifecycle & Orchestration
Coordinates the overall lifecycle of the VPN connection:
- `VpnEngine.cs`: Central orchestrator (`StartAsync`, `ApplyAsync`, `Stop`, `TeardownInternal`). Coordinates configuration generation, process tracking, firewall engagement, ETW monitoring, and health checks.
- `StartupPipeline.cs`: Sequential startup phase pipeline with ordered dependency initialization.
- `ResilientStarter.cs`: Retry logic, graceful fallback, and staged startup under transient OS or network failures.
- `AutoFailoverEngine.cs`: Autonomous failover to backup servers or alternate profiles when connection degradation is confirmed.
- `TunnelStateResync.cs`: Re-synchronizes internal engine state with external network and OS changes.
- `SafeMode.cs`: Fail-safe recovery mode engaged after repeated startup crashes to prevent boot loops.
- `PowerEventListener.cs`: Subscribes to OS power suspend/resume events to perform orderly teardown and reconnect.
- `LaunchFailureCounter.cs`: Tracks consecutive startup failures to trip safe mode thresholds.

### 2. sing-box Process & Clash API Management
Manages the sing-box child process and its control plane:
- `SingBoxManager.cs`: Primary entry point for sing-box process lifecycle.
- `SingBoxManager.Lifecycle.cs`: Process creation, argument construction, startup verification, and termination.
- `SingBoxManager.CrashDetect.cs`: Unobserved crash detection and post-crash recovery triggers.
- `SingBoxManager.Health.cs`: Active process responsiveness and IPC liveliness verification.
- `SingBoxManager.HotReload.cs`: Dynamic configuration reloading via Clash REST API without tearing down TUN.
- `SingBoxManager.LinuxStop.cs`: Linux-specific graceful termination via `pidfd` to prevent PID recycling races.
- `SingBoxFeatures.cs`: Feature flags and capabilities detected in the sing-box runtime binary.
- `ISingBoxApi.cs`, `ClashSingBoxApi.cs`: Interface and HTTP client for the sing-box Clash REST API.
- `ClashLogStream.cs`: WebSocket subscriber streaming real-time logs from sing-box.

### 3. Process Ownership, Tracking & Cleanup
Enforces deterministic process ownership and prevents handle leaks or PID reuse vulnerabilities:
- `ProcessOwnership.cs`: Binds process identity to exact `PID + start ticks + path` tuples. Windows token and handle pinning.
- `UnixOwnedProcessSignal.cs`: Exact owned-PID signaling via `pidfd_open` / `pidfd_send_signal` on Linux and exact PID bounds on macOS.
- `TunOwnershipLock.cs`: Fail-closed named mutex and lock file guarding TUN device allocation against dual-instance collisions.
- `OrphanCleanup.cs`: Safe cleanup of dangling sing-box or helper instances; strictly verifies ownership before termination.
- `WedgeKillPolicy.cs`: Policy for forcibly terminating unkillable or wedged child processes.
- `ProcessQuery.cs`: Safe wrappers (`AnyAlive`, `CountAlive`) ensuring native `Process[]` handles are explicitly disposed.
- `ProcessImagePath.cs`: Platform-specific resolution of full process image paths (`QueryFullProcessImageName` on Windows).
- `ProcessScanner.cs`: Discovers and filters active processes according to profile rules and wildcard patterns.
- `EtwProcessMonitor.cs`: Real-time Windows Event Tracing for Windows (ETW) process creation/termination monitor.
- `RoutingAppListEditor.cs`: Split-tunnel app inclusion/exclusion list manager with survivor-guard logic (`IsStillRoutedByAnother`).

### 4. Configuration Pipeline & Rule Injection
Generates and validates sing-box JSON configurations:
- `ConfigGenerator.cs`: Root JSON configuration generator for routing, DNS, inbounds, and outbounds.
- `ConfigGenerator.Dns.cs`: Generates DNS servers and DNS routing rules (direct, detour, remote).
- `ConfigGenerator.Outbounds.cs`: Generates protocol outbounds (VLESS, Shadowsocks, Trojan, WireGuard).
- `ConfigGenerator.OutboundBuilders.cs`: Low-level JSON building blocks for individual outbound entries.
- `ConfigGenerator.Route.cs`: Generates top-level sing-box route configuration, default rules, and final detours.
- `ConfigGenerator.Rules.cs`: Generates process-based, domain-based, and IP-based routing rules.
- `ConfigPipeline.cs`: Multi-stage pipeline orchestrating raw inputs -> rules -> JSON output.
- `ConfigSanityCheck.cs`: Structural validation of generated sing-box configuration.
- `CustomConfigInjector.cs`: Injects process routing into custom sing-box JSON configurations; enforces fail-closed DNS and route rules.
- `CustomRulesParser.cs`, `CustomRulesImportExport.cs`: Parses and serializes user-defined custom routing rules.
- `RuleSetCacheManager.cs`: Caching and updating of sing-box binary/source rule-sets (geosite, geoip).
- `LeakProtection.cs`: Safety validation for generated configs (missing proxy outbounds, DNS strategy, strict routing).
- `ConfigShareDocument.cs`: Serializes and formats shareable configuration bundles.
- `StjNodeHelpers.cs`: Helper utilities for `System.Text.Json.Nodes.JsonNode` manipulation.
- `SuffixMatch.cs`: Fast suffix tree/trie matcher for domain routing.

### 5. DNS Hardening & Leak Protection
Secures DNS resolution against leaks:
- `DnsLockdownPolicy.cs`: Enforces strict DNS policies during active VPN sessions.
- `StrictDnsFailoverPolicy.cs`: Manages failover behavior if the primary detour DNS fails.
- `DnsFlusher.cs`: Flushes OS DNS resolver caches across Windows (`ipconfig /flushdns`), macOS (`dscacheutil`), and Linux (`systemd-resolve`).
- `WindowsDnsHardening.cs`, `IWindowsDnsHardening.cs`: Windows NRPT (Name Resolution Policy Table) and interface metric configuration.
- `IUnixDnsHardening.cs`: Contract for Unix DNS resolver manipulation.
- `HostsManager.cs`: Safe hosts file modification and restoration.
- `PlaceholderDefense.cs`: Guards against dummy/placeholder DNS records leaking traffic.

### 6. Network & System Probing / Diagnostics
Probes network paths and detects environmental constraints:
- `NetworkInterfaceDetector.cs`: Detects active network interfaces, default gateways, and route transitions.
- `NetPortUtil.cs`: Checks port availability, allocates free ports, and avoids port collisions.
- `ConflictingVpnDetector.cs`: Identifies third-party VPN software (WireGuard, Tailscale, OpenVPN, Cisco AnyConnect).
- `TunAdapterDiagnostics.cs`: Inspects Wintun / TUN adapter status, MTU, and driver registration.
- `TcpTlsProbe.cs`: Low-level TCP and TLS handshake probing to test endpoint reachability.
- `UdpDegradationDetector.cs`: Detects UDP packet loss and degradation (such as WireGuard throttling by DPI).
- `DeepVerifyProbe.cs`: Deep verification of proxy server data plane (HTTPS probe through proxy).
- `DeepVerifyConstants.cs`: Timeouts, probe targets, and thresholds for deep verification.

### 7. Subscriptions & Protocol Parsers
Resolves and parses remote proxy subscription formats:
- `SubscriptionResolver.cs`: Orchestrates subscription updates, caching, and VLESS endpoint extraction.
- `SubscriptionFetcher.cs`: Fetches subscription content over HTTP/HTTPS with timeout and size caps (`PolicyHttpClient`).
- `SubscriptionUserInfo.cs`: Parses upload/download traffic limits and expiration headers (`Subscription-Userinfo`).
- `VlessServersResolver.cs`: Aggregates subscription endpoints to VLESS server lists (single source of truth).
- `VlessUriParser.cs`: Parses VLESS URI schemes (`vless://...`) with query parameters (reality, flow, security, pbk, sid).
- `ServerUriParser.cs`: General parser supporting Trojan, Shadowsocks, and VLESS URI schemes.
- `VlessDeepVerifier.cs`: End-to-end data plane testing for individual VLESS endpoints.
- `ClashYamlParser.cs`: Parses Clash YAML proxy provider configs.
- `ProviderKey.cs`: Deterministic identity keys for proxy providers and endpoints.

### 8. Health Monitoring & Telemetry
Monitors connectivity health and generates diagnostics:
- `HealthCheck.cs`: Point-in-time system health evaluation (binaries, services, network).
- `HealthMonitor.cs`: Periodic background connectivity monitor with exponential backoff and restart triggers.
- `ConnectionHealthClassifier.cs`: Classifies connection health based on latency, errors, and packet loss.
- `ConnectionHealthState.cs`: Telemetry snapshot of current health.
- `ConnectionIntentScorer.cs`: Evaluates routing efficiency and quality of service.
- `ServerHealthClassifier.cs`: Ranks server reliability.
- `ServerHealthProbe.cs`: Active latency and handshake prober for server candidates.
- `ServerHealthStore.cs`: In-memory and persistent cache of server latency and reliability history.
- `ServerHealthPhaseMapper.cs`: Maps probe outcomes to UI-facing phase indicators.
- `CrashReporter.cs`: Generates sanitized crash dumps and panic logs.
- `DiagnosticsExporter.cs`: Packages logs, route tables, and system info into diagnostic archives.
- `DiagnosticsRedactor.cs`: Redactor removing IP addresses, credentials, tokens, and UUIDs from logs.

### 9. Settings, Storage & Persistence
Loads, migrates, and persists application configuration:
- `SettingsLoader.cs`: Loads settings from YAML with schema validation.
- `SettingsMigrator.cs`: Version-to-version settings schema migration.
- `SettingsValidator.cs`: Validates integrity and semantic constraints of settings before save.
- `ProfileManager.cs`: Profile CRUD, priority merging (GitHub > Local > Built-in).
- `ProfileApplication.cs`: Applies selected profiles to current runtime configuration.
- `CacheRecovery.cs`: Recovers corrupt cache files without crashing.
- `StorageBlobRecovery.cs`: Compensating recovery for storage blobs during update failures.
- `LockFile.cs`: Cross-process file locking primitive.
- `RealFileSystem.cs`, `IFileSystem.cs`: Filesystem abstraction and physical implementation.
- `ISettingsStore.cs`: Storage interface for settings.

### 10. Platform Interop & Drivers
OS-level execution helpers and driver management:
- `SplitTunnelDriverManager.cs`: Manager for the Windows kernel split-tunnel driver (`ISplitTunnelDriver`).
- `SplitTunnelDriverProtocol.cs`: IOCTL codes and data structures for driver communication.
- `SplitTunnelDriverInterop.cs`: Native P/Invoke methods for driver handles and device I/O.
- `FirewallManager.cs`: Windows Firewall manager (`netsh.exe` and WFP integration).
- `WindowsPnpDeviceManager.cs`: Enumerates and manages Windows PnP devnodes for Wintun.
- `WindowsServiceCommand.cs`: Resolves absolute `%SystemRoot%\System32\sc.exe` and formats safe SCM commands.
- `LinuxRuntimeEnvironment.cs`: Evaluates Linux distribution, init system (systemd), and capabilities.

### 11. Updates & Canaries
Application update delivery and safety:
- `UpdateChecker.cs`: Checks GitHub releases for application updates, manages downgrade protections.
- `UpdateBackup.cs`: Creates snapshot backups of application binaries with compensation and rollbacks.
- `RemoteVersionChecker.cs`: Queries remote version manifests.
- `CanaryPolicy.cs`: Governs canary/rolling update eligibility.
- `CanaryTargets.cs`: Evaluation of client eligibility for canary builds.
- `PolicyHttpClient.cs`: Resilient HTTP client with strict timeouts, size limits (4 MiB cap), and credential scrubbing.
- `IHttpClient.cs`: HTTP client abstraction.

### 12. Sub-Feature Integrations
Specialized modules and third-party tools:
- `Services/FreeConfigs/`: Free config aggregation, validation, geo-lookup, and deep verification pipeline.
- `ZapretManager.cs`, `ZapretActions.cs`, `ZapretAutoStrategy.cs`, `ZapretProbeCache.cs`, `ZapretUpdater.cs`: Integration with the Zapret DPI bypass engine.
- `TgProxyManager.cs`, `TgProxyUpdater.cs`, `TgProxyPortConflictException.cs`: Built-in MTProto / SOCKS Telegram proxy daemon management.
- `WgturnUpdater.cs`, `WgturnDownloadException.cs`: WireGuard / Wgturn utility updater.
- `SlipstreamManager.cs`: Manages slipstream DNS/traffic acceleration.
- `Services/EmergencyChannel/`: Fail-safe backup connectivity fallback.
- `AndroidDpiBypassInjector.cs`, `AndroidStorageSane.cs`, `BuiltInAndroidProfiles.cs`: Android-specific services.

## Critical Invariants & Execution Rules

1. **Process Handle Disposal**: Always wrap calls that return `Process[]` or native handles in `try...finally` blocks or use `ProcessQuery` wrappers (`AnyAlive`, `CountAlive`). Never leave process handles open.
2. **Credential Scrubbing in Logs**: Raw sing-box stderr or subscription bodies may contain VLESS tokens or private keys. All logging must route through `DiagnosticsRedactor` or `PolicyHttpClient` scrubbing before writing to disk.
3. **Fail-Closed Policy**: If network validation, TUN allocation, or DNS detour generation fails, the pipeline must fail closed. Never fall back to unencrypted direct routing without explicit user configuration.
4. **Case Sensitivity**: Retain exact process image casing (`Discord.exe`) for sing-box compatibility; do not call `ToLowerInvariant()` on process names.
5. **Lock Ordering**: To prevent deadlocks between UI and engine, never acquire `TunOwnershipLock` while holding the engine's internal synchronization locks.
