# Результаты аудита тестов роем Gemini: Пакет 15 (The Night Shift Regression Suites)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **106**

## Сводная статистика по Пакету 15

- **KEEP (Сохранить без изменений)**: **55** (51.9%) — критические тесты защиты от гонок потоков при подключении (`TwoPhaseStartCoordinator`), изоляции циклов failover (`NIGHT-06`), защиты от переполнения счетчиков трафика (`NightConnStatsSessionTests`), таймаутов отмены UDP-проб и защиты DNS-приватности.
- **MERGE / SIMPLIFY (Объединить)**: **36** (34.0%) — повторяющиеся сценарии очередей диспетчера Avalonia, тесты методов `StopInternal` с разными исключениями и дубликаты JSON-парсинга.
- **DROP (Кандидаты на удаление)**: **15** (14.1%) — **6 целых файлов-дубликатов** и временных свидетельств («witness tests»), а также статические скраперы исходного кода C#.

---

## Детализация по файлам Пакета 15

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `NightBaselineEndpointTests.cs` | 2 | 0 | 0 | 2 | **DROP FILE** | Временный witness-файл дефекта NIGHT-05. Дословно дублирует тесты в `LinuxFirewallManagerTests` и `MacFirewallManagerTests`. |
| 2 | `NightBaselineFailoverTests.cs` | 1 | 0 | 1 | 0 | MERGE | 1 тест сброса пула failover при `Stop()`. Перенести в `NightFailoverIntentTests`. |
| 3 | `NightBaselineOwnershipCharacterizationTests.cs` | 1 | 0 | 1 | 0 | **DROP FILE** | 1 тест удержания секретов в TgProxyManager. Перенести в `TgProxyOwnershipCharacterizationTests` и удалить файл. |
| 4 | `NightBaselineRegressionTests.cs` | 5 | 4 | 1 | 0 | KEEP | Пакет регрессий: сохранение `detour: "wg"`, лимит 8 воркеров в `ProbeAllAsync`, отмена UDP. |
| 5 | `NightBaselineStatsTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Одиночный устаревший тест сброса статистики при 500 ошибке. Полностью перекрыт `NightConnStatsSessionTests`. |
| 6 | `NightBaselineStopCharacterizationTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Одиночный тест удержания TUN lease на Windows. Полностью перекрыт `NightWindowsStopCharacterizationTests`. |
| 7 | `NightBaselineTelemetryWiringTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | 100% текстовый скрапер: читает `VpnEngine.cs` и ищет `ClashApiSecret`. Уже покрыт в `ClashLogStreamTests`. |
| 8 | `NightConnStatsSessionTests.cs` | 15 | 12 | 2 | 1 | CLEANUP | Сессионная математика трафика: защита от скачков (дельта), сброс при смене сервера. 1 текстовый скрапер — DROP. |
| 9 | `NightConnectionsFreshnessTests.cs` | 12 | 4 | 6 | 2 | CLEANUP | Защита от сбоев поллинга соединений. 2 теста свойств DTO — DROP, 6 парсингов — MERGE. |
| 10 | `NightDnsPrivacyRegressionTests.cs` | 9 | 8 | 1 | 0 | 100% KEEP | Защита от утечек DNS через WireGuard-эндпоинты и матрица Strict DNS в кастомных конфигах. |
| 11 | `NightDurableReadinessTests.cs` | 17 | 5 | 11 | 1 | CLEANUP | Защита от преждевременного перехода UI в «Подключено». 10 проверок очереди диспетчера слить в Theory, 1 grep — DROP. |
| 12 | `NightFailoverIntentTests.cs` | 8 | 4 | 2 | 2 | CLEANUP | Изоляция контекста failover при рестартах и сменах серверов. 2 проверки порядка строк в исходнике — DROP. |
| 13 | `NightFailoverRollbackTests.cs` | 8 | 6 | 2 | 0 | KEEP | Защита инварианта NIGHT-06: откат серверов при сбое и блокировка отката, если пользователь уже переключил сервер. |
| 14 | `NightTypedReadinessTests.cs` | 6 | 0 | 4 | 2 | **DROP FILE** | Все 6 тестов — хрупкие регулярные выражения по тексту C# файлов (`File.ReadAllText`). Реальное поведение покрыто в `MvmTwoPhaseStartTimerTests`. |
| 15 | `NightUdpCancellationTests.cs` | 8 | 6 | 1 | 1 | CLEANUP | Отмена UDP-проб на реальных сокетах loopback. 1 source-guard — DROP. |
| 16 | `NightWindowsStopCharacterizationTests.cs` | 11 | 5 | 6 | 0 | CLEANUP | Обработка отказов остановки sing-box на Windows. Тесты 1–5 (разные виды исключений `Kill()`) объединяются в `[Theory]`. |

---

## Главные выводы аудита Пакета 15

1. **Массовая ликвидация файлов-дубликатов (6 файлов на DROP)**:
   Во время ночного спринта аудита (NIGHT Shift) для каждого дефекта создавался временный файл-свидетель (`NightBaseline*Tests.cs`). После того как дефекты были исправлены, а полные наборы тестов написаны в целевых файлах, эти временные файлы остались в репозитории как мертвый груз:
   - `NightBaselineEndpointTests.cs`
   - `NightBaselineOwnershipCharacterizationTests.cs`
   - `NightBaselineStatsTests.cs`
   - `NightBaselineStopCharacterizationTests.cs`
   - `NightBaselineTelemetryWiringTests.cs`
   - `NightTypedReadinessTests.cs` (целиком состоящий из поиска текста в `.cs`)
   **Рекомендация**: удалить все 6 файлов (12 тестов).

2. **Высочайшая ценность сессионных тестов трафика и готовности**:
   Тесты `NightConnStatsSessionTests`, `NightDurableReadinessTests` и `NightFailoverRollbackTests` страхуют систему от сложнейших асинхронных гонок (race conditions), когда UI и фоновые процессы обновляют состояние одновременно.
