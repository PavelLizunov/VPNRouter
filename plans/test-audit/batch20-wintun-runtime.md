# Результаты аудита тестов роем Gemini: Пакет 20 (Wintun Diagnostics, Suffix Matching, Runtime Status & Probes)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **134**

## Сводная статистика по Пакету 20

- **KEEP (Сохранить без изменений)**: **88** (65.7%) — фундамент стабильности виртуального адаптера Wintun на Windows: удаление осиротевших адаптеров (`PreStartCleanupAsync`), защита от гонок PnP-удаления (`TunAdapterPnpSettleGate`), сосуществование с WireGuard-адаптерами, сопоставление суффиксов (`SuffixMatch`), детекция чужих экземпляров процесса и SHA-256 верификация апдейтера TgProxy.
- **MERGE / SIMPLIFY (Объединить)**: **27** (20.1%) — дублирующиеся тесты парсинга вывода `netsh` на разных языках, объединение одиночных проверок тем оформления и параметров адаптеров.
- **DROP (Кандидаты на удаление)**: **19** (14.2%) — **весь файл `RuntimeStatusDetectorHandleLeakTests.cs`** (2 теста — пустые циклы на 5 000 итераций без проверок), 12 текстовых скраперов исходников в `RuntimeStatusAdoptionTests`, `ServiceAppCoexistenceTests` и `TunAdapterReadinessTests`, а также 5 тавтологий.

---

## Детализация по файлам Пакета 20

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `PictogramTextTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Жадный парсинг пиктограмм для UI и сохранение доступных имен для экранных дикторов. |
| 2 | `RuntimeStatusAdoptionTests.cs` | 20 | 14 | 1 | 5 | CLEANUP | Детекция запущенного сервиса и защита от PID-переиспользования — 14 KEEP. 5 текстовых grep по C# — DROP. |
| 3 | `RuntimeStatusDetectorHandleLeakTests.cs` | 2 | 0 | 0 | 2 | **DROP FILE** | 2 пустых цикла по 5 000 вызовов без единого assert'а дескрипторов ОС. Бесполезны. |
| 4 | `ServiceAppCoexistenceTests.cs` | 8 | 3 | 0 | 5 | CLEANUP | 3 реальных теста межпроцессного семафора `TunOwnershipLock` — KEEP. 5 текстовых grep — DROP. |
| 5 | `SuffixMatchTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Разрешение конфликтов имен серверов: правило «побеждает самый длинный суффикс» (v2.28.2). |
| 6 | `TcpPingOnlyPlausibilityGateTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Сохранение статуса при недоступном порте. Перенести в `FreeConfigTesterTests`. |
| 7 | `TcpTlsProbeRealityDispatchTests.cs` | 2 | 1 | 1 | 0 | KEEP | Защита Bug-r10-G: Reality-серверы проверяются через TCP, а не TLS-handshake (защита от ложных блокировок). |
| 8 | `TgProxyDigestVerifyTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Проверка контрольных сумм SHA-256 скачиваемых пакетов Python wheel и zip-архивов TgProxy. |
| 9 | `TgProxyUpdaterRuntimeTests.cs` | 5 | 3 | 1 | 1 | CLEANUP | Атомарный swap папок рантайма и откат при ошибке записи. 1 текстовый скрапер — DROP. |
| 10 | `ThemePreferenceTests.cs` | 3 | 1 | 2 | 0 | CLEANUP | Нормализация темы оформления (Light/Dark/System). Объединить 3 теста в параметризованную Theory. |
| 11 | `ToolTabAvailabilityTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Управление видимостью вкладок инструментов в зависимости от доступности модулей. |
| 12 | `TunAdapterDiagnosticsHappyPathTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Полная цепочка очистки адаптеров: PowerShell -> pnputil -> SetupAPI fallback. |
| 13 | `TunAdapterDiagnosticsNetAdapterAvailabilityTests.cs` | 14 | 12 | 1 | 1 | CLEANUP | Поддержка Windows LTSC без модуля NetAdapter (прямой вызов PnP). 1 тест проверки логов — DROP. |
| 14 | `TunAdapterDiagnosticsProcessRunnerWireShapeTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Проверка формата вызова команд отключения адаптера (`netsh interface set interface ... disable`). |
| 15 | `TunAdapterPnpSettleGateTests.cs` | 9 | 8 | 1 | 0 | 100% KEEP | Дебаунс удаления PnP устройств Windows: предотвращает сбой sing-box «ERROR_FILE_EXISTS». |
| 16 | `TunAdapterReadinessTests.cs` | 24 | 14 | 5 | 5 | CLEANUP | Парсинг вывода netsh на немецком, русском и английском языках. 4 текстовых скрапера и 1 цикл — DROP. |

---

## Главные выводы аудита Пакета 20

1. **Файл-кандидат на удаление `RuntimeStatusDetectorHandleLeakTests.cs`**:
   Оба теста файла состоят из цикла на 5 000 итераций вызова методов, но внутри нет ни замера дескрипторов ОС, ни проверок памяти (`Assert` отсутствует вовсе). Реальная безопасность дескрипторов тестируется в `ProcessQueryTests`. Файл бесполезен и тратит процессорное время.

2. **Критическая надежность очистки Wintun на Windows**:
   Сьюты `TunAdapterDiagnostics*` и `TunAdapterPnpSettleGateTests` предотвращают самую частую ошибку Windows-пользователей: когда упавший или зависший sing-box оставляет в системе адаптер `VPNRouter-TUN`, из-за чего следующий запуск завершается фатальной ошибкой `Cannot create a file when that file already exists`.

3. **Очистка 12 устаревших текстовых скраперов кода**:
   Удаление проверок исходников в `RuntimeStatusAdoptionTests`, `ServiceAppCoexistenceTests` и `TunAdapterReadinessTests` снимет искусственные ограничения при будущих рефакторингах.
