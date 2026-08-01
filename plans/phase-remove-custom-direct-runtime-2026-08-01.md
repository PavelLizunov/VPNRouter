# Phase F1 — Удаление мёртвого runtime CustomDirectRules (v2.29)

**Owner**: Qwen, non-interactive session, 2026-08-01
**Branch**: qwen/remove-custom-direct-runtime
**Roadmap ref**: `plans/qwen-context-footprint-and-code-reduction-audit-2026-08-01.md` §F1 · `plans/OPEN-DEFECTS.md`, секция "Codebase/context reduction audit — 2026-08-01", запись P2
**Effort**: ~1–2 часа
**Risk**: MEDIUM
**Risk rationale**: удаляется свыше 500 LOC, а совместимость legacy-схемы (миграция и YAML-контракт) — несущая.
**Blast radius**: 9 файлов · ≈ −697 LOC · нулевое изменение рантайма (мёртвый код)
**Rollback**: `git revert <commit>` / branch delete

## Why

Аудит 2026-08-01 (static / call-graph verified) подтвердил находку F1: runtime-поверхность CustomDirectRules из v2.29 — парсер, internal-генератор и UI-алиасы — мёртвая, производственных вызовов нет, живая функциональность давно перенесена в движок CustomRules (v2.30). Удаление подтверждённо мёртвой поверхности снимает ≈ 697 LOC (≈ 6–8k токенов) сопровождения и следа модели в контексте без удаления поведения или истории. Основание: `plans/qwen-context-footprint-and-code-reduction-audit-2026-08-01.md` (§F1) и открытая запись P2 в `plans/OPEN-DEFECTS.md`.

## What

Удалить при реализации:

- `VPNRouter.Core/Services/CustomDirectRulesParser.cs` — целиком (195/196 LOC).
- `VPNRouter.Tests/CustomDirectRulesParserTests.cs` — целиком (142/143 LOC).
- `VPNRouter.Tests/CustomDirectRulesGeneratorTests.cs` — целиком (238/239 LOC).
- `VPNRouter.Core/Services/ConfigGenerator.cs` — только internal `ApplyCustomDirectRules` и `BuildCustomDirectRouteRule` вместе с прилегающим документационным блоком Custom direct rules, останавливаясь строго перед блоком Russian geo bypass; исторический комментарий «superseded» оставить, если он остаётся точен.
- Четыре алиаса локализации CustomDirectRules (`CustomDirectRulesTitle`, `CustomDirectRulesDescription`, `CustomDirectRulesPlaceholder`, `CustomDirectRulesErrorHeader`) совместно из `VPNRouter.Core/Localization/Strings.cs`, `VPNRouter.App/Localization/Strings.cs`, `VPNRouter.Android/Localization.cs` — 12 объявлений суммарно, legacy-блок целиком из каждого файла.
- Две устаревшие строки инвентарной таблицы удалённых тестовых классов в `VPNRouter.Tests/CLAUDE.md`.
- `VPNRouter.Core/Services/CustomRulesParser.cs` — заменить/удалить XML-док `cref` на удалённый `CustomDirectRulesParser`, чтобы не осталось битого cref.

```diff
- VPNRouter.Core/Services/CustomDirectRulesParser.cs      /* файл целиком */
- VPNRouter.Tests/CustomDirectRulesParserTests.cs         /* файл целиком */
- VPNRouter.Tests/CustomDirectRulesGeneratorTests.cs      /* файл целиком */
  ConfigGenerator.cs:
-     /* блок Custom direct rules: ApplyCustomDirectRules, BuildCustomDirectRouteRule + прилегающий doc */
      /* Russian geo bypass — не трогать */
```

Явный список KEEP (несущая обратная совместимость legacy-схемы, не трогать):

- `AppConfig.CustomDirectRules` и yaml-алиас `custom_direct_rules` (`VPNRouter.Core/Models/AppConfig.cs`);
- модель `CustomDirectRule` целиком (`VPNRouter.Core/Models/CustomDirectRule.cs`);
- legacy-санитизацию в `VPNRouter.Core/Models/AppSettingsSane.cs`;
- `SettingsMigrator.Migrate_1_to_2` (`VPNRouter.Core/Services/SettingsMigrator.cs`);
- регистрацию `CustomDirectRule` в `VPNRouter.Core/Yaml/YamlStaticContext.cs`;
- `VPNRouter.Tests/CustomRulesV2_30_MigrationTests.cs` целиком и релевантные YAML round-trip/robustness-ассерты в `VPNRouter.Tests/YamlStaticContextRoundTripTests.cs` и `VPNRouter.Tests/SettingsLoaderRobustnessTests.cs`.

## How

Чистое удаление. Без абстракций, зависимостей, заменяющей функциональности, контекстного инструментария и новых тестов поведения: производственных вызовов нет, существующие миграционные и текущие тесты CustomRules сохраняются.

