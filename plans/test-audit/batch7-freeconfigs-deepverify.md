# Результаты аудита тестов роем Gemini: Пакет 7 (FreeConfigs & Deep Verification Pipeline)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **94**

## Сводная статистика по Пакету 7

- **KEEP (Сохранить без изменений)**: **73** (77.7%) — ядро пайплайна бесплатных конфигураций: защита от zip-бомб, кэширование, ранжирование свежести (Freshness Tiers), гейты подключения только проверенных серверов, генерация конфигов sing-box под VLESS/Hy2/TUIC/Shadowsocks и изоляция процессов проверки.
- **MERGE / SIMPLIFY (Объединить)**: **15** (16.0%) — тесты граничных возрастов кэша и форматирования миллисекунд в лейблах, объединяемые в чистые `[Theory]`.
- **DROP (Кандидаты на удаление)**: **6** (6.3%) — тест дефолтного значения константы (`SavedListRetentionDays == 30`), рефлексия приватного HTTP-клиента, тяжелая 25k-аллокационная симуляция LINQ и проверка тривиального конструктора.

---

## Детализация по файлам Пакета 7

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `FreeConfigFetcherTests.cs` | 9 | 7 | 1 | 1 | CLEANUP | Защита от овер-сайз пейлоадов (>4MB), отмена тасок. 1 тест рефлексии приватного поля — DROP. |
| 2 | `FreeConfigPoolFetcherTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Защита от decompression bomb (zip-bomb), распаковка gzip, сохранение кэша при битом ответе. |
| 3 | `FreeConfigSortKeyTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Сортировка серверов по статусам (Verified бьёт Ok независимо от пинга). |
| 4 | `FreeConfigsApplyGateTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Защитный барьер: запрет применения непроверенных строк и блокировка кликов во время `IsBusy`. |
| 5 | `FreeConfigFreshnessTierTests.cs` | 16 | 14 | 2 | 0 | CLEANUP | Чёткая градация свежести (Fresh < 24ч, Ageing < 7д, Stale, Failed). 2 дубля объединить в Theory. |
| 6 | `FreeConfigCacheMigrationTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Сброс поврежденных пингов [1..4 мс] в 0. Рекомендовано перенести в `CacheRecoveryTests`. |
| 7 | `FreeConfigEntrySchemaTests.cs` | 3 | 1 | 1 | 1 | CLEANUP | JSON round-trip таймстемпа — KEEP. Базовое сравнение `>` дат C# — DROP. |
| 8 | `FreeConfigAggregatorPreserveTests.cs` | 10 | 6 | 3 | 1 | CLEANUP | Инвариант сохранения проверенных серверов при обновлении пула (регрессия v2.28.3). 1 дубль убрать. |
| 9 | `FreeConfigItemViewModelDisplayTests.cs` | 2 | 0 | 2 | 0 | CLEANUP | Защита от бага F-25 (отображение `— ✓✓` вместо `0 ms ✓✓`). Объединить оба теста в `[Theory]`. |
| 10 | `FreeConfigSavedRetentionTests.cs` | 7 | 3 | 3 | 1 | CLEANUP | Срок хранения сохраненных серверов (30 дней). 1 тест константы (`Assert.Equal(30, Const)`) — DROP. |
| 11 | `FreeConfigKeepPolicyTests.cs` | 4 | 3 | 0 | 1 | CLEANUP | Фильтрация `Verified` строк. 1 тест с созданием 25 000 объектов для проверки LINQ `.Where` — DROP. |
| 12 | `FreeConfigRecheckMergeTests.cs` | 7 | 5 | 2 | 0 | CLEANUP | Стейт-машина фонового перепрогона серверов (сохранение маркеров сбоев). 2 теста null объединить. |
| 13 | `FreeConfigDeepVerifyCheckpointTests.cs` | 7 | 1 | 5 | 1 | CLEANUP | 5 тестов проверяют тестовый локальный хелпер `ShouldSkipDeepVerify`. Вынести хелпер в Core или слить. |
| 14 | `VlessDeepVerifierTests.cs` | 11 | 9 | 2 | 0 | CLEANUP | Генерация конфигураций sing-box для тестового подключения ко всем протоколам. 2 строковых трима объединить. |
| 15 | `VlessDeepVerifierProcessRunnerTests.cs` | 5 | 4 | 0 | 1 | CLEANUP | Аргументы запуска sing-box, перехват stderr и гарантированный kill процесса в `finally`. 1 тест конструктора — DROP. |

---

## Главные выводы аудита Пакета 7

1. **Исключительная надежность подсистемы верификации**:
   Сьюты глубокой проверки (`FreeConfigPoolFetcherTests`, `VlessDeepVerifierTests`, `VlessDeepVerifierProcessRunnerTests`) защищают приложение от зависания фоновых процессов sing-box, атак типа zip-bomb и утечек памяти. 

2. **Защита от UI-артефактов и ложных срабатываний**:
   Тесты `FreeConfigsApplyGateTests`, `FreeConfigFreshnessTierTests` и `FreeConfigItemViewModelDisplayTests` защищают пользователя от подключения к неработающим узлам и гарантируют честное отображение задержек (устранение бага F-25).

3. **Балласт для зачистки**:
   - `RetentionDays_Const_Is30` — проверка константы саму на себя;
   - `TrimSimulation_DropsToVerifiedOnly` — бессмысленное выделение 25 тысяч объектов в памяти для проверки стандартного метода `.Where()`;
   - Тесты тестового зеркального хелпера в `FreeConfigDeepVerifyCheckpointTests`.
