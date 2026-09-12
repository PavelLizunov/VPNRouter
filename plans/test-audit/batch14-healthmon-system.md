# Результаты аудита тестов роем Gemini: Пакет 14 (HealthMonitor, Process Monitors, Streaming & System Stores)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **112**

## Сводная статистика по Пакету 14

- **KEEP (Сохранить без изменений)**: **76** (67.9%) — критические тесты восстановления после аварий (`HealthMonitor`), защиты от сиротских TUN-адаптеров, стриминга ответов `IHttpClient`, контрактов хранилища настроек (`ISettingsStore`), счётчика ступенчатой эскалации сбоев запуска (`LaunchFailureCounter`) и синтаксической валидации CMD-скрипта автоапдейтера.
- **MERGE / SIMPLIFY (Объединить)**: **17** (15.2%) — параметры трансляции событий ETW, дублирующиеся пороги рекомендаций и IL-инспекции.
- **DROP (Кандидаты на удаление)**: **19** (17.0%) — 5 тестов-самопроверок тестового дублера `FakeSingBoxApi`, 1 файл `HealthMonitorTimerRaceTests.cs` (проверка BCL `Interlocked.Exchange` вместо логики приложения), 2 теста дублирующего инлайн-цикла в `FirewallManagerLocalizedNetshTests`, 2 рефлексии приватных полей и 7 текстовых скраперов исходного кода C#.

---

## Детализация по файлам Пакета 14

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `EtwProcessMonitorTests.cs` | 12 | 1 | 8 | 3 | CLEANUP | Трансляция событий ETW. 6 тестов полей объединить в Theory. 1 тест `Assert.True(true)` и 2 тавтологии — DROP. |
| 2 | `FailoverRestartConcurrencyAuditTests.cs` | 4 | 2 | 0 | 2 | CLEANUP | 2 живых теста защиты от рестарта на утилизированном менеджере — KEEP. 2 текстовых grep по исходникам — DROP. |
| 3 | `FirewallManagerLocalizedNetshTests.cs` | 3 | 1 | 0 | 2 | CLEANUP | 2 теста выполняют локальный дублирующий цикл вместо тестирования класса `FirewallManager` (DROP). 1 реальный — KEEP. |
| 4 | `HealthCheckRobloxDiagnosticsTests.cs` | 8 | 6 | 2 | 0 | KEEP | Парсинг аутбаундов sing-box и таймаутов прокси для генерации рекомендаций по Roblox/Discord. |
| 5 | `HealthMonitorLeakValidationTests.cs` | 2 | 0 | 2 | 0 | CLEANUP | 2 теста проверяют IL-байткод методов через рефлексию (хрупко). Переписать на вызов поведения. |
| 6 | `HealthMonitorRecoveryGapTests.cs` | 8 | 5 | 0 | 3 | CLEANUP | Восстановление после краша (v2.31.5-r2) и обработка выхода из сна. 2 рефлексии приватных полей и 1 grep — DROP. |
| 7 | `HealthMonitorStartIdempotencyTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | 1 поведенческий тест предотвращения утечки таймеров и подписок SystemEvents — KEEP. 1 grep — DROP. |
| 8 | `HealthMonitorStopVsRestartRaceTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Проверка гонки рестарта M-9 через grep. Рекомендовано заменить на вызов через шов. |
| 9 | `HealthMonitorStrictDnsFailoverTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Стейт-машина Strict DNS failover (гистерезис сбоев N=2, откат при ошибке reload, фильтрация full-tunnel). |
| 10 | `HealthMonitorTimerRaceTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Тестирует `Interlocked.Exchange` из стандартной библиотеки .NET с 1 600 таймерами. 0 строк кода VPNRouter. |
| 11 | `HealthMonitorTunOrphanRestartTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Решение Fix #3: при столкновении TUN адаптеров выполняется адресный `netsh disable` перед рестартом sing-box. |
| 12 | `HelperCmdParserGuardTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Защита от синтаксических ошибок в CMD-скрипте апдейтера (кавычки `set`, задержки ping, самоудаление `del /Q`). |
| 13 | `IHttpClientStreamingContractTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Стриминг больших тел ответов (5MB без OOM), чтение заголовков до выкачки тела, отмена стрима. |
| 14 | `ISettingsStoreContractTests.cs` | 12 | 11 | 0 | 1 | KEEP | Полноценный контракт хранилища настроек (изоляция путей, защита SafeMode, гонки). 1 тест синглтона — DROP. |
| 15 | `ISingBoxApiContractTests.cs` | 7 | 2 | 0 | 5 | CLEANUP | 2 теста проверяют реальный HTTP REST/DTO контракт и защиту loopback в `ClashSingBoxApi` (KEEP). 5 тестов — самопроверка `FakeSingBoxApi` (DROP). |
| 16 | `LaunchFailureCounterTests.cs` | 20 | 13 | 4 | 3 | CLEANUP | Ступенчатая эскалация сбоев запуска (Self-Repair -> Config-Reset -> Safe Mode) и кулдауны. 2 grep и 1 константа — DROP. |

---

## Главные выводы аудита Пакета 14

1. **Файлы-кандидаты на удаление**:
   - `HealthMonitorTimerRaceTests.cs` (1 тест) — тратит процессорное время на 1 600 таймеров, проверяя метод `Interlocked.Exchange` среды выполнения Microsoft .NET. Код приложения не задействован.
   - 5 тестов в `ISingBoxApiContractTests.cs` — тестируют геттеры и словари внутри тестовой заглушки `FakeSingBoxApi`, а не продакшн-код.

2. **Критическая надежность восстановления туннеля**:
   Тесты `HealthMonitorStrictDnsFailoverTests`, `HealthMonitorTunOrphanRestartTests` и `LaunchFailureCounterTests` представляют собой основу надёжности десктопного клиента: они защищают систему от бесконечных циклов перезапуска, устраняют коллизии виртуальных адаптеров Wintun и обеспечивают ступенчатый выход из аварийных ситуаций.

3. **Ликвидация 7 текстовых скраперов кода**:
   В `FailoverRestartConcurrencyAuditTests`, `HealthMonitorRecoveryGapTests`, `HealthMonitorStartIdempotencyTests` и `LaunchFailureCounterTests` очистка строковых проверок исходников повысит стабильность сьюта и избавит от падений при рефакторинге.
