# Triage открытых PR и исторического красного CI — 2026-07-29

## Scope / границы

- **Что покрыто:** 7 открытых PR (`#22, #29, #31, #32, #33, #42, #46`) + независимая
  диагностика merge commit `43649922` (PR `#45`) с упавшим `test`-чеком.
  Аудит-PR `#48` и `#49` намеренно исключены из скоупа.
- **Current main SHA:** `b39a28c32fae26838e615b5080d183dc33ee551b`
  (`Merge pull request #47`, HEAD `origin/main`).
- **Дата/время:** 2026-07-29 (среда).
- **Метод:** 8 параллельных read-only ревьюеров (по одному на PR и на commit) +
  master-pass с перепроверкой ключевых утверждений напрямую по `origin/main`.
- **Ограничения (read-only):** только `gh pr view/diff/checks`, `gh run view`,
  `gh api`, `git show/diff/log`, `rg`, чтение файлов. Без edits, build/test,
  `dotnet restore`, запуска приложений/бинарников, VM/WinRM/ADB/MCP, загрузок
  и любых мутаций PR (close/merge/approve/comment/rebase/rerun/update).
  PR-чеки трактуются как историческое свидетельство; каждый патч сверен с
  текущим `main`.
- **Маркировка:** `[FACT]` — проверено командой/файлом/git; `[INFER]` — вывод/рассуждение.

### Ключевой контекст зависимостей (current `origin/main`)

| Параметр | Значение | Источник |
|---|---|---|
| TFM / SDK | `net10.0` / `10.0.301` | `global.json`, csproj |
| Тест-стек | xUnit v3 (`xunit.v3 3.2.2`), `xunit.runner.visualstudio 3.1.5`, `Microsoft.NET.Test.Sdk 17.14.1`, `coverlet.collector 6.0.4` | `VPNRouter.Tests.csproj` |
| Avalonia | `12.0.4` (все `Avalonia.*`), `AvaloniaUI.DiagnosticsSupport 2.2.2` | `VPNRouter.App.csproj` |
| SkiaSharp | `3.119.4` (direct ref). Avalonia 12 транзитивно пинит `>= 3.119.4-preview.1.1`; более низкая stable → NU1605 | `VPNRouter.App.csproj` (комментарий) |
| YAML | `YamlDotNet 15.3.0` + `Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2` (намеренно coupled) | `VPNRouter.Core.csproj` |
| README build examples | `2.47.0-r13` | `README.md:182/187`, `README.ru.md:181/186` |
| Required check | job id `test` (`test.yml`, ubuntu; исключает Headless/PageScreenshot/VisualDiff) | `.github/workflows/CLAUDE.md` |

---

## Executive table (ровно 8 строк)

> **Update 2026-07-29:** статусы #29/#31/#32/#33 актуализированы после авторизованных
> follow-up действий (подробности — в «Follow-up actions — 2026-07-29» в конце файла).
> Строки #22/#42/#46 без изменений (KEEP, owner-gated).

| # | Объект | Состояние | Required `test` | Вердикт | Код-работа? |
|---|---|---|---|---|---|
| **#22** | coverlet.collector 6.0.4 → 10.0.1 | OPEN, MERGEABLE/BEHIND | green | **KEEP** | нет |
| **#29** | actions-all group, 9 updates | OPEN, MERGEABLE/BLOCKED | **green** (re-run 2026-07-29) | **KEEP** (ready; owner merge decision) | нет |
| **#31** | SkiaSharp 3.119.4 → 4.150.0 | **CLOSED** (2026-07-29) | **cancelled** (нет green) | **CLOSED_AS_STALE** (нет green evidence) | нет |
| **#32** | Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 → 18.1.0 | **CLOSED** (superseded by #71) | **red (CS0535)** | **REPLACED** → combined PR #71 (CI SUCCESS) | **да** (PR #71 green) |
| **#33** | YamlDotNet 15.3.0 → 18.1.0 | **CLOSED** (superseded by #71) | **red (CS0535)** | **REPLACED** → combined PR #71 (CI SUCCESS) | **да** (PR #71 green) |
| **#42** | docs(readme): build examples → 2.47.0 | OPEN, MERGEABLE/BEHIND | green | **KEEP** | нет |
| **#46** | Avalonia + 12 others (minor/patch group) | OPEN, MERGEABLE/**CLEAN** | **green** | **KEEP** | нет |
| **43649922** | merge PR #45, упавший `test` | merged (ancestor main) | red на предке, green на HEAD | **HISTORICAL_SUPERSEDED** | нет (опц. harden flaky test) |

---

## Детально по каждому PR

### PR #22 — `build: Bump coverlet.collector from 6.0.4 to 10.0.1`

- **Метаданные [FACT]:** OPEN, не draft, автор `app/dependabot`. head
  `dependabot/nuget/VPNRouter.Tests/coverlet.collector-10.0.1` → base `main`.
  `mergeable: MERGEABLE`, `mergeStateStatus: BEHIND`. Создан 2026-06-20,
  обновлён 2026-07-10 (rebase на net10.0 main). Чеки на head `70c8736`:
  `test` = success (~2m52s), `grep` = success, `characterization-windows` = skipped. 0 падающих.
