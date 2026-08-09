<p align="center">
  <img src="VPNRouter.App/Assets/penguin_logo.png" width="96" alt="VPNRouter logo"/>
</p>

<h1 align="center">VPNRouter</h1>
<p align="center"><b>Virtual Penguin Network</b> — процессный split-tunnel VPN-роутер для Windows, macOS, Linux и Android.</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.ru.md"><b>Русский</b></a>
</p>

<p align="center">
  <a href="https://github.com/PavelLizunov/VPNRouter/releases/latest">
    <img src="https://img.shields.io/github/v/release/PavelLizunov/VPNRouter?include_prereleases&color=7C3AED" alt="Последний релиз"/>
  </a>
  <a href="https://github.com/PavelLizunov/VPNRouter/releases">
    <img src="https://img.shields.io/github/downloads/PavelLizunov/VPNRouter/total?color=22C55E" alt="Загрузки"/>
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/PavelLizunov/VPNRouter?color=2563EB" alt="Лицензия"/>
  </a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/platform-Win%20%7C%20macOS%20%7C%20Linux%20%7C%20Android-lightgrey" alt="Платформы"/>
  <img src="https://img.shields.io/badge/LOC-94k-blue" alt="94k LOC"/>
  <img src="https://img.shields.io/badge/тестов-765-success" alt="765 тестов"/>
</p>

---

## Установка (one-liner на всех трёх платформах)

<table>
<tr>
<td width="80" align="center">🐧<br><b>Linux</b></td>
<td>

```bash
curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh
```
Debian / Ubuntu / Mint / Pop / elementary. Добавляет подписанный apt-репо, ставит `vpnrouter`, включает passwordless VPN через POSIX capabilities. Обновление: `sudo apt upgrade`.
</td>
</tr>
<tr>
<td align="center">🍎<br><b>macOS</b></td>
<td>

```bash
brew install --cask pavellizunov/vpnrouter/vpnrouter
```
Apple Silicon. Авто-снимает Gatekeeper quarantine. При первом запуске однократно просит пароль для sudoers, дальше passwordless. Обновление: `brew upgrade --cask vpnrouter`.
</td>
</tr>
<tr>
<td align="center">🪟<br><b>Windows</b></td>
<td>

```powershell
iwr -useb https://vpn.ninitux.com/install.ps1 | iex
```
Windows 10/11 x64. Авто-поднимается через UAC. Регистрирует Start Menu + Add/Remove Programs. Обновление: запустить ту же команду повторно. Удалить: Settings → Приложения → VPNRouter.
</td>
</tr>
<tr>
<td align="center">🤖<br><b>Android</b></td>
<td>

```
Скачайте VPNRouter-v{version}-android.apk со страницы Releases
```
Android 6.0+ (API 23). Side-load через APK (Play Store пока нет). Live-preview QR сканер, magic 1-step paste подписки, минимум permissions (`CAMERA` + `INTERNET` + `VPN_SERVICE`). Авто-обновление через in-app banner.
</td>
</tr>
</table>

