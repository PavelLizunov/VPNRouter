# VPNRouter.Core.Services Sub-Zone Instructions

Applies to `VPNRouter.Core/Services/` and descendants. Follow the
[Core instructions](../AGENTS.md) and [canonical contract](../../docs/agent-contract.md).
This is a navigation map, not an exhaustive service inventory or a guarantee
that every failure path has the same behavior.

## Lifecycle and process ownership

- [VpnEngine.cs](VpnEngine.cs): public start/apply/stop entry points,
  `_lifecycleGate`, session cancellation and failover restart callbacks.
- [StartupPipeline.cs](StartupPipeline.cs): startup phases, configuration
  checks, firewall setup and warmup probing. Read together with `VpnEngine`.
- `SingBoxManager.cs` and `SingBoxManager.*.cs`: process launch, stop, crash
  detection, health and hot reload.
- `TunOwnershipLock.cs`, `ProcessOwnership.cs`, `UnixOwnedProcessSignal.cs`
  and `OrphanCleanup.cs`: ownership checks and cleanup paths. Inspect the
  actual acquisition/release paths before changing synchronization; there is
  no blanket lock-order rule in this map.
- `ResilientStarter.cs`, `AutoFailoverEngine.cs` and `SafeMode.cs`: startup
  recovery and failover decisions.

## Health and power events

[HealthMonitor.cs](HealthMonitor.cs) owns periodic probes and recovery policy.
It wires [PowerEventListener.cs](PowerEventListener.cs) to `ProbeNow`.
On Windows, resume, session unlock and console/remote connect invoke that
callback; `ProbeNow` checks stopping/disposed state and calls `OnHealthTick`.
This is a request to probe, not an orderly suspend teardown or a guaranteed
reconnect. Non-Windows listener startup is inactive.

`ConnectionHealthClassifier.cs`, `ConnectionHealthState.cs` and
`ClashLogStream.cs` provide connection-health observations.

## Configuration, routing and subscriptions

- `ConfigGenerator.cs` and `ConfigGenerator.*.cs`: routing, DNS and outbound
  generation. `CustomConfigInjector.cs` handles custom JSON; `LeakProtection.cs`
  owns validation. Check mode-specific branches rather than inferring a
  universal fail-closed guarantee from a validator's name.
- `VlessServersResolver.cs`, `SubscriptionResolver.cs`, `SubscriptionFetcher.cs`,
  `VlessUriParser.cs` and `ServerUriParser.cs`: subscription and URI inputs.
- [SuffixMatch.cs](SuffixMatch.cs): `LongestSuffixIndex` scans candidates
  linearly and compares `EndsWith(..., StringComparison.Ordinal)`, retaining
  the longest matching name. It maps urltest member tags back to subscription
  rows; it is not a domain-routing trie.
- `ProcessScanner.cs`, `EtwProcessMonitor.cs` and `RoutingAppListEditor.cs`:
  process discovery and application routing lists.

## Platform and feature entry points

- [Platform instructions](../Platform/AGENTS.md): firewall mode boundaries and
  DNS adapter navigation. `FirewallManager.cs` is the Windows implementation;
  the split-driver seam is separate in `SplitTunnelDriverManager.cs`.
- `SettingsLoader.cs`, `SettingsMigrator.cs` and `ProfileManager.cs`: persisted
  settings, migrations and profile loading.
- `FreeConfigs/`: aggregation, cache and verification pipeline.
- `Diagnostics/`: export and redaction; `UpdateSources/`: update-source adapters.
- `ZapretManager.cs`, `TgProxyManager.cs` and `SlipstreamManager.cs`: helper
  process integrations; consult their updater/action files for each lifecycle.

## Editing rules

- Preserve process-name casing; deduplicate with `StringComparer.OrdinalIgnoreCase`
  as required by the canonical contract.
- Dispose owned process handles; use `ProcessQuery` for its supported queries.
- Do not log raw subscription bodies, credentials or unsanitized process output.
  Inspect the relevant redaction path before adding diagnostics.
- Verify failure and recovery branches in the owning implementation and tests.
  Do not describe an unarmed firewall or best-effort DNS hardening as fail-closed.