- **Изменения [FACT]:** один файл/одна строка — `VPNRouter.Tests.csproj`:
  `coverlet.collector 6.0.4 → 10.0.1`.
- **Сравнение с main [FACT]:** main всё ещё пинит `6.0.4`; контекст диффа не
  менялся → патч ложится чисто (согласуется с MERGEABLE). `BEHIND` — следствие
  миграции net8.0→net10.0, строку coverlet она не трогала.
- **Риски [FACT+INFER]:** «10.0.1» — реальная версия: `test`-job делает явный
  `dotnet restore`, который завершился success → версия резолвится на NuGet
  (restore не проходит для несуществующей версии). Release notes в теле PR:
  8.0.0 — breaking (min .NET 8, xunit.v3), 10.0.0 — net9/net10 targets. Это
  совпадает со стеком репо (net10.0 + xUnit v3). `[INFER]` Остаточный риск
  низкий: CI никогда не вызывает coverage collection (нет
  `--collect:"XPlat Code Coverage"`), т.е. пакет ссылается, но не упражняется
  ни одним гейтом.
- **Overlap [FACT]:** #46 тоже правит `VPNRouter.Tests.csproj`, но только строки
  `Avalonia.Headless*` (12.0.4→12.1.0), НЕ coverlet → не дубликат, максимум
  текстовая упорядоченность при merge. Других coverlet-PR нет.
