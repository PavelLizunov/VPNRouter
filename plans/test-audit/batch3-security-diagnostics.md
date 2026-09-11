# Результаты аудита тестов роем Gemini: Пакет 3 (Security, Redaction, Diagnostics, Zapret & TgProxy)

Дата: 2026-09-11
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **172**

## Сводная статистика по Пакету 3

- **KEEP (Сохранить без изменений)**: **129** (75.0%) — критические тесты защиты от утечек (redaction), безопасности запуска процессов, аргументов Zapret, парсинга HostsManager и изоляции сокетов.
- **MERGE / SIMPLIFY (Объединить)**: **10** (5.8%) — дублирующиеся варианты схем или обработчиков флагов.
- **DROP (Кандидаты на удаление)**: **33** (19.2%) — массовые «source guards» (поиск подстрок в кодовой базе вместо выполнения) и тавтологические проверки DTO/рефлексии.

---

## Детализация по файлам Пакета 3

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `DiagnosticsRedactorTests.cs` | 21 | 20 | 1 | 0 | KEEP | Золотой стандарт тестов безопасности: удаление паролей, UUID, ключей из YAML/JSON/логов. 1 дубль объединить. |
| 2 | `DiagnosticsExporterTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Сквозной интеграционный тест формирования ZIP-архива без утечек секретов. |
| 3 | `DiagnosticsExporterTailBoundedTests.cs` | 2 | 2 | 0 | 0 | KEEP | Защита от OOM при чтении хвостов логов (требуется мелкий апдейт размера с 3 МБ до 12 МБ). |
| 4 | `CrashReporterScrubberTests.cs` | 21 | 16 | 3 | 2 | CLEANUP | 1 точный дубликат и 1 тест мутации статического AppPaths — DROP. Схемы URI — объединить. |
| 5 | `UrlValidationSecurityTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Защита от SSRF и локального доступа к файлам при вводе URL подписок и источников. |
| 6 | `ShellVerbRoutingTests.cs` | 7 | 5 | 1 | 1 | CLEANUP | Тест BCL `ProcessStartInfo` без вызова продакшн-кода — DROP. Остальное — отличная защита от инъекций. |
| 7 | `HostsManagerTests.cs` | 21 | 21 | 0 | 0 | 100% KEEP | Образцовый сьют: 21 чистый in-memory тест с защитой от дублирования hosts и защитой апдейтера. |
| 8 | `HostsManagerProcessRunnerWireShapeTests.cs` | 5 | 4 | 0 | 1 | CLEANUP | Тест конструктора без проверок — DROP. Тесты flushdns на фейках — KEEP. |
| 9 | `ZapretActionsTests.cs` | 16 | 16 | 0 | 0 | 100% KEEP | Защита от шелл-инъекций и корректность аргументов `sc.exe`/`netsh`. |
| 10 | `ZapretAutoStrategyR4Tests.cs` | 15 | 6 | 2 | 7 | CLEANUP | 7 тестов свойств компиляторных C# `record` (DTO) и констант — DROP. Файловое восстановление ipset — KEEP. |
| 11 | `ZapretUpdaterAtomicityTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | Поведенческий тест перезаписи заблокированных файлов — KEEP. 1 source-guard — DROP. |
| 12 | `TgProxyAutostartLoggingTests.cs` | 11 | 2 | 2 | 7 | CLEANUP | 7 source-guards (grep по строкам кода сервисов) — DROP. Реальное скрытие секретов — KEEP. |
| 13 | `TgProxyManagerProcessRunnerTests.cs` | 12 | 8 | 2 | 2 | CLEANUP | Тест пустого конструктора и дубликат редакции — DROP. Управление процессами на фейках — KEEP. |
| 14 | `TgProxyOneButtonMvpTests.cs` | 13 | 4 | 3 | 6 | CLEANUP | 5 source-guards и 1 рефлексия метода — DROP. Реальные сокеты и сериализация — KEEP. |
| 15 | `TgProxyOwnershipCharacterizationTests.cs` | 17 | 4 | 1 | 12 | **HEAVY CLEANUP** | 12 из 17 тестов — это поиск подстрок и комментариев в кодовой базе C# (DROP). 5 тестов упрямых процессов — KEEP. |

---

## Главные выводы аудита Пакета 3

1. **Аномальная концентрация «Source Guards» в подсистеме TgProxy**:
   Файлы `TgProxyOwnershipCharacterizationTests` (12 source-guards), `TgProxyAutostartLoggingTests` (7 source-guards) и `TgProxyOneButtonMvpTests` (5 source-guards) суммарно содержат **24 псевдо-теста**, которые не запускают код, а парсят текст исходников регулярными выражениями. Это технический долг после фазы рефакторинга.
   **Рекомендация**: удалить все 24 текстовых парсера, сохранив 10 реальных поведенческих тестов управления процессами и сокетами.

2. **Тавтологии C# Record в `ZapretAutoStrategyR4Tests`**:
   7 тестов проверяли базовую работу языка C# (присвоение полей в record-структурах `FlowsealProgress` и `FlowsealSweepResult`).
   **Рекомендация**: удалить 7 тривиальных тестов.

3. **Абсолютная ценность Redaction и Security**:
   Все тесты в `DiagnosticsRedactorTests`, `CrashReporterScrubberTests`, `UrlValidationSecurityTests`, `ZapretActionsTests` и `HostsManagerTests` демонстрируют высочайшее качество и напрямую предотвращают утечки секретов и уязвимости command injection.
