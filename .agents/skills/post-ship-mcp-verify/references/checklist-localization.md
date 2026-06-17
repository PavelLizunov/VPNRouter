# Localization-only checklist

Use when release notes mention pure-localization batches
(Strings.cs members added, inline ternaries swept, no behavior change).

## Approach

Localization-only ships don't change behavior — they swap inline
ternary strings for `Strings.X` member calls. The user-visible test
is: open the affected UI surface in RU locale and confirm Russian
text appears where the changed sites are.

## Setup

1. Window already launched (Phase 2).
2. Ensure language is set to RU. Open kebab menu (top-right) → check
   "Язык: Русский" is selected. If not, switch + restart the window if
   prompted.

## Per-ship verification matrix

For each Strings member added in the release notes, verify the binding
site shows the RU text:

| Release notes member | Where to look | Expected RU text |
|---|---|---|
| `Stopped` (r7) | Zapret/TgProxy status line when stopped | «Остановлен» |
| `RulesFilePickerOpenFailed` (r9) | Network → Rules → Import → cancel file picker | «Не удалось открыть диалог выбора файлов» |
| `RulesImportFailed(w)` (r9) | Network → Rules → Import → invalid file | «Импорт не удался: ...» |
| `RulesExportNothing` (r9) | Network → Rules → Export with empty list | «Нечего экспортировать — список правил пуст» |
| `ZapretCacheCleared` (r10) | DpiBypass → Tools expander → Clear cache | «Кэш стратегий очищен» |
| `ZapretCacheEmpty` (r10) | DpiBypass → Tools expander, after Clear | «Кэш пуст — следующая проверка будет полной» |
| `ZapretCacheInfo(s,n)` (r10) | DpiBypass → Tools expander, with cached strategy | «Кэш: general (ALT3) (успехов: N)» |
| `RuleTypeHintDomain` etc. (r13) | Network → Rules → Add → select Type=domain → see hint | «точное имя (discord.com)» |
| `TgProxyStatsActive/Total` (r16) | TgProxy running state, air-pill | «Активных: N | Всего: N» |
| `ServerTestAll` / `Cancel` (r17) | Servers tab → "Test all" button label | «Проверить все» (or «Отмена» if running) |
| `BadgeTooltipZapret` (r18) | Header status badge → hover tooltip | «Zapret обход DPI» |

## What to actually do

1. Pick ONE Strings member from the release notes (most user-visible).
2. Navigate to the surface where it binds.
3. Trigger the state that surfaces the string (e.g. Clear cache button
   for `ZapretCacheCleared`).
4. Screenshot.
5. Confirm RU text appears (not English).
6. If multiple Strings members in same ship, sample 2-3 not all.

## Pass criteria

- All sampled strings render in Russian when locale=RU.
- No `IsRussian ?` ternary leaks (would show as `True : "..."` or
  similar — but these would be compile errors, so rare).
- No member-name leak ("ZapretCacheCleared" instead of «Кэш стратегий
  очищен" would indicate Strings.cs missing or pass-through broken).

## Skip when

- Ship is also a feature add (use the matching feature checklist
  instead — localization tests run alongside).
- Locale is EN (test in RU because RU is where D1 violations surface).

## Screenshots to attach

- `tmp-rN-loc-sampleN.png` — one screenshot per sampled member.