- **Вердикт: KEEP.** Легитимный, изолированный, зелёный dependabot-bump.
- **Next action:** merge по решению владельца; `BEHIND` косметический
  (dependabot сам rebase'ит). Код-работа не нужна.

### PR #29 — `ci: bump the actions-all group across 1 directory with 9 updates`

- **Метаданные [FACT]:** OPEN, не draft, `app/dependabot`. head
  `dependabot/github_actions/actions-all-37c2115bdc` (`41f2ab1`) → `main`.
  `baseRefOid == b39a28c3` = текущий `origin/main` → PR свеже-rebased, НЕ stale.
  `mergeable: MERGEABLE`, `mergeStateStatus: BLOCKED` (блок только красным
  required-чеком, не конфликтом). Чеки: `test` = **FAILURE**; `grep` = success;
  `test-update` = success; `characterization-windows` = skipped.
- **9 бампов [FACT]:**

  | Action | old | new | на main? |
  |---|---|---|---|
  | actions/checkout | v6.0.3 | v7.0.1 | нет |
  | actions/setup-dotnet | v5.3.0 | v6.0.0 | нет |
  | actions/cache | v5.0.5 | v6.1.0 | нет |
  | actions/setup-java | v5.2.0 | v5.6.0 | нет |
  | actions/setup-go | v5 / v6.4.0 | v7.0.0 | нет |
  | github/codeql-action/init | v3 `dd903d2` | v3 `e4fba86` | нет |
  | github/codeql-action/analyze | v3 `dd903d2` | v3 `e4fba86` | нет |
  | actions/upload-artifact (sign-windows.yml) | v4 | v7 | нет |
  | signpath/github-action-submit-signing-request | v1 | v2 | нет |

- **Сравнение с main [FACT]:** grep по `origin/main:.github/workflows/*.yml` —
  все 9 «old»-пинов совпадают с main байт-в-байт. **0/9 stale, 9/9 live.**
  Конфликта нет.
- **Риски [FACT+INFER]:** бампы #1 (checkout) и #2 (setup-dotnet) трогают
  required `test`-job — рутинные major-bump, SHA-pinned. Красный `test`
  (run 30138066328): **2528 passed / 2 failed / 47 skipped**; оба фейла —
  `DeepVerifyProbeCancellationTests.ExternalCancellation_Rethrows_NotHttpTimeout`
  и `.ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout` — сетевые/тайминговые
  тесты. `[INFER]` Это флейк, не связанный с версиями actions (checkout/setup-dotnet/cache
  не меняют HTTP-cancellation логику). Main на базе `b39a28c` был green; тот же
  тест фейлил и на `4364992` (см. ниже) — предсуществующая flakiness.
  Security-sensitive: checkout v7, codeql SHA advance, code-signing путь
  (`upload-artifact v4→v7`, `signpath v1→v2`). `[INFER]` `sign-windows.yml`
  использует mutable tag-пины (`@v7`,`@v2`) при конвенции full-SHA — не блокер.
- **Overlap [FACT]:** единственный actions-PR; ни один NuGet/docs PR не трогает
  `.github/workflows/` → файловых пересечений нет.
- **Вердикт: KEEP** (merge заблокирован флейком, не контентом).
- **Next action:** код-работа не нужна. Требуется (мутация, вне read-only):
  re-run упавшего `test` (или close/reopen PR) для очистки флейка
  `DeepVerifyProbeCancellationTests`; после green — merge. Если останется красным —
  чинить флейк отдельно на main, не блокая бампы actions.

### PR #31 — `build: Bump SkiaSharp from 3.119.4 to 4.150.0`

- **Метаданные [FACT]:** OPEN, не draft, `app/dependabot`. head
  `dependabot/nuget/VPNRouter.App/SkiaSharp-4.150.0` → `main`. `MERGEABLE`/`BEHIND`.
  Чеки: `dotnet test/test` = **CANCELLED** (15m14s), `grep` ✓, `characterization`
  skipped. **Green build/test evidence отсутствует** — реальный test-job не завершился.
- **Изменения [FACT]:** один файл `VPNRouter.App.csproj`, одна строка:
  `SkiaSharp 3.119.4 → 4.150.0`. Устаревший комментарий про NU1605 не обновлён.
- **Сравнение с main [FACT]:** main пинит `SkiaSharp 3.119.4` + `Avalonia 12.0.4`.
  Комментарий csproj: Avalonia пинит `SkiaSharp >= 3.119.4-preview.1.1`, ниже — NU1605.
- **Риски [INFER, high]:** NU1605 срабатывает на downgrade; direct `4.150.0` >
  транзитивного `3.119.4-preview` → restore, вероятно, пройдёт. НО `Avalonia.Skia`
  из Avalonia 12.0.4 скомпилирована против SkiaSharp **3.x**; форсировать **4.x**
  под ней — major-version mismatch 3→4 → вероятный managed/native ABI-слом рендера.
  Репо не доказывает 4.x-совместимость (комментарий аттестует только 2→3).
  `SKGraphics`-статики 3→4 — не доказуемо из репо. Реальность 4.150.0 подтверждена
  лишь фактом генерации PR dependabot'ом.
- **Overlap [FACT]:** #46 (Avalonia 12.0.4→12.1.0, новее и CLEAN) **не трогает**
  строку SkiaSharp → не строгий дубликат. НО оба правят один hunk `VPNRouter.App.csproj`
  → **конфликтуют** (чисто ляжет только один). `[INFER]` #46 меняет транзитивный
  SkiaSharp-пин, поэтому корректный direct-ref надо выводить ПОСЛЕ #46, а не ставить 4.x сейчас.
- **Вердикт: BLOCKED_PENDING_EVIDENCE** (склоняется к CLOSE_AS_STALE).
- **Next action:** НЕ merge'ить as-is. Последовательность: (1) сначала #46;
  (2) после #46 прогнать restore+build+отменённый `dotnet test` против SkiaSharp 4.150.0
  (проверить NU1605/NU1608 и ABI Avalonia.Skia, узнать транзитивный пин Avalonia 12.1.0);
  (3) если 4.x несовместима (ожидается) — закрыть #31 как stale /
  `@dependabot ignore this major version`, pin SkiaSharp под Avalonia 12.1.0.
  Код-работа пока не обоснована — блокер в недостающем evidence.

### PR #32 — `build: Bump Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 -> 18.1.0`

- **Метаданные [FACT]:** OPEN, не draft, `app/dependabot`. head
  `dependabot/nuget/VPNRouter.Core/Vecc.YamlDotNet.Analyzers.StaticGenerator-18.1.0`
  → `main`. `MERGEABLE`/`BEHIND`. **CI: FAILING** — `dotnet test/test` = X
  (run 29134589534); `grep` ✓; characterization skipped.
- **Изменения [FACT]:** один файл/строка — `VPNRouter.Core.csproj`: analyzer
  `15.1.2 → 18.1.0` (PrivateAssets=all). **YamlDotNet НЕ трогает** (остаётся 15.3.0).
- **Сравнение с main [FACT]:** main = YamlDotNet 15.3.0 + analyzer 15.1.2 (coupled;
  комментарий csproj явно требует lockstep). Использование: `Yaml/YamlStaticContext.cs`,
  `Services/SettingsLoader.cs` (`StaticDeserializerBuilder`/`StaticSerializerBuilder`),
  `Yaml/DateTimeOffsetYamlConverter.cs` (hand-written shim, т.к. «StaticGenerator 15.1.2
  omits DateTimeOffset»).
- **Риски [FACT]:** coupling mismatch подтверждён логом CI — сгенерированный
  18.1.0-анализатором код не компилируется против runtime 15.3.0:
  `YamlDotNetAutoGraph.g.cs(701,40): error CS0535: 'StaticTypeInspector' does not
  implement interface member 'ITypeInspector.GetProperty(Type, object?, string, bool)'`.
  18.1.0 — реальная версия (CI восстановил пакет). `[INFER]` Даже с парным runtime
  18.1.0 breaking change 18.0.0 (`ITypeInspector`/`TypeInspectorSkeleton` +методы) и
  default max-recursion 130 меняют static-builder surface; shim `DateTimeOffsetYamlConverter`
  требует ревалидации.
- **Overlap [FACT]:** #33 — комплементарная половина (YamlDotNet only, analyzer НЕ
  трогает) → тоже сломан в одиночку. #46 оставляет YamlDotNet 15.3.0 и analyzer не
  трогает → не связан. Ни #33, ни #46 не бампят analyzer → #32 не дубликат. #32+#33 —
  две половины одного lockstep-upgrade.
