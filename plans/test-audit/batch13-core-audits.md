# Результаты аудита тестов роем Gemini: Пакет 13 (Core Refactoring Audits, Fork Gates & DNS Tunnels)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **71**

## Сводная статистика по Пакету 13

- **KEEP (Сохранить без изменений)**: **44** (62.0%) — критические гейты поддержки форка sing-box (AmneziaWG, xhttp), экспорт и импорт правил в форматах sing-box JSON/CSV, защита от прозрачных прокси (Host Reflection), DoH-приватность глубокой верификации и ReDoS-защита масок процессов.
- **MERGE / SIMPLIFY (Объединить)**: **20** (28.2%) — разрозненные проверки формата правил, тесты фазы аудита A (перенести в `VlessUriParserTests` и `LeakProtectionTests`), детекция форматов.
- **DROP (Кандидаты на удаление)**: **7** (9.8%) — весь файл `DnsTunnelUiTests.cs` (3 теста — дословные дубликаты), 3 текстовых скрапера исходников в `CrossPlatformUiAndIconPolishTests` и 1 в `CliVersionSourceTests`.

---

## Детализация по файлам Пакета 13

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `CliVersionSourceTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Тестирует регуляркой строку в `Program.cs`. Перенести проверку в CLI runtime или объединенный гардероб. |
| 2 | `ConnectionHealthFixtureCountsTests.cs` | 2 | 0 | 2 | 0 | CLEANUP | В CI молча пропускается, так как логи не закоммичены. Вшить синтетические строки в юнит-тесты. |
| 3 | `CoreAuditPhaseATests.cs` | 3 | 0 | 3 | 0 | CLEANUP | 3 высокоценных теста валидации ключей (защита от паники sing-box). Перенести в доменные файлы. |
| 4 | `CoreAuditPhaseBTests.cs` | 3 | 2 | 1 | 0 | KEEP | Защита от ReDoS (катастрофического бэктрекинга регулярных выражений) в масках процессов. |
| 5 | `CoreAuditPhaseCTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Защита от утечек при дебаунсе конфига и очистка файрвола при прерванном старте. |
| 6 | `CrossPlatformUiAndIconPolishTests.cs` | 5 | 1 | 1 | 3 | CLEANUP | 1 настоящий тест размеров PNG-иконок — KEEP. 3 текстовых поиска строк в `.cs` файлах — DROP. |
| 7 | `CustomConfigInjectorForkGateTests.cs` | 9 | 9 | 0 | 0 | 100% KEEP | Защитные гейты: проверка доступности форка перед стартом AWG/xhttp, предотвращение фатальных сбоев. |
| 8 | `CustomRulesImportExportTests.cs` | 12 | 9 | 3 | 0 | KEEP | Экспорт и импорт правил в CSV, VPNRouter JSON и sing-box JSON. 3 детектора слить в Theory. |
| 9 | `CustomRulesV2_30_MigrationTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Миграция устаревших правил `direct` v1 в пользовательские правила v2 с проверкой идемпотентности. |
| 10 | `DeepVerifierDnsPrivacyTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Инвариант DoH: временные конфиги проверки серверов обязаны использовать шифрованный прямой DNS. |
| 11 | `DeepVerifyHostReflectionTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Инвариант FCP-02: отбраковка серверов, которые не туннелируют трафик, а светят реальный публичный IP хоста. |
| 12 | `DeepVerifyProbeCancellationTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Регрессия F1: строгое разграничение отмены пользователем от сетевого HTTP-таймаута. |
| 13 | `DeepVerifyProbeScopeTests.cs` | 8 | 3 | 2 | 3 | CLEANUP | 3 теста счетчиков и очистки буферов — KEEP. 3 текстовых grep по исходникам — DROP. |
| 14 | `DnsTunnelLabelTests.cs` | 7 | 2 | 5 | 0 | CLEANUP | Отображение субтитров для DNS-туннелей в UI карточках. 4 метода свернуть в Theory. |
| 15 | `DnsTunnelUiTests.cs` | 3 | 0 | 0 | 3 | **DROP FILE** | Все 3 теста — 100% дословные дубликаты тестов из `SimpleInputDetectorTests` и `DnsTunnelLabelTests`. |
| 16 | `GetEffectiveServersTests.cs` | 4 | 3 | 1 | 0 | KEEP | Приоритет списка серверов над устаревшими скалярными полями в `VlessConfig`. 1 дубль объединить. |

---

## Главные выводы аудита Пакета 13

1. **Файл-кандидат на полное удаление**:
   `DnsTunnelUiTests.cs` (3 теста) — временный артефакт ветки интеграции DNS-туннелей. Все 3 теста дословно присутствуют в `SimpleInputDetectorTests` и `DnsTunnelLabelTests`. Файл подлежит удалению.

2. **Защита от ReDoS и прозрачных прокси**:
   - `CoreAuditPhaseBTests` предотвращает зависание потоков мониторинга процессов при сложных масках с `*`;
   - `DeepVerifyHostReflectionTests` гарантирует, что сервер, маскирующийся под VPN, но передающий реальный IP пользователя в открытом виде (transparent proxy), никогда не получит статус Verified.

3. **Ликвидация исторических папок/файлов аудита**:
   Тесты из `CoreAuditPhaseATests` логично вернуть в соответствующие файлы предметных областей (`VlessUriParserTests` и `LeakProtectionTests`), очистив тесты от исторической фрагментации.
