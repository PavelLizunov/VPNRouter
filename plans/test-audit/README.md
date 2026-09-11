# Полный аудит тестового набора VPNRouter (2 847 тестов / 338 файлов)

Дата завершения: **2026-09-12**  
Исполнитель: **Рой Gemini Swarm (`gemini-3.8-flash-high`)**  
Методология: **Анализ каждого тестового метода по 5 контрольным вопросам** (Контракт, Ценность vs Тавтология, Актуальность, Хрупкость, Итоговый вердикт).

---

## 1. Итоговая статистика по всему репозиторию

| Категория вердикта | Количество тестов | Доля от общего числа | Описание категории |
|---|---:|---:|---|
| **KEEP** (100% Core value) | **2 117** | **74.4%** | Критически важные тесты бизнес-логики, защиты от утечек (DNS/IP), генерации конфигов sing-box, бинарного протокола драйвера ядра, миграций настроек и CI пайплайнов. |
| **MERGE / SIMPLIFY** | **491** | **17.2%** | Высокоценные проверки, страдающие от избыточной фрагментации (по 1 ассерту на метод для одного и того же вызова). Объединение в `[Theory]` сократит объём кода без потери охвата. |
| **DROP** (Кандидаты на удаление) | **239** | **8.4%** | Устаревшие файлы-свидетели, «source guards» (grep исходников вместо запуска), тавтологии компилятора C# Record и неизолированные пустые циклы. |
| **ИТОГО** | **2 847** | **100.0%** | **Полный 100% охват всех тестов в проекте.** |

---

## 2. 22 файла, рекомендованных к полному удалению (DROP FILE)

Эти 22 файла содержат **0 уникальной поведенческой ценности** и либо целиком дублируются целевыми тестами, либо не запускают код вовсе (парсят текст исходников регулярными выражениями):

1. `AutoFailoverRestartSelfCancelTests.cs` (3 теста) — искусственная лямбда, дублирует `AutoFailoverUserIntentGuardTests`.
2. `BypassRussianTrafficAbTest.cs` (1 тест) — отладочный принтер в консоль, не является автоматическим тестом.
3. `ConnStatsVisibilityThrottleTests.cs` (1 тест) — текстовый grep строки `if (!IsVisible) return` в исходнике ViewModel.
4. `DnsTunnelUiTests.cs` (3 теста) — 100% дословные дубликаты тестов из `SimpleInputDetectorTests` и `DnsTunnelLabelTests`.
5. `HealthMonitorTimerRaceTests.cs` (1 тест) — проверяет `Interlocked.Exchange` в BCL .NET на 1600 таймерах. 0 строк кода VPNRouter.
6. `NightBaselineEndpointTests.cs` (2 теста) — временный witness ночного спринта, дословно дублирует Linux и Mac файрвол сьюты.
7. `NightBaselineOwnershipCharacterizationTests.cs` (1 тест) — дубликат `TgProxyOwnershipCharacterizationTests`.
8. `NightBaselineStatsTests.cs` (1 тест) — устаревший тест сброса статистики, полностью перекрыт `NightConnStatsSessionTests`.
9. `NightBaselineStopCharacterizationTests.cs` (1 тест) — перекрыт `NightWindowsStopCharacterizationTests`.
10. `NightBaselineTelemetryWiringTests.cs` (1 тест) — текстовый grep по `VpnEngine.cs`, покрыт в `ClashLogStreamTests`.
11. `NightTypedReadinessTests.cs` (6 тестов) — 100% текстовый grep исходников, поведение покрыто в `MvmTwoPhaseStartTimerTests`.
12. `PerformanceStreamAndSocketTests.cs` (4 теста) — текстовый grep исходников вместо замеров производительности.
13. `PerformanceThrottleContractTests.cs` (7 тестов) — 100% сканирование исходников C# и Java регулярными выражениями.
14. `SingBoxManagerRestartTunHandshakeTests.cs` (7 тестов) — устаревшие grep-проверки исходников из Wave 38.
15. `ConfigGeneratorNoGamesDirectTests.cs` (1 тест) — проверка отсутствия давно удаленного правила Roblox из лета 2026.
16. `RuntimeStatusDetectorHandleLeakTests.cs` (2 теста) — пустые циклы по 5 000 итераций без замеров дескрипторов и без assert'ов.
17. `FirewallManagerDnsLockdownTests.cs` (7 тестов) — текстовый grep исходников, полностью заменён `FirewallManagerProcessRunnerWireShapeTests`.
18. `NightBaselineFirewallTests.cs` (2 теста) — дословный дубликат тестов из `LinuxFirewallManagerTests` и `MacFirewallManagerTests`.
19. `TestUpdateCommandExitCodeMappingTests.cs` (5 тестов) — тестирует искусственный локальный метод-симулятор вместо реального CLI.
20. `AndroidAppDumpMembersFact.cs` (1 тест) — постоянно пропущенный скрипт выгрузки членов классов в `%TEMP%`.
21. `AndroidAppCharacterizationTests.cs` (2 теста) — текстовый скрапер исходников вместо запуска Android рантайма.
22. `AndroidSideloadCallerTests.cs` (2 теста) — тавтология мока и дубликат из `IUpdateSourceContractTests`.

