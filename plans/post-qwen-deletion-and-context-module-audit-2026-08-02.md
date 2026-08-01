# Повторный аудит удаления CustomDirectRules и границ модулей — 2026-08-02

Статус: read-only аудит рабочего кода. Продуктовый код в этом проходе не менялся.
Проверен diff `c26e8646..9cd37398` в draft PR #98, отдельно пройдены скрытые
ссылки, legacy-миграция/YAML, динамические UI-ссылки и физические границы
крупных модулей. Оценки токенов приблизительные (`bytes / 3.6`) и нужны для
сравнения размеров, а не для биллинга конкретной модели.

## Итог

- Удаление старого runtime `CustomDirectRules` безопасно: живой вызывающий код
  не потерян, замещающая функциональность `CustomRules` сохранена.
- Обратная совместимость старых YAML сохранена полностью: legacy-поле, модель,
  sane-нормализация, мигратор, source-generation регистрация и тесты миграции
  остались на месте.
- Дополнительного безопасного мёртвого runtime рядом с удалённым блоком не
  найдено.
- Найден только документальный хвост: несколько комментариев в
  `MainWindowViewModel.cs` всё ещё описывают удалённые alias-поля и старый
  Custom Direct Rules UI.
- Единственный файл, который действительно надо физически разбить ради
  контекста модели, — `MainWindowViewModel.cs` (около 102k токенов).

## 1. Перепроверка удаления

Фактический продуктовый/тестовый diff: 9 файлов, `+3 / -699`, три файла удалены
целиком; новый заменяющий код не добавлялся.

Проверено:

- `CustomDirectRulesParser`, `ApplyCustomDirectRules` и
  `BuildCustomDirectRouteRule` до удаления вызывались только собственными
  dedicated-тестами или друг другом;
- удалённые `CustomDirectRules*` localization aliases не имели XAML-, C#-,
  Java-, reflection- или source-generator потребителей;
- SDK-style glob compilation не содержит явных include удалённых файлов;
- текущий `CustomRulesParser` и `ConfigGenerator.ApplyCustomRules` остались
  живым runtime-путём;
- `SettingsLoader.Parse` по-прежнему вызывает sane-нормализацию и
  `SettingsMigrator.Migrate`; `Migrate_1_to_2` переносит
  `App.CustomDirectRules` в `CustomRules` и очищает legacy-список;
- `AppConfig.CustomDirectRules`, `CustomDirectRule`, `AppSettingsSane`,
  `SettingsMigrator`, `YamlStaticContext` и migration/roundtrip/robustness
  тесты не удалены.

Верификация реализации, уже выполненная в PR #98: Release build — 0 ошибок;
фокусный набор совместимости/current CustomRules — 53/53; commit CI — 3 green,
0 red. Локальный полный набор имел только 23 известных environment-failure на
неэлированной dev-машине из-за запрета записи в `C:\ProgramData\VPNRouter`;
они не связаны с diff.

### Найденный хвост

`VPNRouter.App/ViewModels/MainWindowViewModel.cs`:

- строки около 849-852 утверждают, что `CustomDirectRulesText` ещё служит
  upgrade-alias, хотя alias удалён раньше;
- строки около 1687-1696 содержат два подряд XML `<summary>`: старое описание
  v2.29 Custom Direct Rules и актуальное v2.30 Custom Rules;
- строки около 1190-1194 и 1706-1708 — уже не нужные исторические комментарии
  об удалённых alias-уведомлениях.

Это не runtime-дефект. Исправление — только убрать неверный/дублирующий текст,
не менять члены или поведение.

## 2. Измеренные крупные файлы

| Файл | Строк | Токены, прибл. | Решение |
|---|---:|---:|---|
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | 7 692 | 102 382 | Разбить обязательно |
| `VPNRouter.App/Views/Pages/NetworkPage.axaml` | 2 410 | 40 661 | Оставить целиком |
| `VPNRouter.Core/Localization/Strings.cs` | 1 938 | 34 176 | Оставить каталогом |
| `VPNRouter.Android/AndroidApp.axaml.cs` | 2 330 | 33 198 | Уже достаточно мал |
| `VPNRouter.App/Localization/Strings.cs` | 1 447 | 33 097 | Оставить каталогом |
| `VPNRouter.Android/VpnRouterService.java` | 2 080 | 30 356 | Оставить целиком |
| `VPNRouter.Core/Services/ConfigGenerator.cs` | 2 105 | 30 125 | Не дробить сейчас |
| `VPNRouter.Android/Localization.cs` | 971 | 28 453 | Оставить каталогом |

