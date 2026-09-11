# Результаты аудита тестов роем Gemini: Пакет 6 (UI, ViewModels & Avalonia)

Дата: 2026-09-12
Аудиторы: Gemini Swarm (`gemini-3.8-flash-high`)
Охвачено файлов: **15**
Охвачено тестов: **134**

## Сводная статистика по Пакету 6

- **KEEP (Сохранить без изменений)**: **116** (86.6%) — высокоценные тесты логики ViewModels (состояния кнопок, двухфазный таймер подключения, синхронизация списков приложений, визард настройки, предупреждения о битых туннелях, рендеринг страниц).
- **MERGE / SIMPLIFY (Объединить)**: **13** (9.7%) — дублирующиеся промежуточные разрешения окон (например, 720px vs 529px vs 360px), парные тесты свойств.
- **DROP (Кандидаты на удаление)**: **5** (3.7%) — 3 «source-guards» (парсинг C# и XAML файлов текстом), 1 тест стандартного события Avalonia Button и 1 избыточный тест отписки таймера.

---

## Детализация по файлам Пакета 6

| # | Файл | Всего | KEEP | MERGE | DROP | Вердикт по файлу | Ключевые находки |
|---|---|---:|---:|---:|---:|---|---|
| 1 | `MainWindowViewModelModeCoherenceTests.cs` | 2 | 1 | 0 | 1 | CLEANUP | 1 тест поведения вкладок — KEEP. 1 тест проверки исходника C# текстом — DROP. |
| 2 | `MainWindowViewModelDeadConfigAlertTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Защита от ложного «зелёного» статуса в Simple Mode при неработающем сервере. |
| 3 | `MainWindowViewModelAppsModeTests.cs` | 32 | 27 | 4 | 1 | CLEANUP | Громадный высокоценный сьют изоляции списков Include/Exclude и кастомных приложений. 1 grep по XAML файлу — DROP. |
| 4 | `MainWindowViewModelConcurrencyAndDataLossTests.cs` | 3 | 2 | 0 | 1 | CLEANUP | Защита от потери пользовательских настроек при сбое профилей. 1 текстовый scraper исходника — DROP. |
| 5 | `MainWindowViewModelCharacterizationTests.cs` | 2 | 1 | 1 | 0 | CLEANUP | Защита от дрифта публичного API ViewModel через SHA-256 хеш. 1 дубль объединить. |
| 6 | `ServerViewModelSubtitleTests.cs` | 9 | 7 | 2 | 0 | CLEANUP | Форматирование субтитров протоколов (VLESS, Hy2, Naive, AWG). 2 дубля парных серверов объединить. |
| 7 | `ServerViewModelHealthVerdictTests.cs` | 11 | 11 | 0 | 0 | 100% KEEP | Полная матрица вердиктов здоровья серверов и отображения подсказок блокировок. |
| 8 | `SetupWizardViewModelTests.cs` | 6 | 6 | 0 | 0 | 100% KEEP | Стейт-машина мастера настройки: безопасный откат MTU, защита от несохранённых правок. |
| 9 | `SubscriptionViewModelBadgeTests.cs` | 4 | 4 | 0 | 0 | 100% KEEP | Статусные бейджи карточек подписок (кэшировано, недоступно, успех). |
| 10 | `UpdateNotificationViewModelTests.cs` | 10 | 10 | 0 | 0 | 100% KEEP | Уведомления об апдейтах, диалог отката версий и защита от гонок при параллельных проверках. |
| 11 | `HeadlessGuiTests.cs` | 6 | 4 | 1 | 1 | CLEANUP | 4 смоук-теста окон Avalonia. 1 тест клика кнопки Avalonia (тест фреймворка, а не логики приложения) — DROP. |
| 12 | `PageScreenshotTests.cs` | 23 | 20 | 2 | 1 | CLEANUP | Рендеринг всех страниц и узких вьюпортов. Промежуточное разрешение 500px (между 720px и 400px) — DROP. |
| 13 | `VisualDiffTests.cs` | 3 | 3 | 0 | 0 | 100% KEEP | Пиксельный регрессионный контроль верстки страниц через SkiaSharp (Windows-only). |
| 14 | `BoolToChevronConverterTests.cs` | 2 | 2 | 0 | 0 | 100% KEEP | Проверка XAML-конвертера стрелок/шевронов карточек. |
| 15 | `MvmTwoPhaseStartTimerTests.cs` | 19 | 16 | 2 | 1 | CLEANUP | Двухфазный таймер подключения (60с Phase A, 20с Phase B), фикс гонки NIGHT-07. 1 дубль проверки утечки — DROP. |

---

## Главные выводы аудита Пакета 6

1. **Высочайшая надежность и качество ViewModel тестов**:
   Комплекс тестов над `MainWindowViewModel`, `SetupWizardViewModel` и `ServerViewModel` гарантирует, что пользовательский интерфейс корректно отражает состояние ядра, не теряет пользовательские списки при фоновых обновлениях и защищает от ошибочных нажатий во время подключения.

2. **Точечные остатки «Source Guards»**:
   В `MainWindowViewModelModeCoherenceTests`, `MainWindowViewModelAppsModeTests` и `MainWindowViewModelConcurrencyAndDataLossTests` обнаружились единичные проверки, читающие `.cs` и `.axaml` файлы через `File.ReadAllText`. Они не несут пользы и должны быть удалены.

3. **Тесты Headless и скриншотов**:
   Скриншот-тесты `PageScreenshotTests` и `VisualDiffTests` являются важнейшим визуальным оракулом репозитория (не позволяют сломать XAML-разметку и отступы). Лишь пара промежуточных ширин экранов (например, 500px при наличии 400px и 360px) избыточна.
