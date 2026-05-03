# VPNRouter.Core

Бизнес-логика. Чистая C#-библиотека без UI. Используется и `VPNRouter.App`, и
`VPNRouter.CLI`, и `VPNRouter.Service`.

## Что внутри

```
Models/         AppSettings.cs (YAML root), Profile.cs, ProcessRule.cs, VPNConfig.cs (sing-box JSON)
Services/       основные сервисы (см. ниже)
Services/FreeConfigs/   агрегатор + tester + GeoIP + cache + deep verifier
AppPaths.cs     %ProgramData%\VPNRouter\* (или ~/.config на Linux/macOS)
AppVersion.cs   единая версия — обновлять перед каждым релизом!
```

## Service map (Services/)

| Файл | Что делает |
|---|---|
| `VpnEngine.cs` | Lifecycle VPN. `StartAsync` / `Apply` / `Stop`. Обе ветки (StartAsync, Apply) теперь обязаны звать `VlessServersResolver.Resolve` перед `ConfigGenerator.Generate` (см. `plans/session-night-shift-2026-04-25.md` — silent leak fix v2.28.2). |
| `SingBoxManager.cs` | Process lifecycle для sing-box. Hot-reload через Clash API. **`Stop()` ставит `EnableRaisingEvents=false` ДО `Kill()`** — чтобы `Exited`-callback не сработал как false crash. |
| `ConfigGenerator.cs` | sing-box 1.13+ JSON генератор. **Hard guard**: throws если servers пуст (v2.28.2). action-based route rules, type-based DNS. |
| `CustomConfigInjector.cs` | User-provided sing-box JSON + injects process routing. `StripUnsupportedFeatures` мигрирует с legacy 1.11 на 1.13. |
| `LeakProtection.cs` | Validation сгенерированного JSON. Ловит missing proxy outbound, DNS strategy, strict_route. **Зовётся в обоих StartAsync + Apply** с v2.28.2. |
| `HealthMonitor.cs` | Periodic health check, auto-restart с backoff (5/10/20/40/80s), debounced rescan (5s window). |
| `VlessServersResolver.cs` | **Single source of truth** для агрегации subscription→VLESS. Зовётся из `VpnEngine.StartAsync`, `VpnEngine.Apply`, `HealthMonitor.GenerateConfigJson`. (v2.28.2-r1) |
| `SubscriptionResolver.cs` | Service/CLI bootstrap. **Не путать с VlessServersResolver** — этот делает refresh + flip ConfigMode→generated. |
| `SubscriptionFetcher.cs` | HTTP fetch + parse server URI list. Три формата: JSON wrapper, raw base64, plain URIs. Dedup по `Server:Port:UUID:Flow`. Parser extracted to internal `ParseBody` (v2.31.5+) для unit-test без HTTP. |
| `VlessUriParser.cs` | Парсер `vless://...` URI. **`HttpUtility.ParseQueryString` НЕ делает двойной unquote** — `pbk` (base64url с `-_`) приходит как есть. |
| `ProcessScanner.cs` | Резолвит process_name по profile. Поддерживает wildcards через regex. **case-sensitive**: sing-box `process_name` matching через Go map → не использовать `ToLowerInvariant()`. |
| `EtwProcessMonitor.cs` | Real-time process events через ETW. <10ms latency vs WMI 500ms+. |
| `FirewallManager.cs` | Windows Firewall rules через `netsh.exe`. block_on_vpn_fail. |
| `ProfileManager.cs` | GitHub > Local > Built-in source priority. Merging multiple profiles (union processes, strictest DNS wins). |
| `SettingsLoader.cs` | YAML load/save через YamlDotNet. Auto-create defaults. |
| `SettingsMigrator.cs` | Schema migrations (legacy → current). |

## FreeConfigs/