---

## 3. Детальные отчеты по пакетам

Полные пометочные аудиты каждого тестового метода с ответами на 5 вопросов сохранены в отдельных отчётах:

- `batch1-core-lifecycle.md` — Core VPN Lifecycle, ApplyAsync, Start/Stop (15 файлов, 127 тестов)
- `batch2-configgen-routing.md` — Генерация конфигов sing-box, Split/Full режимы (15 файлов, 129 тестов)
- `batch3-security-diagnostics.md` — Редакция логов, Zapret, HostsManager, TgProxy (15 файлов, 172 теста)
- `batch4-platform-update.md` — Платформенный файрвол (nftables, pfctl, netsh) и апдейтер (14 файлов, 188 тестов)
- `batch5-parsers-subscriptions.md` — Парсеры протоколов VLESS, Hy2, TUIC, AWG, подписки (15 файлов, 139 тестов)
- `batch6-ui-viewmodels.md` — UI Avalonia, ViewModels, Headless рендеринг (15 файлов, 134 теста)
- `batch7-freeconfigs-deepverify.md` — Бесплатные конфиги, глубокая верификация, DoS-защита (15 файлов, 94 теста)
- `batch8-network-dns.md` — DNS Hardening, LeakProtection, пользовательские правила (15 файлов, 148 тестов)
- `batch9-android-release.md` — Специфика Android, релизные пайплайны, WINBRAT (15 файлов, 93 теста)
- `batch10-system-seams-cli.md` — Системные швы процессов, CLI, дескрипторы, сигналы Unix (15 файлов, 114 тестов)
- `batch11-autostart-app.md` — Автозапуск, VLESS Detour v1, права доступа Unix (16 файлов, 86 тестов)
- `batch12-telemetry-health.md` — Телеметрия Clash API, классификатор здоровья, канарейки (16 файлов, 154 теста)
- `batch13-core-audits.md` — Аудиты рефакторинга Core, Fork gates, DNS туннели (16 файлов, 71 тест)
- `batch14-healthmon-system.md` — HealthMonitor, монитор ETW, потоковый HTTP, хранилища (16 файлов, 112 тестов)
- `batch15-night-shift-suites.md` — Регрессионные сьюты ночного спринта (NIGHT shift) (16 файлов, 106 тестов)
- `batch16-network-contracts.md` — Сетевые инварианты, песочницы Linux, NaiveProxy, MTU (16 файлов, 139 тестов)
- `batch17-server-health-pipeline.md` — Пайплайн здоровья серверов, плейсхолдеры, списки приложений (16 файлов, 177 тестов)
- `batch18-settings-singbox.md` — Атомарное сохранение настроек, миграции схем, многопоточность (16 файлов, 106 тестов)
- `batch19-truesplit-tun.md` — Протокол драйвера ядра Windows, Slipstream, Wintun lock (16 файлов, 177 тестов)
- `batch20-wintun-runtime.md` — Диагностика Wintun на Windows, SuffixMatch, рантайм-статус (16 файлов, 134 теста)
- `batch21a-tun-vless-events.md` — Исключения TUN, бэкапы обновления, DoH bootstrap (15 файлов, 105 тестов)
- `batch21b-final-suites.md` — FakeIP 1.12+, VpnEngine Orchestration, кэш проб Zapret (15 файлов, 142 теста)
