# VPNRouter — current platform diff (desktop ↔ Android)

**Файл для меня (Claude).** Single-source-of-truth snapshot **текущего**
состояния различий между desktop (Win/Linux/Mac) и Android. Не roadmap, не
post-mortem. Если в session'е нужно быстро вспомнить «что у нас сейчас
живёт на каждой платформе и где gap» — открыть этот файл.

> **Roadmap к закрытию gap'ов** — отдельный файл:
> `plans/vpnrouter-android-platform-parity-roadmap.md`. Этот файл говорит «что
> есть СЕЙЧАС». Тот — «что собираемся менять».

**Last refresh**: 2026-05-08 после v2.32.0 stable cut + Android port pool 5+6+7
merge. **Версия baseline**: `AppVersion.Version = "2.32.0"` (Core, единый source
of truth для всех платформ).

---

## TL;DR

- **Бизнес-логика** (sing-box, VPN routing, free configs, subscriptions, update
  checker) — `VPNRouter.Core` shared между всеми 4 платформами через
  source-link (см. ниже §«Как Core попадает на Android»).
- **UI код** — desktop: `VPNRouter.App` (XAML + ViewModels). Android:
  `VPNRouter.Android` (C#-only, всё inline в `AndroidApp.axaml.cs` ~4666
  строк). Drift возможен, но управляем через AND-* port'ы.
- **Build / release** — desktop собирается single command + GHA Mac/Linux,
  релизит 12 assets. Android — **только локально вручную, нет в GH Releases**.
  Это главный shipping gap.
