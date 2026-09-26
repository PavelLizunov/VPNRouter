# Omarchy Features and Backend Code Review Report

Branch: `dsh/omarchy-plugin-2026-09-17` (base: `517bf7e2`, brief HEAD: `b8bde92c`)  
Scope: `VPNRouter.Headless/RouterBackend.cs`, `VPNRouter.Headless/Features/`, `VPNRouter.Headless/Storage/`, and `VPNRouter.Headless.Tests/FeatureChecks.cs`  
Specification: `plans/omarchy-protocol-v1.md`, `plans/phase-omarchy-plugin-2026-09-17.md`, `docs/agent-contract.md`

---

## Executive Summary

An independent code review was conducted for the `VPNRouter.Headless` backend and feature modules implementing the Omarchy protocol v1. Eight concrete prioritized findings were identified spanning verification state preservation, protocol specification compliance, network isolation on list endpoints, subscription configuration mode integration, setting persistence, candidate rollback safety, and ID/casing edge cases.

---

## Prioritized Findings

### Finding 1 (P1 - High): FreeConfig Verification State Demoted on Latency Probe (`free.test` overwrites `Verified` status with `Ok`, breaking subsequent `free.apply`)
- **Source Anchor**: `VPNRouter.Headless/Features/FreeConfigFeature.cs:103-104` (in `TestAsync`), `VPNRouter.Headless/Features/FreeConfigFeature.cs:169-171` (in `Apply`), and `VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs:112-116, 176-179`.
- **Counterevidence / Core Parity**:
  `FreeConfigTester.TestOneAsync` blindly assigns `cfg.Status = FreeConfigStatus.Ok` upon a successful TCP/TLS probe. When a user or frontend calls `free.test { id }` on an entry that previously passed deep verification (`free.verify`), `TestAsync` runs `TestOneAsync` and saves the degraded `Status = Ok` to the cache. When the user subsequently attempts `free.apply { id }`, the call fails closed with `RouterException("invalid_argument", "Free config must be verified before apply")`.
  In `VPNRouter.Core`, `FreeConfigTester.TcpPingOnlyAsync` was specifically designed for testing verified entries while preserving the `Verified` status: *"Don't mutate Status/LastError on failure — caller (Recheck flow) needs the original Verified status preserved for retention."*
- **Impact**:
  A user performing a latency recheck on a verified free configuration permanently loses the ability to apply it without re-running full deep verification.
- **Fix**:
  In `FreeConfigFeature.TestAsync`, check if `entry.Status == FreeConfigStatus.Verified`. If already verified, use `_tester.TcpPingOnlyAsync(entry, ct)` or preserve `entry.Status` instead of overwriting it with `FreeConfigStatus.Ok`.

---

### Finding 2 (P1 - High): `profiles.list` Initiates Remote Network Fetch on Cold Query
- **Source Anchor**: `VPNRouter.Headless/Features/ProfileFeature.cs:33-35, 121-127` and `VPNRouter.Core/Services/ProfileManager.cs:333-345`.
- **Counterevidence / Core Parity**:
  `plans/omarchy-protocol-v1.md:132` explicitly establishes: *"Backend must not run engine/network in constructor or snapshot/list calls."*
  In `ProfileFeature.ListAsync`:
  ```csharp
  var manager = GetOrCreateProfileManager();
  var collection = manager.Loaded ?? await manager.LoadAsync(ct);
  ```
  On cold startup, `manager.Loaded` is always `null`. `manager.LoadAsync(ct)` iterates over sources, including `GitHubProfileSource`. `GitHubProfileSource.LoadAsync` issues a remote HTTP GET (`await _http.SendAsync(...)`) with a 10-second timeout.
  Furthermore, `profiles.list` is dispatched under `RouterBackend._busyState`, blocking the entire backend while waiting on remote GitHub responses.
- **Impact**:
  Violates the protocol non-network invariant for list queries, causes GUI freezes on first profile list opening, and locks all other backend commands during network degradation or offline launches.
- **Fix**:
  `ProfileFeature.ListAsync` must only query local/cached profiles (e.g. `LocalProfileSource`, `BuiltInProfileSource`, or offline cached `ProfileCollection`). Remote network fetching must be reserved exclusively for the explicit `profiles.refresh {}` method.

---

