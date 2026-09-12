# Результаты аудита тестов роем Gemini: Пакет 2 (Config Generation & Routing)

Дата: 2026-09-11
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **129**

## Сводная статистика по Пакету 2

- **KEEP (Сохранить без изменений)**: **105** (81.4%) — ядро генерации sing-box JSON, маршрутизации Include/Exclude, DNS leak protection, QUIC-блокировок и защиты от плейсхолдеров.
- **MERGE / SIMPLIFY (Объединить)**: **15** (11.6%) — фрагментированные микро-тесты отдельных свойств одного и того же сгенерированного объекта.
- **DROP (Кандидаты на удаление)**: **9** (7.0%) — устаревшие файлы (кастомный вырез Roblox), «source guards» (поиск текста в коде) и тривиальные тавтологии.

---

## Детализация по файлам Пакета 2

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `ConfigGeneratorTests.cs` | 20 | 11 | 8 | 1 | CLEANUP | 8 микро-тестов отдельных свойств (`DnsFinal_IsLocalDns`, `RouteRules_SniffRuleIsFirst` и т.д.) объединяются в сценарные тесты. 1 source-guard (`InboundTun_NoSniffFields`) — DROP. |
| 2 | `ConfigGeneratorIncludeModeTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Базовые тесты split-include режима и изоляции списков процессов. |
| 3 | `ConfigGeneratorExcludeModeTests.cs` | 9 | 9 | 0 | 0 | 100% KEEP | Полная матрица тестов инвертированного режима split-exclude и приоритетов. |
| 4 | `ConfigGeneratorStrictDnsOverrideTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Исчерпывающее тестирование рантайм-переопределений Strict DNS. |
| 5 | `ConfigGeneratorAutoSelectHealthFilterTests.cs` | 9 | 8 | 0 | 1 | CLEANUP | 8 отличных тестов фильтрации серверов по здоровью. 1 тест (`AutoSelectWording...`) — проверка UI-строк в генераторе конфига (DROP). |
| 6 | `ConfigGeneratorAppRoutingFingerprintTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Детерминизм, регистрозависимость и стабильность AppRoutingFingerprint. |
| 7 | `ConfigGeneratorDuplicateNameTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | Тест `DuplicateNames_DoNotProduceDuplicateOutboundTags` написан с ошибкой (GetActiveServers отбрасывает второй сервер по IP еще до проверки дублей имен). |
| 8 | `ConfigGeneratorNoGamesDirectTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Тест проверяет отсутствие давно удаленного прямого правила для `RobloxPlayerBeta.exe` (v2.45.0). Позитивная часть уже покрыта в NaiveProxySupportTests. |
| 9 | `ConfigGeneratorRemoteRuleSetGuardTests.cs` | 2 | 1 | 1 | 0 | CLEANUP | P0 инвариант запрета `type: remote` правил для защиты от краш-лупов sing-box. Рекомендовано объединить тесты и изолировать кэш. |
| 10 | `ConfigGeneratorTcpKeepAliveTests.cs` | 3 | 1 | 0 | 2 | CLEANUP | 1 тест через рефлексию модели и 1 grep по тексту JSON — DROP. Структурный тест через JSON DOM — KEEP. |
| 11 | `ConfigGeneratorQuicBlockTests.cs` | 12 | 9 | 2 | 1 | CLEANUP | Полная матрица QUIC-reject правил. 2 дубля проверок портов объединить. 1 тест дефолта C# DTO — DROP. |
| 12 | `ConfigGeneratorEmptyServersGuardTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Защита от старта с пустым пулом серверов (P0 регрессия v2.28.1). |
| 13 | `ConfigGeneratorSplitCharacterizationTests.cs` | 5 | 4 | 0 | 1 | CLEANUP | 4 больших сквозных интеграционных сценария (Hysteria2, AmneziaWG, Chained detours) — KEEP. 1 source-guard (`AllFiftyMembersPreserved...`) — DROP. |
| 14 | `CustomConfigInjectorTests.cs` | 41 | 37 | 2 | 2 | CLEANUP | Крупный высокоценный сьют валидации пользовательского JSON и подмешивания роутинга/DNS. 2 дубля объединить, 2 лишних проверки убрать. |
| 15 | `CustomConfigPlaceholderTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Защита от подсовывания тестовых плейсхолдеров (Reality pubkey/short_id) в продакшн-конфиг. |

---

## Главные выводы аудита Пакета 2

1. **Фрагментация проверок в `ConfigGeneratorTests`**:
   Тесты вроде `RouteRules_SniffRuleIsFirst`, `RouteRules_HijackDnsIsSecond`, `DnsFinal_IsLocalDns`, `Route_FinalIsDirect` проверяют ровно по одному свойству одного и того же вызова `ConfigGenerator.Generate`. Это увеличивает общее число тестов и время прогона без добавления покрытия. Объединение их в связные тесты конфигурации сократит 8 тестов до 2-3 без потери ни одной проверки.

2. **Source Guards в генерации конфигов**:
   `InboundTun_NoSniffFields` и `AllFiftyMembersPreserved_AcrossSplitFilesOrMonolith` просто открывают `.cs` файлы на диске и ищут имена методов/полей. Они не тестируют работу приложения и падают при обычном форматировании.

3. **Устаревшие вырезы**:
   `ConfigGeneratorNoGamesDirectTests.cs` проверял удаление хардкод-правила для Roblox из лета 2026 года. Файл больше не имеет архитектурной ценности.
