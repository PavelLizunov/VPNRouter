<p align="center">
  <img src="VPNRouter.App/Assets/penguin_logo.png" width="96" alt="VPNRouter logo"/>
</p>

<h1 align="center">VPNRouter</h1>
<p align="center"><b>Virtual Penguin Network</b> — процессный split-tunnel VPN-роутер для Windows, macOS и Linux.</p>

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
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey" alt="Платформы"/>
</p>

---

## Что делает

Направляет трафик **выбранных приложений** через VLESS+Reality прокси (через [sing-box](https://github.com/SagerNet/sing-box) в TUN-режиме), всё остальное идёт напрямую к провайдеру. Это не full-tunnel VPN — это per-process роутер. Discord идёт через прокси, сайт банка остаётся напрямую. Никаких ручных proxy-настроек в каждом приложении.

Дополнительные возможности поверх базового роутера:

- **Вкладка Free Configs** — агрегирует **25 000+ публичных VLESS-конфигов** из 14 открытых источников, проверяет каждый сервер через TCP+TLS-пробы и подтверждает реальную связность через временный sing-box с HTTP round-trip. Серверный агрегатор (GitHub Actions cron) пересчитывает GeoIP каждые 6 часов. Включает пресеты (Gaming / Streaming / Chat / Best effort) с целями по латенси и пропускной способности, опцию skip-RU, пользовательские источники подписок, диалог с security-предупреждением при первом подключении.
- **Тестирование Servers & Subscriptions** (v2.15+) — те же TCP+TLS-пробы и deep-проверка (поднять временный sing-box, HTTP-трейс через SOCKS, тест пропускной на 5 МБ) теперь доступны для ваших VLESS-серверов и пула подписок, не только на вкладке Free Configs.
- **Runtime status dashboard** (v2.15+) — три цветных бейджа в заголовке показывают live-состояние VPN / Zapret / TgProxy, обновляются каждые 2 с через process + port probing. Клик по бейджу → переход на вкладку, которая им управляет.
- **Resilient autostart** (v2.15+) — Windows Service объявляет boot-зависимости на `Tcpip/Dnscache/Dhcp` и использует exponential backoff (5/10/20/40 с) при запуске VPN / Zapret / TgProxy. Транзиентные сбои на холодном boot больше не оставляют компоненты в остановленном состоянии.
- **Обновления с проверкой чек-сумм** (v2.15+) — автоапдейтер скачивает `.sha256`-компаньон к каждому ZIP и прерывает работу при несовпадении хеша, так что обрезанная или битая загрузка не сломает установку молча.
- **Arctic design system** (v2.16+) — палитра семантических токенов (surfaces, text, borders, state colors, отступы и радиусы на 4 px / 11 px grid), кастомная dark-тема с авто-переключением через Avalonia `ThemeDictionaries`, RGB-инвертированный penguin-лого для dark mode. См. `plans/vpnrouter-v2.16-arctic-theme.md`.
- **Service-App coordination hardening** (v2.27+) — установка Windows-службы из UI пока работает VPN больше не рвёт соединение (TunLock-проверка в service orphan-sing-box sweep). Advanced autostart-панель переработана в две семантические секции — «На старте Windows (до логина)» и «При входе пользователя» — сгруппировано по *когда* срабатывает автозапуск, а не *каким* Windows-механизмом. Статус-строка показывает `● Running — PID 1234`, indeterminate progress bar во время `sc create`/`sc start`. См. `plans/vpnrouter-v2.27-service-ux.md`.
- **Core stability: upstream sing-box 1.13.10 + TUN auto-detect** (v2.27.2) — все три платформы теперь бандлят официальный upstream sing-box 1.13.10 (был custom ребилд 1.13.7), подхватывает fix `process_name`-matching, регрессировавший в 1.13.9, плюс 5 других point-release багфиксов. Runtime re-apply авто-детектит структурные изменения TUN-слоя (имя интерфейса, IPv4-подсеть, MTU, auto/strict route, IPv6-toggle, exclude-лист) и эскалирует до полного рестарта процесса — Clash API hot-reload не может передернуть kernel-level adapter state, поэтому раньше такие изменения «успешно» применялись пока живой адаптер сохранял старые значения. Покрыто 12 новыми xUnit-регрессионными тестами и live-verified `tools/live-test-r1.ps1`. См. `plans/vpnrouter-core-stability-audit.md`.
- **DPI-обход (Zapret)** — интегрирован [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) для платформ, заблокированных через DPI без необходимости прокси.
- **Telegram-прокси** — встроенный MTProto-прокси ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) для обхода только в Telegram.
- **Custom sing-box конфиги** — приносите свой JSON (TUIC, Hysteria2, Shadowsocks), per-process routing сохраняется.
- **Подписки** — несколько VLESS subscription URL с авто-обновлением, единый серверный пул.
- **Режим Windows Service** — работает с boot, переживает logoff пользователя.
- **Поддержка macOS** — полный Avalonia UI + sing-box TUN на Apple Silicon. Автоматические сборки DMG через GitHub Actions.
- **Двуязычный UI** — полный RU/EN перевод, переключается в runtime.

## Скриншоты

*Главное окно — вкладки Manual / Subscribe / Network / Applications / Tools / Free.*

*(Скриншоты скоро.)*

## Скачать

Забирайте последний билд из [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest):

| Файл | Платформа | Что это |
|---|---|---|
| `VPNRouter-v{version}-win.zip` | 🪟 Windows | Полный установщик (первая установка) |
| `VPNRouter-update-v{version}-win.zip` | 🪟 Windows | Обновление только DLL (если уже на свежей версии) |
| `VPNRouter-*-win.zip.sha256` | 🪟 Windows | Компаньон-файл SHA256 — автоапдейтер проверяет хеш перед распаковкой (v2.15.8+) |
| `VPNRouter-v{version}-mac.dmg` | 🍎 macOS | Drag-install DMG (Apple Silicon) с `InstallGuide.html` для одноразовой настройки sudoers |
| `VPNRouter-v{version}-mac.zip` | 🍎 macOS | Сырой `.app`-бандл (для ручной установки) |
| `VPNRouter-v{version}-linux-amd64.deb` | 🐧 Linux | Пакет для Debian/Ubuntu (systemd-сервис + desktop entry). Установка: `sudo dpkg -i <file>.deb` |
| `VPNRouter-v{version}-linux-x86_64.AppImage` | 🐧 Linux | Портативный single-file билд. `chmod +x`, запуск, установка не нужна |
| `VPNRouter-v{version}-linux.tar.gz` | 🐧 Linux | Сырой tarball (для ручной установки или упаковки в другие форматы) |

Также обновляется автоматически каждые 6 часов:

| Файл | Что это |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Агрегированные ~25 000 публичных VLESS-конфигов + GeoIP-метаданные. Потребляется вкладкой Free Configs. |

Запускать `VPNRouter.App.exe` от имени Администратора на Windows (нужно для TUN-адаптера + ETW мониторинга процессов + Firewall-правил). На macOS следуйте инструкции `InstallGuide.html` внутри DMG для одноразовой настройки sudoers, чтобы TUN поднимался без ввода пароля каждый раз. На Linux `.deb` ставит systemd-сервис, который берёт на себя root-права; `AppImage` требует `sudo` при первом запуске для TUN/NET_ADMIN capabilities.

## Требования

- **Windows 10/11 x64** — права Администратора (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel пока не собирается. Нужна одноразовая настройка sudoers при первом запуске (с подсказкой)
- **Linux x86_64** — ядро 5.6+ (TUN/wireguard), `glibc` 2.31+. Протестировано на Ubuntu 22.04 / 24.04 и Debian 12. `iptables` или `nftables` для firewall-правил.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — включён в установщик
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
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.27.2"
```

```bash
# macOS DMG — запускается на любом Mac с .NET 8 SDK
./build-mac.sh 2.27.2
```

```bash
# Linux — .deb + .AppImage + .tar.gz через тот же GitHub Actions pipeline
# локально: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

Все три платформы (Win ZIP, Mac DMG, Linux .deb/.AppImage/.tar.gz) собираются автоматически через GitHub Actions на каждый `v*` push тега — см. `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/publish-apt.yml` (APT-репозиторий), `.github/workflows/build-free-pool.yml` (обновляющийся Free Configs пул).

## Архитектура

```
VPNRouter.sln
├── VPNRouter.Core                  — сервисы, модели, интерфейсы (кроссплатформенно)
├── VPNRouter.App                   — Avalonia UI (кроссплатформенный desktop)
├── VPNRouter.CLI                   — CLI-утилита (Spectre.Console)
├── VPNRouter.Service               — Windows Service wrapper
├── VPNRouter.Tools/PoolAggregator  — CI-утилита, собирающая Free Configs pool.json
└── VPNRouter.Tests                 — xUnit
```

Core-сервисы живут в `VPNRouter.Core/Services/` — `VpnEngine`, `SingBoxManager`, `HealthMonitor`, `ProcessScanner`, `ConfigGenerator`, `FirewallManager`, `EtwProcessMonitor`, `LeakProtection`, плюс подсистемы для Zapret, Telegram-прокси, подписок, free configs и т.д. См. [`CLAUDE.md`](CLAUDE.md) для глубокого погружения.

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

Нашли security-issue? Откройте issue или напишите автору (см. профиль).

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