### Finding 3 (P1 - High): Subscription Mode Inaccessible; `servers.list` and `servers.select` Ignore Subscription Servers
- **Source Anchor**: `VPNRouter.Headless/Features/ServerFeature.cs:51-53, 137-145`, `VPNRouter.Headless/RouterBackend.cs:119-122`, and `VPNRouter.Core/Services/VlessServersResolver.cs:108-156`.
- **Counterevidence / Core Parity**:
  In Core architecture (`VPNRouter.Core/AGENTS.md` and `VlessServersResolver.cs`), in `ConfigMode == "subscribe"`, subscription endpoints are stored in `app.subscriptions[i].servers` while `vless.servers` remains empty or unmanaged in settings storage. `VlessServersResolver.Resolve` must run to populate `Vless.Servers`.
  In `ServerFeature.List`:
  ```csharp
  var settings = _storage.GetSettings();
  var servers = settings.Vless?.Servers ?? new List<VlessServerEntry>();
  ```
  `ServerFeature.List` reads only `settings.Vless.Servers` without calling `VlessServersResolver.Resolve`. Consequently, when in subscription mode or when subscriptions exist, `servers.list` never surfaces subscription servers. Furthermore, `servers.select` unconditionally sets `settings.App.ConfigMode = "generated"`, and neither `ServerFeature` nor `SubscriptionFeature` provides an endpoint to select a subscription server or enter `subscribe` mode.
- **Impact**:
  The Linux plugin cannot display or route through subscription-provided servers. Subscription mode is completely disconnected from server listing and selection.
- **Fix**:
  Update `ServerFeature.List` to resolve servers using `VlessServersResolver.Resolve(settings)` or provide subscription server enumeration and a dedicated selection mechanism that sets `App.ActiveSubscriptionServer` and `App.ConfigMode = "subscribe"`.

---

### Finding 4 (P2 - Medium): Subscription Refresh and Removal Drift Leaves Stale Active Server Selectors
- **Source Anchor**: `VPNRouter.Headless/Features/SubscriptionFeature.cs:140-146, 216-226` and `VPNRouter.Core/Services/VlessServersResolver.cs:140-156`.
- **Counterevidence / Core Parity**:
  In `SubscriptionFeature.RefreshAsync`, when servers are updated or fetched for the first time, the active subscription server selector (`App.ActiveSubscriptionServer` and `Vless.ActiveServer`) is never reconciled against the updated server list. If the upstream provider renamed or dropped servers, the active server reference becomes stale.
  In `SubscriptionFeature.Remove`, if the active server belonged to the removed subscription, neither `App.ActiveSubscriptionServer` nor `Vless.ActiveServer` is cleared, and `ConfigMode` is not rolled back to `"generated"` if all subscriptions are removed.
  In Core (`VlessServersResolver.cs:148-155`), active selector fallback is mandatory: *"P1.8 selector-drift fix: this fallback is subscription-scoped... App.ActiveSubscriptionServer... must move WITH Vless.ActiveServer."*
- **Impact**:
  Stale active server selectors survive subscription removal and provider server rotation, causing subsequent connection attempts to fail or fall back to an arbitrary server.
- **Fix**:
  In `SubscriptionFeature.RefreshAsync` and `SubscriptionFeature.Remove`, reconcile active selectors against the remaining enabled subscription servers and fall back to `"generated"` mode if no subscription servers remain.

---

### Finding 5 (P2 - Medium): `SettingsFeature` Silently Discards `dnsMode` and Hardcodes `"vpn_only"` in `Get()`
- **Source Anchor**: `VPNRouter.Headless/Features/SettingsFeature.cs:43-48, 136-143` and `plans/omarchy-protocol-v1.md:95-98`.
- **Counterevidence / Core Parity**:
  `plans/omarchy-protocol-v1.md` lines 95-98 specify `dnsMode` as a mutable property in `settings.get` and `settings.set`.
  In `SettingsFeature.Set`:
  ```csharp
  if (valuesProp.TryGetProperty("dnsMode", out var dmProp))
  {
      ...
      var mode = dmProp.GetString()!.Trim().ToLowerInvariant();
      if (mode != "vpn_only" && mode != "smart" && mode != "direct")
          throw new RouterException("invalid_argument", ...);
  }
  ```
  `dnsMode` is validated and then completely ignored; it is never persisted to `AppSettings` or `ActiveProfile`. In `SettingsFeature.Get()`, `dnsMode` is hardcoded to `"vpn_only"`.
- **Impact**:
  Clients calling `settings.set` with `dnsMode` receive success, but the value is dropped, preventing users from selecting `smart` or `direct` DNS routing.
- **Fix**:
  Persist `dnsMode` to the active profile or settings storage, and read the active profile's configured `DnsMode` in `SettingsFeature.Get()`.

---