1. Удалить три файла целиком (парсер и два выделенных теста).
2. В `ConfigGenerator.cs` вырезать только `ApplyCustomDirectRules` и `BuildCustomDirectRouteRule` с прилегающим документационным блоком Custom direct rules, не заходя в Russian geo bypass.
3. Одним согласованным проходом удалить четыре алиаса локализации (`CustomDirectRulesTitle`, `CustomDirectRulesDescription`, `CustomDirectRulesPlaceholder`, `CustomDirectRulesErrorHeader`) из трёх файлов (Core, App, Android) — 12 объявлений суммарно.
4. Починить XML-док `CustomRulesParser.cs`: убрать/заменить `cref` на удалённый парсер.
5. Удалить две устаревшие инвентарные строки из `VPNRouter.Tests/CLAUDE.md`.
6. ripgrep-аудит ссылок: `CustomDirectRulesParser`, `ApplyCustomDirectRules`, `BuildCustomDirectRouteRule`, алиасы `CustomDirectRules*` — ожидать нуль ссылок вне KEEP-поверхности и исторических планов.
7. Прогнать верификационные ворота ниже, заполнить Outcome.

### Tests written

Не пишутся: задача — чистое удаление мёртвого кода, нового поведения нет. Защитой служат существующие миграционные тесты и текущие тесты CustomRules из KEEP-списка.

### Verification approach

- Явная Release-сборка решения на .NET 10: `dotnet build VPNRouter.sln -c Release` → 0 ошибок.
- Фокусные тесты: `CustomRulesV2_30_MigrationTests`, `YamlStaticContextRoundTripTests`, `SettingsLoaderRobustnessTests`, `CustomRulesV2_30_ParserTests`, `CustomRulesV2_30_GeneratorTests`.
- Полный набор тестов: дельту/счётчик после удаления зафиксировать по итогам верификации.
- Android Release-сборка (`-p:EnableAndroidTarget=true`), если присутствуют `libbox.aar` и тулчейн; иначе — документированный пропуск с причиной в Outcome.
- ripgrep-аудит ссылок (шаг 6).
- ponytail-review, так как дифф превышает 100 LOC.

## Verification gate

Отметить каждый по выполнении:

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` (.NET 10) → 0 ошибок. Android: также `-p:EnableAndroidTarget=true` при наличии `libbox.aar` и тулчейна, иначе документированный пропуск.
- [ ] **Gate 2 — Tests green**: фокусные классы зелёные, затем полный набор (с учётом двух удалённых выделенных классов).
- [ ] **Gate 3 — Docs**: Outcome заполнён. Пользовательских и архитектурных изменений нет, README/CLAUDE.md не меняются (кроме двух инвентарных строк в `VPNRouter.Tests/CLAUDE.md`).
- [ ] **Gate 4 — Self-review**: ponytail-review выполнен (дифф > 100 LOC). Security review: N/A — нет auth/TLS/исполнения процессов/файлового I/O/файрвола/поведения безопасности.
- [ ] **Gate 5 — MCP verify**: N/A — у алиасов нет привязок и вызывающих, видимых изменений UI нет.
- [ ] **Gate 6 — Characterization diff**: N/A — поверхности членов god-класса не меняются, это не разделение god-файла.

## Outcome (filled after implementation verification)

**Status**: READY FOR COMMIT CI — реализация и локальная верификация выполнены; полная зелёность CI пока не подтверждена и не утверждается.
**Commits**: `1258182e` (docs(plan): brief remove custom direct runtime) + реализационный коммит, содержащий этот Outcome.
**Pushed**: `<pending>` — немедленный пуш реализационного коммита.
**Test deltas**: +0 / −2 тестовых класса · −22 устаревших теста из двух удалённых тестовых классов
**Files changed**: 9 · +3 / −699 строк · 3 файла удалено целиком · заменяющего кода нет

**Gate results:**
- [x] Gate 1: PASS — явная Release-сборка решения на .NET 10: 0 ошибок, 227 предупреждений (все pre-existing). Android: документированный SKIP — Android SDK присутствует, но приватный `VPNRouter.Android/Lib/libbox.aar` отсутствует, поэтому локальная сборка Android-проекта невозможна.
- [x] Gate 2: PARTIAL LOCALLY / CI REQUIRED — фокусный фильтр совместимости/текущих правил: 53/53 passed. Полный локальный набор: 2663 passed, 2 skipped, 23 failed; каждый сбой — `UnauthorizedAccessException` при записи в `C:/ProgramData/VPNRouter` на неэлевированной dev-машине, с этим диффом не связано. Полное подтверждение — в CI.
- [x] Gate 3: PASS — инвентарь тестов обновлён, Outcome заполнён.
- [x] Gate 4: PASS — аудит ссылок: ноль живых ссылок на удалённые парсер/методы/алиасы; KEEP-файлы схемы и миграции не изменены; проверка диффа чистая. ponytail-review: ровно «Lean already. Ship.». Security review: N/A — нет auth/TLS/исполнения процессов/файлового I/O/файрвола/поведения безопасности.
- [-] Gate 5: N/A — нет UI-поведения
- [-] Gate 6: N/A — нет изменений поверхности god-класса

**Surprises encountered**:
- В safe-mode у Qwen отсутствовал инструмент удаления файлов, поэтому Qwen опустошил три файла, а их Git-удаление завершил Codex. Продуктовых сюрпризов нет.

**Follow-ups spawned**:
- F3 — мёртвая схема настроек и контекстный профиль остаются в `plans/OPEN-DEFECTS.md`; в этой фазе не реализовывать.

**Lessons for methodology doc** (if any):
- Нет сверх зафиксированного в Surprises.
