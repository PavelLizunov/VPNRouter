# Phase 3 — R09 — Update button localization (UI-1)

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r09-localization-2026-07-29`
**Base**: `origin/main` (verified: no P1 branch touches `MainWindow.axaml` or the `UpdateButton` resource)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R09); prompt pool P06
**IDs**: UI-1
**Effort**: ~30 min
**Risk**: LOW (cosmetic localization)
**Blast radius**: `VPNRouter.App/Views/MainWindow.axaml` (+ a binding/source contract test) · ~+5 LOC · runtime: update-banner button label in RU locale
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| UI-1 | P2 | CONFIRMED | P3 | High |

Corrected scope: cosmetic localization defect with no runtime/data impact → P3.
The localized string already exists; the button simply does not bind it.

## 2. Verified current root cause (commit `b39a28c3`)

- `VPNRouter.App/Views/MainWindow.axaml:712-713` (verified):
  ```xml
  <Button DockPanel.Dock="Right"
          Content="↓ Update"
          Command="{Binding UpdateVm.DownloadAndApplyCommand}" .../>
  ```
  The update-banner button uses a hardcoded English literal.
- The localized resource EXISTS:
  - `VPNRouter.Core/Localization/Strings.cs:773`:
    `public static string UpdateButton => Ru ? "Обновить" : "Update";`
  - `VPNRouter.App/Localization/Strings.cs:439`:
    `public static string UpdateButton => global::VPNRouter.Core.Localization.Strings.UpdateButton;`
- Consequence: RU users see the English "Update" label on the update banner.

## 3. Why

RU-locale users see an English button label even though a localized `UpdateButton`
resource already exists and is surfaced through the App `Strings` forwarder. This
is a one-line binding fix that completes the localization contract for the update
banner.

## 4. What

Bind the button content to the existing localized `UpdateButton` string instead of
the hardcoded literal, using the same localization mechanism the rest of the
window uses (verify how sibling buttons bind `Strings.X` — e.g. a `{x:Static}` /
binding to a VM-exposed string, per the established pattern in `MainWindow.axaml`).
Preserve the decorative `↓` glyph if the existing pattern supports a prefix without
re-hardcoding English (otherwise drop the glyph rather than re-introduce an
unlocalized literal). Do NOT add a new resource.

```diff
- <Button DockPanel.Dock="Right"
-         Content="↓ Update"
+ <Button DockPanel.Dock="Right"
+         Content="{Binding UpdateButtonLabel}"   <!-- or the established {x:Static loc:Strings.UpdateButton} idiom -->
          Command="{Binding UpdateVm.DownloadAndApplyCommand}" .../>
```

(The exact binding form MUST match the idiom already used by sibling localized
buttons in the same file — discover it before editing.)

## 5. How (ordered minimal steps)

1. Read `MainWindow.axaml` around the update banner and find how OTHER localized
   buttons/labels bind their text (the established `Strings` idiom).
2. Confirm the `UpdateButton` resource path (Core `Strings.cs:773` → App
   `Strings.cs:439`).
3. Replace the hardcoded `Content="↓ Update"` with the established localized
   binding. Decide the glyph handling per the existing pattern (do not hardcode
   English).
4. Add a minimal source/binding contract test.

### Tests written

- `LocalizationStringsTests.UpdateButton_RuAndEn_AreLocalized` — asserts
  `Strings.UpdateButton` returns "Обновить" (Ru) and "Update" (En) — pins the
  resource the binding relies on.
- (If the project has a XAML-binding characterization helper) a contract test that
  the update button's `Content` resolves to the localized resource, not a literal.
  If no such helper exists, the source-contract test above is sufficient — do NOT
  add a new test abstraction for one button.

### Verification approach

Source/binding contract assertion. No local Avalonia launch; build and any
headless test run in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- Consumers: the update-banner button (`UpdateVm.DownloadAndApplyCommand`).
  Invariant: the command binding, visibility (`!UpdateVm.IsDownloading`), and
  styling are unchanged; only the label source changes.
- The `UpdateButton` resource is shared — do NOT change its value or add a
  duplicate key.

## 7. Exact expected file list

- `VPNRouter.App/Views/MainWindow.axaml` (bind the update button content)
- `VPNRouter.Tests/LocalizationStringsTests.cs` (or the existing localization test file — add a contract test)

## 8. Non-goals

- Do NOT add a new localization resource or a new localization abstraction.
- Do NOT touch the other `UpdateButton*` resources (Download/Install/Dismiss/
  Retry/GrantPermission) unless the same hardcoded-literal defect is proven there
  (keep scope to the reported button).
- Do NOT change the `Ru`/`En` selection mechanism.
- Do NOT launch the Avalonia app or run MCP (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security / concurrency / data-loss**: none (cosmetic label).
- **Platform**: Avalonia binding, cross-platform.

## 10. Dependencies / overlaps

- No P1 branch touches `MainWindow.axaml` → base `origin/main`.
- R04 (UI-2) touches `NetworkPage.axaml` (different file) — independent. Although
  both were nominally in prompt pool P06, the P06 P1 branch (FLOW-1) touched
  neither; R04 and R09 are independent of each other and of P06.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): localization contract test passes; existing App tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: confirm the binding uses the established idiom (static).
- [ ] Gate 5 — MCP verify: deferred to orchestrator's live stage (RU-locale screenshot); Qwen does NOT run MCP.
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [ ] Gate 5: PENDING (RU-locale screenshot — orchestrator/live stage)
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R09 branch, or delete
`codex/qwen-audit-r09-localization-2026-07-29`. The button reverts to the
hardcoded "↓ Update" label; no state is written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase3-audit-r09-localization-2026-07-29.md через Qwen Code.
ID: UI-1 (P3). Base branch: origin/main. Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md, VPNRouter.App/CLAUDE.md и
VPNRouter.Core/Localization/Strings.cs. Привяжи Content кнопки обновления в
Views/MainWindow.axaml:712-713 к существующей локализованной строке UpdateButton
(Strings.cs:773 / App Strings.cs:439) вместо hardcoded "↓ Update". Используй
существующую localization infrastructure; не добавляй новый ресурс. Напиши
минимальный RU/EN binding/source contract test. НЕ запускай локальные
build/Avalonia app/binary, не делай live/MCP мутаций. Только чтение/поиск/
редактирование и запись тестов. Commit/push/CI делает orchestrator. Без
release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
