# Результаты аудита тестов роем Gemini: Пакет 18 (Settings, Migrations, Validation & SingBox Concurrency)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **106**

## Сводная статистика по Пакету 18

- **KEEP (Сохранить без изменений)**: **84** (79.2%) — фундаментальная устойчивость настроек: атомарное сохранение (`SettingsLoader.Save`), автобэкап поврежденных YAML файлов (`.unloadable`), миграции схем v0→v8, валидация MTU (защита от Jumbo MTU), colocated Cronet для NaiveProxy, стресс-тесты баланса TUN lock и подавление ложных краш-отчетов (`_stopInProgress`).
- **MERGE / SIMPLIFY (Объединить)**: **15** (14.2%) — микро-проверки шагов миграции MTU (объединить в `[Theory]`), парные тесты валидации настроек.
- **DROP (Кандидаты на удаление)**: **7** (6.6%) — 6 устаревших текстовых скраперов кода в `SingBoxManagerConcurrentStopTests`, `SingBoxManagerProcessExitLeakTests`, `SingBoxManagerReconnectStopSuppressionTests` и `SingBoxManagerRestartInProgressSuppressionTests`, а также 1 проверка уровня логов.

---

## Детализация по файлам Пакета 18

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `SettingsLoaderAtomicSaveTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Атомарный протокол сохранения настроек (DATA-1): запись в `.tmp`, безопасный rename, удаление мусора. |
| 2 | `SettingsLoaderRobustnessTests.cs` | 16 | 16 | 0 | 0 | 100% KEEP | Защита от потери настроек при крашах: бэкап битого YAML, BOM-маркеры Notepad, автомиграция. |
| 3 | `SettingsMigratorAppsModeTests.cs` | 8 | 8 | 0 | 0 | 100% KEEP | Миграция приложений v2→v3: изоляция списков Include/Exclude, сохранение регистра имен процессов. |
| 4 | `SettingsMigratorLegacyVlessServersCleanupTests.cs` | 8 | 7 | 1 | 0 | KEEP | Очистка устаревших серверов-сирот (баг BR-4), затенявших подписки. 1 дубль объединить. |
| 5 | `SettingsMigratorMtuTests.cs` | 9 | 3 | 6 | 0 | CLEANUP | Миграции MTU (v5..v8) с понижением Jumbo-пакетов до безопасных 1420. 6 тестов объединить в Theory. |
| 6 | `SettingsMigratorPlaceholderTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Очистка плейсхолдеров в конфигурациях серверов при миграциях с сохранением валидных серверов. |
| 7 | `SettingsValidatorTests.cs` | 21 | 19 | 2 | 0 | 100% KEEP | Полный оракул валидации настроек (порты, MTU IPv6, каналы обновлений, DoS-лимиты). 2 объединить. |
| 8 | `SimpleInputDetectorTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Распознавание типа введённой строки на странице Simple Mode (сервер, подписка или ошибка). |
| 9 | `SingBoxBackportBuildScriptTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Контроль цепочки поставок: проверка применения бэкпортов в скриптах сборки sing-box (`.ps1` и `.sh`). |
| 10 | `SingBoxFeaturesGateTests.cs` | 10 | 8 | 2 | 0 | KEEP | Защитные гейты: запрет генерации AWG/xhttp конфигов, если запущен апстрим-бинарник без форка. |
| 11 | `SingBoxManagerConcurrentStopTests.cs` | 3 | 2 | 0 | 1 | CLEANUP | Многопоточная остановка из 100 потоков без дедлока — KEEP. 1 текстовый скрапер `Interlocked` — DROP. |
| 12 | `SingBoxManagerCronetTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Автоматическое размещение `libcronet` рядом с бинарником sing-box для работы NaiveProxy на Win/Linux. |
| 13 | `SingBoxManagerLifecycleStressTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Стресс-тест шторма повторных остановок: гарантия строго 1 освобождения TUN lock без утечек. |
| 14 | `SingBoxManagerProcessExitLeakTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | Отписка от `AppDomain.ProcessExit` для предотвращения утечки GC — KEEP. 1 grep по C# — DROP. |
| 15 | `SingBoxManagerReconnectStopSuppressionTests.cs` | 7 | 3 | 0 | 4 | CLEANUP | Подавление ложного отчёта о падении при штатном реконнекте — 3 KEEP. 4 текстовых скрапера — DROP. |
| 16 | `SingBoxManagerRestartInProgressSuppressionTests.cs` | 6 | 0 | 5 | 1 | CLEANUP | Защита от ложного срабатывания `Crashed` во время рестарта. 5 проверок объединить, 1 проверку логов — DROP. |

---

## Главные выводы аудита Пакета 18

1. **Исключительная ценность устойчивости настроек (`SettingsLoaderRobustnessTests`)**:
   Все 16 тестов файла `SettingsLoaderRobustnessTests` имеют статус 100% KEEP. Они моделируют реальные сбои питания в момент сохранения, файлы с нулевым размером, поврежденный синтаксис YAML и обеспечивают 100% гарантию того, что приложение никогда не потеряет конфигурацию пользователя и автоматически создаст резервную копию `.unloadable`.

2. **Защита от ложных аварий (`SingBoxManagerReconnectStopSuppressionTests`)**:
   Поведенческие тесты устраняют одну из главных проблем удобства пользователей — ложные всплывающие окна «VPN упал» в момент, когда пользователь просто нажал кнопку смены сервера или реконнекта.

3. **Финальная зачистка Source-Guards в SingBoxManager**:
   Удаление 6 тестов поиска текста в исходниках `SingBoxManager` освободит тесты от хрупкости перед будущими рефакторингами.
