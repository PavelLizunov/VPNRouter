# Архитектура и принцип работы

Часть [README VPNRouter](../../../README.ru.md).

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
- **Диагностика** — локальные логи и отчёты о сбоях помогают разбирать ошибки; перед их передачей см. [Приватность и доверие](privacy-and-trust.md#приватность-и-доверие).

### Ключевые сервисы

Core-сервисы живут в `VPNRouter.Core/Services/` — `VpnEngine` (VPN lifecycle), `SingBoxManager` (sing-box process), `HealthMonitor` (auto-restart + debounce), `ProcessScanner` (process→name resolution), `ConfigGenerator` (sing-box JSON), `FirewallManager` (Windows netsh), `EtwProcessMonitor` (real-time process events), `LeakProtection` (config invariant validator), `PlaceholderDefense` (фильтр known-bad credentials), плюс подсистемы для Zapret, Telegram proxy, подписок, free configs.

> **Примечание**: Desktop-сборки (Windows, macOS, Linux) по умолчанию используют официальные релизные артефакты `PavelLizunov/sing-box-vpnctl` `v1.14.0-vpnctl.5`. Android по решению владельца сохраняет legacy-тулинг sing-box 1.13.10 (`libbox.aar`, Android 6.0+ / API 23+), а не мигрирует целиком на vpnctl.

См. [`CURRENT_STATE.md`](../../../CURRENT_STATE.md) для актуальной матрицы платформ и
сборок. Исторические планы, включая каталог фич и roadmap v3.0, вынесены из
дерева; их можно прочитать через `git show 6491be4c:plans/<file>`.

## Как это работает (высокий уровень)

1. Загружаем профиль → резолвим имена процессов, которые пойдут через VPN
2. Генерируем sing-box JSON-конфиг с нужным TUN-inbound, VLESS+Reality outbound и `process_name`-route-правилами
3. Запускаем sing-box в TUN-режиме (создаётся виртуальный адаптер)
4. Трафик поступает в виртуальный адаптер; sing-box разделяет его на основе совпадения имени процесса
5. На Windows ETW отслеживает запуск новых процессов (сканирование процессов на macOS/Linux) → hot-reload конфига через Clash API (без реконнекта)
6. При сбое действие включённой firewall-защиты зависит от платформы, режима маршрутизации и прав. В Linux/macOS kill-switch поддерживает только full-tunnel и остаётся отключённым в split-режиме; рассчитывать на блокировку отдельных процессов при сбое там нельзя.
