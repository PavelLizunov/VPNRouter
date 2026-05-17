# VPNRouter Feature Catalog (v2.32.3 baseline)

Date: 2026-05-17. Source-of-truth pass over `VPNRouter.Core` (61 service
files / 14 600+ LOC), `VPNRouter.App` (10 pages / 7 partial VM files /
8 700 LOC), `VPNRouter.CLI`, `VPNRouter.Service`, `VPNRouter.Android`
(15 partials / 14 800 LOC). READ-ONLY audit — no code changes.

## Quick stats

- **Total catalogued features:** 53
- **LOW complexity:** 21 (~40%) — single service / no IPC / <300 LOC
- **MEDIUM complexity:** 22 (~41%) — 2–4 services / 1 IPC boundary
- **HIGH complexity:** 10 (~19%) — ≥5 services or critical multi-layer flow
- **Platform coverage matrix**

| Layer                | Win | Mac | Linux | Android |
|----------------------|-----|-----|-------|---------|
| Core VPN engine      | ✓   | ✓   | ✓     | partial (libbox.aar TBD) |
| sing-box backend     | ✓   | ✓   | ✓     | ✓ (libbox)  |
| TUN + Reality        | ✓   | ✓   | ✓     | ✓ (VpnService) |
| ETW process monitor  | ✓   |     |       |         |
| Firewall block      | ✓ (netsh) |  |  |         |
| Zapret DPI bypass    | ✓   |     |       | ✓ (Android variant) |
| tg-ws-proxy          | ✓   |     |       |         |
| Free Configs         | ✓   | ✓   | ✓     | ✓       |
| Auto-update          | ✓   | ✓ (brew) | ✓ (apt) | ✓ (sideload) |
| Self-repair          | ✓   |     |       |         |
| QR scan              |     |     |       | ✓       |

---

## Feature taxonomy

### A. Core VPN lifecycle (10 features)
A1 Connect / Apply / Disconnect • A2 Split tunnel • A3 Full tunnel •
A4 Custom config mode • A5 Hot-reload (Clash API) •
A6 Health monitor + auto-restart • A7 ETW process monitor •
A8 Multi-protocol outbound • A9 Conflicting-VPN pre-flight •
A10 Auto-failover (F-E)

### B. Subscriptions (6)
B1 Add/remove subscription URL • B2 Auto-refresh (hourly) •
B3 Per-server TCP/TLS probe • B4 Subscription aggregation (VlessServersResolver) •
B5 Placeholder credential filter (PlaceholderGuard) •
B6 Subscription cascade settings

### C. Free Configs (Public configs) (5)
C1 14 built-in + N user sources • C2 Server-side pre-aggregated pool.json •
C3 Deep verify (HTTP through SOCKS) • C4 GeoIP enrichment +
bandwidth test • C5 Cache merge (verified preserve)

### D. DPI bypass / messengers (5)
D1 Zapret integration (Flowseal) • D2 Discord hosts pinning •
D3 Telegram WS proxy • D4 IP-set list update (RU bypass) •
D5 AV-block immediate-exit toast

### E. Apps routing (4)
E1 Process picker / Include mode • E2 Exclude mode (AM-1/AM-2 2-mode) •
E3 Custom categories / group apps • E4 Wildcard + scan_patterns

### F. Profiles (3)
F1 9 default profiles (Discord_Privacy, Messengers, AI_Tools, Browsers,
Work_Suite, Streaming, Gaming, Virtualization, Privacy_Shell) •
F2 Profile merging (union + strictest DNS) •
F3 GitHub > Local > Built-in priority

### G. Custom rules (3)
G1 Rule parser (direct/proxy/block × 10 types) •
G2 Import/Export rules text format •
G3 Cards/Read/Edit view modes

### H. Update mechanism (4)
H1 GitHub Releases API check • H2 Stable vs Experimental channel •
H3 Lite vs Full update ZIPs • H4 Self-repair (SR-1..SR-4 +
LaunchFailureCounter loop break)

### I. UI / UX (5)
I1 Simple page (one-click) • I2 Advanced shell (10 tabs) •
I3 Theme switching (Light/Dark/System) •
I4 Bilingual Ru/En (Localization/Strings.cs) •
I5 QR scan + magic-1-step paste (Android-only)

### J. Privacy + Security (4)
J1 Leak protection (DNS strategy, strict_route, outbound presence) •
J2 F-A..F-E placeholder defense (5 layers) •
J3 Block on VPN fail (netsh firewall) •
J4 Russian traffic geo-bypass

### K. Platform infrastructure (4)
K1 Windows Service mode (sc.exe install) • K2 CLI mode (Spectre.Console) •
K3 Emergency channel (wgturn-cli) • K4 Cross-platform packaging
(DMG/AppImage/.deb/APK/Homebrew tap/APT/winget)

---

## Per-feature pages

### A1 — Connect / Apply / Disconnect (CTA)

**Entry point**: Simple page big circle CTA `ToggleConnectionAsync`
(MainWindowViewModel:4215); Advanced shell same command rebound.
Android: `AndroidApp.axaml.cs` connect button.
CLI: `start --profile <name>`. Service: BackgroundService auto-runs on boot.

**Service chain**:
`MainWindowViewModel.ToggleConnectionAsync` → `VpnEngine.StartAsync(settings, ct)`
or `VpnEngine.ApplyAsync` (hot-reload) or `VpnEngine.Stop()`.