- **Auto-update** — desktop: helper.cmd / Homebrew / APT. Android: `AndroidUpdater.cs`
  написан, но **download URL пока ссылается в пустоту** (APK нет в release'ах).
- **Feature parity (UX)** — 9 из 9 desktop-страниц portированы на Android. Несколько
  desktop-only фич Windows-specific (ETW, Firewall, Zapret, TgProxy на Windows
  service'е) — не имеют смысла на Android и ОК что отсутствуют.

---

## 1. Project structure side-by-side

```
VPNRouter.sln (multi-target)
│
├── VPNRouter.Core              ← shared business logic (.NET 8 lib)
│   ├── AppVersion.cs           ← 2.32.0, single source of truth
│   ├── Models/                 ← AppSettings, Profile, VPNConfig, etc.
│   ├── Services/               ← 50+ services (.NET 8 multi-target)
│   ├── Platform/Android/       ← AndroidSingBoxRuntime.cs (libbox shim)
│   └── Platform/macOS/         ← MacProcessMonitor, MacProcessScanner, etc.
│
├── VPNRouter.App               ← Avalonia desktop (.NET 8, Win/Linux/Mac)
│   ├── App.axaml + .cs
│   ├── Views/
│   │   ├── MainWindow.axaml
│   │   ├── AboutWindow.axaml
│   │   └── Pages/              ← 9 страниц XAML (Simple/Subscribe/Servers/
│   │                              Apps/Network/DPI/FreeConfigs/Tg/Tools)
│   ├── ViewModels/             ← 14 VM-файлов + FreeConfigs/, MainWindowVM
│   │                              partial-class разбит на 6 файлов (5920 строк суммы)
│   ├── Localization/Strings.cs
│   ├── Services/               ← desktop-only: SelfRepair, SingleInstance,
│   │                              WindowForegroundHelper, InstallHealthCheck,
│   │                              ShortcutSelfHeal, WindowsServiceHelper
│   └── Styles/Tokens.axaml     ← Arctic palette, design tokens
│
├── VPNRouter.CLI               ← Spectre.Console CLI (Windows only de-facto;
│                                  cross-platform по коду но Service-команды Win-only)
│
├── VPNRouter.Service           ← Windows Service host (Win only)
│
├── VPNRouter.Android           ← Avalonia Android app (.NET 8, net8.0-android)
│   ├── AndroidApp.axaml + .cs              ← UI, 4666 строк inline
│   ├── AndroidApp.AutoUpdate.cs            ← partial: AND-AUTOUPDATE
│   ├── AndroidApp.ConfigShare.cs           ← partial: CONFIG-EXPORT (QR)
│   ├── AndroidApp.FreeConfigs.cs           ← partial: free configs UI
│   ├── AndroidApp.ServerList.cs            ← partial: SERVER-TESTING
│   ├── AndroidApp.SubscribePage.cs         ← partial: subscribe UI
│   ├── AndroidConfigBuilder.cs             ← Android-specific config gen
│   ├── AndroidConfigShare.cs               ← export/import sing-box JSON
│   ├── AndroidFreeConfigsOrchestrator.cs   ← Android free configs runner
│   ├── AndroidStorage.cs                   ← SharedPreferences bridge
│   ├── AndroidUpdater.cs                   ← APK auto-update (waiting for assets)
│   ├── AppIconCache.cs / AppListLoader.cs  ← per-app picker
│   ├── Localization.cs                     ← MIRRORS App/Localization/Strings.cs
│   ├── MainActivity.cs                     ← entry point
│   ├── VpnRouterService.java + .kt         ← libbox tunnel shim (Java)
│   ├── Lib/libbox.aar                      ← sing-box gomobile binding
│   ├── AndroidManifest.xml
│   └── Resources/xml                       ← Android resources
│
├── VPNRouter.Tests             ← xUnit, growing test suite (~250+ tests)
│
└── VPNRouter.Tools             ← PoolAggregator (free configs server-side)
```

---

## 2. Как Core попадает на Android (важно)

**Не через ProjectReference, а через source-link** (`VPNRouter.Android.csproj`):

```xml
<Compile Include="..\VPNRouter.Core\**\*.cs"
         Exclude="..\VPNRouter.Core\bin\**;..\VPNRouter.Core\obj\**"
         LinkBase="Core" />
```

**Почему**: ProjectReference + multi-target + RID-aware Android consumer
запутывают NuGet restore graph. Source-link обходит проблему: Core .cs файлы
компилируются прямо в Android assembly, `#if PLATFORM_ANDROID` /
`#if PLATFORM_WINDOWS` гейтят platform-specific участки.

**Следствие**: Core dependency packages (Newtonsoft.Json, Serilog, YamlDotNet)
**дублированно объявлены** в обоих csproj. При update package version'ов в
Core — обязательно зеркалить в Android csproj.

**Платформенные guards в Core** (выловленные сейчас):
- `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID` (preprocessor) — 5 файлов:
  EtwProcessMonitor, PowerEventListener, ProcessScanner, VpnEngine,
  WindowsDnsHardening
- `OperatingSystem.IsWindows()` (runtime) — 15+ файлов: FirewallManager,
  SingBoxManager, ConfigGenerator, CrashReporter, DnsFlusher, HealthCheck,
  OrphanCleanup, SettingsLoader, TgProxyManager, TunAdapterDiagnostics,
  UpdateChecker, ZapretActions, ZapretUpdater и др.

---

## 3. Build matrix (что чем собирается СЕЙЧАС)

| Платформа | Команда | Где собирается | Output |
|---|---|---|---|
| **Windows** | `build.ps1 -Version X.Y.Z [-Upload]` | local (VM) | 4 zip (App/Service zip + update.zip + 2 sha256) |
| **Mac** | автоматом по push tag | GHA `build-mac.yml` (cloud) | 2 (`.dmg` + `.zip`) |
| **Linux** | автоматом по push tag | GHA `build-linux.yml` (cloud) | 6 (`.deb` + `.AppImage` + `.tar.gz` + 3 sha256) |
| **Android** | `dotnet build VPNRouter.Android -c Release` (manual) | local (VM) | 1 APK (`com.ninitux.vpnrouter-Signed.apk` ~47 MB) |

**В release попадает 12 assets** (12 = 4 + 2 + 6). **APK НЕ попадает.** После
Phase A roadmap'а станет 14.

**CI gates на release** (`.github/workflows/`):
- `build-mac.yml` — собирает Mac DMG
- `build-linux.yml` — собирает Linux пакеты
- `test-windows-update.yml` (T2 from v2.31.10) — live-тест Windows update
- `verify-release-integrity.yml` (T3 from v2.31.10) — checks 12 assets +
  AppVersion внутри Core.dll внутри каждого
- `publish-apt.yml` — APT-репо bump на gh-pages
- `build-free-pool.yml` — ежечасный free configs pool

**Android в этих gate'ах НЕ участвует.** Это shipping-parity gap #1.

---

## 4. Storage / settings layer

| Что | Desktop | Android |
|---|---|---|
| **Backend** | `SettingsLoader` → `%ProgramData%\VPNRouter\config.yaml` (YamlDotNet) | `AndroidStorage` → `SharedPreferences` (per-app sandbox) |
| **Schema validation** | `SettingsValidator` (SR-1) — 7 invariants | inline guards + `AllowedRoutingModes`/`AllowedDnsStrategies`/etc. enums |
| **Migration** | `SettingsMigrator` | inline migration (per-key) |
| **Self-repair on bad load** | `SettingsLoader.Load` never-throws (SR-4) → returns sane defaults + records `LastRecoveryNotice` | inline catch блоки в каждом getter'е, fallback на default |
| **Active server tracking** | в YAML | `KeySelectedServerName` в SharedPreferences |
| **Subscription URL** | в YAML | `KeySubscriptionUrl` |
| **Per-app routing** | NTFS process-name list | `KeyPerAppPackages` (Android package IDs) |
| **DPI bypass mode** | через Zapret (Win-only) | `KeyDpiBypassMode` → `AndroidDpiBypassInjector` (sing-box `tls_fragment` outbound) |
| **Auto-reconnect on network change** | n/a (TUN handles) | `KeyAutoReconnectOnNetworkChange` → `ConnectivityManager.NetworkCallback` |

**Drift-risk**: schema-валидация дублируется. `AppSettingsSane.EnsureSane()` в
Core — единственное **shared** правило целостности. AndroidStorage пользуется
им частично (per-key), не полным `EnsureSane`.

---

## 5. UI / UX layer

| | Desktop | Android |
|---|---|---|
| **XAML files** | 11 (MainWindow + AboutWindow + 9 pages) | 1 (`AndroidApp.axaml` — пустой shell, всё programmatic) |
| **VM layer** | `VPNRouter.App/ViewModels/*.cs` — 14 файлов, MainWindowVM = 5920 строк (6 partial files) | inline в `AndroidApp.axaml.cs` + 5 partial files = ~4666 + ~1000 другие partials |
| **DataBinding** | `{Binding}` через VM properties | manual `_label.Text = ...` + event handlers (~100 OnXxx методов) |
| **Localization** | `Localization/Strings.cs` (ResourceManager-style static class) | `Localization.cs` (mirror того же API, ручная синхронизация) |
| **Design tokens** | `Styles/Tokens.axaml` | linked через `<AvaloniaResource Include="..\VPNRouter.App\Styles\Tokens.axaml" Link="Styles\Tokens.axaml" />` ✓ shared |
| **Penguin mascot** | `Assets/penguin_mascot.png` | linked тем же `<AvaloniaResource>` приёмом ✓ shared |
| **Dark/light theme** | Avalonia FluentTheme | mirror, `KeyTheme` SharedPreference |
| **Languages** | RU + EN | RU + EN (mirrors) |

**Зеркалирование Localization** — самая частая drift причина. Phase H в
roadmap'е закрывает её single-source через `Strings.cs` в shared project.

**Зеркалирование VM** — самая дорогая. Phase G в roadmap'е (30-50 hours).

---

## 6. Feature parity matrix

Легенда: ✅ работает на платформе · ⚠ работает но с разницей · ❌ нет ·
n/a платформенно бессмысленно.

### VPN routing core
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| sing-box TUN mode | ✅ | ✅ | ✅ | ✅ (через `VpnService` + libbox) |
| VLESS + Reality + uTLS | ✅ | ✅ | ✅ | ✅ |
| Hysteria2 / TUIC | ✅ | ✅ | ✅ | ✅ |
| Trojan / Shadowsocks | ✅ | ✅ | ✅ | ✅ |
| gRPC transport | ⚠ (server-specific) | ⚠ | ⚠ | ⚠ |
| WireGuard outbound | ✅ | ✅ | ✅ | ✅ |
| AmneziaWG | ✅ | ✅ | ✅ | ⚠ (нет тестов) |
| DNS strategies (vpn_only/smart/direct) | ✅ | ✅ | ✅ | ✅ |
| Hot-reload (Clash API) | ✅ | ✅ | ✅ | ⚠ (libbox API differs, restart-only сейчас) |

### Process / app filtering
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| Per-process routing (split tunnel) | ✅ ETW <10ms detection | ✅ MacProcessMonitor (lsof poll ~500ms) | ✅ /proc poll | ✅ Per-app via Android `addAllowedApplication`/`addDisallowedApplication` (UID-based) |
| Process wildcards | ✅ | ✅ | ✅ | n/a (Android operates на package ID, не на process name) |
| Child process tracking | ✅ via WMI | ❌ | ❌ | n/a |
| ETW real-time monitor | ✅ | n/a | n/a | n/a |

### Auto-update
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| In-app update check | ✅ UpdateChecker | ✅ (через Homebrew) | ✅ (через APT) | ⚠ AndroidUpdater написан, но APK pattern в release не существует — download fail |
| Backup snapshot перед update | ✅ UpdateBackup (T5) | n/a (brew) | n/a (apt) | ❌ planned |
| Helper.cmd update flow | ✅ + lint check (T1) | n/a | n/a | n/a (PackageInstaller intent flow) |
| Live update CI test | ✅ T2 workflow | ❌ | ❌ | ❌ |
| Channel selector (stable/prerelease) | ✅ | ✅ | ✅ | ✅ |

### Self-repair (v2.32.0 stack)
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| InstallHealthCheck (hash verify) | ✅ App/Service | ⚠ (Homebrew handles) | ⚠ (apt handles) | ❌ |
| SelfRepair (re-download bad files) | ✅ | n/a | n/a | ❌ |
| SettingsValidator (SR-1) | ✅ Core, applies to all | ✅ | ✅ | ⚠ (partial — inline guards в AndroidStorage) |
| LaunchFailureCounter (SR-2) | ✅ Core | ✅ | ✅ | ❌ (Android lifecycle differs — no "launch loop" pattern) |
| CacheRecovery (SR-3) | ✅ Core | ✅ | ✅ | ⚠ (применимо к FreeConfigCache, не подключено) |
| Settings load never-throws (SR-4) | ✅ Core | ✅ | ✅ | ⚠ (per-getter, не single wrapper) |
| ShortcutSelfHeal | ✅ (re-creates Start Menu/Desktop shortcuts) | n/a | n/a | n/a |

### Auxiliary tools
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| Zapret (DPI bypass) | ✅ | ❌ | ❌ | ✅ через `tls_fragment` outbound (`AndroidDpiBypassInjector`) — НЕ Zapret сам, но эквивалент |
| TgProxy (Telegram proxy via WS) | ✅ Win Service | ❌ | ❌ | ❌ |
| Hosts manager (Discord voice fix) | ✅ | ❌ (нет admin host edit) | ❌ | n/a |
| Firewall block-on-VPN-fail | ✅ netsh | ❌ | ❌ | n/a (Android VPN routing handles) |
| Windows DNS hardening | ✅ | n/a | n/a | n/a |
| Geo data downloader | ✅ | ✅ | ✅ | ✅ (Core, shared) |
| Free configs pool | ✅ | ✅ | ✅ | ✅ (Core, shared, через `AndroidFreeConfigsOrchestrator` для UI) |
| Subscription fetcher | ✅ | ✅ | ✅ | ✅ |
| Custom rules import/export | ✅ | ✅ | ✅ | ✅ (CONFIG-EXPORT pool 7 — QR + JSON) |
| Server testing (TCP/TLS probe) | ✅ | ✅ | ✅ | ✅ (SERVER-TESTING pool 6) |
| QR code share configs | ⚠ (через CONFIG-EXPORT, есть) | ⚠ | ⚠ | ✅ |
| Crash reporter | ✅ | ✅ | ✅ | ⚠ (CrashReporter Core code есть, не уверен что hook'нут на Android Application.UnhandledException) |

### Service / autostart
| Feature | Win | Mac | Linux | Android |
|---|---|---|---|---|
| Run as system service | ✅ Windows Service (`VPNRouter.Service`) | ❌ (launchd возможно но не делали) | ❌ (systemd возможно но не делали) | ✅ Android `Service` через VpnService API |
| Boot autostart | ✅ Service auto + HKCU\Run для App | ⚠ (через "Login Items") | ⚠ (через .desktop autostart) | ✅ `BootReceiver` + Always-on VPN |
| TgProxy autostart | ✅ (DBG-1..5 cycle v2.31.x) | n/a | n/a | n/a |
| Single instance lock | ✅ Mutex `Global\VPNRouter.App.SingleInstance.v2` | ❌ | ❌ | n/a (Android lifecycle differs) |
| Bring-to-foreground existing | ✅ WindowForegroundHelper | n/a | n/a | n/a |

### Pages / UI surface
| Page | Desktop | Android |
|---|---|---|
| Simple mode (one-tap toggle) | ✅ SimplePage | ✅ |
| Subscribe (URL paste + provider list) | ✅ SubscribePage | ✅ (AndroidApp.SubscribePage.cs) |
| Servers (list + ping + select) | ✅ ServersPage | ✅ (AndroidApp.ServerList.cs) |
| Free Configs (server-side pool) | ✅ FreeConfigsPage | ✅ (AndroidApp.FreeConfigs.cs) |
| Applications (per-process select) | ✅ ApplicationsPage | ✅ (per-app picker) |
| Network (DNS/routing/TUN) | ✅ NetworkPage | ✅ |
| DPI Bypass (Zapret config) | ✅ DpiBypassPage | ✅ (mode picker → AndroidDpiBypassInjector) |
| Telegram Proxy | ✅ TelegramPage | ❌ no equivalent (Android doesn't run TgProxy) |
| Tools (logs, diagnostics) | ✅ ToolsPage | ✅ (log overlay + diagnostic chips) |
| About | ✅ AboutWindow | ✅ (settings overlay shows version) |

---

## 7. Distribution channels

| Channel | Desktop | Android |
|---|---|---|
| GitHub Releases | ✅ 12 assets per release | ❌ нет APK в release |
| One-liner install | `install.sh` (Linux) / `install.ps1` (Win) / `brew install --cask vpnrouter` (Mac) | ❌ |
| APT repo `vpn.ninitux.com/apt/` | ✅ Linux | n/a |
| Homebrew tap `PavelLizunov/homebrew-vpnrouter` | ✅ Mac | n/a |
| Winget | manifests готовы, не submitted | n/a |
| F-Droid | n/a | ❌ planned long-term |
| Play Store | n/a | ❌ planned long-term |
| Direct sideload | n/a | ⚠ (manual `adb install` или scp APK на телефон) — единственный путь сейчас |

**Главный gap**: Android-юзер сейчас **не имеет canonical install path**. См. Phase
A+B+D в roadmap'е.

---

## 8. Test coverage diff

| | Desktop | Android |
|---|---|---|
| Unit tests | ✅ ~250+ tests в `VPNRouter.Tests` (xUnit) | ⚠ только если код в Core — тогда покрывается общими тестами |
| Integration tests | ⚠ part-manual через MCP+UIA (rule 1a) | ⚠ part-manual через `mcp__computer-use__*` на phone via Mac SSH |
| Live update test (CI) | ✅ T2 workflow | ❌ |
| Release integrity (CI) | ✅ T3 hard-gate (Win) + soft-warn (Mac/Linux AOT trim) | ❌ no APK to verify |
| Regression suite per ship | ✅ 59/59 last green в v2.32.0 | n/a (Android-specific shaders nothing yet) |

---

## 9. Cross-platform helpers / utilities

Что физически живёт в Core и **сейчас уже работает на обеих сторонах**:

- `ConfigGenerator` — генерит sing-box JSON (с `OperatingSystem.IsWindows()`
  guards для Windows-specific outbounds)
- `VlessUriParser` / `VlessServersResolver` / `SubscriptionResolver`
- `FreeConfigAggregator` / `FreeConfigCache` / `FreeConfigDeepVerifier`
  / `FreeConfigGeoIp` / `FreeConfigKeepPolicy` / `FreeConfigPoolFetcher`
  / `FreeConfigSources` / `FreeConfigTester` / `FreeConfigFetcher`
  / `FreeConfigFreshness` / `FreeConfigModels`
- `SubscriptionFetcher`
- `UpdateChecker` (с CI-mode env-check для Windows test workflow)
- `UpdateInfo` model
- `ServerUriParser`
- `TcpTlsProbe`
- `CrashReporter` (если hook'нут на caller side)
- `LeakProtection`
- `LockFile`
- `SafeMode`
- `SettingsValidator` (SR-1) — Android partially
- `SettingsMigrator`
- `AppSettingsSane`
- `CacheRecovery` (SR-3)
- `LaunchFailureCounter` (SR-2) — Android n/a
- `UpdateBackup` (T5) — Android n/a
- `ResilientStarter`
- `RuleSetCacheManager`
- `GeoDataDownloader`
- `HostsManager` — Win-only (admin host edit)
- `StorageBlobRecovery`
- `RuntimeStatusDetector`
- `CustomConfigInjector`, `CustomDirectRulesParser`, `CustomRulesImportExport`,
  `CustomRulesParser`
- `ConfigShareDocument` + `QrCode` (CONFIG-EXPORT) — pure C#, work everywhere
- `AndroidDpiBypassInjector` — лежит в Core/Services, по имени Android но
  работает как pure data transformation; не активируется на других платформах
  потому что fr из неё дёргается только из AndroidConfigBuilder

---

## 10. Что **только** на одной стороне (de-jure отсутствует)

### Только на desktop, Android **не нужно**:
- `EtwProcessMonitor` (ETW Win-only)
- `FirewallManager` (Windows Firewall via netsh)
- `WindowsDnsHardening`
- `TgProxyManager` + `TgProxyUpdater` (Telegram WS proxy as Win Service)
- `ZapretManager` + `ZapretActions` + `ZapretUpdater` (DPI bypass via Cygwin
  winws.exe — Win-only механизм)
- `MacProcessMonitor` / `MacProcessScanner` (macOS lsof-based polling)
- `NullFirewallManager` (Linux placeholder)
- `OrphanCleanup` (Win-only, чистит зависшие sing-box.exe от прошлых run'ов)
- `PowerEventListener` (Win SystemEvents.PowerModeChanged)
- `TunAdapterDiagnostics` (Wintun adapter checks)
- `TunOwnershipLock` (Wintun shared instance lock)
- `DnsFlusher` (`ipconfig /flushdns`)
- `ProcessScanner` — есть на Mac/Linux в Platform/, но Android n/a

### Только на Android:
- `AndroidSingBoxRuntime` (Core/Platform/Android/) — JNI shim для libbox
- `AndroidConfigBuilder` — wrap'ит ConfigGenerator output под libbox API
- `AndroidConfigShare` — sing-box JSON export/import через Android Storage
  Access Framework
- `AndroidFreeConfigsOrchestrator` — wraps Core FreeConfig services под Android
  UI lifecycle
- `AndroidStorage` — SharedPreferences bridge (~80 keys)
- `AndroidUpdater` — APK download + PackageInstaller intent
- `AppIconCache` / `AppListLoader` — `PackageManager` query для per-app picker
- `MainActivity.cs` — entry point + permission flow (notification + storage)
- `VpnRouterService.java` (~1300 строк) — Android `VpnService` subclass + libbox
  PlatformInterface implementation + wake-lock + ConnectivityManager
- `VpnRouterService.kt` — Kotlin reference (не компилится в APK; для readability)
- `AndroidApp.AutoUpdate.cs` / `ConfigShare.cs` / `FreeConfigs.cs`
  / `ServerList.cs` / `SubscribePage.cs` — UI partials

---

## 11. Open feature gaps на Android (для будущих pool'ов)

После pool 5+6+7 закрыт почти весь UI. Что осталось:

- **AND-PROFILES** — последняя фича чтобы достичь 100% page parity
  (профили рутинга — preset'ы аппов). Уже spawned, не запущен. См. roadmap
  Pool A item #4.
- **AND-CRASH-HOOK** — убедиться что `CrashReporter` подцеплен на Android
  через `AppDomain.CurrentDomain.UnhandledException` или Android-specific
  `Thread.setDefaultUncaughtExceptionHandler`. Status: TBD.
- **AND-SELF-REPAIR-ADAPT** — SR-1/SR-2/SR-3/SR-4 layer на Android. Большинство
  применимы (settings validator, cache recovery, settings load never-throws),
  кроме launch-failure counter (Android lifecycle отличается от Chrome-style
  bad-flags pattern). Спланировать вручную в формате pool-task.
- **gRPC server-specific** — server-side issue, не platform.
- **Android performance audit pass 2** — handbook §8.3.

---

## 12. Что ШИПАЕТСЯ одним push'ом сейчас vs в идеале

### Сейчас (push tag `v2.32.0`):
1. GHA `build-mac.yml` срабатывает → DMG + ZIP в release ✅
2. GHA `build-linux.yml` срабатывает → deb + AppImage + tar.gz ✅
3. Локально на Win VM — `build.ps1 -Version 2.32.0 -Upload` → 4 win zip ✅
4. T2 + T3 workflows проверяют integrity и live update ✅
5. publish-apt.yml пушит `.deb` в APT repo ✅
6. Homebrew tap auto-bumps на новый stable cask ✅
7. **APK** — нет. Нужно вручную `dotnet build` локально и `gh release upload` руками. **❌ shipping gap**

### В идеале (после Phase A в roadmap):
1-6 — те же ✅
7. GHA `build-android.yml` срабатывает → APK signed → `gh release upload`
   автоматом ✅
8. T3 расширен на APK integrity ✅
9. AndroidUpdater в установленных копиях видит новый APK и предлагает обновиться ✅

Effort до этого состояния: **~10-15 hours work** + 1 ship cycle для validate
keystore + first APK in release.

---

## 13. Lessons / lore (не повторять)

### `bin/`/`obj/` после быстрых rebuild-revert циклов на Android (2026-05-07)

При непонятном `mono_method_get_unmanaged_callers_only_ftnptr` SIGABRT в
init_android_runtime — **первое действие clean rebuild**, не bisect:

```bash
rm -rf VPNRouter.Android/bin VPNRouter.Android/obj
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release
```

Stale typemap/JCW state в obj/ ломает Mono runtime init вне зависимости от
исходника. Полный post-mortem: `plans/v2.32.0-android-pool7-mono-crash-fix.md`,
handbook §1.6.

### `process_name` в sing-box case-sensitive (всегда)

И на Win (где Process name из `QueryFullProcessImageName` имеет filesystem
casing типа `Discord.exe`), и на Mac/Linux. Никогда не `ToLowerInvariant()`.
Дедупликация — `StringComparer.OrdinalIgnoreCase` без mutation. Применимо ко
всем платформам через Core ConfigGenerator.

### `AppVersion.Version` ВСЕГДА совпадает с release tag, включая `-rN` суффикс

Иначе UpdateChecker не различит `2.25.0-r1` от `2.25.0-r2`. См. CLAUDE.local.md
урок v2.25.0-r1→r2. Применимо одинаково к Win update flow и AndroidUpdater
(который тоже использует UpdateChecker под капотом).

### Source-link Core в Android требует mirror'а package references

Core зависимости (Newtonsoft.Json, Serilog*, YamlDotNet) — в **обоих** csproj.
При bump'е версии в Core, забыть зеркалить в Android = build fail только
Android.

### Localization drift

Strings.cs в App + Localization.cs в Android вручную синхронизируются. История
показывает ~3 случая drift'а на пунктуацию за pool 5+6+7. Phase H в roadmap —
single-source через shared project.

---

## 14. Quick-look references

- **Roadmap к закрытию gap'ов**: `plans/vpnrouter-android-platform-parity-roadmap.md`
- **Android handbook** (build/test/lessons): `plans/vpnrouter-android-handbook.md`
- **Android research baseline**: `plans/vpnrouter-android-research.md`
- **Mono crash post-mortem**: `plans/v2.32.0-android-pool7-mono-crash-fix.md`
- **Release strategy**: `plans/vpnrouter-release-strategy.md`
- **Cut-stable checklist**: `plans/cut-stable-checklist.md`
- **Core stability audit**: `plans/vpnrouter-core-stability-audit.md`

---

**Когда обновлять этот файл**: после каждого major Android pool merge (когда
feature/distribution gap меняется) и после каждой stable cut (для baseline
versioning). Last refresh — выше в шапке.

---

## 15. Неразрешимые противоречия (платформенные ограничения)

Это различия которые **нельзя** убрать через refactoring или дополнительный код —
они продиктованы фундаментальной архитектурой OS и принять их как `de facto`.

### 15.1 Process-name routing физически невозможен на Android

**Desktop**: sing-box matches packets по `process_name` (Discord.exe). Это
требует kernel access к process table — на Win через ETW + WMI, на Mac через
lsof, на Linux через /proc.

**Android**: VpnService API даёт **только UID-based filtering**:
`addAllowedApplication(packageName)` / `addDisallowedApplication(packageName)`.
Никакого process_name. Это специально — Android security model запрещает
user-app process enumeration. Даже у нас нет `Process.GetProcessesByName` —
SELinux + Android sandboxing блокируют.

**Следствие**:
- На Android понятие «routing» = «route by app package» (что **app целиком**
  идёт через VPN)
- Wildcards типа `*.exe` или `chrome*` не работают — Android оперирует
  package ID (`com.android.chrome`), один app = один UID
- Child process tracking (Win has it via WMI parent PID) — n/a, у Android
  app's child processes наследуют тот же UID

**Что делать**: уже сделано — Android UI использует package picker
(`AppListLoader` через PackageManager), показывает app icon + display name.
Routing через `addDisallowedApplication` (split tunnel) или
`addAllowedApplication` (allowlist).

### 15.2 ETW (Event Tracing for Windows) — Win kernel-only

**Desktop Win**: `EtwProcessMonitor` — <10ms latency на process start/stop.
Critical для split-tunnel UX (Discord открыли — сразу же routing активирован).

**Mac/Linux**: нет equivalent. Polling /proc или lsof = ~500ms+ latency.
**Android**: даже polling недоступен (нет API).

Не лечится — это NT kernel feature.

### 15.3 Admin/root privilege model

**Desktop**: User имеет admin → даём им TUN driver, ETW, Firewall control,
DNS hardening. Trust gate один раз через UAC.

**Android**: Root **запрещён в commercial flow** (отказ от Play Store, F-Droid
warns, OEM unlock breaks app trust). VpnService API специально designed чтобы
не требовать root — но ограничивает:
- Нет Firewall control (только VPN routing)
- Нет DNS hardening (только VPN-tunnel DNS rules)
- Нет System file modification
- Нет hosts manipulation (для Discord voice fix)

Не лечится — это Android security model. Половина Win-only services (Firewall,
DNS hardening, Hosts, Zapret через Cygwin) **никогда** не будут на Android.

### 15.4 Single-instance + bring-to-foreground

**Desktop**: `Mutex Global\VPNRouter.App.SingleInstance.v2` + `AttachThreadInput`
для бросить window наверх (v2.31.7 fix). Pure user-mode trick.

**Android**: lifecycle managed by OS. Single instance — `launchMode="singleTask"`
в manifest. Bring-to-foreground — startActivity intent (но если app в `onStop`
система может отказать). **Невозможно** сделать identical UX — Android
специально ограничивает foreground promotion.

Не лечится. Это разные lifecycle модели.

### 15.5 File system + storage paths

**Desktop**: `%ProgramData%\VPNRouter\config.yaml`, `%ProgramData%\VPNRouter\logs\`,
shared between user accounts, accessible через any tool.

**Android**: `/data/data/com.ninitux.vpnrouter/` — sandbox per-app, **не
доступно** другим apps без `content://` provider. Logs не достать через adb на
non-rooted phone.

Не лечится. Android sandboxing — fundamental design.

**Implication**: Diagnostic UX отличается. На desktop user может прислать
`logs\vpnrouter.log`. На Android нужен in-app log viewer (`AndroidApp` log
overlay) + share sheet с экспортом в Storage Access Framework.

### 15.6 Update mechanism

**Desktop**:
- helper.cmd kills sing-box + xcopy + restart App
- Все файлы накладываются in-place
- Backup snapshot (UpdateBackup T5) для rollback

**Android**:
- PackageInstaller intent: OS sees signed APK, prompts user, replaces app
- Signature **должна совпадать** со старой иначе reject
- Backup невозможен на нашей стороне — OS atomically replaces APK

Не лечится. Это разные OS-level mechanisms. Но цель одинаковая — обновить
binary без потери настроек.

**Implication**:
- Keystore management critical на Android (потеряли — все юзеры залочены на
  старой версии)
- T5 UpdateBackup на Android n/a — OS handles
- Helper.cmd lint (T1) на Android n/a

### 15.7 TUN driver ownership

**Desktop**:
- Win: Wintun installed by us, TunOwnershipLock prevents conflicts
- Mac: utun via syscall
- Linux: /dev/net/tun
- Все требуют admin/root

**Android**: VpnService API — TUN-tunnel создаётся системой, нам передаётся
file descriptor. **Не наш TUN, не наш driver.** Никаких diagnostics типа
"is Wintun installed", "is device claimed by another VPN".

Implication: `TunAdapterDiagnostics` + `TunOwnershipLock` n/a на Android —
система гарантирует exclusive access (но только одному VPN app одновременно
на Android — это и есть единственный механизм).

### 15.8 Background lifecycle (Doze + foreground service)

**Desktop**: процесс живёт пока user не закрыл (или service постоянно).

**Android**: 
- Без foreground service → **OS убъёт** в background через ~5-10 мин
- В Doze mode (screen off + idle) → CPU throttle + network requests batched
- Foreground service → notification mandatory (otherwise crash в Android 14+)
- **Always-on VPN** — отдельная OS setting (Settings → Network → VPN → нашлёт
  always-on). Не наша feature, но мы должны быть совместимы

Не лечится. Это battery policy. **Implication**:
- Wake-lock в `VpnRouterService.java` (NETRES added)
- START_STICKY return для VpnService.onStartCommand
- Notification persistent (как trade-off за foreground promotion)
- Auto-reconnect на network change через `ConnectivityManager.NetworkCallback`

### 15.9 Hot-reload через Clash API

**Desktop**: `PUT /configs` на localhost:9090 — sing-box swaps config без TUN
restart. Юзер не замечает.

**Android**: libbox API (`Libbox.newService(json)`) возвращает BoxService.
Reconfig = `boxService.close() + Libbox.newService(newJson)`. Это **kill+restart**
по факту, плюс на 1 секунду VPN tunnel закрыт.

**Полу-resolvable** — может быть через libbox HTTP endpoint exposed (требует
upstream sing-box-for-android API extension). Сейчас не делаем — overhead
превышает benefit.

### 15.10 Случай: case-sensitivity в filesystem

**Win**: NTFS case-insensitive (по умолчанию). Process matching case-sensitive
(Go map в sing-box).

**Mac/Linux**: filesystem case-sensitive по умолчанию.

**Android**: ext4 case-sensitive.

В Core ConfigGenerator уже handle'нуто — preserve case `OrdinalIgnoreCase` для
dedup. Применимо к всем платформам одинаково. Но это противоречие источник
багов — урок «не использовать `ToLowerInvariant()` на process_name» в
CLAUDE.md (golden rule #7).

---

## 16. Разрешимые противоречия (backlog, требуют работы)

### 16.1 VM duplication (Phase G в roadmap)

Сейчас: `MainWindowViewModel.cs` (5920 строк) на desktop ↔ inline в
`AndroidApp.axaml.cs` (4666 строк). Каждая фича = два захода.

Решение: extract VM в shared `VPNRouter.Avalonia.UI` project. Effort
**30-50 hours**.

### 16.2 Localization duplication (Phase H в roadmap)

`Strings.cs` (App) ↔ `Localization.cs` (Android) ручная синхронизация. ~3
случая drift'а зафиксированы.

Решение: single Strings.cs в shared project. Effort **2-3 hours**. Можно
сделать **до** Phase G.

### 16.3 SettingsValidator partial wiring на Android

Core has SR-1 validator, Android делает inline guards в `AndroidStorage`
getter'ах. Логика дублируется.

Решение: вызывать `AppSettingsSane.EnsureSane()` после load из SharedPreferences.
Effort **2-4 hours**.

### 16.4 CacheRecovery не подключён на Android

Core has SR-3 (FreeConfigCache, ProfileManager, StateFile recovery), Android
не вызывает.

Решение: один call site в `AndroidApp.OnFrameworkInitializationCompleted`.
Effort **1-2 hours**.

### 16.5 SR-1/SR-3/SR-4 на Android — wiring

SR-1 SettingsValidator + SR-3 CacheRecovery + SR-4 never-throws — applicable
концептуально. Wiring tasks отдельные. (SR-2 LaunchFailureCounter Android n/a —
lifecycle не применим.)

### 16.6 CrashReporter unhooked на Android

`CrashReporter.cs` в Core, но не подцеплен через
`Android.App.Application.OnUnhandledException` или
`Thread.setDefaultUncaughtExceptionHandler`.

Решение: hook в `MainActivity.OnCreate` или `AndroidApp` startup. Effort
**1-2 hours**.

### 16.7 Build pipeline + APK в release (Phase A в roadmap)

`build-android.yml` + keystore + APK upload. Effort **4-6 hours** + 1 ship
cycle для validate.

### 16.8 AndroidUpdater download URL pattern (Phase C)

Класс existsуется, но pattern `VPNRouter-vX.Y.Z-android-arm64.apk` — пока
не верифицирован к реальным release assets (потому что APK нет в release).
После Phase A — live test и patch URL template если не совпадёт.

Effort **2-3 hours**.

### 16.9 XAML / programmatic UI duplication

Сейчас Avalonia Mobile плохо handle'ит heavy XAML с ResourceDictionary
lookups, поэтому Android ушёл в C#-only widget building. Это структурное
различие, не drift.

**Полу-resolvable** — может быть через shared `UserControl`-классы в
shared project. Но Avalonia Mobile ограничения могут вернуть нас к C#-only
ставку — нужен POC.

Effort: TBD после Phase G.

### 16.10 Test coverage gap на Android

Сейчас xUnit тесты в `VPNRouter.Tests` покрывают **только Core** (потому что
Android-specific code в `VPNRouter.Android` не тестируется). Если код в
Core — он покрывается. Если inline в AndroidApp — нет.

**Полу-resolvable** — после Phase G (extract VM) большая часть logic'а
переедет в shared, автоматом покроется. Android-shell тесты (libbox interop,
SharedPreferences) можно добавить через инструментальные тесты (требует
эмулятор в CI). Effort: **большой**, deferred.

### 16.11 Hot-reload на Android

Сейчас restart-only. Через libbox API extension можно сделать hot-reload.

Effort: **TBD**, требует exploration upstream sing-box-for-android.

---

## 17. Полу-противоречия / философские различия

Это различия где обе стороны **правильные** — просто разная природа OS.
Не gap, не bug, не TODO. Записывать как «принимаем такими» чтоб не
пытаться унифицировать.

### 17.1 Trust model

**Desktop**: один admin gate (UAC). После него — всё дозволено.

**Android**: per-permission user dialogs:
- VPN connection (mandatory + per-app или global toggle)
- Notification permission (Android 13+)
- Storage Access Framework для config export
- Battery exception (для not-killed-by-Doze)
- Network state read

Это **не недостаток** — это explicit consent model. Нам уже привыкнуть.

### 17.2 Multi-user / multi-profile

**Desktop**: один user типично. Settings в `%ProgramData%` shared. Profiles
не Windows feature, our абстракция.

**Android**: multi-user system из коробки:
- Primary user vs guest
- Work profile (Android Enterprise)
- VpnService instance per-user (work profile может иметь свой VPN, primary —
  свой)
- SharedPreferences per-user-per-app

Implication: на Android **наши настройки не shared между work-profile и
primary**. Это не bug, это by design. У нас даже нет UI surface для multi-user.

### 17.3 Logging strategy

**Desktop**: file-based logs в `%ProgramData%\VPNRouter\logs\` + Serilog
sinks. User может прислать .log file для diagnostic.

**Android**: Android Log (logcat) + наш log overlay UI + Storage Access
Framework export. Logcat недоступен на non-rooted phone через adb (без USB
debug). 

Оба правильные. Workaround на Android: in-app "Поделиться логом" → SAF intent.
Уже сделано через AndroidApp log overlay.

### 17.4 UI scaling / layout

**Desktop**: window resize, multi-monitor, DPI scale, аspect ratio любой.

**Android**: orientation (portrait/landscape) + system bars (notch, gesture
nav, status bar) + density buckets (mdpi/hdpi/xhdpi/etc).

Layout philosophy differs. Avalonia handle'ит большую часть автоматически,
но Android-specific issue — нотчи, system insets, soft keyboard push-up.

Не противоречие, просто разная задача. Желательно portrait-first дизайн
для Android (что мы делаем).

### 17.5 Update channel concept

**Desktop**: stable/prerelease — наша абстракция (тег с `-rN`).

**Android**: native abstraction есть в Play Store (internal/closed/open
testing channels), но мы sideload — поэтому используем тот же UpdateChecker
pattern что и desktop. Convergent path.

Если когда-нибудь придём на Play Store — Play channels overlap с нашими, но
не conflict. Resolvable in long term.

### 17.6 Configuration export/share

**Desktop**: copy-paste sing-box JSON в файл.

**Android**: QR code (CONFIG-EXPORT pool 7) + ConfigShareDocument schema.
Отлично работает cross-device — desktop сгенерил → phone scan → подцепил
config.

Это **усиление** Android, а не недостаток. QR работает в обе стороны.

---

## 18. Bottom-line

| Категория | Количество | Можно убрать? |
|---|---|---|
| Неразрешимые (платформенные) | 10 | Нет — это OS architecture |
| Разрешимые (backlog) | 11 | Да — ~50-70 hours total |
| Полу-противоречия | 6 | Не нужно — обе стороны правильные |

**Главный takeaway**: **80% разрыва между desktop и Android — work, не
противоречие.** Тех 10 неразрешимых — это специфика Android security model
+ lifecycle, и мы их обходим архитектурно (UID-based routing вместо
process_name, foreground service вместо просто running, VpnService API
вместо нашего TUN driver, и т.д.).

**Implication для development**:
- Цель «одна правка → две платформы» достижима через Phase G+H для UI/VM
  layer (~80% повседневной работы)
- Cross-platform Core layer уже работает (поправка в `SettingsLoader` —
  обе платформы)
- 20% остаётся ручной port — Android-specific UI surface (per-app picker,
  notification, SAF, lifecycle hooks)

**Для shipping**: Phase A+B+C+D закрывает distribution gap полностью. После
этого — `gh release create vX.Y.Z` действительно triggers все 4 платформы.

