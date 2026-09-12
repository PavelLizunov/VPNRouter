# Результаты аудита тестов роем Gemini: Пакет 11 (Autostart, App Config & State Automation)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **86**

## Сводная статистика по Пакету 11

- **KEEP (Сохранить без изменений)**: **62** (72.1%) — цепочки VLESS Detour (`detour-v1`), автоматизация UI (`AppAutomationDriver`), детекция дублей серверов, права доступа POSIX на Unix (`0700`/`0600`), скоринг намерений в Simple Mode и контракты автозапуска.
- **MERGE / SIMPLIFY (Объединить)**: **14** (16.3%) — парные тесты YAML-сериализации (`true`/`false`), разрозненные проверки путей и текстовые проверки документации.
- **DROP (Кандидаты на удаление)**: **10** (11.6%) — 5 текстовых скраперов кода сервиса в `AutostartContractTests`, 2 ненадёжных смоук-теста хоста в `AutostartHelperShapeTests`, 2 парсера в `AppAutostartTgProxyTests` и 1 дубликат DoH.

---

## Детализация по файлам Пакета 11

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `AddServerDuplicateDetectionTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Защита от тихих сбросов серверов при добавлении: серверы с одинаковым хостом, но разными портами разрешены. |
| 2 | `AgentContextContractTests.cs` | 10 | 7 | 3 | 0 | CLEANUP | Статический линтер документации DSH, контрактов агентов и скриншотов. 3 текстовых проверки объединить. |
| 3 | `AntiCensorshipDnsTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | 1 тест подавления ECH (`HTTPS`/`SVCB`) — критический KEEP. 1 тест формата DoH дублирует `ConfigGeneratorTests` (DROP). |
| 4 | `AppAutomationDriverTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Полный HTTP-драйвер автоматизации UI: метрики, скриншоты, действия. |
| 5 | `AppAutostartTgProxyTests.cs` | 6 | 0 | 4 | 2 | CLEANUP | Файл состоит из поиска регулярных выражений в тексте C# файлов. 2 тривиальных — DROP, 4 переписать на поведение. |
| 6 | `AppConfigDetourTests.cs` | 10 | 10 | 0 | 0 | 100% KEEP | Золотой стандарт тестов: клиентские цепочки VLESS-прокси (`detour-v1`), заголовок `X-VPNRouter-Capabilities`, fail-closed. |
| 7 | `AppPathsUnixPermissionsTests.cs` | 6 | 5 | 1 | 0 | KEEP | Защита прав на Unix: `0700` для папок, `0600` для `config.yaml`, защита от симлинк-атак и umask. |
| 8 | `AppSettingsDnsLeakLockdownTests.cs` | 6 | 4 | 2 | 0 | CLEANUP | Дефолт опции BR-10 (`DnsLeakLockdown = false`) и миграция v2→v3. 2 теста объединить в Theory. |
| 9 | `AppUrlRedactionSourceTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Защита R13-A: гарантия оборачивания URL в `ScrubSecrets` в логах ViewModels. |
| 10 | `AutoFailoverRecoveryAndPersistTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Восстановление пула после исчерпания попыток и предотвращение утечки временных серверов в `config.yaml`. |
| 11 | `AutoIntentScoringTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Скоринг и ранжирование серверов под намерения пользователя (Gaming -> UDP-native/AWG, Privacy, General). |
| 12 | `AutoSelectServerPoolTests.cs` | 5 | 3 | 2 | 0 | CLEANUP | Формирование пулов автовыбора по протоколам (изоляция flow vision от no-flow). 2 мелких теста слить. |
| 13 | `AutoSelectStatusTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Честное отображение имени и IP реального узла выхода в карточке подписки вместо номинального. |
| 14 | `AutostartContractTests.cs` | 10 | 5 | 0 | 5 | CLEANUP | 5 реальных тестов проверки файлов и директорий `TgProxyUpdater` — KEEP. 5 текстовых парсеров кода C# — DROP. |
| 15 | `AutostartHelperShapeTests.cs` | 5 | 2 | 1 | 2 | CLEANUP | 2 теста валидации путей — KEEP. 2 смоук-теста, зависящие от состояния реестра хоста — DROP. |
| 16 | `AvailableRuleTypesSurfaceTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Предотвращение регрессии AU-10 (доступность типов правил `domain_regex` и `process_path` в UI). |

---

## Главные выводы аудита Пакета 11

1. **Критическая ценность VLESS Detour (`AppConfigDetourTests`)**:
   Все 10 тестов цепочек VLESS-прокси безупречны: они гарантируют, что клиент правильно объявляет свои возможности серверу подписок, строит цепочки `chain-entry -> proxy` и падает fail-closed, если цепочка разорвана или содержит неподдерживаемый транспорт.

2. **Защита прав на Unix (`AppPathsUnixPermissionsTests`)**:
   Тесты прав доступа к файлам конфигурации защищают закрытые ключи и токены от чтения другими непривилегированными пользователями в Linux и macOS, а также предотвращают уязвимости перезаписи файлов через симлинки.

3. **Балласт текстовых парсеров кода в автозапуске**:
   В `AutostartContractTests` половина методов не запускает код, а открывает `VPNRouterService.cs` и считает фигурные скобки и ключевые слова регулярными выражениями. При любом косметическом рефакторинге они ломаются или пропускаются. Их следует удалить.