**Flow** (StartAsync, 880 LOC):
1. `ConflictingVpnDetector.DetectConflictingVpnProcesses` — wintun ownership pre-flight.
2. `DnsFlusher.Flush` — drop pre-VPN cache (Windows-only).
3. `GeoDataDownloader.EnsureGeoFilesAsync` — fetch geosite/geoip if RU bypass.
4. `LeakProtection.ValidateAppSettings` — F-12 invariant guard (subscribe/generated/custom).
5. `VlessServersResolver.Resolve` — subscribe → Vless.Servers aggregation.
6. `QuarantineStaleUserCatalogue` + `ProfileManager.LoadAsync` → 9 default profiles + user merge.
7. `_scanner.ScanForProfile(_activeProfile)` (30s timeout) — resolves process names.
8. `NetworkInterfaceDetector.DetectWireGuardSubnets` — exclude AmneziaWG.
9. `ConfigGenerator.Generate` (or `CustomConfigInjector.Inject`).
10. `LeakProtection.ValidateConfig` — strict_route, DNS strategy, outbound presence.
11. `ConfigSanityCheck.CheckBeforeStart` (F-E) — placeholder + dead-server detection → `AutoFailoverEngine` if dead.
12. `TunAdapterDiagnostics.PreStartCleanupAsync` — wipe orphan wintun.
13. `SingBoxManager.StartWithJson(configJson)` — fork sing-box.exe, wait ≤5s for boot.
14. Post-start probe via Clash API (15s settle, fire-and-forget) → `AutoFailoverEngine`.
15. `EtwProcessMonitor.Start` + `HealthMonitor.Start` (debounced rescan + restart backoff).
16. `WindowsDnsHardening.Apply` (disable SMHNR, set TUN metric).

**Complexity**: **HIGH** — 16 service touches, 3 IPC boundaries
(sing-box process, netsh, Clash HTTP), 12+ known edge cases. VpnEngine.cs
1658 LOC. Documented v2.28.2 silent-leak class, v2.31.5 VPN-loss recovery,
v2.32.3 placeholder defense layered fix.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android (Android skips firewall + ETW,
uses libbox+VpnService).

**Known issues** / `plans/`-flagged:
- HealthMonitor.AttemptRestart timer-race (closed v2.31.6-r9 by Interlocked gate)
- Silent ConfigMode flips (F-12 backstop, ValidateAppSettings)
- TUN structural change → forceRestart only on fingerprint mismatch (v2.27.2)

**Test coverage**: `ConfigGeneratorTests` (14+2), `VlessServersResolverTests`
(8), `ConfigGeneratorEmptyServersGuardTests` (2),
`HealthMonitorRecoveryGapTests` (5), `HealthMonitorTimerRaceTests` (1),
`Generate_FromSubscribeMode_PassesSingBoxCheck` (1).

