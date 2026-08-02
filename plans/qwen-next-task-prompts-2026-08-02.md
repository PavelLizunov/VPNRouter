# Qwen: следующие задачи и готовые промты

Статус: очередь после повторного аудита удаления CustomDirectRules и границ
модулей. Начинать с задачи 1. Android и ConfigGenerator пока не дробить.

## 1. Точная карта разбиения MainWindowViewModel — P2

Цель: определить перенос каждого member без изменения поведения.

```text
Perform a read-only, symbol-level decomposition audit of:
VPNRouter.App/ViewModels/MainWindowViewModel.cs

Read AGENTS.md, VPNRouter.App/CLAUDE.md, plans/OPEN-DEFECTS.md and
plans/post-qwen-deletion-and-context-module-audit-2026-08-02.md first.

Prepare an exact move manifest for these partial files:
- MainWindowViewModel.CustomRules.cs
- MainWindowViewModel.SettingsPersistence.cs
- MainWindowViewModel.Connection.cs
- MainWindowViewModel.Zapret.cs
- MainWindowViewModel.TgProxy.cs
- MainWindowViewModel.Servers.cs

Constraints:
- Move existing complete members only.
- No new types, interfaces, services, helpers, DI or abstractions.
- Preserve attributes, XML docs, partial methods, #if blocks and signatures.
- Preserve process_name casing and VPN lifecycle ordering.
- Leave state, constructor and minimal cross-concern orchestration in the main file.
- Target <=30k approximate tokens per resulting file; hard limit 50k.
- Do not edit files.

Output:
1. Exact symbol-to-file move table with stable start/end anchors.
2. Required using directives for every target partial.
3. Cross-partial dependencies and initialization-order risks.
4. Members that must remain together.
5. Expected approximate size of every resulting file.
6. Minimal verification commands.
```

## 2. Проверка подготовленного разбиения — обязательный gate

Запускается после механического переноса членов.

```text
Adversarially review the current task diff for the MainWindowViewModel partial split.

Compare the task base with HEAD. This must be a behavior-neutral mechanical extraction.

Verify:
- No declaration was lost, duplicated, renamed or signature-changed.
- Every ObservableProperty, RelayCommand, NotifyPropertyChangedFor and partial callback retained its attributes.
- All #if PLATFORM_WINDOWS blocks and using directives remain correct.
- Constructor and field initialization order is unchanged.
- Connect, disconnect, reconnect, failover and teardown ordering is unchanged.
- Zapret and TgProxy cancellation/lifecycle behavior is unchanged.
- process_name values retain original casing.
- Reflection-based MainWindowViewModel characterization surface should remain identical.
- No new abstraction or helper was introduced merely to support the split.

Trace suspicious findings to real callers before reporting them.
Return only verified findings, ranked P0-P3, with exact file and line.
Return [] if the extraction is clean.
Do not edit files.
```

## 3. Очистка комментариев и актуализация карт модулей — P3

Включается в тот же механический PR.

```text
Perform a read-only documentation accuracy audit after the MainWindowViewModel split.

Check:
- VPNRouter.App/CLAUDE.md
- VPNRouter.Android/CLAUDE.md
- plans/v3.0-refactor-roadmap.md
- VPNRouter.App/ViewModels/MainWindowViewModel*.cs

Required outcome:
- Report the exact current MVM and AndroidApp partial-file inventories.
- Identify obsolete file counts, line counts and already-completed Phase 2B/2C proposals.
- Identify stale CustomDirectRules comments, especially references to removed
  CustomDirectRulesText and CustomDirectRulesErrorText aliases.
- Produce exact replacement text for the affected documentation sections.
- Remove only false or duplicate historical comments.
- Do not create another architecture manifest.
- Do not change runtime behavior or member signatures.
- Do not edit files.
```

## 4. Проверка контекстного профиля <=1M — P3

```text
Design and dry-run a search-driven whole-repository review profile for VPNRouter.

Use approximate tokens = bytes / 3.6.

Requirements:
- Tier 0: AGENTS/CLAUDE instructions, current state, OPEN-DEFECTS and module map.
- Tier 1: production code grouped by behavior concern.
- Tier 2: tests selected only for the loaded concern.
- Tier 3: historical plans, screenshots, evidence and designs, searchable on demand.
- Keep every ordinary product file <=50k tokens where practical.
- Keep each end-to-end concern bundle <=100k, preferably <=60k.
- Keep the default whole-review bundle <=1M.
- Do not delete or move repository files.
- Do not create a blanket .qwenignore.
- Do not exclude security, migration or release invariants.

Output:
1. Exact included file list and approximate total.
2. Excluded-on-default categories and retrieval rules.
3. Three example bundles: VPN lifecycle, Custom Rules and Android connection.
4. Any module that still cannot be reviewed completely within its concern budget.
5. The smallest implementation needed, preferably an existing script or manifest.
```

## 5. Отдельный аудит старой settings-схемы — P3, позже

```text
Perform a read-only compatibility audit of these suspected dead settings:
- AutoDownload
- DownloadUrl
- ProcessScanInterval

Trace YAML names, deserialization, validators, migration code, UI bindings,
runtime reads, tests and sample configs.

For each field classify:
- LIVE
- LEGACY_COMPATIBILITY_REQUIRED
- SAFE_TO_REMOVE
- UNKNOWN_REQUIRES_RUNTIME_MEASUREMENT

Do not infer deadness from missing direct references alone.
Account for IgnoreUnmatchedProperties and static YAML source generation.
Provide the minimal coordinated deletion set, including validators and tests.
Do not edit files.
```

## Порядок выполнения

1. Карта MVM.
2. Механическое разбиение.
3. Независимая проверка разбиения.
4. Комментарии и карты модулей в том же PR.
5. Контекстный dry-run.
6. Settings-схема отдельным последующим PR.
