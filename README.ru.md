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
    <img src="https://img.shields.io/github/v/release/PavelLizunov/VPNRouter?color=7C3AED" alt="Последний релиз"/>
  </a>
  <a href="https://github.com/PavelLizunov/VPNRouter/releases">
    <img src="https://img.shields.io/github/downloads/PavelLizunov/VPNRouter/total?color=22C55E" alt="Загрузки"/>
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/PavelLizunov/VPNRouter?color=2563EB" alt="Лицензия"/>
  </a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/platform-Win%20%7C%20macOS%20%7C%20Linux%20%7C%20Android-lightgrey" alt="Платформы"/>
</p>

---

## Установка

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
Скачайте VPNRouter-v{version}-android-arm64.apk со страницы Releases
```
Android 6.0+ (API 23), ARM64. Установка APK вне Play Store. Поддерживаются сканирование QR, вставка подписки и предложение обновления в приложении. Разрешения нужны для VPN, состояния сети, уведомлений, списка приложений, установки APK, камеры и управления питанием; полный список — в [Android manifest](VPNRouter.Android/AndroidManifest.xml).
</td>
</tr>
</table>

Предпочитаете установку вручную? См. [**Ручная установка**](#ручная-установка) ниже для ZIP / DMG / AppImage / deb / tar.gz.

---

## Что делает

Направляет трафик приложений через прокси с помощью [sing-box](https://github.com/SagerNet/sing-box) в TUN-режиме. В стандартном режиме split/include выбранные приложения используют прокси, остальной трафик идёт напрямую с учётом настроенных правил. В split/exclude выбранные приложения остаются на прямом маршруте; full-tunnel направляет трафик через прокси, кроме настроенных исключений. Задавать прокси отдельно в каждом приложении не требуется.

### Кроссплатформенная основа

- **Split-tunnel маршрутизация** — выберите приложения из списка процессов и укажите, включить их в прокси-маршрут или исключить из него.
- **VLESS+Reality + кастомные конфиги** — используйте встроенную VLESS-настройку или принесите свой sing-box JSON (TUIC, Hysteria2, Shadowsocks). Per-process routing подмешивается в любом случае.
- **Подписки** — вставьте один или несколько subscription URL, серверы обновляются в единый пул автоматически. Desktop-команды добавления подписок и пользовательских источников бесплатных конфигураций принимают только абсолютные HTTP(S) URL; частные и loopback-адреса HTTP(S) остаются разрешены. Capability-aware провайдеры могут публиковать VLESS-цель, которая подключается только через связанный входной сервер; отсутствие метаданных цепочки или входного сервера завершается fail-closed без прямого подключения к цели.
- **Настройка DNS** — сгенерированные DNS-маршруты зависят от режима маршрутизации, strict-DNS и пользовательских правил. Встроенный прямой DoH-resolver и добавляемый fallback используют Google `8.8.8.8`; настроенные LAN-суффиксы могут использовать системный resolver. Пользовательские конфигурации проходят подстановку DNS/маршрутов и валидацию: проверяйте сгенерированный конфиг, не предполагая, что все исходные DNS-настройки сохраняются без изменений.
- **Тестирование серверов** — в один клик TCP+TLS-проба любого сервера. Deep verification (реальный HTTP round-trip + 5 МБ bandwidth) для ваших серверов и пулов подписок.
- **Мастер настройки и диагностики (desktop)** — проверяет конфигурацию, TUN, DNS и доступность сети, умеет сбросить MTU к безопасному значению `1420`, сохраняет выбранный режим маршрутизации и предлагает отмену плюс экспорт обезличенной диагностики. Безопасный режим остаётся отдельным временным запуском.
- **Безопасный откат** — desktop-приложение показывает до трёх предыдущих стабильных версий только при наличии `.sha256`-файла, проверяет выбранный архив перед установкой и требует отдельного подтверждения. Перед понижением версии сохраняется копия `config.yaml`.
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

Исторический каталог фич (аудит baseline v2.32.3 от 2026-05-17): **53 фичи** в 11 категориях. Полный справочник с flow-диаграммами + рейтингом сложности + service chains лежит в [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md). Сводка:

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

Вкладка Free Configs собирает публичные VLESS-серверы. Серверное задание запускается по расписанию раз в шесть часов; размер пула, доступность и результаты проверки меняются. Серверы принадлежат третьим лицам: успешная проверка соединения не подтверждает надёжность оператора.

## Скриншоты

Эти скриншоты генерируются теми же headless UI-тестами, которые используются
для визуальной регрессии, поэтому в них нет реальных учётных данных.

<p align="center">
  <img src="VPNRouter.Tests/screenshots/page-simple.png" width="49%" alt="Простой экран подключения VPNRouter"/>
  <img src="VPNRouter.Tests/screenshots/page-applications.png" width="49%" alt="Маршрутизация приложений в VPNRouter"/>
</p>
<p align="center">
  <img src="VPNRouter.Tests/screenshots/page-tools.png" width="98%" alt="Инструменты обхода DPI в VPNRouter"/>
</p>

## Ручная установка

Команды установки desktop-версий и инструкции для Android APK приведены в разделе [Установка](#установка). Последняя стабильная сборка доступна в [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest), rolling-кандидаты — в [общем списке релизов](https://github.com/PavelLizunov/VPNRouter/releases).

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
| `VPNRouter-v{version}-android-arm64.apk` | 🤖 Android | Подписанный ARM64 APK, API 23+. Собирается и подписывается в `build-android.yml` для каждого release-тега, затем публикуется в Releases и на [`vpn.ninitux.com/android`](https://vpn.ninitux.com/android). In-app апдейтер доставляет будущие APK. |
| `*.sha256` для каждого бинарника | All | SHA256-сайдкары рядом с каждым артефактом (Windows `*-win.zip` + `*-update-win.zip`, macOS `*-mac.dmg` + `*-mac.zip`, Linux `*.deb` + `*.AppImage` + `*.tar.gz`). Авто-апдейтер + CI integrity check проверяют hash перед распаковкой. Сравните результат `sha256sum <file>` на Linux, `shasum -a 256 <file>` на macOS или `Get-FileHash -Algorithm SHA256 <file>` на Windows с 64-символьным хешем из сайдкара. Часть сайдкаров содержит только хеш и не подходит для прямого вызова `sha256sum -c`. |

При успешном выполнении серверное задание публикует отдельный артефакт:

| Файл | Что это |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Публичные VLESS-конфигурации и GeoIP-метаданные; размер пула меняется. Потребляется вкладкой Free Configs. |

Запускать `VPNRouter.App.exe` от имени Администратора на Windows (нужно для TUN-адаптера + ETW мониторинга процессов + Firewall-правил). На macOS следуйте инструкции `InstallGuide.html` внутри DMG для одноразовой настройки sudoers, чтобы TUN поднимался без ввода пароля каждый раз. На Linux `.deb` применяет `setcap cap_net_admin,cap_net_bind_service` к встроенному sing-box, чтобы TUN поднимался без root и без пароля (systemd-сервис не ставится); AppImage без песочницы использует системный `pkexec` с запросом пароля. AppImage, обёрнутый в bubblewrap или user namespace (включая NixOS `appimageTools.wrapType2`), не может получить право создать системный TUN-интерфейс, даже если `getcap` показывает capability файла. Используйте нативный пакет дистрибутива вне этой песочницы.

## Требования

- **Windows 10/11 x64** — права Администратора (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel пока не собирается. Нужна одноразовая настройка sudoers при первом запуске (с подсказкой)
- **Linux x86_64** — ядро 5.6+ (TUN/wireguard), `glibc` 2.31+. Протестировано на Ubuntu 22.04 / 24.04 и Debian 12. `iptables` или `nftables` для firewall-правил.
- **Android 6.0+** (API 23+), ARM64 — использует `VpnService`, root не требуется. Камера запрашивается только для сканирования QR-кода.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — включён в установщик
- Сервер VLESS+Reality, или используйте вкладку Free Configs с публичными серверами

## Сборка из исходников

Установите .NET SDK из [`global.json`](global.json) (10.0.301, с разрешённым переходом на новые патчи). Стандартная сборка solution не собирает Android-приложение. Для Android также нужны workload, Android SDK, JDK и локальные нативные библиотеки; см. [инструкцию сборки Android](VPNRouter.Android/AGENTS.md).

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release-сборка + упаковка:

```powershell
# Windows (PowerShell) — производит full + update ZIP'ы + их .sha256
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.50.0-r1"
```

```bash
# macOS DMG — запускается на любом Mac с .NET 10 SDK
./build-mac.sh 2.50.0-r1
```

```bash
# Linux — .deb + .AppImage + .tar.gz через тот же GitHub Actions pipeline
# локально: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

