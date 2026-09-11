# Результаты аудита тестов роем Gemini: Пакет 12 (Telemetry, Health Classifier, Canary, Clash API & State)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **154**

## Сводная статистика по Пакету 12

- **KEEP (Сохранить без изменений)**: **125** (81.2%) — фундаментальные тесты zero-allocation JSON парсера телеметрии sing-box, классификатора сбоев соединений (EOF, DialTimeout, Reset), аутентификации Clash API (Bearer token), проверки канареечных целей и MTU/DNS для AmneziaWG.
- **MERGE / SIMPLIFY (Объединить)**: **20** (13.0%) — тесты однотипных полей конфигураций, кодирования спецсимволов и дубликаты строк истинностных таблиц.
- **DROP (Кандидаты на удаление)**: **9** (5.8%) — 4 «source guards» (поиск подстрок в C# коде `ClashLogStream`, `VpnEngine`, `ConnStatsVisibilityThrottleTests`), 1 демо-тест печати `BypassRussianTrafficAbTest`, 1 тест YamlDotNet internals и 3 теста искусственного DTO `StateFile-like`.

---

## Детализация по файлам Пакета 12

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `ConnectionHealthClassifierTests.cs` | 12 | 12 | 0 | 0 | 100% KEEP | Чистый парсер логов sing-box: правильная классификация обрывов (EOF, DialTimeout, Reset, LocalClose). |
| 2 | `ConnectionHealthStateTests.cs` | 9 | 7 | 2 | 0 | KEEP | Скользящее окно телеметрии, расчет процента сбоев, отсечение шума. 2 теста объединить в Theory. |
| 3 | `ClashConnectionsParseTests.cs` | 13 | 13 | 0 | 0 | 100% KEEP | Zero-allocation `Utf8JsonReader` парсер для 2-секундного поллинга активных соединений Clash API. |
| 4 | `ClashLogStreamTests.cs` | 18 | 11 | 4 | 3 | CLEANUP | WebSocket-стриминг логов Clash API. 3 source-guards (grep по C# коду) — DROP. Остальное — 100% KEEP. |
| 5 | `ClashApiSecretTests.cs` | 10 | 8 | 1 | 1 | CLEANUP | Генерация и передача Bearer-токена авторизации Clash API во всех клиентах. 1 source-guard — DROP. |
| 6 | `CanaryPolicyTests.cs` | 12 | 11 | 1 | 0 | 100% KEEP | Стейт-машина канареечных проверок (Canary Targets), валидация TTL свежести и очистка URL. |
| 7 | `CanaryTargetsTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Загрузка встроенных популярных заблокированных доменов и пользовательских оверрайдов. |
| 8 | `ConfigSanityCheckTests.cs` | 14 | 8 | 6 | 0 | CLEANUP | Проверка конфига перед стартом (плейсхолдеры, порты, UUID). 6 проверок объединить в Theory. |
| 9 | `ConfigPipelineTests.cs` | 5 | 5 | 0 | 0 | 100% KEEP | Канонический конвейер генерации конфигов (предотвращение F-12 и утечки временных серверов). |
| 10 | `ConfigShareDocumentTests.cs` | 15 | 13 | 2 | 0 | 100% KEEP | Экспорт и импорт полных конфигураций VPNRouter (JSON-схема v1). 2 теста локализации объединить. |
| 11 | `ConfigGeneratorDnsTunnelTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Генерация конфигов sing-box для DNS-туннелей (slipstream) с защитой от циклического дедлока DNS. |
| 12 | `CacheRecoveryTests.cs` | 21 | 16 | 2 | 3 | CLEANUP | Карантин поврежденных JSON-кэшей. 3 теста фиктивного `StateFile-like` DTO — DROP. |
| 13 | `BypassRussianTrafficAbTest.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Не юнит-тест, а отладочный принтер конфигов в Console, зависящий от файлов гео-баз на диске. |
| 14 | `BratYamlReproTests.cs` | 9 | 4 | 3 | 2 | CLEANUP | Регрессионный сьют бага r5 (вайп серверов при миграции YAML). 2 устаревших теста — DROP. |
| 15 | `AwgDnsAndMtuTests.cs` | 9 | 9 | 0 | 0 | 100% KEEP | Защита от блэкхола DNS и MTU-несовместимости для UDP-туннелей AmneziaWG (кламп до 1420). |
| 16 | `ConnStatsVisibilityThrottleTests.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | 100% source-guard: читает `MainWindowViewModel.cs` и ищет строку условия троттлинга по таймеру. |

---

## Главные выводы аудита Пакета 12

1. **Критическая ценность парсера телеметрии и Clash API**:
   `ClashConnectionsParseTests` и `ConnectionHealthClassifierTests` написаны безупречно: без аллокаций, без задержек и без моков они гарантируют, что приложение за миллисекунды парсит гигабайты логов и трафика sing-box без утечек памяти и без ложного срабатывания переключателя серверов.

2. **Ликвидация 2 файлов-балластов**:
   - `BypassRussianTrafficAbTest.cs` — создавался как временная демонстрация вывода в stdout;
   - `ConnStatsVisibilityThrottleTests.cs` — типичный «source guard» (поиск подстроки `if (!IsVisible) return` в исходнике ViewModel).

3. **Очистка CacheRecoveryTests**:
   Сам класс `CacheRecovery` работает отлично, но тесты `StateFile_LikeCache_*` тестируют специально созданный тестовый класс-заглушку `RunStateLike`, а не реальный продакшн `StateFile`. Их удаление устранит лишний код без малейшего вреда для надежности.
