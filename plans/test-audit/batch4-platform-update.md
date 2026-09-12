# Результаты аудита тестов роем Gemini: Пакет 4 (Platform, Firewall & Update)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **14**
Охвачено тестов: **188**

## Сводная статистика по Пакету 4

- **KEEP (Сохранить без изменений)**: **137** (72.9%) — критические тесты защиты от утечек файрвола (Linux nftables, macOS pfctl, Windows netsh), семантики обновления, SHA256-проверок и изоляции staging.
- **MERGE / SIMPLIFY (Объединить)**: **35** (18.6%) — тесты однотипных вариантов правил (дубликаты проверок Dispose/Disable, разрозненные тесты парсинга SemVer).
- **DROP (Кандидаты на удаление)**: **16** (8.5%) — устаревшие «source guards» старого файрвола, дубликаты ночных регрессий, тавтологии рефлексии интерфейсов и тест симулятора вместо продакшн CLI.

---

## Детализация по файлам Пакета 4

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `FirewallManagerDnsLockdownTests.cs` | 7 | 0 | 0 | 7 | **DROP FILE** | Все 7 тестов — устаревшие «source guards» (поиск подстрок в `.cs` коде). Полностью перекрыты `FirewallManagerProcessRunnerWireShapeTests`. |
| 2 | `FirewallManagerProcessRunnerWireShapeTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Полноценные тесты передачи аргументов в `netsh` через `FakeProcessRunner`. |
| 3 | `FirewallManagerResolveProcessPathTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Сквозное разрешение путей запущенных процессов через Win32 API. |
| 4 | `FirewallManagerTunAllowTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Чистая математика расчета комплементарных диапазонов IP для исключения TUN из блокировок. |
| 5 | `CommittedFirewallConfigTests.cs` | 6 | 2 | 1 | 3 | CLEANUP | 3 теста рефлексии интерфейсов — DROP (проверяется компилятором). Парсинг IP из JSON — KEEP. |
| 6 | `LinuxFirewallManagerTests.cs` | 51 | 40 | 11 | 0 | CLEANUP | Мощный сьют nftables: правила, защита от утечек, восстановление после сбоев. 11 однотипных тестов Dispose/Disable объединить. |
| 7 | `MacFirewallManagerTests.cs` | 60 | 48 | 12 | 0 | CLEANUP | Полное покрытие pfctl на macOS: токены, анкоры, очистка орфанов. 12 дублей объединить. |
| 8 | `NightBaselineFirewallTests.cs` | 2 | 0 | 0 | 2 | **DROP FILE** | 2 теста — дословные дубликаты существующих тестов в `LinuxFirewallManagerTests` и `MacFirewallManagerTests`. |
| 9 | `IUpdateSourceContractTests.cs` | 12 | 12 | 0 | 0 | 100% KEEP | Контракты источников обновлений (GitHub, Sideload APK), защита от повреждённых релизов. |
| 10 | `TestUpdateCommandExitCodeMappingTests.cs` | 5 | 0 | 0 | 5 | **DROP FILE** | Тестирует приватный локальный метод-симулятор `SimulateCommandFlow` вместо реального CLI `TestUpdateCommand`. |
| 11 | `UpdateCheckerChecksumTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Критическая проверка P01 UPD-1: валидация SHA256 и удаление битых архивов до распаковки. |
| 12 | `UpdateCheckerStagingTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Изоляция staging-каталогов при параллельных проверках обновлений. |
| 13 | `UpdateCheckerDowngradeTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Защита настроек: бэкап `config.yaml` перед даунгрейдом, очистка маркеров сбоев. |
| 14 | `UpdateCheckerTests.cs` | 21 | 12 | 9 | 0 | CLEANUP | 9 разрозненных тестов SemVer объединить в параметризованные `[Theory]`. Тесты эскейпинга shell-аргументов — KEEP. |

---

## Главные выводы аудита Пакета 4

1. **Файлы-кандидаты на полное удаление**:
   - `FirewallManagerDnsLockdownTests.cs` (7 тестов) — создавался до появления `FakeProcessRunner` и состоит из поиска подстрок в тексте файла. Полностью заменён поведенческими тестами в `WireShape`.
   - `NightBaselineFirewallTests.cs` (2 теста) — 100% дубликат тестов из Linux и Mac сьютов.
   - `TestUpdateCommandExitCodeMappingTests.cs` (5 тестов) — тавтология: внутри файла написан симулятор `SimulateCommandFlow`, и тесты проверяют его, а не реальный продакшн CLI.
   **Рекомендация**: удалить все 3 файла (14 тестов), сократив кодовую базу тестов без потери покрытия.

2. **Масштаб и качество Unix Firewall сьютов**:
   `LinuxFirewallManagerTests` (51 тест) и `MacFirewallManagerTests` (60 тестов) обеспечивают выдающееся качество покрытия системных команд без реального выполнения на хосте. Однако внутри накопилось около 23 мелких дубликатов (проверки Dispose vs Disable vs DeleteAll), которые можно слить в параметризованные тесты.

3. **Надёжность подсистемы UpdateChecker**:
   Все поведенческие тесты `UpdateCheckerChecksumTests`, `StagingTests`, `DowngradeTests` и `IUpdateSourceContractTests` имеют статус 100% KEEP — они предотвращают критические сбои при обновлении и откате версий.