**macOS (DMG)**, **Linux** (.deb/.AppImage/.tar.gz) и подписанный **Android ARM64 APK** собираются автоматически через GitHub Actions на каждый `v*` push тега — см. `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/build-android.yml`, `.github/workflows/publish-apt.yml` (APT-репозиторий), `.github/workflows/build-free-pool.yml` (обновляющийся Free Configs пул). Загрузка релизных файлов требует существующего draft и точного соответствия тега/SHA; ручные сборки запускаются с `--ref vVERSION`. **Windows**-команда `build.ps1 -Upload` только загружает неподписанные файлы в draft и отказывается работать при полной или частичной настройке SignPath. При настроенной подписи используется `Sign Windows (SignPath)`. Публикация — отдельное действие владельца после сборки, тестов и проверки ровно 16 файлов; кандидаты остаются prerelease, а не Latest. См. [процедуру выпуска](.dsh/skills/ship-rolling-candidate/SKILL.md). Актуальная матрица сборки/платформ — [`CURRENT_STATE.md`](CURRENT_STATE.md).

## Архитектура

```
VPNRouter.sln
├── VPNRouter.Core                  — сервисы, модели и платформенные адаптеры
├── VPNRouter.App                   — desktop-интерфейс Avalonia
├── VPNRouter.Android               — Android-приложение (собирается отдельно)
├── VPNRouter.CLI                   — инструменты командной строки
├── VPNRouter.Service               — служба Windows
├── VPNRouter.Tools/PoolAggregator  — генератор пула Free Configs
└── VPNRouter.Tests                 — xUnit и headless-тесты Avalonia
```

