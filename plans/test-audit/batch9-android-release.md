# Результаты аудита тестов роем Gemini: Пакет 9 (Android Specifics & Release Tooling Contracts)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **93**

## Сводная статистика по Пакету 9

- **KEEP (Сохранить без изменений)**: **76** (81.7%) — критические контракты безопасности релизных пайплайнов (проверка 16 артефактов, Authenticode-подпись, изоляция staging, защита от взлома скриптов CI, пины appimagetool), Android DPI-bypass и самовосстановление настроек.
- **MERGE / SIMPLIFY (Объединить)**: **12** (12.9%) — дублирующиеся тесты строкового парсинга скриптов и одиночные категории Android.
- **DROP (Кандидаты на удаление)**: **5** (5.4%) — 3 неисполняемых файла (дампер интерфейса `AndroidAppDumpMembersFact`, текстовый скрапер `AndroidAppCharacterizationTests` и тавтологичный тест `AndroidSideloadCallerTests`).

---

## Детализация по файлам Пакета 9

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `AndroidDpiBypassInjectorTests.cs` | 13 | 13 | 0 | 0 | 100% KEEP | Мутации фрагментации TLS/UDP для обхода блокировок на Android: режимы standard/aggressive. |
| 2 | `AndroidAppDumpMembersFact.cs` | 1 | 0 | 0 | 1 | **DROP FILE** | Не тест: постоянно пропущенный (`[Fact(Skip=...)]`) вспомогательный скрипт дампа членов класса в `%TEMP%`. |
| 3 | `AndroidAppCharacterizationTests.cs` | 2 | 0 | 0 | 2 | **DROP FILE** | 0 исполняемых тестов — оба метода читают исходники `.cs` с диска и вычисляют хеш строк (хрупкий change-detector). |
| 4 | `AndroidCategoryLocalizationTests.cs` | 4 | 2 | 2 | 0 | CLEANUP | Локализация категорий приложений (Ru/En) и сохранение кастомных ID без искажений. |
| 5 | `AndroidStorageSaneTests.cs` | 12 | 10 | 2 | 0 | KEEP | Самовосстановление и карантин повреждённых настроек SharedPreferences на Android. |
| 6 | `AndroidSideloadCallerTests.cs` | 2 | 0 | 0 | 2 | **DROP FILE** | 1 тест тавтологично проверяет mock `FakeUpdateSource`, 2-й — дословный дубликат из `IUpdateSourceContractTests`. |
| 7 | `AptReleaseWorkflowTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Защита цепочки поставок APT-репозитория: подпись GPG, порядок обновления пакетов, защита от подмены тегов. |
| 8 | `PlatformReleaseWorkflowTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Проверка скриптов GitHub Actions (macOS, Linux, Android): защита от шелл-инъекций и запрет публикации драфтов. |
| 9 | `ReleaseToolingContractTests.cs` | 20 | 19 | 1 | 0 | KEEP | Оракул релизных скриптов: проверка PowerShell 5.1 AST, `repair.cmd`, драйвер TrueSplit, изоляция `install.ps1`. |
| 10 | `ReleaseDefectGateTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Исполнение `check-open-p0.ps1`: гарантия блокировки релиза при открытых дефектах P0/P1 без явного вейвера. |
| 11 | `ReleaseIntegrityWorkflowTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Статическая и динамическая проверка workflow целостности релизов: ровно 16 ассетов, строгая грамматика тегов. |
| 12 | `ReleaseSafetyBehaviorTests.cs` | 7 | 5 | 2 | 0 | CLEANUP | Вычисление SHA256 в PowerShell 5.1, парсинг скриптов автоматизации, защита от зависаний в soak-тестах. |
| 13 | `BratVerifierContractTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Контракты верификатора WINBRAT: изоляция учеток, проверка адреса целевой машины, запрет мутаций без подтверждения. |
| 14 | `PostShipVerifierContractTests.cs` | 8 | 4 | 4 | 0 | CLEANUP | Сквозное исполнение пост-релизных тестов на Windows. 4 строковых скрапера объединить. |
| 15 | `BuildLinuxAppImageToolPinTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Контроль цепочки поставок: жесткая проверка SHA256 и размера бинарника `appimagetool` в CI до запуска. |

---

## Главные выводы аудита Пакета 9

1. **Файлы-кандидаты на удаление**:
   - `AndroidAppDumpMembersFact.cs` — закомментированный скрипт ручной отладки;
   - `AndroidAppCharacterizationTests.cs` — устаревший текстовый сканер файлов вместо тестирования рантайма;
   - `AndroidSideloadCallerTests.cs` — 100% дубликат и тавтология.
   **Рекомендация**: удалить все 3 файла (5 тестов).

2. **Критическая значимость Release & Supply-Chain тестов**:
   Сьюты `ReleaseIntegrityWorkflowTests`, `PlatformReleaseWorkflowTests`, `AptReleaseWorkflowTests` и `BuildLinuxAppImageToolPinTests` закрывают колоссальный вектор рисков: они гарантируют, что в релиз не попадет битый бинарник, что CI не запустит вредоносный скрипт через контекст GitHub Actions и что в драфт-релиз попадут ровно все 16 требуемых файлов с правильными контрольными суммами.
