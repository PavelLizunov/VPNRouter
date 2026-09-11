# Результаты аудита тестов роем Gemini: Пакет 16 (Network Invariants, Sandboxes, Naive Proxy & System Contracts)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **16**
Охвачено тестов: **139**

## Сводная статистика по Пакету 16

- **KEEP (Сохранить без изменений)**: **106** (76.3%) — критические сетевые инварианты (исключение локальных подсетей P0.1, детекция песочниц Linux, расчет подсетей Tailscale/WireGuard, парсеры вывода утилит macOS, защита от MTU-блэкхола Jumbo/Roblox, привязка UDP-сиблингов для NaiveProxy, одноэкземплярный lock-файл).
- **MERGE / SIMPLIFY (Объединить)**: **20** (14.4%) — избыточно детализированные проверки URI-схем NaiveProxy (объединить в `[Theory]`), повторяющиеся проверки подсетей и локальные DTO сериализации.
- **DROP (Кандидаты на удаление)**: **13** (9.4%) — **2 целых файла-балласта** (`PerformanceThrottleContractTests.cs` и `PerformanceStreamAndSocketTests.cs` — 100% поиск текста в коде), 4 теста десериализации локальных фиктивных DTO в `Phase3StjJsonRoundTripTests`, 1 неизолированный тест `LockFile` и 2 тавтологии.

---

## Детализация по файлам Пакета 16

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `LinuxTunSandboxTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Детекция песочниц и namespaces в Linux (`/proc/self/uid_map`), приоритет `pkexec`. |
| 2 | `LocalNetworkInvariantConfigTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | P0.1 инвариант: локальные приватные диапазоны IPv4/IPv6 гарантированно исключаются из TUN. |
| 3 | `LockFileTests.cs` | 11 | 9 | 1 | 1 | CLEANUP | Одноэкземплярный запуск приложения, баннер после краша. 1 неизолированный тест (трогает ProgramData) — DROP. |
| 4 | `MacDnsParsersTests.cs` | 10 | 10 | 0 | 0 | 100% KEEP | Чистые парсеры вывода утилит macOS (`networksetup`, `route get`) без платформенных зависимостей. |
| 5 | `MacHelperNameExpansionTests.cs` | 9 | 7 | 2 | 0 | KEEP | Экспансия имен дочерних процессов Chromium и WebKit XPC для сплит-туннеля на macOS. |
| 6 | `MergeUserCustomizationTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Бизнес-логика слияния пользовательских настроек списков приложений с профилями (Phase F). |
| 7 | `MtuJumboFixTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Защита от блэкхола пакетов Jumbo MTU (исправление ошибки Roblox 277): ограничение MTU до 1420. |
| 8 | `NaivePairingUdpLivenessTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Умная привязка UDP-сиблинга для NaiveProxy (проверка доступности, приоритет Hy2 над TUIC). |
| 9 | `NaiveProxySupportTests.cs` | 29 | 16 | 13 | 0 | CLEANUP | Полный сьют NaiveProxy: парсинг схем, генерация аутбаунда. 13 микро-тестов схем свернуть в Theory. |
| 10 | `NetworkInterfaceDetectorTests.cs` | 20 | 12 | 6 | 2 | CLEANUP | Битовая арифметика подсетей, расширение /32 до /24 для WireGuard, детекция CGNAT 100.64.0.0/10 для Tailscale. |
| 11 | `NetworkPageLayoutTests.cs` | 1 | 0 | 1 | 0 | CLEANUP | Текстовый парсер XAML строки кнопки удаления. Рекомендовано влить в `HeadlessGuiTests`. |
| 12 | `OrphanCleanupGuardTests.cs` | 1 | 1 | 0 | 0 | 100% KEEP | Защита F-4: гарантия того, что чистильщик фоновых демонов случайно не завершит процесс самого `VPNRouter.App`. |
| 13 | `PerAppFilterModeTests.cs` | 7 | 7 | 0 | 0 | 100% KEEP | Проекции и парсинг три-стейт режимов фильтрации приложений на Android (Simple vs Advanced). |
| 14 | `PerformanceStreamAndSocketTests.cs` | 4 | 0 | 2 | 2 | **DROP FILE** | 2 теста — текстовый grep по исходникам; 2 теста парсинга перенести в их родные парсерные файлы. |
| 15 | `PerformanceThrottleContractTests.cs` | 7 | 0 | 0 | 7 | **DROP FILE** | 100% балласт: 7 тестов сканируют исходники C# и Java через регулярные выражения. 0 исполняемой логики. |
| 16 | `Phase3StjJsonRoundTripTests.cs` | 16 | 8 | 4 | 4 | CLEANUP | Миграция DTO на System.Text.Json. 4 теста десериализуют локальные тестовые DTO вместо продакшн-кода (DROP). |

---

## Главные выводы аудита Пакета 16

1. **Ликвидация 2 файлов-балластов (PerformanceThrottleContractTests и PerformanceStreamAndSocketTests)**:
   Оба файла не имеют отношения к реальным замерам производительности или сокетам, а представляют собой устаревшие статические grep-проверки по исходникам `.cs` и `.java`. Их удаление избавит сьют от 11 фиктивных тестов.

2. **Критическая ценность сетевых инвариантов**:
   - `LocalNetworkInvariantConfigTests` защищает домашнюю локальную сеть от случайного перехвата туннелем;
   - `NetworkInterfaceDetectorTests` обеспечивает бесконфликтное сосуществование VPNRouter с Tailscale и AmneziaWG;
   - `MtuJumboFixTests` предотвращает разрыв сетевых соединений в играх и приложениях из-за некорректного MTU.

3. **Очистка Phase3StjJsonRoundTripTests**:
   Удаление 4 тестов локальных фейковых DTO (`ServerTestResultDto`, `GitHubRelease` dummy) устранит тавтологии, сохранив полноценное тестирование продакшн-моделей `Profile` и `VlessServerEntry`.