Предпочитаете установку вручную? См. [**Ручная установка**](#ручная-установка) ниже для ZIP / DMG / AppImage / deb / tar.gz.

---

## Что делает

Направляет трафик **выбранных приложений** через VLESS+Reality прокси (через [sing-box](https://github.com/SagerNet/sing-box) в TUN-режиме); всё остальное идёт напрямую к провайдеру. Это не full-tunnel VPN — это per-process роутер. Discord идёт через прокси, сайт банка остаётся напрямую. Никаких ручных proxy-настроек в каждом приложении.

### Кроссплатформенная основа

- **Split-tunnel маршрутизация** — выберите приложения из живого списка процессов; они пойдут через ваш прокси, всё остальное останется напрямую.
- **VLESS+Reality + кастомные конфиги** — используйте встроенную VLESS-настройку или принесите свой sing-box JSON (TUIC, Hysteria2, Shadowsocks). Per-process routing подмешивается в любом случае.
- **Подписки** — вставьте один или несколько subscription URL, серверы обновляются в единый пул автоматически.
- **Тестирование серверов** — в один клик TCP+TLS-проба любого сервера. Deep verification (реальный HTTP round-trip + 5 МБ bandwidth) для ваших серверов и пулов подписок.
- **Мастер настройки и диагностики (desktop)** — проверяет конфигурацию, TUN, DNS и доступность сети, умеет сбросить MTU к безопасному значению `1420`, сохраняет выбранный режим маршрутизации и предлагает отмену плюс экспорт обезличенной диагностики. Безопасный режим остаётся отдельным временным запуском.
- **Безопасное авто-обновление** — каждый релиз сопровождается `.sha256`-файлом; встроенный апдейтер проверяет хеш перед распаковкой, чтобы обрезанная загрузка не установилась молча.
- **Status dashboard + Arctic dark theme + RU/EN UI** — live-бейджи VPN / Zapret / TgProxy в хедере, кастомная Avalonia-тема, полностью переведённый интерфейс.

### Платформенные детали

- **Windows** — UAC-elevation; опциональный Windows Service для boot-time автозапуска, переживающий logoff пользователя.
- **macOS** — нативный Apple Silicon; одноразовая настройка sudoers из DMG даёт passwordless TUN после.
- **Linux** — POSIX capabilities (`cap_net_admin`, `cap_net_bind_service`) для passwordless TUN, применяются postinst-хуком `.deb` (`setcap`); session-автозапуск через `.desktop`-запись. systemd-сервиса / boot-time демона пока нет.

### Windows-only дополнения *(опционально)*

Это тонкие обёртки вокруг сторонних проектов — не часть ядра роутера и не работают на macOS / Linux. Пропустите, если не нужен DPI-обход или отдельный Telegram-роутинг.

- **DPI-обход (Zapret)** — интеграция [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube). Скачивается по запросу из вкладки Tools. Полезно, когда провайдер блокирует сайт через DPI, а полный прокси не нужен.
- **Telegram-прокси** — встроенный MTProto-прокси ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) для обхода конкретно Telegram.

## Каталог фич

**53 фичи** в 11 категориях. Полный справочник с flow-диаграммами + рейтингом сложности + service chains лежит в [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md) (auto-generated audit). Сводка:

| Категория | Фич | Сложность | Платформы |
|---|---:|---|---|
| **Core VPN** (Connect, hot-reload, multi-protocol, custom configs) | 10 | 4 HIGH · 4 MED · 2 LOW | Win · Mac · Linux · Android |
| **Подписки** (Add, refresh, test, aggregated pool) | 6 | 2 MED · 4 LOW | Все |
| **Free Configs** (агрегатор, deep verify, GeoIP) | 5 | 1 HIGH · 3 MED · 1 LOW | Все |
| **DPI Bypass / Zapret** (Flowseal integration, strategies) | 5 | 2 HIGH · 2 MED · 1 LOW | Win only |
| **Apps routing** (Include/Exclude, scan_patterns, child detection) | 4 | 1 MED · 3 LOW | Все |
| **Профили** (GitHub > Local > Built-in, merge) | 3 | 1 MED · 2 LOW | Все |
| **Custom rules** (IP/domain/regex, action priority) | 3 | 1 MED · 2 LOW | Все |
| **Обновления** (UpdateChecker, channels, self-repair tiers) | 4 | 1 HIGH · 2 MED · 1 LOW | Все |
| **UI/UX** (Simple/Advanced, theme, QR scan, paste-and-go) | 5 | 2 MED · 3 LOW | Все |
| **Приватность + безопасность** (Leak protection, F-A..F-E placeholder defense) | 4 | 1 HIGH · 3 MED | Все |
| **Platform infra** (Service install, ETW, Firewall, Homebrew/APT) | 4 | 1 HIGH · 1 MED · 2 LOW | platform-specific |

**Распределение сложности**: 21 LOW (40%) · 22 MED (41%) · 10 HIGH (19%).

### Бонус: вкладка Free Configs

Агрегатор публичных VLESS-конфигов — ~25 000 из 14 открытых источников, предварительно провалидированных (TCP+TLS + GeoIP) на сервере раз в 6 часов. Удобно попробовать приложение без своего VPN-сервера; не замена оплачиваемому или self-hosted endpoint'у.

## Скриншоты

*Главное окно — вкладки Manual / Subscribe / Network / Applications / Tools / Free.*

*(Скриншоты скоро.)*

## Ручная установка

One-liner'ы для всех трёх платформ — см. секцию [**Установка**](#установка-one-liner-на-всех-трёх-платформах) выше. Хочется поставить руками? Забирайте последний билд из [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest):

| Файл | Платформа | Что это |
|---|---|---|
| `VPNRouter-v{version}-win.zip` | 🪟 Windows | Полный установщик (первая установка) |
| `VPNRouter-update-v{version}-win.zip` | 🪟 Windows | Обновление только DLL (если уже на свежей версии) |
| `VPNRouter-*-win.zip.sha256` | 🪟 Windows | Компаньон-файл SHA256 — автоапдейтер проверяет хеш перед распаковкой (v2.15.8+) |
| `VPNRouter-v{version}-mac.dmg` | 🍎 macOS | Drag-install DMG (Apple Silicon) с `InstallGuide.html` для одноразовой настройки sudoers |
| `VPNRouter-v{version}-mac.zip` | 🍎 macOS | Сырой `.app`-бандл (для ручной установки) |
| `VPNRouter-v{version}-linux-amd64.deb` | 🐧 Linux | Пакет для Debian/Ubuntu (desktop entry + `setcap` для passwordless TUN; systemd-сервиса нет). Установка: `sudo dpkg -i <file>.deb` |
| `VPNRouter-v{version}-linux-x86_64.AppImage` | 🐧 Linux | Портативный single-file билд. `chmod +x`, запуск, установка не нужна |
| `VPNRouter-v{version}-linux.tar.gz` | 🐧 Linux | Сырой tarball (для ручной установки или упаковки в другие форматы) |
| `VPNRouter-v{version}-android.apk` | 🤖 Android | Подписанный APK, API 23+, arm64/arm/x64/x86 универсальный. Выходит в stable-релизах + на [`vpn.ninitux.com/android`](https://vpn.ninitux.com/android). Собирается без подписи локально (`build-android.ps1`) и **подписывается в CI** (`sign-android.yml`) — чистая CI-сборка заблокирована `NU1102` (.NET 10 убрал host Mono runtime pack для всех runner-ОС), поэтому сборка и подпись разделены. In-app апдейтер доставляет будущие APK. |
| `*.sha256` для каждого бинарника | All | SHA256-сайдкары рядом с каждым артефактом (Windows `*-win.zip` + `*-update-win.zip`, macOS `*-mac.dmg` + `*-mac.zip`, Linux `*.deb` + `*.AppImage` + `*.tar.gz`). Авто-апдейтер + CI integrity check проверяют hash перед распаковкой. Ручная проверка: `sha256sum -c <file>.sha256` на Linux или `Get-FileHash <file>` на Windows. |

Также обновляется автоматически каждые 6 часов:

| Файл | Что это |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Агрегированные ~25 000 публичных VLESS-конфигов + GeoIP-метаданные. Потребляется вкладкой Free Configs. |

Запускать `VPNRouter.App.exe` от имени Администратора на Windows (нужно для TUN-адаптера + ETW мониторинга процессов + Firewall-правил). На macOS следуйте инструкции `InstallGuide.html` внутри DMG для одноразовой настройки sudoers, чтобы TUN поднимался без ввода пароля каждый раз. На Linux `.deb` применяет `setcap cap_net_admin,cap_net_bind_service` к встроенному sing-box, чтобы TUN поднимался без root и без пароля (systemd-сервис не ставится); AppImage без песочницы использует системный `pkexec` с запросом пароля. AppImage, обёрнутый в bubblewrap или user namespace (включая NixOS `appimageTools.wrapType2`), не может получить право создать системный TUN-интерфейс, даже если `getcap` показывает capability файла. Используйте нативный пакет дистрибутива вне этой песочницы.

## Требования

- **Windows 10/11 x64** — права Администратора (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel пока не собирается. Нужна одноразовая настройка sudoers при первом запуске (с подсказкой)
- **Linux x86_64** — ядро 5.6+ (TUN/wireguard), `glibc` 2.31+. Протестировано на Ubuntu 22.04 / 24.04 и Debian 12. `iptables` или `nftables` для firewall-правил.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — включён в установщик
- Сервер VLESS+Reality, или используйте вкладку Free Configs с публичными серверами

## Сборка из исходников

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release-сборка + упаковка:

```powershell
# Windows (PowerShell) — производит full + update ZIP'ы + их .sha256
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.48.0"
```

```bash
# macOS DMG — запускается на любом Mac с .NET 10 SDK
./build-mac.sh 2.48.0
```

```bash
# Linux — .deb + .AppImage + .tar.gz через тот же GitHub Actions pipeline
# локально: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

**macOS (DMG)** и **Linux** (.deb/.AppImage/.tar.gz) собираются автоматически через GitHub Actions на каждый `v*` push тега — см. `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/publish-apt.yml` (APT-репозиторий), `.github/workflows/build-free-pool.yml` (обновляющийся Free Configs пул). **Windows** ZIP'ы собираются локально через `build.ps1 -Upload` и прикладываются к тому же релизу (не в CI). **Android** APK собирается без подписи локально через `build-android.ps1` и **подписывается в CI** через `.github/workflows/sign-android.yml` (чистая CI-сборка заблокирована `NU1102` — .NET 10 убрал host Mono runtime pack — поэтому сборка и подпись разделены). Актуальная матрица сборки/платформ — [`CURRENT_STATE.md`](CURRENT_STATE.md).

## Архитектура

```
VPNRouter.sln (~94k LOC C# в 233 файлах / 7 проектах)
├── VPNRouter.Core                  — 32k LOC · 97 файлов — сервисы, модели, интерфейсы (zero UI deps)
├── VPNRouter.App                   — 17k LOC · 42 файла  — Avalonia desktop UI
├── VPNRouter.Android               — 21k LOC · 24 файла  — Mono.Android + Avalonia.Android
├── VPNRouter.CLI                   —  1k LOC · 12 файлов — Spectre.Console TUI
├── VPNRouter.Service               —  1k LOC · 3 файла   — Windows BackgroundService wrapper
├── VPNRouter.Tools/PoolAggregator  — CI-утилита, собирающая Free Configs pool.json
└── VPNRouter.Tests                 — 19k LOC · 55 файлов — 765 xUnit тестов + headless Avalonia
```

### Layering

- **`VPNRouter.Core`** — единственный источник истины. Нет ни одного `Avalonia.*`, `System.Windows.*`, `Mono.Android.*` reference. Платформенный код только через `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID`.
- **Android** не `ProjectReference` Core — source-link через `<Compile Include="..\VPNRouter.Core\**\*.cs">` в csproj (держит Android restore отдельно от desktop-графа net10.0).
- **Free Configs `pool.json`** строится server-side каждые 6 часов через `VPNRouter.Tools/PoolAggregator` в GitHub Actions → выкладывается на rolling-release `free-pool-latest`. Клиенты подтягивают + кешируют.

### Best-practice заметки

- **Bilingual UI** — все строки в `VPNRouter.Core/Localization/Strings.cs` (`Ru ? "..." : "..."`). App/Android — pass-through wrapper'ы, никаких дублей.
- **Async hygiene** — 0 `async void` в Core; UI-handler'ы стандартным `async void EventHandler` pattern; нет `.Result` blocking calls кроме `VpnEngine.cs:461` (на refactor).
- **Cross-cutting**: каждый сервис принимает `ILogger?` (Serilog) для diagnostic-trace в `vpnrouter*.log`.
- **Нет телеметрии**. Никакой аналитики, error reporter'ов, ping-home. UpdateChecker читает только публичный GitHub Releases API.

### Ключевые сервисы

Core-сервисы живут в `VPNRouter.Core/Services/` — `VpnEngine` (VPN lifecycle), `SingBoxManager` (sing-box process), `HealthMonitor` (auto-restart + debounce), `ProcessScanner` (process→name resolution), `ConfigGenerator` (sing-box 1.13 JSON), `FirewallManager` (Windows netsh), `EtwProcessMonitor` (real-time process events), `LeakProtection` (config invariant validator), `PlaceholderGuard` (v2.32.3 — фильтр known-bad credentials), плюс подсистемы для Zapret, Telegram proxy, подписок, free configs.

См. [`CLAUDE.md`](CLAUDE.md) для глубокого тура, [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md) для полного feature-flow справочника, и [`plans/v3.0-refactor-roadmap.md`](plans/v3.0-refactor-roadmap.md) для v3.0 modernization плана.

## Как это работает (высокий уровень)

1. Загружаем профиль → резолвим имена процессов, которые пойдут через VPN
2. Генерируем sing-box JSON-конфиг с нужным TUN-inbound, VLESS+Reality outbound и `process_name`-route-правилами
3. Запускаем sing-box в TUN-режиме (создаётся виртуальный адаптер)
4. ОС направляет весь трафик через адаптер; sing-box разделяет на основе совпадения имени процесса
5. ETW следит за новыми процессами → hot-reload конфига через Clash API (без реконнекта)
6. При crash — firewall-правила блокируют перечисленные процессы пока sing-box не вернётся (leak protection)

## Приватность и доверие

Это VPN-клиент — перед доверием следует проверить код.

- **Никакой телеметрии.** Ни аналитики, ни пингов домой, ни автоотчётов о багах. Автоапдейтер только читает публичное GitHub Releases API.
- **Никаких утечек credentials.** Credentials (UUID, Reality ключи) живут в `%ProgramData%\VPNRouter\config.yaml` на диске, никуда не отправляются кроме локального sing-box процесса.
- **Воспроизводимо.** Собирайте из исходников командами выше. Сравните хеш бинарника с вашим билдом для проверки.
- **Открытая лицензия.** GPL-3.0 — любой форк, распространяющий бинарник, должен также публиковать исходники.

Нашли security-issue? Сообщите **приватно** — см. [`SECURITY.md`](SECURITY.md). Не открывайте публичный issue по security-проблемам.

## Благодарности

Стоим на плечах гигантов:

- [sing-box](https://github.com/SagerNet/sing-box) — универсальная proxy-платформа (GPL-3.0)
- [Avalonia UI](https://avaloniaui.net/) — кроссплатформенный XAML-фреймворк (MIT)
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) — стратегии DPI-обхода (MIT)
- [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — ядро MTProto-прокси (MIT)
- [bol-van/zapret](https://github.com/bol-van/zapret) — оригинальный DPI-bypass-движок (MIT)
- [Serilog](https://serilog.net/) · [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) · [YamlDotNet](https://github.com/aaubry/YamlDotNet)

Публичные VLESS config-агрегаторы, используемые вкладкой Free Configs (14 источников):
[zieng2/wl](https://github.com/zieng2/wl) · [EtoNeYaProject](https://github.com/EtoNeYaProject/etoneyaproject.github.io) · [igareck/vpn-configs-for-russia](https://github.com/igareck/vpn-configs-for-russia) · [CidVpn](https://github.com/CidVpn/cid-vpn-config) · [ByeWhiteLists2](https://github.com/ByeWhiteLists/ByeWhiteLists2) · [nowmeow.pw](https://nowmeow.pw) · [sevcator/5ubscrpt10n](https://github.com/sevcator/5ubscrpt10n) · [ebrasha/free-v2ray-public-list](https://github.com/ebrasha/free-v2ray-public-list) · [barry-far/V2ray-config](https://github.com/barry-far/V2ray-config) · [kort0881/vpn-vless-configs-russia](https://github.com/kort0881/vpn-vless-configs-russia) · [Epodonios/v2ray-configs](https://github.com/Epodonios/v2ray-configs) · [MatinGhanbari/v2ray-configs](https://github.com/MatinGhanbari/v2ray-configs) · [V2RayRoot/V2RayConfig](https://github.com/V2RayRoot/V2RayConfig) · [etoneya.a9fm.site зеркало](https://etoneya.a9fm.site)

GeoIP-обогащение для серверного pool-агрегатора: [ip-api.com](https://ip-api.com) (бесплатный тариф, batch endpoint, API-ключ не требуется).

## Лицензия

[GPL-3.0-or-later](LICENSE) © 2026 Pavel Lizunov

Форки, распространяющие бинарники, должны публиковать свой исходный код под той же лицензией.