### Layering

- **`VPNRouter.Core`** — единственный источник истины. Нет ни одного `Avalonia.*`, `System.Windows.*`, `Mono.Android.*` reference. Платформенный код только через `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID`.
- **Android** не `ProjectReference` Core — source-link через `<Compile Include="..\VPNRouter.Core\**\*.cs">` в csproj (держит Android restore отдельно от desktop-графа net10.0).
- **Free Configs `pool.json`** строится server-side каждые 6 часов через `VPNRouter.Tools/PoolAggregator` в GitHub Actions → выкладывается на rolling-release `free-pool-latest`. Клиенты подтягивают + кешируют.

### Best-practice заметки

- **Bilingual UI** — все строки в `VPNRouter.Core/Localization/Strings.cs` (`Ru ? "..." : "..."`). App/Android — pass-through wrapper'ы, никаких дублей.
- **Async hygiene** — 0 `async void` в Core; UI-handler'ы стандартным `async void EventHandler` pattern; везде в Core используется async/await без блокирующих `.Result` на асинхронных путях.
- **Диагностика и логирование** — сервисы логируют через Serilog (внедрение `ILogger?` или статический логер) для детальной трассировки в `vpnrouter*.log`.
- **Диагностика** — локальные логи и отчёты о сбоях помогают разбирать ошибки; перед их передачей см. [Приватность и доверие](#приватность-и-доверие).

### Ключевые сервисы

Core-сервисы живут в `VPNRouter.Core/Services/` — `VpnEngine` (VPN lifecycle), `SingBoxManager` (sing-box process), `HealthMonitor` (auto-restart + debounce), `ProcessScanner` (process→name resolution), `ConfigGenerator` (sing-box 1.13 JSON), `FirewallManager` (Windows netsh), `EtwProcessMonitor` (real-time process events), `LeakProtection` (config invariant validator), `PlaceholderGuard` (v2.32.3 — фильтр known-bad credentials), плюс подсистемы для Zapret, Telegram proxy, подписок, free configs.

> **Примечание**: Desktop-сборки (Windows, macOS, Linux) по умолчанию используют официальные релизные артефакты `PavelLizunov/sing-box-vpnctl` `v1.14.0-vpnctl.3`. Android по решению владельца сохраняет legacy-тулинг sing-box 1.13.10 (`libbox.aar`, Android 6.0+ / API 23+), а не мигрирует целиком на vpnctl.

См. [`CURRENT_STATE.md`](CURRENT_STATE.md) для актуальной матрицы платформ и
сборок, [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md)
для feature-flow справочника и
[`plans/v3.0-refactor-roadmap.md`](plans/v3.0-refactor-roadmap.md) для
исторического baseline и дальнейших задач v3.0.

## Как это работает (высокий уровень)

1. Загружаем профиль → резолвим имена процессов, которые пойдут через VPN
2. Генерируем sing-box JSON-конфиг с нужным TUN-inbound, VLESS+Reality outbound и `process_name`-route-правилами
3. Запускаем sing-box в TUN-режиме (создаётся виртуальный адаптер)
4. Трафик поступает в виртуальный адаптер; sing-box разделяет его на основе совпадения имени процесса
5. На Windows ETW отслеживает запуск новых процессов (сканирование процессов на macOS/Linux) → hot-reload конфига через Clash API (без реконнекта)
6. При сбое действие включённой firewall-защиты зависит от платформы, режима маршрутизации и прав. В Linux/macOS kill-switch поддерживает только full-tunnel и остаётся отключённым в split-режиме; рассчитывать на блокировку отдельных процессов при сбое там нельзя.

## Приватность и доверие

Это VPN-клиент — перед доверием следует проверить код.

- **Локальная диагностика.** Отчёты о сбоях записываются в каталог данных приложения с попыткой скрыть распознаваемые форматы секретов перед записью. Crash reporter не отправляет их автоматически. Перед передачей диагностики проверьте её содержимое.
- **Сетевые обращения.** Обновления, подписки, публичные источники конфигураций и проверки доступности обращаются к настроенным сервисам. Выбранный прокси-сервер обрабатывает направленный через него трафик; выбирайте провайдера, которому доверяете.
- **Учётные данные.** Настройки и сгенерированная конфигурация sing-box содержат параметры доступа. Защищайте каталог данных приложения и не публикуйте конфигурации или необработанные логи.
- **Проверка файлов.** SHA256-сайдкары подтверждают соответствие загрузки указанному хешу, но не удостоверяют издателя независимо. Сборка из исходников поддерживается; побайтовое совпадение с релизными бинарниками не установлено.
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