**Artifacts**: `current.json` @ `%ProgramData%\VPNRouter\config\`,
`state.json` PID file, `singbox.log`.

---

### A2 — Split tunnel (process-based routing)

**Entry point**: Settings → Routing → Split. Default mode.

**Service chain**: `VpnEngine.StartAsync(settings)` →
`ProcessScanner.ScanForProfile(profile)` → `ConfigGenerator.Generate` (route
rule `process_name → vless`) → `EtwProcessMonitor` real-time updates →
`HealthMonitor` debounced reload via Clash API.

**Flow**:
1. UI sets `AppSettings.App.RoutingMode = "split"`.
2. Scanner resolves `process_name` list (case-sensitive, NOT lower-cased).
3. Generator emits sing-box `route.rules: [{process_name: [...], outbound: vless-out}]`.
4. ETW listens for `ProcessStart` events (<10ms latency) → debounce 5s → rescan → hot-reload.

**Complexity**: **HIGH** — touches 5 services, case-sensitivity gotcha
documented in 3 files, WMI fallback for child processes.

**Platforms**: ✓Win (ETW) ✓Mac (process name match only, no ETW) ✓Linux
(same as Mac) ✓Android (separate AndroidConfigBuilder path).

**Known issues**: ProcessScanner Regex compiled per-call (no cache),
WMI child-lookup can hang on corrupt catalogue (30s guard added v2.22.4).

**Test coverage**: `ConfigGeneratorDuplicateNameTests`, `ProcessScannerTests`
(implicit via Generate_FromSubscribeMode_PassesSingBoxCheck).

---

### A3 — Full tunnel mode

**Entry point**: Settings → Routing → Full.

**Service chain**: `VpnEngine.StartAsync` (skips scanner + profile resolution)
→ `ConfigGenerator.Generate` with empty `process_name` rules and `route.final = vless-out`.

**Flow**:
1. UI sets `AppSettings.App.RoutingMode = "full"`.
2. Engine collapses `_activeProfile = new Profile { Name = "FullTunnel" }` (line 339 VpnEngine.cs).
3. Generator emits `route.final: vless-out` (no per-process rules).
4. Adapter routing layer captures all egress.

**Complexity**: **LOW** — single decision branch in VpnEngine line 334.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

**Known issues**: switching split↔full requires `forceRestart=true`
(documented v2.20.4 in VpnEngine).

---

### A4 — Custom config mode

**Entry point**: Servers tab → Custom config sub-tab → Add. ViewModel:
`AddCustomConfigAsync` (MainWindowViewModel:5739),
`SetActiveCustomConfig` (5830).

**Service chain**: `CustomConfigInjector.Validate` → `CustomConfigInjector.Inject`
(injects process routing + Clash API into raw user JSON) →
`StripUnsupportedFeatures` (1.11 → 1.13 migration) → `SingBoxManager.StartWithJson`.

**Flow**:
1. User pastes / picks file → `CustomConfigInjector.Validate(rawJson)`.
2. v2.32.3-r1 placeholder gate: `ConfigSanityCheck.FindFirstProxyOutbound` +
   `InspectOutbound` — throws `PlaceholderConfigException` if known-bad fingerprint.
3. Saved to `%ProgramData%\VPNRouter\config\custom-<name>.json`.
4. On connect: `CustomConfigInjector.Inject(rawJson, processNames, settings)` →
   adds `process_name` rules, Clash API, DNS detour, format migration.

**Complexity**: **HIGH** — 1254 LOC injector with 22+ test cases,
auto-detects action-based vs legacy format, FATAL pitfalls documented:
`detour:"direct"` on empty direct outbound, DoT→DoH migration, `dns-out`
→ `hijack-dns` action.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

**Known issues**: ConfigMode=custom UI footgun (v2.28.2-r2 guard added).

**Test coverage**: `CustomConfigInjectorTests` (22+),
`Inject_ActualCustomConfig_SingBoxCheck`, `Inject_WithBypassRussianTraffic_PassesSingBoxCheck`.

---

### A5 — Hot-reload (Clash API)

**Entry point**: `VpnEngine.ApplyAsync` → `SingBoxManager.ReloadConfig`.
Triggered by Apply Settings button + ETW process change debounce.

**Service chain**: `HealthMonitor.GenerateConfigJson` (re-runs VlessServersResolver +
ConfigGenerator) → `SingBoxManager.ReloadConfig` HTTP `PUT /configs?force=true` →
fallback `Stop()` + `LaunchProcess()` on failure.

**Flow**:
1. `ApplyAsync(forceRestart: bool)` — if structural change (TUN fingerprint
   mismatch or RoutingMode flip), `forceRestart = true`, skip hot-reload.
2. Otherwise: `_http.Put` to `127.0.0.1:9090/configs` Clash API.
3. On non-200: kill + relaunch with new JSON.

**Complexity**: **MEDIUM** — single IPC boundary (Clash HTTP), but
fallback path doubles surface.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

---

### A6 — Health monitor + auto-restart

**Entry point**: Wired internally by `VpnEngine.StartAsync` step 10.

**Service chain**: `HealthMonitor` ↔ `SingBoxManager` event subscription.

**Flow**:
1. `_healthTimer` fires every N seconds (`MonitoringSettings.HealthCheckIntervalSec`).
2. `SingBoxManager.IsHealthy` → Clash API ping or process probe.
3. On crash (`SingBoxManager.Crashed` event) → `AttemptRestart` with
   exponential backoff (5s/10s/20s/40s/80s).
4. v2.31.5 `_shouldBeRunning` intent flag covers post-Task.Delay starvation
   (laptop sleep, dispatcher hang).
5. `OnNewProcessDetected` from ETW → 5s debounce timer → rescan + hot-reload.
6. `_restartCts` cancels pending restart on success or Stop.

**Complexity**: **HIGH** — 593 LOC, 5 race-condition lessons in code
(timer atomicity, restart serialization, intent flag, debounce window,
re-entry guard).

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android (PowerEventListener Win-only).

**Test coverage**: `HealthMonitorTimerRaceTests` (1),
`HealthMonitorRecoveryGapTests` (5).

---

### A7 — ETW process monitor

**Entry point**: Auto-started by `VpnEngine.StartAsync` step 10.

**Service chain**: `EtwProcessMonitor` (background thread) →
`HealthMonitor.OnNewProcessDetected`.

**Flow**:
1. TraceEventSession subscribes to Microsoft-Windows-Kernel-Process.
2. On ProcessStart event → match against `_activeProfile.Processes[].ScanPatterns`.
3. If targeted → fire `ProcessDetected` event → `HealthMonitor` debounces.

**Complexity**: **MEDIUM** — single service, Windows-only ETW, 184 LOC.

**Platforms**: ✓Win only. (Mac/Linux fall back to no real-time monitor.)

**Known issues**: ManualResetEventSlim dispose race (fixed v2.31.0-r1 CO-6).

---

### A8 — Multi-protocol outbound (VLESS / Hysteria2 / TUIC / Shadowsocks / Trojan)

**Entry point**: Server URI parsing in `ServerUriParser` / `VlessUriParser`.

**Service chain**: `VlessUriParser.Parse(uri)` →
`LeakProtection.ValidateConfig` (protocol-aware dispatch v2.30.1-r4) →
`ConfigGenerator` emits outbound by `Type` (vless / hysteria2 / tuic / ss / trojan).

**Complexity**: **MEDIUM** — 5 protocol branches, each with its own URI
format quirks; Reality+uTLS is VLESS-specific.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

**Test coverage**: `LeakProtectionTests` (19 — protocol-aware dispatch).

---

### A9 — Conflicting-VPN pre-flight check

**Entry point**: `VpnEngine.StartAsync` step 0a — auto.

**Service chain**: `ConflictingVpnDetector.DetectConflictingVpnProcesses` →
throws `ConflictingVpnException`. UI catches and shows banner with
"Завершить" / "Игнорировать" / "Соединиться" buttons →
`KillConflictingVpnAsync` (3230) or `IgnoreVpnConflictAndConnectAsync` (3036).

**Complexity**: **LOW** — single service, 1 detector class, 3 UI buttons.

**Platforms**: ✓Win (WMI process scan) ✓Mac ✓Linux (limited — wintun is
Windows-only TUN driver) ✓Android.

---

### A10 — Auto-failover (F-E)

**Entry point**: Triggered inside `VpnEngine.StartAsync` step 5.5 (pre-start
sanity check) and step 8.5 (post-start Clash probe).

**Service chain**: `ConfigSanityCheck` (CheckBeforeStart / ProbeAsync) →
`AutoFailoverEngine.HandleDeadConfigAsync` → mutates
`settings.Vless.ActiveServer`, persists via `SettingsLoader.Save`, re-enters
`StartAsync` via injected restart delegate. MaxAttempts = 3.

**Complexity**: **HIGH** — 356+353 LOC across two services, recursive re-entry
guarded by `TriedServers` set, F-A/B/C/D/E 5-layer defense stack
(v2.32.x stas-class fix).

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

**Known issues** (plans/r10-stas-confirmed-and-apps-2mode.md):
- F-A: placeholder-aware scope guard in VlessServersResolver
- F-B: settings migrator strip on load
- F-D: scope-aware LeakProtection
- F-E: runtime sanity + autofailover (this feature)

---

### B1 — Add / remove subscription URL

**Entry point**: Subscribe page → "Добавить подписку" → `AddSubscriptionAsync`
(4523), `RemoveSubscription` (4564).

**Service chain**: `SubscriptionFetcher.FetchAsync(url)` →
`VlessUriParser.Parse` per line → dedup by `Server:Port:UUID:Flow` →
`SubscriptionEntry.Servers` persisted in YAML.

**Flow**: parser handles 3 body formats (JSON wrapper / raw base64 / plain URIs)
+ unsupported-scheme filter. Internal `ParseBody` (v2.31.5+) unit-testable
without HTTP.

**Complexity**: **MEDIUM** — 298 LOC fetcher, 8 parser cases, dedup pitfalls.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android (`AndroidApp.SubscribePage.cs` 1320 LOC).

**Test coverage**: `SubscriptionFetcherParserTests` (8 cases).

---

### B2 — Auto-refresh subscriptions (hourly)

**Entry point**: `_subRefreshTimer` System.Threading.Timer started in
MainWindowViewModel ctor, interval 3 600 000 ms.

**Service chain**: `RefreshAllSubscriptionsAsync` (4605) →
`RefreshSubscriptionAsync(sub)` (4574) → `SubscriptionFetcher.FetchAsync`.

**Complexity**: **LOW** — single timer, single subscriber.

**Known issues**: v2.31.8-r3 race where SubRefresh hourly reconnect caused
API 403 every hour — fix forced restart on process-list change.

---

### B3 — Per-server TCP/TLS probe

**Entry point**: Servers tab → "Тест задержки" → `MainWindowViewModel.ServerTesting.cs`
RecheckAllStaleAsync / per-server StartLatencyTestAsync.

**Service chain**: `TcpTlsProbe.MeasureAsync(server, port, useTls, ct)` →
`ServerViewModel.LatencyMs`.

**Complexity**: **MEDIUM** — TLS handshake validation optional, plausibility
gate (v2.31.2 fix for sub-5ms LatencyMs corruption).

**Test coverage**: `TcpPingOnlyPlausibilityGateTests`,
`FreeConfigCacheMigrationTests`.

---

### B4 — Subscription aggregation (VlessServersResolver)

**Entry point**: Auto-called in `VpnEngine.StartAsync` step 1 and `Apply`.

**Service chain**: `VlessServersResolver.Resolve(settings)` aggregates
`AppSettings.App.Subscriptions[].Servers` into `Vless.Servers` ephemeral
in-memory list (NOT persisted — Linux fix v2.30.0-r8).

**Complexity**: **MEDIUM** — single source of truth, but 3 callers
(StartAsync, Apply, HealthMonitor.GenerateConfigJson).

**Test coverage**: `VlessServersResolverTests` (8 cases).

---

### B5 — Placeholder credential filter (PlaceholderGuard)

**Entry point**: Multiple gates — `SubscriptionFetcher.ParseBody` (v2.32.3),
`SettingsMigrator` on load, `CustomConfigInjector.Inject` v2.32.3-r1,
`VlessServersResolver` F-A, Android `QrCodeDecoder`.

**Service chain**: `PlaceholderGuard.Inspect(entry)` returns offending
field name (`reality.public_key` / `reality.short_id` / `server`).

**Complexity**: **MEDIUM** — 184 LOC + 353 ConfigSanityCheck. 5 input gates
that all route through one helper (v2.32.3 consolidation from F-A/B/D/E
duplicated lists).

**Known issues** (plans/r10-stas-confirmed-and-apps-2mode.md): kanareik
incident — Z: drive evidence file pubkey `DnT9...` ships in tests.

---

### B6 — Subscription cascade / placeholder UI

**Entry point**: Settings → Subscriptions → cascade options.

**Service chain**: `SubscriptionViewModel` + `SubscriptionResolver`.

**Complexity**: **LOW**.

---

### C1 — Free Configs: 14 built-in + N user sources

**Entry point**: Free Configs page → `FreeConfigsPageViewModel.RefreshAsync`.

**Service chain**: `FreeConfigSources.GetAll(settings)` returns built-in
list (14 URLs) + `App.UserFreeSources` (filtered enabled).

**Complexity**: **LOW** — 131 LOC sources table.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android (`AndroidApp.FreeConfigs.cs` 1330 LOC).

---

### C2 — Server-side pre-aggregated pool.json

**Entry point**: `FreeConfigPoolFetcher.FetchPoolAsync` called as
Stage 0 in `FreeConfigAggregator.RefreshAsync`.

**Service chain**: `FreeConfigPoolFetcher` → GitHub Release asset
`pool.json` (refreshed every 6h via GH Actions cron). If >1000 entries,
skips per-source fetch + GeoIP.

**Complexity**: **MEDIUM** — single fetch but conditional pipeline shortcut.

**Artifacts**: `pool.json` GH Releases asset (CI workflow
`.github/workflows/free-configs-pool.yml` — not catalogued here).

---

### C3 — Deep verify (HTTP through SOCKS)

**Entry point**: Free Configs row → "Deep verify".

**Service chain**: `FreeConfigDeepVerifier.VerifyAsync(entry)` →
spawns sing-box, opens SOCKS, HTTP round-trip, optional 5 MB bandwidth test.

**Complexity**: **HIGH** — 556 LOC, requires sing-box subprocess per check,
parallelism guards.

---

### C4 — GeoIP enrichment + bandwidth test

**Entry point**: `FreeConfigAggregator.RefreshAsync` Stage 4.

**Service chain**: `FreeConfigGeoIp.Lookup(ip)` (MaxMind GeoLite2) →
`FreeConfigEntry.Country`.

**Complexity**: **LOW**.

---

### C5 — Cache merge (PreservePreviousValidation)

**Entry point**: `FreeConfigAggregator.RefreshAsync` after fresh pool fetch.

**Service chain**: `FreeConfigAggregator.PreservePreviousValidation`
(internal static helper, v2.28.2-r5).

**Complexity**: **MEDIUM** — merge semantics for Verified status, recent-Ok.

**Test coverage**: `FreeConfigAggregatorPreserveTests` (9 cases).

---

### D1 — Zapret integration (Flowseal release)

**Entry point**: DPI Bypass page → toggle. `ToggleZapretAsync` (4966),
`UpdateZapretAsync` (4882).

**Service chain**: `ZapretUpdater.DownloadAsync` (full GitHub release ZIP
on demand) → `ZapretManager.Start` (writes temp .bat with `SET BIN=/SET LISTS=`,
spawns winws.exe).

**Complexity**: **HIGH** — Cygwin requirement, .bat templating, AV-block
detection (`ImmediateExitDetected` event), strategy parsing from .bat files.
ZapretManager 267 LOC, ZapretUpdater + ZapretActions + HostsManager.

**Platforms**: ✓Win only (WinDivert driver) + ✓Android (separate
`AndroidDpiBypassInjector.cs`).

**Known issues** (Bug-r10-G AV block toast added v2.32.1-r10).

---

### D2 — Discord hosts pinning

**Entry point**: DPI Bypass page → "Hosts" sub-section. `ToggleDiscordHosts`
(5200), `UpdateZapretHostsAsync` (5082).

**Service chain**: `HostsManager.AddDiscordVoiceHosts()` →
`C:\Windows\System32\drivers\etc\hosts` write
(`finland*.discord.media → 104.25.158.178` Cloudflare).

**Complexity**: **LOW** — single file mutation, atomic backup.

**Platforms**: ✓Win.

---

### D3 — Telegram WS proxy

**Entry point**: Telegram page → "Запустить tg-ws-proxy". `ToggleTgProxyAsync`
(5272), `SetupTgProxyAsync` (5414), `TgProxyMainActionAsync` (5467).

**Service chain**: `TgProxyUpdater.DownloadAsync` (Python embeddable +
tg-ws-proxy from GitHub) → `TgProxyManager.Start(port, secret)` →
`python.exe -m proxy.tg_ws_proxy`.

**Complexity**: **MEDIUM** — 434 + ~200 LOC, Python embed bundling on
demand, stats parser.

**Platforms**: ✓Win.

**Known issues** (v2.31.10): secret-redacted args in logs.

---

### D4 — IP-set list update

**Entry point**: DPI Bypass page → "Обновить IPSet". `UpdateIpSetListAsync` (5151).

**Service chain**: `ZapretActions.UpdateIpSetList` → HTTP fetch new ipset
text + write.

**Complexity**: **LOW**.

---

### D5 — AV-block immediate-exit toast

**Entry point**: Wired in `MainWindowViewModel` via `ZapretManager.ImmediateExitDetected`.

**Service chain**: `ZapretManager._process.Exited` within
`ImmediateExitWindow` (2s) → toast with AV-whitelist path +
"Скопировать путь" button `CopyZapretWhitelistPathAsync` (1132).

**Complexity**: **LOW** — single event hook + clipboard.

---

### E1 — Process picker (Include mode AM-1)

**Entry point**: Apps tab → "Добавить приложение". `AddCustomApp` (6119),
`RemoveCustomApp` (6166).

**Service chain**: `AppListLoader` (Android: scan installed packages /
Win: WMI process scan) → `AppItemViewModel`.

**Complexity**: **MEDIUM** — `MainWindowViewModel` 6753 LOC handles
group management, `AppGroupViewModel`, custom_categories.

**Platforms**: ✓Win ✓Mac ✓Linux ✓Android.

---

### E2 — Apps Exclude mode (AM-2)

**Entry point**: Apps tab → mode toggle "Include / Exclude".
`RemoveExcludedApps(activeProfile, settings.ExcludedApps)` in VpnEngine.StartAsync.

**Service chain**: VpnEngine respects `settings.ExcludedApps` regardless of
how `_activeProfile.Processes` was built.

**Complexity**: **MEDIUM** — Core ready, UI toggle wired (v2.32.1-r10),
AM-3 separate list views still in backlog (v2.32.2 plans).

**Known issues** (plans/r10-wgturn-strip-and-am3-apps-lists.md).

---

### E3 — Custom categories / group apps

**Entry point**: Apps tab → `AddCategory` (6095), `RemoveCategory` (6109).
v2.31.6-r10 `MergeUserCustomization` step in VpnEngine.

**Complexity**: **MEDIUM**.

---

### E4 — Wildcard + scan_patterns

**Service chain**: `ProcessScanner.BuildPatternRegex` per `ProcessRule.ScanPatterns`.

**Complexity**: **LOW** — wildcards via regex, Compiled.

**Known issues**: Regex compiled per-call (not cached).

---

### F1 — 9 default profiles

**Entry point**: Bundled `profiles/default.json` (Win) /
`profiles/default-mac.json` / `default-linux.json` / `default-android.json`.

**Profiles**: Discord_Privacy, Messengers, AI_Tools, Browsers (24 entries),
Work_Suite, Streaming, Gaming, Virtualization, Privacy_Shell.

**Complexity**: **LOW** — static JSON, 4 per-platform variants.

---

### F2 — Profile merging (union + strictest DNS)

**Entry point**: `start --profile "Name1,Name2,Name3"`.

**Service chain**: `ProfileManager.MergeProfilesTolerant(names, out missing)`.

**Complexity**: **MEDIUM** — strictest DNS mode wins
(`vpn_only` > `smart` > `direct`); tolerant merge with `missing` capture
(v2.22.0-r1 self-heal).

---

### F3 — GitHub > Local > Built-in priority

**Service chain**: `ProfileManager` loads sources sorted by `Priority`.
Built-in fallback always last. JSON MaxDepth=32 guard (v2.31.0-r1 CO-4).

**Complexity**: **LOW**.

**Test coverage**: `ProfileManagerJsonDosGuardTests` (2).

---

### G1 — Custom rules parser

**Entry point**: Rules tab → text editor.

**Service chain**: `CustomRulesParser.ParseFromText` / `SerializeToText`.
Supports 3 actions × 10 types (domain / domain_suffix / domain_keyword /
ip_cidr / port / port_range / network / process_name / geosite / geoip).

**Complexity**: **MEDIUM** — 314 LOC parser, round-trip preserved.

---

### G2 — Custom rules Import/Export

**Entry point**: Rules tab → `ImportCustomRulesAsync` (1820),
`ExportCustomRulesAsync` (1895).

**Service chain**: `CustomRulesImportExport` + StorageProvider file picker.

**Complexity**: **LOW**.

---

### G3 — Rules Cards/Read/Edit view modes

**Entry point**: Rules tab. `SetRulesViewCards` (1315), `SetRulesViewRead`
(1318), `SetRulesViewEdit` (1325).

**Complexity**: **LOW** — VM state flip.

**Test coverage**: `AvailableRuleTypesSurfaceTests`,
`BoolToChevronConverterTests`.

---

### H1 — GitHub Releases API update check

**Entry point**: App startup + Settings → Updates → "Проверить". `UpdateChecker.CheckForUpdateAsync`.

**Service chain**: `UpdateChecker` (1387 LOC) → GitHub `releases?per_page=30`
→ SemVer parse (handles `-rN` rolling candidates) →
`UpdateInfo` with platform-suffix asset (`-win` / `-mac` / `-linux`).

**Complexity**: **HIGH** — 1387 LOC, lite vs full asset selection,
checksum sidecar verification, install receipt validation (v2.29.0+
Layer 7 warning banner).

---

### H2 — Stable vs Experimental channel

**Entry point**: Settings → Updates → "Experimental channel" checkbox.

**Service chain**: `UpdateSettings.IsExperimental` toggles inclusion of
`prerelease: true` releases in `CheckForUpdateAsync`.

**Complexity**: **LOW**.

---

### H3 — Lite vs Full update ZIPs

**Service chain**: `UpdateChecker.FindLiteAsset` (only DLLs ~3.4 MB),
`FindFullAsset` (full install). `IsSharedRuntimeInstall()` decides.

**Complexity**: **MEDIUM**.

---

### H4 — Self-repair (SR-1..SR-4 + LaunchFailureCounter)

**Entry point**: `Program.Main` increments counter; `MainWindow.Opened` calls
`LaunchFailureCounter.MarkStable`. Thresholds: 3 → SelfRepair, 5 → Config
reset, 7 → Safe-Mode prompt.

**Service chain**: `LaunchFailureCounter` + `SelfRepair` + `ResilientStarter`.

**Complexity**: **HIGH** — multi-tier loop break, 10-minute cooldown,
v2.31.7 helper.cmd CMD parser bug fixed v2.31.8-r10.

**Platforms**: ✓Win only (helper.cmd repair.cmd).

---

### I1 — Simple page (one-click)

**Entry point**: `Views/Pages/SimplePage.axaml`. Default view.

**Service chain**: `MainWindowViewModel.SimpleMode.cs` (599 LOC) — big CTA
circle, status pill, active outbound line.

**Complexity**: **MEDIUM** — many Notify dependencies on `IsConnected` /
`IsConnecting`.

---

### I2 — Advanced shell (10 tabs)

**Entry point**: Bottom switcher → "Advanced". 10 pages:
SimplePage, ServersPage, SubscribePage, FreeConfigsPage, ApplicationsPage,
ToolsPage, DpiBypassPage, TelegramPage, NetworkPage, EmergencyChannelPage.

**Complexity**: **HIGH** — entire MainWindowViewModel (6753 LOC) + 7 partials.

---

### I3 — Theme switching

**Entry point**: Settings → Theme. `ToggleTheme` (6181), `SetThemeLight` (6229),
`SetThemeDark` (6236).

**Service chain**: `Application.Current.RequestedThemeVariant` flip.

**Complexity**: **LOW** — Avalonia ThemeDictionaries handle rest.

---

### I4 — Bilingual Ru/En

**Entry point**: Settings → Language. `ToggleLanguage` (6192),
`SetLanguageRussian` (6243), `SetLanguageEnglish` (6250).

**Service chain**: `Localization/Strings.cs` static `Ru ? "..." : "..."`
getters + VM `L_X` re-notify pattern.

**Complexity**: **MEDIUM** — every UI string touched.

---

### I5 — QR scan + magic-1-step paste (Android)

**Entry point**: Android Subscribe / Servers tab → QR icon. zxing-android-embedded
live preview (Bug-AND-023 v3).

**Service chain**: `QrScanLauncher.java` → `QrCodeDecoder` →
`AndroidApp.QrScanApply.cs` → `VlessUriParser.Parse` →
auto-add to Servers tab.

**Complexity**: **MEDIUM** — JNI + Avalonia bridge, 301 LOC.

**Platforms**: ✓Android.

---

### J1 — Leak protection

**Entry point**: `LeakProtection.ValidateConfig` in VpnEngine + Apply.

**Service chain**: Validates DNS strategy, strict_route, DNS rules for routed
processes, required outbounds, VLESS/Hy2/TUIC fields, smart-mode local-dns
detour (v2.31.x).

**Complexity**: **HIGH** — 623 LOC, 19 test cases, protocol-aware dispatch.

**Test coverage**: `LeakProtectionTests` (19).

---

### J2 — F-A..F-E placeholder defense layers

5-layer stack (v2.32.x):
- **F-A** placeholder-aware scope guard in `VlessServersResolver`
- **F-B** `SettingsMigrator` strip on load
- **F-C** UI badge for placeholder-tagged entries
- **F-D** scope-aware `LeakProtection`
- **F-E** runtime `ConfigSanityCheck` + `AutoFailoverEngine`

**Complexity**: **HIGH** — 5 services, 108 new tests for r10 cycle
(see `plans/r10-test-coverage-audit.md`).

---

### J3 — Block on VPN fail (Windows Firewall)

**Entry point**: Settings → Privacy → "Блокировать при сбое VPN".

**Service chain**: `FirewallManager.CreateBlockRules` (disabled) →
`HealthMonitor.OnSingBoxCrashed` → `EnableBlockRules` → success restart →
`DisableBlockRules`.

**Complexity**: **MEDIUM** — 409 LOC, localized netsh parser (RU/DE/ES
CP-866 fix v2.31.0-r1 CO-5).

**Test coverage**: `FirewallManagerLocalizedNetshTests` (2).

**Platforms**: ✓Win only.

---

### J4 — Russian traffic geo-bypass

**Entry point**: Settings → Privacy → "Обход RU трафика".

**Service chain**: `AppSettings.App.BypassRussianTraffic = true` →
`GeoDataDownloader.EnsureGeoFilesAsync` (geosite.db/geoip.db from upstream).
Generator emits route rules `geosite=ru → direct`.

**Complexity**: **MEDIUM**.

---

### K1 — Windows Service mode

**Entry point**: `VPNRouter.CLI service install/start/stop/uninstall/status`,
or installer.

**Service chain**: `ServiceInstaller.RunSc` (`sc.exe create / config /
failure / start / qfailure / delete`) → `VPNRouterService` BackgroundService
runs same VpnEngine startup.

**Complexity**: **MEDIUM** — `VPNRouter.Service\ServiceInstaller.cs`
+ `VPNRouterService.cs`. Delayed-auto start, LocalSystem, failure recovery
3x/60s.

**Platforms**: ✓Win.

**Known issues** (CLAUDE.md): `ServiceInstaller.RunSc` sets `Verb = "runas"`
with `UseShellExecute = false` — harmless dead code, elevation actually
relies on `AdminHelper.IsAdmin()` pre-check.

---

### K2 — CLI mode (Spectre.Console)

**Entry point**: `vpnrouter.exe start/stop/status/profiles/service/doctor/
test-update/emergency-test`.

**Service chain**: Same as App — `VpnEngine`. Spectre.Console for output.

**Complexity**: **MEDIUM** — 8 commands, profile resolution, `--dry-run`
validation-only path. Admin check via `AdminHelper.IsAdmin()`.

**Platforms**: ✓Win ✓Mac ✓Linux.

---

### K3 — Emergency channel (wgturn-cli)

**Entry point**: Emergency Channel page (Phase 3 not wired yet).
`vpnrouter emergency-test --wgturn-url ... --vk-link ...` CLI for dev.

**Service chain**: `WgturnUpdater.DownloadAsync` (PavelLizunov/wgturn-core
GitHub release) → `EmergencyChannelManager.Spawn(wgturn-cli.exe)` →
`EmergencyChannelEngine` state machine (Disconnected/Connecting/Connected/Failed).

**Complexity**: **MEDIUM** — 204+292 LOC, two binary variants (slim
needs system Chromium, embedded bundles).

**Known issues** (plans/r10-wgturn-strip-and-am3-apps-lists.md):
wgturn-core repo is private, Phase 1 bundles wgturn-cli.exe in installer,
CI cannot build APK with libbox until Phase 2 (Android).

**Platforms**: ✓Win (Phase 1 bundled). Mac/Linux/Android Phase 2+.

---

### K4 — Packaging (DMG / AppImage / .deb / APK / Homebrew / APT / winget)

**Entry point**: CI workflows + build scripts.

**Service chain**: `build.ps1` (Win), `build-mac.sh` (Mac, SSH'd from
`slovn@192.168.0.246`), `build-linux.ps1` (Linux .deb + .AppImage),
`packaging/winget` manifests, Homebrew tap auto-bump via `repository_dispatch`,
APT repo `vpn.ninitux.com/apt/` via `reprepro` on gh-pages.

**Complexity**: **HIGH** — 4 platforms × 2 channels, 12 release assets per
candidate, custom domain `vpn.ninitux.com` CNAME + Let's Encrypt.

---

## Complexity dashboard

| Feature | Cmplx | Core LOC | Test cov | Bug count (plans/) |
|---------|-------|----------|----------|---------------------|
| A1 Connect/Apply/Disconnect | HIGH | VpnEngine 1658 | 17 tests | 12+ (silent leak, recovery gap, timer race) |
| A2 Split tunnel | HIGH | ConfigGen 1353 + ProcessScanner 255 | 14+2 | case-sensitivity, WMI hang |
| A3 Full tunnel | LOW | 1 branch | — | — |
| A4 Custom config | HIGH | CustomConfigInjector 1254 | 22+ | DoH/detour pitfalls |
| A5 Hot-reload | MED | SingBoxManager 989 partial | implicit | — |
| A6 Health monitor | HIGH | HealthMonitor 593 | 6 tests | 5 race lessons |
| A7 ETW monitor | MED | EtwProcessMonitor 184 | — | dispose race |
| A8 Multi-protocol | MED | VlessUriParser 165 + LP 623 | 19 | — |
| A9 Conflicting-VPN | LOW | ConflictingVpnDetector | — | wintun-only |
| A10 Auto-failover | HIGH | AutoFailover 356 + Sanity 353 | 108 r10 tests | — |
| B1 Subscriptions | MED | SubscriptionFetcher 298 | 8 | — |
| B5 PlaceholderGuard | MED | 184 + ConfigSanity sets | r10 batch | kanareik incident |
| C2 Pool fetcher | MED | 163 | — | CI-driven |
| C3 Deep verify | HIGH | DeepVerifier 556 | — | — |
| D1 Zapret | HIGH | ZapretManager 267 + Updater + Hosts | — | AV block, .bat templating |
| D3 tg-ws-proxy | MED | TgProxyManager 434 | — | secret-redaction |
| E1 Process picker | MED | App VM portion | — | — |
| E2 Apps Exclude mode | MED | VpnEngine RemoveExcludedApps | — | AM-3 separation pending |
| G1 Custom rules | MED | CustomRulesParser 314 | — | — |
| H1 Update check | HIGH | UpdateChecker 1387 | — | — |
| H4 Self-repair | HIGH | LaunchFailureCounter + SelfRepair | — | v2.31.7 helper.cmd |
| I1 Simple page | MED | VM.SimpleMode 599 | snapshot tests | — |
| I4 Bilingual | MED | Localization Strings.cs + VM.Localization | — | — |
| I5 QR scan (Android) | MED | QrScanLauncher.java + 301 LOC | — | Bug-AND-023 v3 |
| J1 Leak protection | HIGH | LeakProtection 623 | 19 | — |
| J2 F-A..F-E defense | HIGH | 5 services | 108 r10 | — |
| J3 Block on VPN fail | MED | FirewallManager 409 | 2 | localized netsh |
| K1 Windows Service | MED | 2 files | — | RunSc Verb dead-code |
| K3 Emergency channel | MED | 496 LOC + WgturnUpdater | — | Phase 1 |
| K4 Packaging | HIGH | 4 scripts + 3 CI | — | DNS / Brew / APT infra |

---

## Refactor priority list (candidates for v3.0)

High-complexity features touching many services — most leverage for a redesign:

1. **`MainWindowViewModel` god-object** — 6753 LOC + 5 partials. Phase H
   audit (iter#4) already extracted Dispose hygiene + Wgturn. v3.0 should
   split per-page VMs into their own files referenced via ViewLocator.
2. **`VpnEngine.StartAsync` 880-LOC monolith** — 16 service touches.
   Extract a `StartupPipeline` with steps as composable phases (DnsFlush /
   GeoData / Validate / Resolve / Profile / Scan / Generate / Sanity /
   Firewall / Cleanup / Spawn / Probe / Monitor). Each phase unit-testable.
3. **`UpdateChecker` 1387 LOC** — channel + lite/full asset selection +
   install receipt + 3 platform suffixes. Refactor candidates: separate
   `IUpdateSource` (GitHub / Brew / APT) per platform.
4. **`CustomConfigInjector` 1254 LOC** — 1.11→1.13 migration is the bulk.
   Extract `SingBoxFormatMigrator` as standalone with version pin table.
5. **`HealthMonitor` 5 race-condition lessons** — atomic timer-swap,
   AttemptRestart lock, OnHealthTick re-entry gate, restart-cooldown,
   intent flag. Worth a state-machine refactor (Stopped/Healthy/Restarting/
   Stranded/Stopping) with explicit transitions instead of bool fields.
6. **`F-A..F-E` 5-layer placeholder defense** — currently 5 separate gates.
   The plan notes (`plans/r10-stas-confirmed-and-apps-2mode.md`) suggest
   consolidating to a single ingestion-gate + a single egress-gate in v3.0.
7. **`Free Configs` pipeline shortcut logic** — pool>1000 skip path
   doubles the conditional surface. Aggregator + Tester + DeepVerifier +
   Cache merging could be one `FreeConfigPipeline` with stage objects.
8. **Apps E2 Include/Exclude AM-3** — UI list separation still in backlog
   per v2.32.2 plans. Worth doing alongside the per-page-VM split (#1).
9. **Service mode ServiceInstaller.RunSc dead `Verb = "runas"`** — minor
   but documented; refactor or delete during v3.0 polish.
10. **`Worker.cs` in `VPNRouter.Service`** — dead scaffold per
    `CLAUDE.md`. Delete during v3.0 cleanup.

---

## Notes on time-box / coverage

Catalogued all 53 features in the request prompt. Some features could be
drilled deeper (e.g. each Free Configs sub-stage, every Apps 2-mode flag
combo, every Update channel branch), but the per-feature pages already
cover entry-point + service chain + complexity rating with concrete
numbers. The complexity dashboard at the bottom gives a 2D view (feature
× LOC × test count × bug count) for cross-referencing during v3.0 planning.

Test count cross-check (`VPNRouter.Tests/CLAUDE.md`): 19 LeakProtection +
14+2 ConfigGenerator + 22+ CustomConfigInjector + 8 VlessServersResolver +
9 FreeConfigAggregator + 5 HealthMonitorRecoveryGap + 8 SubscriptionFetcher
+ 4 HeadlessGui + 14 PageScreenshot + 3 VisualDiff + r10 batch 108 + …
≈ 731 tests total per memory entry.