- **Вердикт: REPLACE** (красный/заблокен as-is; отдельно не merge'ить).
- **Next action:** **код-работа нужна.** Один combined PR: `YamlDotNet 15.3.0 → 18.1.0`
  **И** analyzer `15.1.2 → 18.1.0` в одном коммите + фикс `DateTimeOffsetYamlConverter`
  (см. #33). Затем build clean (CS0535 ушёл) + `YamlStaticContextRoundTripTests` +
  v2.28.x regression + проверка max-depth-130. После green — закрыть #32 и #33 как superseded.

### PR #33 — `build: Bump YamlDotNet 15.3.0 -> 18.1.0`

- **Метаданные [FACT]:** OPEN, не draft, `app/dependabot`. head
  `dependabot/nuget/VPNRouter.Core/YamlDotNet-18.1.0` → `main`. `MERGEABLE`/`BEHIND`.
  Чеки: 1 failing (`dotnet test/test`), `grep` ✓, characterization skipped.
- **Изменения [FACT]:** ровно одна строка `VPNRouter.Core.csproj`: `YamlDotNet 15.3.0 → 18.1.0`.
  **Analyzer НЕ трогает** (15.1.2) → coupling ломается с этой стороны.
- **Сравнение с main [FACT]:** main = 15.3.0 + 15.1.2 (coupled). API-surface в Core:
  `StaticDeserializerBuilder/StaticSerializerBuilder` (SettingsLoader.cs:372,531),
  рефлективные `DeserializerBuilder/SerializerBuilder` (DiagnosticsRedactor.cs:154,157;
  ClashYamlParser.cs:77), hand-written `IYamlTypeConverter` (DateTimeOffsetYamlConverter.cs),
  `[YamlStaticContext]` (YamlStaticContext.cs).
- **Риски [FACT, из CI-log]:** PR **не компилируется** — 10× CS0535 (run 29134596470), 2 категории:
  1. **Слом hand-written кода (breaking 16.0.0):** `DateTimeOffsetYamlConverter` не
     реализует `IYamlTypeConverter.ReadYaml(IParser, Type, ObjectDeserializer)` /
     `WriteYaml(IEmitter, object?, Type, ObjectSerializer)`. **Master-pass подтвердил:**
     на `origin/main` сигнатуры всё ещё старые — `ReadYaml(IParser parser, Type type)`
     (строка 55) и `WriteYaml(IEmitter emitter, object? value, Type type)` (строка 74).
     → **нужна реальная правка исходника**, не только бамп версии.
  2. **Слом analyzer-coupling:** analyzer 15.1.2 генерирует `StaticPropertyDescriptor`/
     `StaticTypeInspector`, не реализующие новые `IPropertyDescriptor.AllowNulls/Required/
     ConverterType` и `ITypeInspector.GetProperty/GetEnumName/...` из YamlDotNet 18.
  `[INFER]` Доп. риски 18.x (compile падает раньше): default max-recursion 130 (18.1.0),
  `Mark/Cursor/SimpleKey` int→long (16.0.0) — нужен changelog/NuGet для полного impact.
- **Overlap [FACT]:** #32 — парный analyzer-bump (создан на 13с раньше), тоже фейлит
  `test`. #32+#33 неделимы (каждый ломает coupling со своей стороны). #46 в Core.csproj
  меняет **только Serilog 4.0.0 → 4.4.0**; YamlDotNet — неизменный контекст, analyzer не
  трогает → **#46 НЕ supersede'ит #32/#33**.
- **Вердикт: REPLACE** (не merge'ить отдельно; неделим с #32 + требует фикс конвертера).
- **Next action:** **код-работа нужна.** (1) Объединить #32+#33 в один PR; (2) править
  `VPNRouter.Core/Yaml/DateTimeOffsetYamlConverter.cs` под интерфейс 16.0.0+
  (`ReadYaml(IParser, Type, ObjectDeserializer)`, `WriteYaml(IEmitter, object?, Type, ObjectSerializer)`;
  лишние делегаты для скалярного конвертера можно игнорировать); (3) re-run CI +
  round-trip `YamlStaticContextRoundTripTests` + проверка max-depth-130. #46 независим.

### PR #42 — `docs(readme): bump build examples to 2.47.0`

- **Метаданные [FACT]:** OPEN, не draft, автор `PavelLizunov`. head
  `docs/readme-v2.47.0` → `main`. Создан/обновлён 2026-07-14 (с тех пор не обновлялся).
  `MERGEABLE`/`BEHIND`. Чеки: 2 success, 1 skipped, 0 failing.
- **Изменения [FACT]:** `README.md` (182/187) и `README.ru.md` (181/186):
  `build.ps1 -Version "2.47.0-r13"` → `"2.47.0"`; `./build-mac.sh 2.47.0-r13` → `2.47.0`.
  В ru.md также срезан trailing whitespace (безвредно).
- **Сравнение с main [FACT]:** `origin/main` всё ещё показывает `2.47.0-r13`
  (подтверждено master-pass grep). «from»-строки PR совпадают с main, «to»-строк на main
  НЕТ. `git log origin/main -- README.md`: последний README-коммит = `aecdd140` (#38) —
  stable-бамп так и не применён. Stable `v2.47.0` **существует** (isPrerelease=false,
  published 2026-07-14T06:39:04Z — за 32 мин до PR). `[INFER]` Это пропущенный post-stable
  `update-readme-versions` бамп — PR актуален и нужен, не stale/дубликат.
- **Риски [INFER]:** функциональных нет — docs-only, 4 строки. MERGEABLE → конфликта нет.
- **Overlap [FACT]:** ни один другой PR не трогает build-example строки README.
- **Вердикт: KEEP.**
- **Next action:** готов к merge as-is (по решению владельца). Опц. rebase (BEHIND),
  но MERGEABLE позволяет обычный merge. Код-работа не нужна.

### PR #46 — `build: Bump Avalonia and 12 others`

- **Метаданные [FACT]:** OPEN, не draft, `app/dependabot`. head
  `dependabot/nuget/VPNRouter.App/nuget-minor-patch-4eb12513dc` → `main`.
  Создан 2026-07-18, обновлён 2026-07-25. `mergeable: MERGEABLE`, `mergeStateStatus: CLEAN`.
  Чеки: **все success** — `dotnet test/test` ✓ (3m16s), `grep` ✓, characterization skipped.
  Required `test` = **GREEN**. Ветка `nuget-minor-patch` = minor/patch-группа Dependabot;
  четыре других NuGet-PR (#22/#31/#32/#33) — **major** бампы, которые Dependabot намеренно
  держит отдельно (поэтому наборы пакетов не пересекаются).
- **13 бампов [FACT, проверено master-pass по `gh pr diff 46`]:**

  | # | Пакет | old → new | csproj |
  |---|---|---|---|
  | 1 | Avalonia | 12.0.4 → 12.1.0 | App |
  | 2 | Avalonia.Desktop | 12.0.4 → 12.1.0 | App |
  | 3 | Avalonia.Themes.Fluent | 12.0.4 → 12.1.0 | App |
  | 4 | Avalonia.Fonts.Inter | 12.0.4 → 12.1.0 | App |
  | 5 | Avalonia.HarfBuzz | 12.0.4 → 12.1.0 | App |
  | 6 | AvaloniaUI.DiagnosticsSupport | 2.2.2 → 2.2.3 | App |
  | 7 | Serilog | 4.3.0→4.4.0 (CLI/Service), **4.0.0→4.4.0** (Core) | CLI/Core/Service |
  | 8 | Spectre.Console | 0.49.1 → 0.57.2 | CLI |
  | 9 | Spectre.Console.Cli | 0.49.1 → 0.55.0 | CLI |
  | 10 | Microsoft.Extensions.Hosting | 10.0.8 → 10.0.10 | Service |
  | 11 | Microsoft.Extensions.Hosting.WindowsServices | 10.0.8 → 10.0.10 | Service |
  | 12 | Avalonia.Headless | 12.0.4 → 12.1.0 | Tests |
  | 13 | Avalonia.Headless.XUnit | 12.0.4 → 12.1.0 | Tests |

  **НЕ трогает** (context без изменений): SkiaSharp `3.119.4`, YamlDotNet `15.3.0`,
  analyzer `15.1.2`, CommunityToolkit.Mvvm `8.4.2`, coverlet.collector `6.0.4`,
  xunit.v3 `3.2.2`, xunit.runner.visualstudio `3.1.5`, Microsoft.NET.Test.Sdk `17.14.1`.
- **Сравнение с main [FACT]:** все «old»-версии совпадают с main; CLEAN подтверждает
  актуальность базы. Все 13 бампов **live** (не stale, не конфликт). Rebase не нужен
  (в отличие от #22/#31/#32/#33, которые BEHIND).
- **Риски [FACT+INFER]:** Avalonia 12.1.0 vs SkiaSharp-пин: зелёный `test` сделал
  restore+build Tests→App (SkiaSharp 3.119.4 + Avalonia 12.1.0) → NU1605 бы упал на
  restore; green CI = прямое доказательство чистого графа. `[INFER]` Доказано только для
  desktop TFM, не для opt-in `net10.0-android`. Headless.XUnit 12.1.0 vs xunit.v3 3.2.2 —
  констрейнт выполнен (green CI). YamlDotNet/analyzer coupling — N/A (#46 их не трогает).
  Spectre.Console 0.49.1→0.57.2 — самый рискованный бамп (большой minor-jump, изменения
  CLI-поведения: IConfigurator return type, version/help formatting, async overloads);
  green CI компилирует CLI, но `[INFER]` поведенческие изменения (help/`--version`) могут
  быть не покрыты unit-тестами — главная вещь для post-merge smoke.
- **SUPERSEDE-таблица [FACT, ключевой вывод]:**

  | PR | Пакет | В #46? | #46 supersede'ит? |
  |---|---|---|---|
  | #22 | coverlet.collector 6.0.4→10.0.1 (major) | нет | **НЕТ** |
  | #31 | SkiaSharp 3.119.4→4.150.0 (major) | нет | **НЕТ** |
  | #32 | analyzer 15.1.2→18.1.0 (major) | нет | **НЕТ** |
  | #33 | YamlDotNet 15.3.0→18.1.0 (major) | нет | **НЕТ** |

  **#46 НЕ supersede'ит ни один из #22/#31/#32/#33.** Гипотеза задачи не подтверждается:
  #46 — minor/patch-группа, остальные четыре — major-бампы, которые Dependabot маршрутизирует в
  отдельные PR; они сосуществуют by design и не пересекаются по пакетам. → Ни один из
  #22/#31/#32/#33 нельзя закрывать как дубликат #46.
- **Вердикт: KEEP.** Чистый, зелёный, MERGEABLE/CLEAN; все 13 бампов live; не конфликтует
  и не дублирует. Не stale, не дубликат, не заблокирован.
- **Next action:** владелец может merge'ить as-is. Код-работа не нужна. Рекомендованный
  (не обязательный) post-merge smoke CLI (`--help`, `--version`, prompt) из-за Spectre-jump.
  `[INFER]` остаточные риски: Android TFM не упражняется CI против Avalonia 12.1.0; CLI-поведение Spectre.

---

## Commit 43649922 / PR #45 — упавший `test`

- **Точный упавший check run [FACT]:** name `test`, conclusion `failure`, status completed.
  run id **29481203691**, job id 87565122788.
  URL: `https://github.com/PavelLizunov/VPNRouter/actions/runs/29481203691/job/87565122788`.
  Триггер: `push` на merge commit 43649922 (merge PR #45 в main), ~2026-07-16.
  Build/Restore green; упал шаг "Test". Аннотация: `X Process completed with exit code 1.`
- **Сигнатура фейла [FACT]:** ровно один упавший тест:
  `VPNRouter.Tests.DeepVerifyProbeCancellationTests.ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout [63 ms]`.
  ```
  Assert.Equal() Failure: Strings differ
  Expected: "http timeout"
  Actual: "http: An error occurred while establishing a conne"···
  at .../DeepVerifyProbeCancellationTests.cs:line 55
  ```
  Механизм (`DeepVerifyProbe.ProbeViaSocksAsync`, строки 124–140): probe мапит
  `TaskCanceledException → "http timeout"`, но `HttpRequestException → "http: {Short(hx.Message)}"`.
  Тихий loopback-listener принимает TCP, но не завершает SOCKS-handshake → **гонка** между
  500мс `HttpClient.Timeout` (→ TaskCanceledException → ожидаемый "http timeout") и
  SOCKS/connection-слоем, бросающим `HttpRequestException` первым (→ упавший путь).
  **Не связано с PR #45:** PR #45 менял только `SingBoxManagerProcessExitLeakTests.cs`
  (`git show --stat`: 1 файл, +29/−27). Фейлящий тест последний раз трогал `d03412c6`.
- **Воспроизводится ли на current main [FACT]:** код теста И probe **байт-идентичен**
  между 43649922 и b39a28c3 (`git diff` пуст). `Assert.Equal("http timeout", err)` на строке 55
  не изменился на HEAD (подтверждено master-pass grep). `[INFER]` Латентная тайминговая
  чувствительность остаётся, но это НЕ детерминированный/статический фейл — гонка, которая
  срабатывает только при неудачном тайминге раннера.
- **Green ли новые коммиты [FACT]:** `704d22e3` ("feat: deliver registered VPNRouter mission")
  — `test` = success (также grep/test-update success). PR #47 (b39a28c3) — `dotnet test/test` = success.
  Тот же неизменный тест прошёл → фейл 43649922 не воспроизвёлся.
- **Вердикт: HISTORICAL_SUPERSEDED** (корневая причина — **flaky test**).
  Красный X стоит на уже влитом предке текущего main. HEAD (b39a28c3) и его контент-коммит
  (704d22e3) green по `test`. Фейл — разовый тайминговый флейк в `DeepVerifyProbeCancellationTests`,
  не связанный с диффом PR #45, не повторился. Не реальная регрессия, не characterization
  hash drift, не инфра-аут. Branch protection смотрит на последний коммит (green) — этот
  исторический красный ничего не блокирует.
- **Минимальное безопасное действие:** ничего не требуется для разблокировки — main green.
  Опц. для человека (не выполнялось): (a) сделать `ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout`
  детерминированным (принять либо `"http timeout"`, либо `"http: …"` connection-error строку,
  либо вести harness так, чтобы выигрывал только timeout-путь); и/или (b) re-run check на
  run 29481203691 ради чистой истории. Ни одно не меняет текущее состояние main.
- **Полнота evidence:** полные логи упавшего job доступны (не истекли); assertion, stack trace
  и оба исходника прочитаны напрямую. Блокеров нет.

> **Перекрёстная находка:** тот же flaky `DeepVerifyProbeCancellationTests` объясняет и
> красный `test` в PR #29 (run 30138066328, 2 фейла этого класса). Т.е. оба красных чека
> (#29 и 43649922) — одна и та же латентная flakiness на `origin/main`, а не два разных дефекта.

---

## Dependency / overlap matrix (7 открытых PR)

Строки/столбцы = PR. Ячейка = характер пересечения.

| | #22 | #29 | #31 | #32 | #33 | #42 | #46 |
|---|---|---|---|---|---|---|---|
| **#22** coverlet (Tests) | — | нет | нет | нет | нет | нет | текст. же файл (Tests.csproj), разные строки |
| **#29** actions (.github) | нет | — | нет | нет | нет | нет | нет |
| **#31** SkiaSharp (App) | нет | нет | — | нет | нет | нет | **конфликт hunk** App.csproj; #46 меняет транзитивный SkiaSharp-пин (секвенс) |
| **#32** analyzer (Core) | нет | нет | нет | — | **парный coupling** (нерасторжимы) | нет | нет (#46 не трогает YAML) |
| **#33** YamlDotNet (Core) | нет | нет | нет | **парный coupling** (нерасторжимы) | — | нет | нет (#46 меняет рядом только Serilog) |
| **#42** readme (docs) | нет | нет | нет | нет | нет | — | нет |
| **#46** Avalonia+12 (App/CLI/Core/Service/Tests) | текст. Tests.csproj | нет | конфликт hunk App.csproj | нет | нет | нет | — |

**Ключевые связи:**
- **#32 ↔ #33:** жёсткая положительная связь — analyzer и YamlDotNet coupled по версии;
  оба PR красные по отдельности (CS0535) и должны стать ОДНИМ combined PR + фикс конвертера.
- **#31 ↔ #46:** конфликт по hunk в `VPNRouter.App.csproj`; #46 (Avalonia 12.1.0) меняет
  транзитивный SkiaSharp-пин → #31 нужно ре-выводить ПОСЛЕ #46. #46 при этом НЕ бампит SkiaSharp.
- **#22 ↔ #46:** оба правят `VPNRouter.Tests.csproj`, но разные строки (coverlet vs Headless) —
  тривиальная текстовая упорядоченность, не семантический конфликт.
- **#29, #42:** полностью изолированы (`.github/workflows/` и `README*.md` соответственно).
- **Supersede:** ни один PR не является дубликатом другого; #46 НЕ supersede'ит major-PR
  (#22/#31/#32/#33) — наборы пакетов дизъюнктны (minor/patch vs major группы Dependabot).

---

## Финальная очередь действий (упорядочена)

Принцип: **никогда не рекомендовать merge при красном required-чеке или недостающем evidence.**

1. **#46 — merge первым** (green, CLEAN, 13 live бампов). Среди dependency-PR идёт первым,
   т.к. меняет транзитивный SkiaSharp-пин Avalonia и переопределяет базу для #31.
   Код-работа не нужна. Пост-merge: smoke CLI (Spectre 0.49→0.57).
2. **#42 — merge** (green docs, актуален, main всё ещё `2.47.0-r13`, stable `v2.47.0` существует).
   Код-работа не нужна.
3. **#22 — merge** (green, реальная версия 10.0.1, изолирован). Код-работа не нужна.
   (Позиции 2–3 взаимозаменяемы по порядку.)
4. **#29 — НЕ merge'ить сейчас** (required `test` красный из-за флейка
   `DeepVerifyProbeCancellationTests`). Контент корректен (9/9 live, base==main).
   Действие: re-run упавшего `test` (мутация, вне read-only); после green — merge.
   Если флейк устойчив — чинить тест отдельно на main (см. шаг 7), не блокая #29 надолго.
   **Update 2026-07-29:** re-run выполнен удалённо (run `30138066328`, attempt 2 = SUCCESS) →
   `test` green, флейк подтверждён. PR ready; merge по решению владельца. Код-работа не нужна.
5. **#32 + #33 — НЕ merge'ить по отдельности** (оба красные, CS0535). **REPLACE:** открыть
   один combined lockstep PR (`YamlDotNet 15.3.0 → 18.1.0` + analyzer `15.1.2 → 18.1.0`)
   **с правкой `DateTimeOffsetYamlConverter.cs`** под интерфейс 16.0.0+
   (`ReadYaml(IParser, Type, ObjectDeserializer)` / `WriteYaml(IEmitter, object?, Type, ObjectSerializer)`).
   Верификация: build clean + `YamlStaticContextRoundTripTests` + v2.28.x regression + max-depth-130.
   После green — закрыть #32 и #33 как superseded. **Код-работа нужна.**
   **Update 2026-07-29:** combined replacement **PR #71** создан (commit `54c069ce`; парный бамп
   YamlDotNet + Vecc generator 18.1.0 + адаптация сигнатур конвертера). Remote CI **SUCCESS**
   (run `30456780192`: restore + build + full test). #32 и #33 **закрыты** как superseded by #71.
6. **#31 — НЕ merge'ить** (BLOCKED_PENDING_EVIDENCE; test-job cancelled, ABI-риск 3→4).
   После шага 1 (#46): прогнать restore+build+`dotnet test` против SkiaSharp 4.150.0;
   если 4.x несовместима с Avalonia.Skia (ожидается) — закрыть #31 как stale /
   `@dependabot ignore this major version`, pin SkiaSharp под транзитивный пин Avalonia 12.1.0.
   **Update 2026-07-29:** re-run (run `29134583027`, attempt 2) — restore+build success, но
   `test` снова ушёл в 15-минутный cancellation; green evidence нет. PR #31 **закрыт** как
   stale/unsafe (comment `5118508265`). Дальнейших действий не требуется.
7. **43649922 — действий не требуется** (HISTORICAL_SUPERSEDED; main green). Опц. harden
   `DeepVerifyProbeCancellationTests.ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout`
   против тайминговой гонки — это же снимает флейк-блокер с #29 (шаг 4).

---

## Инвариант полноты

- **Открытые PR: 7/7** — #22, #29, #31, #32, #33, #42, #46. Каждая строка присутствует в
  executive table, детальном разделе и dependency-матрице. Пропусков и дублей строк нет.
- **Исторический красный: 1/1** — commit 43649922 / PR #45 обработан (точный run URL,
  сигнатура, статус на current main, вердикт HISTORICAL_SUPERSEDED).
- **Master-pass:** все 8 результатов перепроверены по `origin/main`
  (пакетный набор #46 через `gh pr diff 46`; сигнатуры `DateTimeOffsetYamlConverter`;
  assertion `DeepVerifyProbeCancellationTests.cs:55`; README `2.47.0-r13`).
- **Read-only соблюдён:** создан/изменён только этот файл
  (`plans/qwen-open-pr-triage-2026-07-29.md`); никаких мутаций PR/CI/кода не выполнялось.

---

## Follow-up actions — 2026-07-29 (post-snapshot addendum)

> Датированное дополнение к read-only snapshot'у выше. Исходные FACT-секции
> («Детально по каждому PR») не переписаны — этот раздел фиксирует авторизованные
> follow-up действия, выполненные после snapshot'а, и актуализирует статусы, которые
> иначе вводили бы в заблуждение. Merge/release здесь НЕ утверждаются — только
> состояние PR/CI. Маркировка `[FACT]`/`[INFER]` сохранена.

- **PR #29 — green, ready (owner-gated) [FACT].** Упавший `test` пере-запущен удалённо:
  workflow run `30138066328`, **attempt 2 = SUCCESS**. Предыдущий фейл подтверждён как
  флейк `DeepVerifyProbeCancellationTests` (тот же класс, что на commit 43649922). PR теперь
  green и готов к merge по решению владельца. Код-изменений нет.
- **PR #31 — CLOSED, нет green evidence [FACT].** Workflow run `29134583027` пере-запущен
  удалённо: attempt 2 успешно сделал restore+build, но `test` снова шёл до 15-минутного
  cancellation. Green test-evidence так и не получено → PR закрыт как stale/unsafe.
  Комментарий закрытия: https://github.com/PavelLizunov/VPNRouter/pull/31#issuecomment-5118508265
  `[INFER]` Закрытие согласуется с прежним BLOCKED_PENDING_EVIDENCE → CLOSE_AS_STALE
  (ABI-риск SkiaSharp 3→4 из детальной секции подтвердить зелёным прогоном не удалось).
- **PR #32 / #33 — CLOSED, superseded by green PR #71 [FACT].** Вместо двух красных PR открыт
  один combined replacement: **PR #71** (https://github.com/PavelLizunov/VPNRouter/pull/71),
  commit `54c069ce` — парный бамп `YamlDotNet 15.3.0 → 18.1.0` +
  `Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 → 18.1.0` плюс адаптация сигнатур
  `DateTimeOffsetYamlConverter` под интерфейс 16.0.0+. Remote CI **SUCCESS** (run `30456780192`:
  restore + build + full test). #32 и #33 **закрыты** как superseded by #71 с комментариями:
  #32 https://github.com/PavelLizunov/VPNRouter/pull/32#issuecomment-5118547247,
  #33 https://github.com/PavelLizunov/VPNRouter/pull/33#issuecomment-5118551489.
  `[INFER]` Merge #71 остаётся owner-gated.
- **PR #66 — CLOSED, superseded by green #67 [FACT].** Закрыт как заменённый зелёным replacement
  **PR #67** (https://github.com/PavelLizunov/VPNRouter/pull/67, run `30454340845`). #66/#67 не
  входили в исходный snapshot из 8 строк; зафиксировано здесь как связанное follow-up действие,
  в executive table не добавляется.
- **Без изменений [FACT]:** #22, #42, #46 остаются **KEEP** и owner-gated (зелёные; merge
  по решению владельца). Никаких merge/release в этом разделе не утверждается.