### Finding 6 (P2 - Medium): Flawed Candidate Rollback and Non-Atomic Custom Config Deletion in `ExecuteMutationAsync`
- **Source Anchor**: `VPNRouter.Headless/RouterBackend.cs:274-302`, `VPNRouter.Headless/Features/CustomConfigFeature.cs:144-159`, and `VPNRouter.Headless/Storage/ConfigStorage.cs:102-174`.
- **Counterevidence / Core Parity**:
  `RouterBackend.ExecuteMutationAsync` saves mutated settings to disk before verifying and applying them to the live session via `_session.ApplyAsync`. If `ApplyAsync` throws, it calls:
  `_storage.SaveSettings(previousSettings, _storage.CurrentRevision);`
  This causes:
  1. The disk revision advances twice (Rev A -> Rev B -> Rev C), invalidating the client's cached revision and causing subsequent mutations to fail with `conflict`.
  2. In `CustomConfigFeature.Remove`, `_customStorage.DeleteCustomConfig(match.Name)` deletes the file from disk immediately. If `ApplyAsync` fails, `previousSettings` is restored to `config.yaml`, but the referenced custom sing-box JSON file on disk is permanently destroyed.
- **Impact**:
  Failed live-tunnel applies cause revision desynchronization and unrecoverable deletion of custom configuration files.
- **Fix**:
  Validate and stage mutations in memory before committing changes to disk. For deletions, defer unlinking files until after `ApplyAsync` succeeds.

---

### Finding 7 (P2 - Medium): Dead `_inFlightRequests` Map in `RouterBackend` Causes In-Process `cancel` to Always Return False
- **Source Anchor**: `VPNRouter.Headless/RouterBackend.cs:43, 78-79, 307-329` and `VPNRouter.Headless/Protocol/RouterBackendHandler.cs:22-23`.
- **Counterevidence / Core Parity**:
  `RouterBackend` declares `_inFlightRequests`, but entries are never added (`TryAdd` is never called). `RouterBackend.ExecuteAsync` does not receive the request `id`. When `HandleCancel` is invoked, `_inFlightRequests.TryRemove(id, out _)` is always false. Direct programmatic consumers of `RouterBackend.ExecuteAsync("cancel", ...)` cannot cancel active requests.
- **Impact**:
  Direct invocations of the backend `cancel` endpoint fail to cancel active tasks.
- **Fix**:
  Either pass the request `id` into `ExecuteAsync` and register cancellation tokens in `_inFlightRequests`, or document that cancellation is managed exclusively by `ProtocolDispatcher` and remove the dead dictionary in `RouterBackend`.

---

### Finding 8 (P3 - Low): Selecting Server with Empty Name Causes Silent Active Server Loss
- **Source Anchor**: `VPNRouter.Headless/Features/ServerFeature.cs:66-67, 113-116, 143-144` and `VPNRouter.Core/Services/VlessServersResolver.cs:102-105`.
- **Counterevidence / Core Parity**:
  When a URI without a `#Name` fragment is imported, `ServerUriParser` leaves `Name` empty. In `ServerFeature.Select`:
  `settings.Vless!.ActiveServer = server.Name;`
  This assigns an empty string to `Vless.ActiveServer`. In `VlessServersResolver.cs:103-104`, matching requires `!string.IsNullOrEmpty(s.Name) && s.Name.Equals(settings.Vless.ActiveServer, ...)`. Consequently, the server is never resolved as active, causing fallback or start failure.
- **Impact**:
  Selecting unnamed servers breaks active server routing resolution.
- **Fix**:
  Ensure `ServerFeature.Import` assigns a fallback synthetic name (such as `$"{server.Server}:{server.Port}"`) if `server.Name` is empty.

---

## Test Coverage Analysis & Untested Boundaries

1. **Absence of Real Parity Tests (Fake Sessions)**:
   `FeatureChecks.cs` relies exclusively on `FakeRouterSession` (a 72-line stub). Real lifecycle interactions with `VpnEngine`, `SingBoxManager`, and `PlatformCapabilityVerifier` are never exercised within feature checks.
2. **Completely Untested Feature Endpoints**:
   The following approved protocol v1 endpoints have zero test coverage in `FeatureChecks.cs`:
   - `servers.test` and `servers.verify`
   - `subscriptions.refresh`
   - `free.refresh`, `free.test`, and `free.verify`
   - `profiles.list`, `profiles.select`, and `profiles.refresh`
   - `routing.set`
   - `rules.import`
   - Valid `custom.select`
3. **Mocked Cache Data in Free Config Tests**:
   `CheckFreeConfigsVerifiedOnlyApplyAsync` does not test the real `FreeConfigFeature` pipeline; it manually instantiates an unverified object and writes it directly to disk, bypassing `free.refresh`, `free.test`, and `free.verify`.