Семейство `MainWindowViewModel*.cs`: 14 файлов, около 177 636 токенов.
Семейство `AndroidApp*.cs`: 19 файлов, около 194 525 токенов. Эти суммы не
означают один модуль: существующие partial-файлы уже отделяют независимые
сценарии, поэтому модель должна загружать нужный concern, а не всю семью.

## 3. Практический предел контекста

Для дальнейшей разработки принять ориентиры, а не новый hard-gate:

- предпочтительно до 30k токенов на C#/Java-файл;
- до 40k допустимо для цельного XAML или localization-каталога;
- 50k — жёсткий порог для обычного продуктового файла;
- один end-to-end concern вместе с entrypoint и ключевыми зависимостями —
  предпочтительно до 60k, максимум около 100k;
- whole-review bundle — только после dry-run, подтверждающего не более 1M.

Так остаётся место для запроса, связанных файлов, тестов и ответа модели.
Число файлов само по себе не цель: разделение оправдано только там, где оно
совпадает с поведением приложения.

## 4. Обязательное разбиение MainWindowViewModel

Минимальный вариант — только перемещение существующих членов между partial
class без новых типов, интерфейсов, DI или изменения сигнатур:

| Новый/существующий partial | Текущий диапазон, прибл. | Размер, прибл. | Содержимое |
|---|---:|---:|---|
| `MainWindowViewModel.CustomRules.cs` | 827-2460 | 23k | текст/rows/filter/CRUD/validation custom rules |
| `MainWindowViewModel.SettingsPersistence.cs` | 2934-3994 | 15k | load/apply/save и синхронизация settings |
| `MainWindowViewModel.Connection.cs` | 3995-4514 | 8k | connect/disconnect/status lifecycle |
| `MainWindowViewModel.Zapret.cs` | 4515-6300 | 24k | Zapret orchestration |
| `MainWindowViewModel.TgProxy.cs` | 6301-6818 | 6k | Telegram proxy lifecycle/helpers |
| `MainWindowViewModel.Servers.cs` | 6819-7369 | 8k | server/config/reconnect flow |
| основной файл | остаток | около 20k | state, constructor и короткая cross-concern orchestration |

Диапазоны — карта текущего файла, не инструкция резать текст по строкам.
Перемещаются только целые members вместе с attributes, XML docs и `#if`.
Reflection-based characterization hash сортирует набор членов, поэтому чистое
перемещение без смены сигнатур должно сохранить hash; это обязательная проверка.

## 5. Что не разбивать

- `AndroidApp.axaml.cs`: каждый Android partial уже меньше примерно 33k.
  Опциональный перенос Simple-page builder в `AndroidApp.SimplePage.cs` даст
  косметический выигрыш, но сейчас не нужен.
- `ConfigGenerator.cs`: около 30k и один связный pipeline. Можно отдельно
  перенести current custom-rules methods в static partial, но выигрыш около 4k
  не оправдывает дополнительную навигацию.
- `NetworkPage.axaml`: Rules pane крупный, но новый UserControl разорвёт
  bindings/resources и усложнит визуальный аудит сильнее, чем поможет контексту.
- localization-каталоги: физический split создаст риск RU/EN drift. Огульное
  схлопывание App/Core forwarding также не является безопасным удалением.
- тесты, планы и evidence не удалять ради контекста; их надо исключать только
  из специального whole-review bundle, сохраняя доступными для поиска.

## 6. Карта модулей устарела

- `VPNRouter.App/CLAUDE.md` сообщает 10 sibling-partial-файлов и около 7 250
  строк в main; фактически 13 siblings (14 файлов с main) и 7 692 строки /
  около 102k токенов.
- `VPNRouter.Android/CLAUDE.md` сообщает 14 partial-файлов; фактически 19.
- `plans/v3.0-refactor-roadmap.md` всё ещё описывает Phase 2B/2C по старым
  размерам 6 753/7 177 строк и уже частично выполненным extraction-планам.

До физического split эти карты надо обновить одним проходом, чтобы следующая
модель не загружала неверный набор файлов и не предлагала уже выполненную работу.

## 7. Решение

1. PR #98 по удалению CustomDirectRules можно продолжать: потерянного runtime
   или legacy-совместимости не найдено.
2. Следующий отдельный механический PR — только split основного
   `MainWindowViewModel.cs` и обновление двух zone-карт/roadmap.
3. Небольшую чистку stale CustomDirectRules-комментариев включить в этот же
   механический PR.
4. Android, ConfigGenerator, XAML и localization сейчас не дробить.