| Файл | Что |
|---|---|
| `FreeConfigAggregator.cs` | Refresh pipeline: fetch → parse → dedupe → GeoIP → test → cache. **`PreservePreviousValidation`** static helper (v2.28.2-r5) — мерджит cache verified + recent-Ok с fresh pool. |
| `FreeConfigCache.cs` | Single JSON @ `%ProgramData%\VPNRouter\cache\free_configs.json`. Atomic save (.tmp + rename). |
| `FreeConfigTester.cs` | TCP+TLS probe. Fast scan = TCP only, пропускает TLS handshake. |
| `FreeConfigDeepVerifier.cs` | Real connectivity: spawns sing-box, HTTP round-trip через SOCKS, optional 5MB bandwidth test. |
| `FreeConfigGeoIp.cs` | MaxMind lookup. |
| `FreeConfigPoolFetcher.cs` | Server-side pre-aggregated `pool.json` (GitHub Actions cron каждые 6ч). Skip-2-stages если pool >1000 entries. |
| `FreeConfigSources.cs` | 14 встроенных источников + user-added. |

## Critical patterns / gotchas

### sing-box process_name matching — case-sensitive
Windows `QueryFullProcessImageName` возвращает filesystem casing (`Discord.exe`,
не `discord.exe`). НЕ применять `ToLowerInvariant()` в:
- `ConfigGenerator.cs` (process_name array)
- `ProcessScanner.cs` (resolved names)
- `HealthMonitor.cs` (debounce diff)

Дедупликация — `StringComparer.OrdinalIgnoreCase`, но preservе оригинальный case.

### SingBoxManager intentional stop
```csharp
_process.EnableRaisingEvents = false; // ДО Kill()
_process.Kill(entireProcessTree: true);
```
Иначе `Exited`-event прилетит на threadpool как false crash. Альтернативы
(`volatile bool _intentionalStop`, `int _generation`) не сработали из-за race
с `Restart()`.

### sing-box 1.13.3+ DNS gotcha
`detour:"direct"` FATAL когда `direct` outbound пуст. Решение: добавить
`dns-direct` outbound с `udp_fragment:true` (делает non-empty), DNS-серверы
без proxy detour указывают на него.

### Subscription→VLESS flow (v2.28.2 silent leak)
- В YAML: `app.subscriptions[0].servers` хранит подписочные серверы.
- `vless.servers: []` пуст в subscribe mode.
- В памяти: `VlessServersResolver.Resolve` агрегирует subs→Vless.Servers.
- **Каждый caller `ConfigGenerator.Generate` ОБЯЗАН сначала позвать Resolve**
  (или использовать VpnEngine, который делает это сам).

### sing-box версия
1.13.10 upstream бандлится во все 3 платформы. **Не custom rebuild**.
- `with_utls`, `with_clash_api`, `with_quic` теги — стандартные с 1.13+.
- `process_name` regression в 1.13.9 был — fixed в 1.13.10.

## Тестирование

`VPNRouter.Tests/UnitTest1.cs` ~4900 строк, 80+ unit-тестов в ~25 классах
плюс headless Avalonia tests в отдельных файлах. Полный inventory с
покрытием — `VPNRouter.Tests/CLAUDE.md` "Test classes" таблица.

Headline классы по Core (не исчерпывающе):
- `GetEffectiveServersTests` — backward compat legacy fields
- `ConfigGeneratorTests` — DNS rules, route rules, outbound generation
- `VlessServersResolverTests` (v2.28.2) — 8 cases для subscription aggregation
- `ConfigGeneratorEmptyServersGuardTests` (v2.28.2) — hard guard pin
- `LeakProtectionTests` — 19 cases (включая протокол-aware dispatch
  v2.30.1-r4 + smart-mode local-dns v2.31.x, добавлены в v2.31.5+)
- `CustomConfigInjectorTests` — 22+ cases (Validate / Inject / DNS
  optimization / sing-box check integration)
- `SubscriptionFetcherParserTests` (v2.31.5+) — 8 cases на 3 body формата
- `FreeConfigAggregatorPreserveTests` (v2.28.3-r5) — 9 cases для merge logic
- `Generate_FromSubscribeMode_PassesSingBoxCheck` — integration: запускает
  `sing-box check` на сгенерированном JSON

`InternalsVisibleTo VPNRouter.Tests` уже настроен в `.csproj` — internal
helpers (`SubscriptionFetcher.ParseBody`,
`FreeConfigAggregator.PreservePreviousValidation`,
`FreeConfigCache.HealCorruptedSubThresholdLatencies`) тестируются напрямую.
